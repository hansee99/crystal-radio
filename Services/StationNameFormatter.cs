using System.Text.RegularExpressions;

namespace RadioPlayer.Services;

/// <summary>
/// Strips the technical noise many stations append to their name — bitrate, codec and
/// sample-rate tokens — e.g. "Jazz FM (128K MP3)" → "Jazz FM".
///
/// Two uses, and the difference matters:
/// <list type="bullet">
/// <item>Display (search results, the station list): purely cosmetic.</item>
/// <item>DJ harvest identity (<see cref="DjHarvestService"/>): the harvest pool treats codec
/// variants of one station as the SAME station, because they carry identical audio. A session
/// that put "SomaFM Beat Blender (128k MP3)" and "(128k AAC)" on two of its four harvesters
/// recorded every song twice and halved its effective pool.</item>
/// </list>
/// The visible search deliberately does NOT do this — there, bitrate variants are distinct,
/// playable choices the user is picking between.
/// </summary>
public static partial class StationNameFormatter
{
    // Bitrate ("128k", "320 kbps", "96kbit"), codec, or sample-rate ("44.1kHz") tokens.
    // AAC+/AACP are matched with an explicit optional "+" and a lookahead rather than a trailing
    // \b: a word boundary can't sit between "+" and a space, so "AAC+" previously left its plus
    // behind ("Liquid DnB - 96kbit AAC+" → "Liquid DnB - +").
    [GeneratedRegex(@"\b\d{2,4}\s?k(bps|bit)?\b|\b(?:aacp|aac)\+?(?![a-z0-9])|\b(?:mp3|ogg|opus|flac|wma|hls)\b|\b\d{1,3}(?:\.\d)?\s?khz\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TechTokenRegex();

    // A bracket/paren group left holding only separators after the tokens were removed: "()", "[ - ]".
    [GeneratedRegex(@"[\(\[\{]\s*[-–,|/]*\s*[\)\]\}]")]
    private static partial Regex EmptyBracketsRegex();

    // Leftover leading/trailing separators (including a stray "+" from a stripped codec) and
    // runs of whitespace.
    [GeneratedRegex(@"^\s*[-–|/+]+\s*|\s*[-–|/+]+\s*$|\s{2,}")]
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
