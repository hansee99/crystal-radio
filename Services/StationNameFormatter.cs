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

    // Quality decorations that distinguish two feeds of the SAME broadcast: "FM4 | ORF" and
    // "FM4 | ORF | HQ" are one station at two bitrates. Only stripped for identity, never for
    // display — in the visible search those two are distinct, playable choices and collapsing
    // their labels would leave the user with two identical rows.
    [GeneratedRegex(@"\b(?:hq|lq|hi-?fi|high|low)\s*(?:quality|bitrate)?\b|\bquality\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTokenRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphanumericRegex();

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

    /// <summary>
    /// A comparison key for "is this the same station?", as opposed to <see cref="Clean"/>'s
    /// "what should this be called?". On top of the codec/bitrate stripping it drops quality
    /// decorations and every non-alphanumeric character, so all of these collapse to one key:
    /// <c>FM4 | ORF</c>, <c>FM4 | ORF | HQ</c>, <c>fm4-orf (192k MP3)</c>.
    ///
    /// <para>Needed because a directory lists the same broadcast several times and the obvious
    /// keys don't catch it. Observed 2026-08-03: <c>FM4 | ORF</c> and <c>FM4 | ORF | HQ</c> took two
    /// of four harvester slots — different names, and URLs differing only in <c>q1a</c> vs
    /// <c>q2a</c>, so neither name nor URL deduplication saw them. Same for
    /// <c>ORF Hitradio Ö3</c> / <c>… | HQ</c>. Four slots yielded two stations' worth of songs.</para>
    ///
    /// Returns the lower-cased alphanumeric run, or empty when nothing survives.
    /// </summary>
    public static string IdentityKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var s = QualityTokenRegex().Replace(Clean(name), " ");
        return NonAlphanumericRegex().Replace(s, "").ToLowerInvariant();
    }
}
