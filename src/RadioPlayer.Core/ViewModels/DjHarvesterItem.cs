namespace RadioPlayer.ViewModels;

/// <summary>One row in the DJ panel's "Sources" tab — a snapshot of one currently-connected
/// harvester. Dead harvesters are removed from <see cref="Services.DjHarvestService"/>'s active
/// pool (and replaced from reserve) the instant they die, so only genuinely live connections ever
/// appear here — there's no "dead" state to show. Rebuilt wholesale each time
/// <see cref="Services.DjHarvestService.StatusChanged"/> fires, not mutated in place.</summary>
public sealed record DjHarvesterItem(string Label, int TitlesSeen, int Kept, int Rejected)
{
    /// <summary>
    /// What this station has actually contributed. Deliberately NOT a title count: TitlesSeen
    /// tracks ICY metadata changes, and reporting those as "songs" overstated every station by
    /// roughly double — two boundaries are needed to complete one segment, and QC rejects some
    /// of what's left. A row claiming four songs while the session had produced one was the
    /// clearest possible way to make the panel untrustworthy.
    /// </summary>
    public string StatusText =>
        Kept > 0
            ? (Rejected > 0 ? $"{Songs(Kept)} collected · {Rejected} skipped" : $"{Songs(Kept)} collected")
            : Rejected > 0
                ? $"Nothing kept yet · {Rejected} skipped"
                : "Listening…";

    private static string Songs(int n) => n == 1 ? "1 song" : $"{n} songs";
}
