using System.Net;
using System.Security.Cryptography;
using System.Text;
using ValDoctor.Core.Legacy;
using ValDoctor.Core.Network;

namespace ValDoctor.Core.Hosts;

/// <summary>
/// In-memory model of a Windows hosts file that preserves every byte ValDoctor does not own.
/// <para>
/// ValDoctor owns exactly three kinds of lines:
/// <list type="bullet">
/// <item>block markers (<see cref="BeginMarker"/> / <see cref="EndMarker"/>);</item>
/// <item>pin lines <c>IP HOST # valdoctor:XXXXXXXX</c>, where the tag is a hash of "IP|HOST";</item>
/// <item>disabled third-party lines <c># valdoctor-off:XXXXXXXX ORIGINAL</c>, where the tag is a hash of ORIGINAL.</item>
/// The same lines written by RiotFix (<c># riotfix:</c>, <c># riotfix-off:</c>, "RiotFix BEGIN/END") are owned as well,
/// see <see cref="Legacy.RiotFixLegacy"/>; any rewrite of the block converts them to the ValDoctor format in place.
/// </list>
/// The hash makes ownership self-verifying: if a user edits a ValDoctor line, the hash no longer
/// matches and the line is treated as the user's own, so ValDoctor never removes or "restores" it.
/// No external state file is needed — the hosts file itself is the source of truth.
/// </para>
/// </summary>
public sealed class HostsDocument
{
    public const string BeginMarker = "# >>> ValDoctor BEGIN (managed block, https://github.com/IskhakovMA/riotfix)";
    public const string EndMarker = "# <<< ValDoctor END";
    internal const string BeginMarkerPrefix = "# >>> ValDoctor BEGIN";
    internal const string PinTagPrefix = "# valdoctor:";
    internal const string DisabledPrefix = "# valdoctor-off:";

    // Formats ValDoctor writes first, then the RiotFix (0.1.0–0.2.1) formats it still recognises and rolls back.
    private static readonly string[] BeginMarkerPrefixes = [BeginMarkerPrefix, RiotFixLegacy.HostsBeginMarkerPrefix];
    private static readonly string[] EndMarkers = [EndMarker, RiotFixLegacy.HostsEndMarker];
    private static readonly string[] PinTagPrefixes = [PinTagPrefix, RiotFixLegacy.PinTagPrefix];
    private static readonly string[] DisabledPrefixes = [DisabledPrefix, RiotFixLegacy.DisabledPrefix];

