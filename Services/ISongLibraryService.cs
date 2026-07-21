namespace RadioPlayer.Services;

/// <summary>
/// Owns the local song library: persists saved songs and, in the background, enriches each with
/// an AI-derived description + facets and a local embedding — the index the future offline/
/// AI-curated player queries. Best-effort and non-blocking; enrichment failures degrade to a
/// row with core metadata only. Knows nothing about playback.
/// </summary>
public interface ISongLibraryService
{
    /// <summary>
    /// Persist a freshly-saved song, then enrich + embed it in the background (fire-and-forget).
    /// Returns immediately; never throws.
    /// </summary>
    void AddAndEnrich(SavedSong song);

    /// <summary>All library rows, newest first.</summary>
    IReadOnlyList<SavedSong> GetAll();

    /// <summary>Remove a song from the index (e.g. its file no longer exists).</summary>
    void Remove(string path);

    /// <summary>
    /// One-time/background pass: reconcile the index against the files on disk (drop rows whose
    /// file is gone), then enrich rows missing a description and embed rows missing a current-
    /// model vector. Fire-and-forget; safe to call at startup.
    /// </summary>
    void BackfillInBackground();
}
