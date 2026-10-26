namespace RiotFix.Core.Hosts;

/// <summary>
/// Decides which hosts file to use from the raw (NOT environment-expanded) Tcpip "DataBasePath" registry value.
/// The elevated helper's environment comes from the user profile (HKCU\Environment is user-writable), so
/// "%SystemRoot%" or any other variable must never be expanded through it. Only two forms are accepted:
/// the standard value (mapped to the real system directory from the OS API) or a literal absolute local path.
/// </summary>
public static class HostsPathPolicy
{
    public const string StandardDataBasePath = @"%SystemRoot%\System32\drivers\etc";

    public static string Resolve(string? rawDataBasePath, string systemDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(systemDirectory);
        var standard = Path.Join(systemDirectory, "drivers", "etc", "hosts");
        if (string.IsNullOrWhiteSpace(rawDataBasePath))
        {
            return standard;
        }

        var value = rawDataBasePath.Trim().TrimEnd('\\');
        if (string.Equals(value, StandardDataBasePath, StringComparison.OrdinalIgnoreCase))
        {
            return standard;
        }

        // A literal drive path such as "D:\Windows\System32\drivers\etc" (HKLM is admin-only, so it is trusted).
        // No variables, no UNC/device paths, no relative paths, no ".." segments.
        var isDrivePath = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';
        if (isDrivePath && !value.Contains('%', StringComparison.Ordinal) && !value.Contains('/', StringComparison.Ordinal) &&
            !value.Split('\\').Any(segment => segment is ".." or "."))
        {
            return value + @"\hosts";
        }

        return standard;
    }
}
