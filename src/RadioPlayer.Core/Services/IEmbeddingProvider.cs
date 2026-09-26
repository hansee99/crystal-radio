namespace RadioPlayer.Services;

/// <summary>
/// Turns text into a vector. Provider-agnostic so a hosted embeddings API (e.g. Voyage)
/// could drop in behind the same interface. Knows nothing else about the app.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Model identifier, stored with every vector — vectors from different models are not comparable.</summary>
    string ModelId { get; }

    /// <summary>Vector dimension (e.g. 384 for all-MiniLM-L6-v2).</summary>
    int Dimension { get; }

    /// <summary>False when the model/assets aren't available; callers must degrade gracefully.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Embed text into an L2-normalized vector (so cosine == dot product). Synchronous CPU
    /// work — callers run it off the UI thread. Returns null when <see cref="IsAvailable"/> is false.
    /// </summary>
    float[]? Embed(string text);
}
