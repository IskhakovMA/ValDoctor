using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using ValDoctor.Core.Elevation;
using ValDoctor.Core.Hosts;
using ValDoctor.Core.Legacy;
using ValDoctor.Core.Logging;
using ValDoctor.Core.Updates;

namespace ValDoctor.App.Services;

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public const string RepositoryUrl = "https://github.com/IskhakovMA/riotfix";

    /// <summary>Full version including any prerelease suffix (from the informational version, "+commit" removed).</summary>
    public static SemVersion SemanticVersion { get; } =
        SemVersion.TryParse(typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, out var full)
            ? full
            : typeof(AppInfo).Assembly.GetName().Version is { } v ? SemVersion.Create(v.Major, v.Minor, v.Build) : SemVersion.Create(0, 0, 0);

    public static string DataDirectory { get; } =
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ValDoctor");

    /// <summary>Folder used by RiotFix 0.1.0–0.2.1; read for the update-check state, never written.</summary>
    public static string LegacyDataDirectory { get; } =
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), RiotFixLegacy.AppDataFolder);

    public static string LogDirectory { get; } = Path.Join(DataDirectory, "logs");
}

internal static class HostsLocator
{
    /// <summary>
    /// The hosts file lives in the directory named by the Tcpip "DataBasePath" registry value (HKLM, admin-only).
    /// The value is read WITHOUT expanding environment variables: the elevated helper's environment comes from
    /// the user profile (HKCU\Environment is user-writable), so "%SystemRoot%" must not be resolved through it.
    /// Only the standard location (resolved via the system API) or a literal absolute local path is accepted.
    /// </summary>
    public static string GetHostsPath()
    {
        string? raw = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
            raw = key?.GetValue("DataBasePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        // Environment.SystemDirectory comes from GetSystemDirectoryW, not from environment variables.
        return HostsPathPolicy.Resolve(raw, Environment.SystemDirectory);
    }
}

internal static partial class DnsCache
{
    /// <summary>Clears the Windows DNS client cache (same as <c>ipconfig /flushdns</c>; no admin rights needed).</summary>
    public static void Flush()
    {
        try
        {
            if (DnsFlushResolverCache())
            {
                return;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        var ipconfig = Path.Join(Environment.SystemDirectory, "ipconfig.exe");
        using var process = Process.Start(new ProcessStartInfo(ipconfig, "/flushdns")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        process?.WaitForExit(10_000);
    }

    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}

internal static partial class NativeHardening
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    /// <summary>
    /// Restricts further native DLL loading of this process to System32, so an elevated ValDoctor
    /// cannot pick up a planted DLL from the download folder or the current directory.
    /// </summary>
    public static void RestrictDllSearchToSystem32()
    {
        try
        {
            _ = SetDefaultDllDirectories(LoadLibrarySearchSystem32);
            _ = SetDllDirectoryW(string.Empty);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Not available: nothing else to do, the helper still validates all input.
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string pathName);
}

internal static class RiotProcesses
{
    private static readonly string[] Names =
    [
        "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "Riot Client",
        "VALORANT", "VALORANT-Win64-Shipping", "LeagueClient", "LeagueClientUx", "League of Legends",
    ];

    /// <summary>Only checks process names. ValDoctor never touches Riot processes, Vanguard or game files.</summary>
    public static bool AnyRunning()
    {
        foreach (var name in Names)
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var p in processes)
            {
                p.Dispose();
            }

            if (running)
            {
                return true;
            }
        }

        return false;
    }
}

internal enum ElevationStatus
{
    Completed,
    UserDeclined,
    Failed,
}

internal sealed record ElevationOutcome(ElevationStatus Status, ElevatedExitCode ExitCode);

/// <summary>Runs one hosts operation in a short-lived elevated copy of ValDoctor.exe (UAC prompt).</summary>
internal sealed class ElevationService(HostsFileService hosts, IAppLog log)
{
    private const int ErrorCancelled = 1223;

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<ElevationOutcome> RunAsync(ElevatedRequest request)
    {
        var args = ElevatedProtocol.ToArguments(request);
        log.Info($"Elevation: requesting '{request.Operation}' with {request.Pins.Count} pins (disable conflicts: {request.DisableConflicts}).");

        if (IsAdministrator())
        {
            var code = await Task.Run(() => ElevatedRunner.Run(args, hosts, DnsCache.Flush, log));
            log.Info($"Elevation: in-process result {code}.");
            return new ElevationOutcome(ElevationStatus.Completed, code);
        }

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            // Every argument is validated (allowlisted host names, IP literals) and contains no spaces or quotes.
            Arguments = string.Join(' ', args),
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ElevationOutcome(ElevationStatus.Failed, ElevatedExitCode.UnexpectedError);
            }

            await process.WaitForExitAsync();
            var code = Enum.IsDefined(typeof(ElevatedExitCode), process.ExitCode)
                ? (ElevatedExitCode)process.ExitCode
                : ElevatedExitCode.UnexpectedError;
            log.Info($"Elevation: helper exited with {code}.");
            return new ElevationOutcome(ElevationStatus.Completed, code);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log.Info("Elevation: user declined the UAC prompt.");
            return new ElevationOutcome(ElevationStatus.UserDeclined, ElevatedExitCode.UnexpectedError);
        }
        catch (Win32Exception ex)
        {
            log.Error("Elevation: could not start helper.", ex);
            return new ElevationOutcome(ElevationStatus.Failed, ElevatedExitCode.UnexpectedError);
        }
    }
}
