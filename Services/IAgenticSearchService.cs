using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>A station the agent chose, with its one-line rationale.</summary>
public sealed record RankedStation(Station Station, string Reason);

/// <summary>
/// Pattern B: web-search discovery. Drives an Anthropic two-tool agentic loop
/// (server-side <c>web_search</c> + client-side <c>search_radio_browser</c>) and returns a
/// validated, ranked list. Every returned station was fetched from Radio Browser by this
/// service — never a URL the model or a web page produced. No playback knowledge.
/// </summary>
public interface IAgenticSearchService
{
    /// <summary>True when the interpreter has what it needs to run (e.g. an API key).</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<RankedStation>> SearchAsync(string prompt, CancellationToken ct = default);
}
