namespace RadioPlayer.Services;

/// <summary>
/// Structured Radio Browser search parameters. This is the *only* thing the LLM
/// produces — it never supplies station names or URLs (those come from Radio Browser).
/// Mirrors the Pattern A schema in CLAUDE.md.
/// </summary>
public sealed class StationSearchQuery
{
    /// <summary>Genre/mood keywords mapped from the prompt (e.g. "jazz", "ambient").</summary>
    public string[] Tags { get; set; } = [];

    /// <summary>Only set if the user named a specific station.</summary>
    public string? Name { get; set; }

    public string? Country { get; set; }

    public string? Language { get; set; }

    /// <summary>Minimum bitrate in kbps; 0 means no minimum.</summary>
    public int BitrateMin { get; set; }

    /// <summary>Radio Browser ranking hint: votes | clickcount | name.</summary>
    public string Order { get; set; } = "votes";
}
