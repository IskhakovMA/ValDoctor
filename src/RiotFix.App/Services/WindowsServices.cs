using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using RiotFix.Core.Elevation;
using RiotFix.Core.Hosts;
using RiotFix.Core.Logging;

namespace RiotFix.App.Services;

internal static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public const string RepositoryUrl = "https://github.com/IskhakovMA/riotfix";

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiotFix", "logs");
}

internal static class HostsLocator
{
    /// <summary>
    /// The hosts file lives in the directory named by the Tcpip "DataBasePath" registry value
    /// (only administrators can change it); falls back to %SystemRoot%\System32\drivers\etc.
    /// </summary>
    public static string GetHostsPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters");
            if (key?.GetValue("DataBasePath") is string raw)
            {
                var expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
                if (Path.IsPathFullyQualified(expanded) && !expanded.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    return Path.Combine(expanded, "hosts");
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        return Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
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

        var ipconfig = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
        using var process = Process.Start(new ProcessStartInfo(ipconfig, "/flushdns")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        });
        process?.WaitForExit(10_000);
    }

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}

internal static class RiotProcesses
{
    private static readonly string[] Names =
    [
        "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "Riot Client",
        "VALORANT", "VALORANT-Win64-Shipping", "LeagueClient", "LeagueClientUx", "League of Legends",
    ];

    /// <summary>Only checks process names. RiotFix never touches Riot processes, Vanguard or game files.</summary>
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

/// <summary>Runs one hosts operation in a short-lived elevated copy of RiotFix.exe (UAC prompt).</summary>
internal sealed class ElevationService(HostsFileService hosts, IRiotFixLog log)
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
