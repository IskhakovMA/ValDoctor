using System.Text.RegularExpressions;

namespace RiotFix.Core.Diagnostics;

/// <summary>
/// The main screen speaks plain language. This finds technical details that belong only in «Подробности»:
/// IP addresses, host names, protocol names and file paths. Used by unit tests and by the GUI smoke test.
/// </summary>
public static partial class PlainTextPolicy
{
    public static IReadOnlyList<string> FindTechnicalTerms(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return Technical().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(
        @"\b\d{1,3}(?:\.\d{1,3}){3}\b" +                                   // IPv4
        @"|\b[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{0,4}){2,7}\b" +               // IPv6
        @"|\b[\w-]+\.(?:riotgames|riotcdn|com|net|org)\b" +                 // host names
        @"|\b(?:DNS|DoH|TLS|SSL|TCP|UDP|HTTPS?|SNI|IPv[46]|IP)\b" +        // protocols
        @"|\b[A-Za-z]:\\|drivers\\etc|%\w+%",                               // paths
        RegexOptions.CultureInvariant)]
    private static partial Regex Technical();
}
