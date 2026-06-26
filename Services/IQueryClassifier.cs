namespace RadioPlayer.Services;

/// <summary>
/// Decides which search engine a prompt should go to: a literal directory lookup (a specific
/// station name or concrete attributes — Pattern A) vs a fuzzy/semantic "vibe" query (local
/// semantic search + web discovery). Knows nothing about playback or the search services.
/// </summary>
public interface IQueryClassifier
{
    bool IsConfigured { get; }

    /// <summary>
    /// Returns true if the prompt is fuzzy/semantic, false if it's a literal lookup, or null
    /// when the classifier couldn't run (no key / error) — the caller should then fall back to
    /// its own cheap heuristic.
    /// </summary>
    Task<bool?> IsFuzzyAsync(string prompt, CancellationToken ct = default);
}
