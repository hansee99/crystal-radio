namespace RadioPlayer.Services;

/// <summary>
/// Why a curation call produced what it did. Exists because an empty playlist has several very
/// different causes and the app used to swallow all of them: a DJ session whose prompt matched
/// nothing looked exactly like a normal cold start, so a one-letter typo ("happy musing for a
/// coding session") silently degraded the feature with no way to tell.
/// </summary>
public enum CurationOutcome
{
    /// <summary>Songs were returned.</summary>
    Ok,

    /// <summary>No embedded rows to match against — a genuinely empty library, or one whose
    /// descriptions haven't been embedded yet.</summary>
    LibraryEmpty,

    /// <summary>
    /// The best song in the WHOLE library scored far below the relevance floor. That is evidence
    /// about the prompt rather than the library: a real music request lands around 0.45–0.63 on this
    /// index, and "happy musing for a coding session" peaked at 0.197 while "happy music for a
    /// coding session" reached 0.501 over the same 83 songs.
    ///
    /// <para>Not proof, though — a tiny library legitimately scores low for a perfectly good
    /// prompt, so the message this drives says "check the wording" rather than asserting a typo.</para>
    /// </summary>
    PromptOutOfDomain,

    /// <summary>Songs came reasonably close but none cleared the relevance floor. The library
    /// simply doesn't hold this kind of music yet.</summary>
    NothingRelevant,

    /// <summary>The pool cleared the floor, but the LLM curator picked nothing out of it. Worth
    /// distinguishing from <see cref="NothingRelevant"/>: the semantic index and the model
    /// disagreed, which is a different problem from an unmatched prompt.</summary>
    RankerDeclined
}

/// <summary>
/// A curation result and the reason behind it. <see cref="BestScore"/> is the top cosine score seen
/// over the whole library — the number that separates "your library lacks this" from "your prompt
/// doesn't read as music", and worth logging either way.
/// </summary>
public sealed record CurationResult(
    IReadOnlyList<CuratedSong> Songs,
    CurationOutcome Outcome,
    double BestScore)
{
    public static CurationResult Empty(CurationOutcome outcome, double bestScore = 0) =>
        new([], outcome, bestScore);

    public bool HasSongs => Songs.Count > 0;
}
