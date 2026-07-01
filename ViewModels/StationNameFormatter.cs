using System.Text.RegularExpressions;

namespace RadioPlayer.ViewModels;

/// <summary>
/// Cosmetic cleanup of Radio Browser station names for display: strips the technical noise many
/// stations append to their name — bitrate, codec, and sample-rate tokens — e.g.
/// "Jazz FM (128K MP3)" → "Jazz FM". Display-only: callers keep the raw <c>Station.Name</c> for
/// identity and near-duplicate detection (which deliberately treats bitrate variants as distinct).
/// </summary>
public static partial class StationNameFormatter
{
    // Bitrate ("128k", "320 kbps", "96kbit"), codec, or sample-rate ("44.1kHz") tokens.
    [GeneratedRegex(@"\b\d{2,3}\s?k(bps|bit)?\b|\b(?:aacp|aac\+?|mp3|ogg|opus|flac|wma|hls)\b|\b\d{1,3}(?:\.\d)?\s?khz\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TechTokenRegex();

    // A bracket/paren group left holding only separators after the tokens were removed: "()", "[ - ]".
    [GeneratedRegex(@"[\(\[\{]\s*[-–,|/]*\s*[\)\]\}]")]
    private static partial Regex EmptyBracketsRegex();

    // Leftover leading/trailing separators and runs of whitespace.
    [GeneratedRegex(@"^\s*[-–|/]+\s*|\s*[-–|/]+\s*$|\s{2,}")]
    private static partial Regex TidyRegex();

    /// <summary>Returns a display-friendly name, or the trimmed original if cleaning empties it.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name ?? string.Empty;

        var s = TechTokenRegex().Replace(name, "");
        s = EmptyBracketsRegex().Replace(s, "");
        s = TidyRegex().Replace(s, " ").Trim();

        return string.IsNullOrWhiteSpace(s) ? name.Trim() : s;
    }
}
