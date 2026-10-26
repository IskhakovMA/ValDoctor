using System.Net;

namespace ValDoctor.Core.Hosts;

/// <summary>How the hosts file bytes are mapped to text. Every mode round-trips the original bytes exactly.</summary>
public enum HostsTextEncoding
{
    /// <summary>No BOM. Bytes are mapped 1:1 to chars (Latin-1), so ANSI and UTF-8 content survives unchanged.</summary>
    SingleByte,

    /// <summary>UTF-8 with BOM. The BOM is kept; the rest is mapped 1:1 like <see cref="SingleByte"/>.</summary>
    Utf8Bom,

    /// <summary>UTF-16 little endian with BOM.</summary>
    Utf16LittleEndian,

    /// <summary>UTF-16 big endian with BOM.</summary>
    Utf16BigEndian,
}

/// <summary>A hosts pin: the host name is resolved to this address.</summary>
public sealed record HostPin(string Host, IPAddress Address)
{
    public override string ToString() => $"{Address} {Host}";
}

/// <summary>An active (not commented out) hosts entry line that ValDoctor did not write.</summary>
public sealed record ForeignEntry(int LineIndex, string LineText, IPAddress Address, IReadOnlyList<string> HostNames);

/// <summary>
/// A third-party hosts line that maps one of the hosts ValDoctor wants to pin.
/// <see cref="CanDisable"/> is false when the same line also maps unrelated names:
/// ValDoctor never comments out a line that would affect anything except Riot hosts.
/// </summary>
public sealed record HostsConflict(
    int LineIndex,
    string LineText,
    IPAddress Address,
    IReadOnlyList<string> HostNames,
    IReadOnlyList<string> AffectedHosts,
    bool CanDisable);

/// <summary>A third-party line ValDoctor commented out and can restore byte-for-byte.</summary>
public sealed record DisabledLine(int LineIndex, string OriginalText, IReadOnlyList<string> HostNames);

/// <summary>Summary of what a hosts edit changed.</summary>
public sealed record HostsChange(bool Changed, int PinsAdded, int PinsRemoved, int LinesDisabled, int LinesRestored)
{
    public static HostsChange None { get; } = new(false, 0, 0, 0, 0);
}

public sealed class HostsConflictException : Exception
{
    public HostsConflictException(IReadOnlyList<HostsConflict> conflicts)
        : base("The hosts file already contains third-party entries for the hosts being pinned.")
    {
        Conflicts = conflicts;
    }

    public HostsConflictException()
        : this([])
    {
    }

    public HostsConflictException(string message)
        : base(message)
    {
        Conflicts = [];
    }

    public HostsConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
        Conflicts = [];
    }

    public IReadOnlyList<HostsConflict> Conflicts { get; }
}

public sealed class HostsFormatException : Exception
{
    public HostsFormatException()
    {
    }

    public HostsFormatException(string message)
        : base(message)
    {
    }

    public HostsFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
