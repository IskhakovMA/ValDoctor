using System.Globalization;
using System.Text;

namespace RiotFix.Core.Logging;

public interface IRiotFixLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : IRiotFixLog
{
    public static NullLog Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// Local diagnostic log. Contains only Riot host names, Riot server IPs, error categories and timings.
/// The user's name and profile path are scrubbed from every line; no credentials, tokens or
/// local network addresses are ever logged. Nothing is sent anywhere.
/// </summary>
public sealed class FileLog : IRiotFixLog
{
    private const int KeepFiles = 10;
    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly string[] _scrub;

    public FileLog(string directory)
    {
        _directory = directory;
        var scrub = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            scrub.Add(profile);
        }

        // Profile paths are the only place the user name can appear (exception messages with paths).
        // Plain user/machine names are not scrubbed as words: they could collide with host names.
        _scrub = [.. scrub.OrderByDescending(s => s.Length)];
        TryCleanup();
    }

    public string Directory => _directory;

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    internal string Sanitize(string text)
    {
        foreach (var s in _scrub)
        {
            text = text.Replace(s, "<redacted>", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private void Write(string level, string message, Exception? exception)
    {
        var sb = new StringBuilder();
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level).Append(' ').Append(message);
        if (exception is not null)
        {
            sb.Append(" | ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message)
                .Append(" (0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture)).Append(')');
        }

        var line = Sanitize(sb.ToString()) + Environment.NewLine;
        try
        {
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_directory);
                var file = Path.Combine(_directory, $"riotfix-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the app.
        }
    }

    private void TryCleanup()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return;
            }

            var old = new DirectoryInfo(_directory).GetFiles("riotfix-*.log")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(KeepFiles);
            foreach (var file in old)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
