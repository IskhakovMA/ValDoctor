using System.Globalization;

namespace RiotFix.Core.Updates;

/// <summary>Semantic version (SemVer 2.0) with prerelease ordering; build metadata is ignored.</summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public IReadOnlyList<string> Prerelease { get; }

    public bool IsPrerelease => Prerelease.Count > 0;

    public static SemVersion Create(int major, int minor, int patch) => new(major, minor, patch, []);

    /// <summary>Accepts "1.2.3", "v1.2.3", "1.2.3-beta.1", "1.2.3+build". Anything else is rejected.</summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string[] pre = [];
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].Split('.');
            s = s[..dash];
            if (pre.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return false;
            }
        }

        var core = s.Split('.');
        if (core.Length != 3 || !TryNumber(core[0], out var major) || !TryNumber(core[1], out var minor) || !TryNumber(core[2], out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }

        if (c != 0)
        {
            return c;
        }

        // A release is newer than any of its prereleases.
        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var a = Prerelease[i];
            var b = other.Prerelease[i];
            var aNum = TryNumber(a, out var an);
            var bNum = TryNumber(b, out var bn);
            c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (IsPrerelease ? "-" + string.Join('.', Prerelease) : string.Empty);

    public static bool operator >(SemVersion a, SemVersion b) => a is not null && a.CompareTo(b) > 0;

    public static bool operator <(SemVersion a, SemVersion b) => b is not null && b.CompareTo(a) > 0;

    public static bool operator >=(SemVersion a, SemVersion b) => !(a < b);

    public static bool operator <=(SemVersion a, SemVersion b) => !(a > b);

    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);

    private static bool TryNumber(string s, out int value)
    {
        value = 0;
        return s.Length is > 0 and <= 9 && s.All(char.IsAsciiDigit) && (s.Length == 1 || s[0] != '0') &&
               int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