    private readonly List<Line> _lines;

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];

    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    private HostsDocument(List<Line> lines, HostsTextEncoding encoding, string newLine)
    {
        _lines = lines;
        Encoding = encoding;
        NewLine = newLine;
    }

    public HostsTextEncoding Encoding { get; }

    /// <summary>The line terminator used for lines ValDoctor inserts (matches the file's dominant style).</summary>
    public string NewLine { get; }

    public int LineCount => _lines.Count;

    public static HostsDocument Empty() => new([], HostsTextEncoding.SingleByte, "\r\n");

    public static HostsDocument Parse(ReadOnlySpan<byte> bytes)
    {
        HostsTextEncoding encoding;
        string text;

        if (bytes.StartsWith(Utf8Bom))
        {
            encoding = HostsTextEncoding.Utf8Bom;
            text = System.Text.Encoding.Latin1.GetString(bytes[3..]);
        }
        else if (bytes.StartsWith(Utf16LeBom))
        {
            encoding = HostsTextEncoding.Utf16LittleEndian;
            text = DecodeStrict(new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), bytes[2..]);
        }
        else if (bytes.StartsWith(Utf16BeBom))
        {
            encoding = HostsTextEncoding.Utf16BigEndian;
            text = DecodeStrict(new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), bytes[2..]);
        }
        else
        {
            if (bytes.Contains((byte)0))
            {
                // Most likely UTF-16 without a BOM or a binary file. Refuse rather than risk corrupting it.
                throw new HostsFormatException("The hosts file contains NUL bytes and is not a supported text format.");
            }

            encoding = HostsTextEncoding.SingleByte;
            text = System.Text.Encoding.Latin1.GetString(bytes);
        }

        var lines = SplitLines(text, out var crlf, out var lf, out var cr);
        var newLine = crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
        if (crlf == 0 && lf == 0 && cr == 0)
        {
            newLine = "\r\n";
        }

        return new HostsDocument(lines, encoding, newLine);
    }

    public byte[] ToBytes()
    {
        var text = ToText();
        return Encoding switch
        {
            HostsTextEncoding.SingleByte => System.Text.Encoding.Latin1.GetBytes(text),
            HostsTextEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.Latin1.GetBytes(text)],
            HostsTextEncoding.Utf16LittleEndian => [0xFF, 0xFE, .. new UnicodeEncoding(false, false).GetBytes(text)],
            HostsTextEncoding.Utf16BigEndian => [0xFE, 0xFF, .. new UnicodeEncoding(true, false).GetBytes(text)],
            _ => throw new InvalidOperationException("Unknown encoding."),
        };
    }

    /// <summary>The decoded text (without BOM). For single-byte files non-ASCII bytes appear as Latin-1 chars.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            sb.Append(line.Text).Append(line.Terminator);
        }

        return sb.ToString();
    }

    /// <summary>True when the file contains anything ValDoctor wrote: pins, disabled lines or markers.</summary>
    public bool HasManagedContent =>
        _lines.Any(l => IsMarker(l.Text) || TryParsePin(l.Text, out _) || TryParseDisabled(l.Text, out _, out _));

    /// <summary>True when the file still contains lines in the RiotFix (0.1.0–0.2.1) format.</summary>
    public bool HasLegacyRiotFixContent =>
        _lines.Any(l =>
        {
            var t = l.Text.Trim();
            return t.StartsWith(RiotFixLegacy.HostsBeginMarkerPrefix, StringComparison.Ordinal) ||
                   t == RiotFixLegacy.HostsEndMarker ||
                   (TryParsePin(l.Text, RiotFixLegacy.PinTagPrefix, out _)) ||
                   (t.StartsWith(RiotFixLegacy.DisabledPrefix, StringComparison.Ordinal) && TryParseDisabled(l.Text, out _, out _));
        });

    public IReadOnlyList<HostPin> GetPins()
    {
        var result = new List<HostPin>();
        foreach (var line in _lines)
        {
            if (TryParsePin(line.Text, out var pin))
            {
                result.Add(pin);
            }
        }

        return result;
    }

    public HostPin? GetPin(string host) =>
        GetPins().FirstOrDefault(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<DisabledLine> GetDisabledLines()
    {
        var result = new List<DisabledLine>();
        for (var i = 0; i < _lines.Count; i++)
        {
            if (TryParseDisabled(_lines[i].Text, out var original, out var names))
            {
                result.Add(new DisabledLine(i, original, names));
            }
        }

        return result;
    }

    /// <summary>Active entries ValDoctor does not own (third-party or user-written, including edited ValDoctor lines).</summary>
    public IReadOnlyList<ForeignEntry> GetForeignEntries()
    {
        var result = new List<ForeignEntry>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var text = _lines[i].Text;
            if (TryParsePin(text, out _))
            {
                continue;
            }

            if (TryParseEntry(text, out var address, out var names))
            {
                result.Add(new ForeignEntry(i, text, address, names));
            }
        }

        return result;
    }

    public IReadOnlyList<ForeignEntry> GetForeignEntries(string host) =>
        GetForeignEntries()
            .Where(e => e.HostNames.Contains(host, StringComparer.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<HostsConflict> FindConflicts(IEnumerable<string> hosts)
    {
        var targets = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
        var result = new List<HostsConflict>();
        foreach (var entry in GetForeignEntries())
        {
            var affected = entry.HostNames.Where(targets.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (affected.Count == 0)
            {
                continue;
            }

            var canDisable = entry.HostNames.All(targets.Contains);
            result.Add(new HostsConflict(entry.LineIndex, entry.LineText, entry.Address, entry.HostNames, affected, canDisable));
        }

        return result;
    }

    /// <summary>
    /// Makes the ValDoctor block contain exactly <paramref name="pins"/>. Pins for hosts not listed are removed,
    /// third-party lines disabled earlier for hosts that are no longer pinned are restored.
    /// Re-applying the same pins is a no-op (<see cref="HostsChange.Changed"/> is false).
    /// </summary>
    /// <exception cref="HostsConflictException">
    /// Third-party entries exist for pinned hosts and either <paramref name="disableConflicts"/> is false
    /// or at least one conflicting line also maps unrelated names. The document is left unchanged.
    /// </exception>
    public HostsChange SetPins(IReadOnlyCollection<HostPin> pins, bool disableConflicts)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ValidatePins(pins);

        var targets = new HashSet<string>(pins.Select(p => p.Host), StringComparer.OrdinalIgnoreCase);
        var conflicts = FindConflicts(targets);
        if (conflicts.Count > 0 && (!disableConflicts || conflicts.Any(c => !c.CanDisable)))
        {
            throw new HostsConflictException(conflicts);
        }

        var before = Snapshot();
        var oldPins = GetPins();
        var disabled = 0;
        var restored = 0;

        // 1. Comment out conflicting third-party lines (only reached with explicit consent).
        foreach (var conflict in conflicts)
        {
            var line = _lines[conflict.LineIndex];
            _lines[conflict.LineIndex] = line with { Text = MakeDisabled(line.Text) };
            disabled++;
        }

        // 2. Restore lines we disabled earlier once none of their hosts is pinned any more. A line that still maps
        //    a pinned host stays disabled (restoring it would conflict); a full revert always restores it.
        for (var i = 0; i < _lines.Count; i++)
        {
            if (TryParseDisabled(_lines[i].Text, out var original, out var names) && !names.Any(targets.Contains))
            {
                _lines[i] = _lines[i] with { Text = original };
                restored++;
            }
        }

        // 3. Remove the old block (markers and pin lines anywhere) and remember where it was.
        var insertAt = RemoveManagedLines(_ => true);

        // 4. Write the new block at the old position, or append it at the end.
        if (pins.Count > 0)
        {
            InsertBlock(insertAt, pins);
        }

        var changed = !before.SequenceEqual(_lines);
        if (!changed)
        {
            return HostsChange.None;
        }

        var oldSet = oldPins.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSet = pins.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new HostsChange(
            true,
            PinsAdded: newSet.Count(k => !oldSet.Contains(k)),
            PinsRemoved: oldSet.Count(k => !newSet.Contains(k)),
            LinesDisabled: disabled,
            LinesRestored: restored);
    }

    /// <summary>Removes everything ValDoctor owns and restores every third-party line it disabled.</summary>
    public HostsChange RevertAll()
    {
        var before = Snapshot();
        var pinsRemoved = GetPins().Count;
        var restored = 0;

        for (var i = 0; i < _lines.Count; i++)
        {
            if (TryParseDisabled(_lines[i].Text, out var original, out _))
            {
                _lines[i] = _lines[i] with { Text = original };
                restored++;
            }
        }

        RemoveManagedLines(_ => true);

        return before.SequenceEqual(_lines)
            ? HostsChange.None
            : new HostsChange(true, 0, pinsRemoved, 0, restored);
    }

    /// <summary>
    /// Partial rollback: removes ValDoctor pins for the given hosts and restores third-party lines that were
    /// disabled for them. Markers are removed only when no ValDoctor pins remain.
    /// </summary>
    public HostsChange Revert(IEnumerable<string> hosts)
    {
        var set = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
        var before = Snapshot();
        var restored = 0;
        var remaining = GetPins().Select(p => p.Host).Where(h => !set.Contains(h)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _lines.Count; i++)
        {
            // Restore only when no host on that line stays pinned; otherwise it would conflict with a remaining pin.
            if (TryParseDisabled(_lines[i].Text, out var original, out var names) && names.Any(set.Contains) && !names.Any(remaining.Contains))
            {
                _lines[i] = _lines[i] with { Text = original };
                restored++;
            }
        }

        var removedPins = 0;
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (TryParsePin(_lines[i].Text, out var pin) && set.Contains(pin.Host))
            {
                _lines.RemoveAt(i);
                removedPins++;
            }
        }

        if (GetPins().Count == 0)
        {
            RemoveManagedLines(text => IsMarker(text));
        }

        return before.SequenceEqual(_lines)
            ? HostsChange.None
            : new HostsChange(true, 0, removedPins, 0, restored);
    }

    internal static string PinLine(HostPin pin) => PinLine(pin, PinTagPrefix);

    /// <summary>Tests only: a correctly tagged line for any address (used to model a forged line).</summary>
    internal static string PinLineUnchecked(HostPin pin) => PinLine(pin, PinTagPrefix);

    private static string PinLine(HostPin pin, string tagPrefix) =>
        $"{pin.Address} {pin.Host.ToLowerInvariant()} {tagPrefix}{Hash($"{pin.Address}|{pin.Host.ToLowerInvariant()}")}";

    internal static string MakeDisabled(string original) => $"{DisabledPrefix}{Hash(original)} {original}";

    private static void ValidatePins(IReadOnlyCollection<HostPin> pins)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in pins)
        {
            ArgumentNullException.ThrowIfNull(pin);
            if (!RiotServices.IsAllowed(pin.Host))
            {
                throw new ArgumentException($"Host '{pin.Host}' is not in the ValDoctor allowlist.", nameof(pins));
            }

            if (!AddressPolicy.IsPublicUnicast(pin.Address))
            {
                throw new ArgumentException($"Address '{pin.Address}' is not a public unicast address.", nameof(pins));
            }

            if (!seen.Add(pin.Host))
            {
                throw new ArgumentException($"Host '{pin.Host}' is pinned more than once.", nameof(pins));
            }
        }
    }

    private static string Key(HostPin pin) => $"{pin.Host.ToLowerInvariant()}|{pin.Address}";

    private List<Line> Snapshot() => [.. _lines];

    /// <summary>Removes marker and pin lines matching the filter; returns the index of the first removed line (or the end).</summary>
    private int RemoveManagedLines(Func<string, bool> filter)
    {
        var insertAt = -1;
        var kept = new List<Line>(_lines.Count);
        var removedUnterminatedLast = false;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var owned = IsMarker(line.Text) || TryParsePin(line.Text, out _);
            if (owned && filter(line.Text))
            {
                if (insertAt < 0)
                {
                    insertAt = kept.Count;
                }

                removedUnterminatedLast = i == _lines.Count - 1 && line.Terminator.Length == 0;
                continue;
            }

            kept.Add(line);
        }

        // ValDoctor writes its END marker without a newline when the original file had no trailing newline
        // (and adds one to the user's last line instead). Undo that so the original bytes come back exactly.
        if (removedUnterminatedLast && kept.Count > 0 && kept[^1].Terminator.Length > 0)
        {
            kept[^1] = kept[^1] with { Terminator = string.Empty };
        }

        _lines.Clear();
        _lines.AddRange(kept);
        return insertAt < 0 ? _lines.Count : insertAt;
    }

    private void InsertBlock(int index, IReadOnlyCollection<HostPin> pins)
    {
        var endTerminator = NewLine;
        if (index == _lines.Count && _lines.Count > 0 && _lines[^1].Terminator.Length == 0)
        {
            // The file does not end with a newline: move the newline to before our block and leave the END
            // marker unterminated, so removing the block restores the original bytes.
            _lines[^1] = _lines[^1] with { Terminator = NewLine };
            endTerminator = string.Empty;
        }

        var block = new List<Line> { new(BeginMarker, NewLine) };
        block.AddRange(pins
            .OrderBy(p => RiotServices.OrderOf(p.Host))
            .ThenBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
            .Select(p => new Line(PinLine(p), NewLine)));
        block.Add(new Line(EndMarker, endTerminator));
        _lines.InsertRange(index, block);
    }

    private static bool IsMarker(string text)
    {
        var t = text.Trim();
        return BeginMarkerPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)) || EndMarkers.Contains(t, StringComparer.Ordinal);
    }

    private static bool TryParsePin(string text, out HostPin pin)
    {
        foreach (var prefix in PinTagPrefixes)
        {
            if (TryParsePin(text, prefix, out pin))
            {
                return true;
            }
        }

        pin = null!;
        return false;
    }

    private static bool TryParsePin(string text, string tagPrefix, out HostPin pin)
    {
        pin = null!;
        var tagIndex = text.IndexOf(tagPrefix, StringComparison.Ordinal);
        if (tagIndex < 0)
        {
            return false;
        }

        var tag = text[(tagIndex + tagPrefix.Length)..].Trim();
        if (!TryParseEntry(text[..tagIndex], out var address, out var names) || names.Count != 1)
        {
            return false;
        }

        var host = names[0].ToLowerInvariant();
        // A correctly tagged line with a non-public address was never written by ValDoctor or RiotFix (both refuse such
        // addresses), so it is treated as the user's own and never removed.
        if (!RiotServices.IsAllowed(host) || !AddressPolicy.IsPublicUnicast(address) ||
            !string.Equals(tag, Hash($"{address}|{host}"), StringComparison.Ordinal))
        {
            return false;
        }

        // The line must be exactly what ValDoctor writes (or RiotFix wrote, for the legacy tag), modulo surrounding whitespace.
        if (!string.Equals(text.Trim(), PinLine(new HostPin(host, address), tagPrefix), StringComparison.Ordinal))
        {
            return false;
        }

        pin = new HostPin(host, address);
        return true;
    }

    private static bool TryParseDisabled(string text, out string original, out IReadOnlyList<string> names)
    {
        original = string.Empty;
        names = [];
        var prefix = DisabledPrefixes.FirstOrDefault(p => text.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null)
        {
            return false;
        }

        var rest = text[prefix.Length..];
        if (rest.Length < 9 || rest[8] != ' ')
        {
            return false;
        }

        var tag = rest[..8];
        var candidate = rest[9..];
        if (!string.Equals(tag, Hash(candidate), StringComparison.Ordinal))
        {
            return false;
        }

        // ValDoctor and RiotFix only ever disable entry lines whose names are ALL Riot hosts (CanDisable). The tag is an
        // unkeyed hash, so anyone can forge it: restoring arbitrary forged lines would let a planted comment become an
        // active entry for any name. Anything else is therefore an ordinary comment, never restored.
        if (!TryParseEntry(candidate, out _, out var parsed) || parsed.Count == 0 || !parsed.All(RiotServices.IsAllowed))
        {
            return false;
        }

        original = candidate;
        names = parsed;
        return true;
    }

    /// <summary>Parses an active hosts entry: "IP name [name...] [# comment]".</summary>
    internal static bool TryParseEntry(string text, out IPAddress address, out IReadOnlyList<string> names)
    {
        address = IPAddress.None;
        names = [];

        var hash = text.IndexOf('#', StringComparison.Ordinal);
        var content = hash >= 0 ? text[..hash] : text;
        var tokens = content.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !AddressPolicy.TryParseStrict(tokens[0], out address))
        {
            return false;
        }

        // "auth.riotgames.com." (fully qualified) names the same host.
        names = tokens[1..].Select(n => n.Length > 1 ? n.TrimEnd('.') : n).ToArray();
        return true;
    }

    private static string Hash(string value)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value), digest);
        return Convert.ToHexStringLower(digest[..4]);
    }

    private static string DecodeStrict(System.Text.Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new HostsFormatException("The hosts file is not valid UTF-16.", ex);
        }
    }

    private static List<Line> SplitLines(string text, out int crlf, out int lf, out int cr)
    {
        crlf = lf = cr = 0;
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    lines.Add(new Line(text[start..i], "\r\n"));
                    crlf++;
                    i++;
                }
                else
                {
                    lines.Add(new Line(text[start..i], "\r"));
                    cr++;
                }

                start = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(new Line(text[start..i], "\n"));
                lf++;
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(new Line(text[start..], string.Empty));
        }

        return lines;
    }

    private sealed record Line(string Text, string Terminator);
}
