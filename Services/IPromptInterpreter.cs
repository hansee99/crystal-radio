namespace RadioPlayer.Services;

/// <summary>
/// Translates a natural-language prompt into structured Radio Browser search parameters
/// using an LLM. Knows nothing about playback or how the parameters are used.
/// </summary>
public interface IPromptInterpreter
{
    /// <summary>True when the interpreter has what it needs to run (e.g. an API key).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns structured search parameters, or null if the model produced malformed/empty
    /// output. Throws on transport/HTTP errors so the caller can surface them.
    /// </summary>
    Task<StationSearchQuery?> InterpretAsync(string prompt, CancellationToken ct = default);
}
