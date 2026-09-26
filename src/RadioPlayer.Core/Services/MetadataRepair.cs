using System.Text;
using System.Text.RegularExpressions;

namespace RadioPlayer.Services;

/// <summary>
/// Repairing track metadata that arrived already broken (#31).
///
/// <para>Some stations transcode their ICY metadata badly and put the Unicode replacement
/// character on the wire <b>themselves</b>. Captured from 011.fm on 2026-08-06, "Mötley Crüe"
/// arrives as the bytes <c>4D EF BF BD 74 6C 65 79 20 43 72 EF BF BD 65</c> — that is a literal
/// U+FFFD, valid UTF-8, which strict decoding accepts. No amount of encoding sniffing recovers it,
/// which is why <see cref="IcyTags"/> — correct, and tested against the Latin-1 case — could not
/// fix this. The original character is gone before the app ever sees the stream.</para>
///
/// <para>So the only repair is to look the name up somewhere else and check that the answer fits.
/// This class is the checking half, kept pure: given the broken string, does a candidate agree with
/// every character that survived? That test is what makes an outside suggestion safe to accept —
/// LRCLIB's title-only search returns 20 rows for "Breaking the Silence", of which exactly one
/// matches <c>Queensr?che</c>, so the check does the choosing and no ranking has to be trusted.</para>
/// </summary>
public static class MetadataRepair
{
    /// <summary>U+FFFD, the character the app receives where the real one was lost.</summary>
    public const char Replacement = '�';

    /// <summary>True when a string carries damage worth trying to repair.</summary>
    public static bool NeedsRepair(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(Replacement);

    /// <summary>
    /// Whether <paramref name="candidate"/> could be what <paramref name="broken"/> was before the
    /// station mangled it: same text either side of the damage, something plausible in its place,
    /// and no damage of its own.
    /// <para>
    /// A run of N replacement characters may stand for anywhere from 1 to N real characters — an
    /// encoder that emits one per lost <i>byte</i> turns a single two-byte character into two — so a
    /// run matches 1..N of anything. Comparison is case-insensitive: the surviving characters are
    /// evidence about spelling, and insisting on case would reject "MÖTLEY CRÜE" for no good reason.
    /// </para>
    /// </summary>
    public static bool IsPlausibleRepair(string? broken, string? candidate)
    {
        if (string.IsNullOrEmpty(broken) || string.IsNullOrWhiteSpace(candidate))
            return false;
        if (!NeedsRepair(broken))
            return false;               // nothing was broken, so nothing may be "repaired"
        if (candidate.Contains(Replacement))
            return false;               // trading one damaged string for another

        return Regex.IsMatch(candidate, BuildPattern(broken),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The first candidate that could be the original, or null if none fits. Order is the caller's
    /// preference; the check is what decides, so a wrong guess earlier in the list costs nothing.
    /// </summary>
    public static string? ChooseRepair(string? broken, IEnumerable<string?> candidates)
    {
        if (candidates is null || !NeedsRepair(broken))
            return null;
        foreach (var candidate in candidates)
            if (IsPlausibleRepair(broken, candidate))
                return candidate;
        return null;
    }

    /// <summary>
    /// An anchored pattern where surviving text is literal and each run of replacement characters
    /// becomes "1 to N of anything".
    /// </summary>
    private static string BuildPattern(string broken)
    {
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < broken.Length)
        {
            if (broken[i] == Replacement)
            {
                var run = 0;
                while (i < broken.Length && broken[i] == Replacement) { run++; i++; }
                sb.Append(".{1,").Append(run).Append('}');
            }
            else
            {
                var start = i;
                while (i < broken.Length && broken[i] != Replacement) i++;
                sb.Append(Regex.Escape(broken[start..i]));
            }
        }
        return sb.Append('$').ToString();
    }
}
