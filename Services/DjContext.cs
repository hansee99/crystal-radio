using System.Globalization;

namespace RadioPlayer.Services;

/// <summary>
/// One ambient fact about the listener's "now" that the DJ may mention (#55).
///
/// <para>An interface rather than a couple of helper methods because the issue anticipates more of
/// them — weather and location were named — and they all have the same shape: something true about
/// this moment, phrased for a person, or nothing at all. A source that has nothing to say returns
/// null and disappears from the line rather than contributing "unknown".</para>
/// </summary>
public interface IDjContextSource
{
    /// <summary>A short phrase for the prompt, or null when there is nothing worth saying.</summary>
    string? Describe(DateTimeOffset now);
}

/// <summary>
/// Where in the day the listener is: "early morning (06:30)", "late night (01:00)".
///
/// <para>The clock time as well as the label, because the label alone is ambiguous and the model
/// reads it too loosely — asked for "music for working late" at 06:30 it wrote about "the small
/// hours" twice. The prompts forbid reciting the time back; this is for judgement, not quotation.</para>
/// </summary>
public sealed class TimeOfDayContext : IDjContextSource
{
    public string? Describe(DateTimeOffset now) =>
        $"{Label(now.Hour)} ({now:HH:mm})";

    private static string Label(int hour) => hour switch
    {
        >= 5 and < 8 => "early morning",
        >= 8 and < 12 => "morning",
        >= 12 and < 14 => "the middle of the day",
        >= 14 and < 17 => "afternoon",
        >= 17 and < 20 => "early evening",
        >= 20 and < 23 => "evening",
        _ => "late night",
    };
}

/// <summary>
/// The day, the season, and any holiday worth a nod.
///
/// <para><b>Northern hemisphere.</b> Season is derived from the month, which is wrong by six months
/// south of the equator — the app has no location to do better with, and the issue names location
/// as a future source. When one exists, this is the class that should consult it; until then the
/// assumption is stated rather than hidden.</para>
/// </summary>
public sealed class CalendarContext : IDjContextSource
{
    public string? Describe(DateTimeOffset now)
    {
        var parts = new List<string>
        {
            $"{now.DayOfWeek}, {now.ToString("d MMMM", CultureInfo.InvariantCulture)}",
            Season(now.Month),
        };

        if (Holiday(now) is { } holiday)
            parts.Add(holiday);

        return string.Join(", ", parts);
    }

    private static string Season(int month) => month switch
    {
        12 or 1 or 2 => "winter",
        >= 3 and <= 5 => "spring",
        >= 6 and <= 8 => "summer",
        _ => "autumn",
    };

    /// <summary>
    /// Only fixed-date occasions, and only ones a DJ would plausibly mention unprompted. Easter and
    /// the like move around and would need a calendar library for a line the model can already
    /// write from the date if it matters.
    /// </summary>
    private static string? Holiday(DateTimeOffset now) => (now.Month, now.Day) switch
    {
        (12, >= 24 and <= 26) => "Christmas",
        (12, >= 27 and <= 30) => "the quiet week between Christmas and New Year",
        (12, 31) or (1, 1) => "New Year",
        (10, 31) => "Halloween",
        _ => null,
    };
}

/// <summary>
/// Assembles what the DJ knows about right now into one line for a prompt.
///
/// <para>Composed rather than passed as fields so adding a source never changes a prompt's shape:
/// every prompt that wants context takes the same single sentence, and a new source simply makes it
/// longer. Sources that return null contribute nothing.</para>
/// </summary>
public static class DjContext
{
    /// <summary>The sources in use. Order is reading order in the composed line.</summary>
    public static IReadOnlyList<IDjContextSource> Default { get; } =
        [new TimeOfDayContext(), new CalendarContext()];

    public static string? Compose(IEnumerable<IDjContextSource>? sources, DateTimeOffset now)
    {
        if (sources is null)
            return null;

        var parts = new List<string>();
        foreach (var source in sources)
        {
            string? part;
            // A context source is a nicety; one that throws must not take a DJ line with it.
            try { part = source.Describe(now); }
            catch (Exception ex)
            {
                AppLog.Debug($"[DjContext] {source.GetType().Name} failed: {ex.Message}");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(part))
                parts.Add(part.Trim());
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
