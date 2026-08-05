namespace RadioPlayer.Services;

/// <summary>
/// Why a station-sourcing attempt returned what it did. An empty station list has several very
/// different causes and the listener deserves to be told which one it was (#26) — "the directory
/// is down and your local catalog has nothing like that" is actionable in a way that
/// "nothing matched that vibe" isn't.
/// </summary>
public enum DjSourcingOutcome
{
    /// <summary>Stations were found. Says nothing about which source they came from.</summary>
    Ok,

    /// <summary>The directory answered and nothing in it fit the prompt.</summary>
    NothingRelevant,

    /// <summary>
    /// The directory was unreachable, so the local catalog was searched instead, and nothing in it
    /// scored above the relevance floor. A near-empty catalog (a fresh install) lands here too.
    /// </summary>
    OfflineNoMatch,

    /// <summary>
    /// The directory was unreachable AND the strict ranker couldn't run, so there was nothing left
    /// to judge relevance with. Deliberately refused rather than starting a session on unranked
    /// cosine hits — an empty answer is the correct one here.
    /// </summary>
    OfflineUnranked
}
