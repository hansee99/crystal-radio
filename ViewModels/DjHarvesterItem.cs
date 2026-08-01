namespace RadioPlayer.ViewModels;

/// <summary>One row in the DJ panel's "Harvesting" tab — a snapshot of one currently-connected
/// harvester. Dead harvesters are removed from <see cref="Services.DjHarvestService"/>'s active
/// pool (and replaced from reserve) the instant they die, so only genuinely live connections ever
/// appear here — there's no "dead" state to show. Rebuilt wholesale each time
/// <see cref="Services.DjHarvestService.StatusChanged"/> fires, not mutated in place.</summary>
public sealed record DjHarvesterItem(string Label, int TitlesSeen)
{
    /// <summary>Row subtitle. "Listening…" instead of a bare zero (UX audit: a zero is the
    /// least reassuring number to show someone waiting), then plain "N songs so far" —
    /// "titles seen" was internal vocabulary.</summary>
    public string StatusText => TitlesSeen == 0
        ? "Listening…"
        : $"{TitlesSeen} song{(TitlesSeen == 1 ? "" : "s")} so far";
}
