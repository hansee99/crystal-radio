using System.Text.RegularExpressions;

namespace RadioPlayer.Services;

/// <summary>
/// Cleans the empty-field artifacts stations leave in ICY StreamTitle metadata before they
/// reach the UI, history, SMTC, or harvest filenames (UX audit: no more
/// <c>Song Name ( / 1965)</c> at 44px). Some stations template their titles as
/// "Title (Artist / Year)" or "Title [Label - Year]" and emit the separators verbatim when a
/// field is blank. This trims separator runs left dangling at the edges of (...) / [...]
/// groups, drops groups that end up empty, and collapses the leftover whitespace.
/// Deliberately narrow: it never touches separators outside brackets ("AC/DC" is safe), and
/// it does not attempt to strip legitimate content like remix or mix names.
/// </summary>
public static partial class TrackTitleCleaner
{
    public static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return title;

        var s = title;
        s = LeadingSeparators().Replace(s, "$1");   // "( / 1965)" -> "(1965)"
        s = TrailingSeparators().Replace(s, "$1");  // "(1965 / )" -> "(1965)"
        s = EmptyGroup().Replace(s, "");            // "( )" / "[-]" -> gone
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length == 0 ? title.Trim() : s;
    }

    [GeneratedRegex(@"([(\[])\s*[/\-–·|,]+\s*")]
    private static partial Regex LeadingSeparators();

    [GeneratedRegex(@"\s*[/\-–·|,]+\s*([)\]])")]
    private static partial Regex TrailingSeparators();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyGroup();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
