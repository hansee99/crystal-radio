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
    OfflineUnranked,

    /// <summary>
    /// The directory was unreachable and there was no local index to fall back ON — the embedding
    /// provider is missing or the catalog has no vectors, so the offline path never ran at all.
    ///
    /// <para>Distinct from <see cref="OfflineNoMatch"/> on purpose, and the distinction is the
    /// whole point: that one means the catalog was searched and came up short, which "try a
    /// broader prompt" is fair advice for. This one means nothing was searched, and no wording of
    /// the prompt can change the answer (#60).</para>
    /// </summary>
    OfflineNoCatalog
}
