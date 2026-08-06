namespace RadioPlayer.Services;

/// <summary>
/// Keeps the same artist from landing twice in a row in the mix (#51).
///
/// <para>Two tracks by one artist back to back reads as the mix having run out of ideas, and it
/// happens easily: a station dedicated to one artist contributes several, or two stations in the
/// pool both play the same act. Spreading them costs nothing — every song still plays, just not
/// shoulder to shoulder.</para>
///
/// <para><b>Never at the cost of the mix.</b> If the only songs available are all by one artist —
/// which is exactly what the listener asked for when the prompt names one — they play in order
/// rather than being held back. That falls out of the rule rather than needing to detect intent:
/// a repeat is deferred only while there is something else to play.</para>
/// </summary>
public static class ArtistSpread
{
    /// <summary>
    /// Reorders so that no two adjacent entries share an artist, wherever that is possible.
    ///
    /// <para>Greedy and stable: it walks the list taking the first entry whose artist differs from
    /// the one just taken, and falls back to the next remaining entry when every candidate would
    /// repeat. Stability matters — the input is already in the order the curator or ranker chose,
    /// and this may only break ties, never re-rank.</para>
    /// </summary>
    /// <param name="previousArtist">The artist already playing or last queued, so a batch appended
    /// behind existing tracks doesn't repeat across the join.</param>
    public static List<T> Spread<T>(IReadOnlyList<T> items, Func<T, string?> artistOf,
        string? previousArtist = null)
    {
        ArgumentNullException.ThrowIfNull(artistOf);
        if (items is null || items.Count <= 1)
            return items is null ? [] : [.. items];

        var remaining = new List<T>(items);
        var ordered = new List<T>(items.Count);
        var last = previousArtist;

        while (remaining.Count > 0)
        {
            var pick = 0;
            for (var i = 0; i < remaining.Count; i++)
            {
                if (!SameArtist(artistOf(remaining[i]), last))
                {
                    pick = i;
                    break;
                }
            }
            // No index differed — everything left is the same artist, so take them in order.
            ordered.Add(remaining[pick]);
            last = artistOf(remaining[pick]);
            remaining.RemoveAt(pick);
        }

        return ordered;
    }

    /// <summary>
    /// Whether two artist names are the same act. An unknown artist never counts as a repeat: a
    /// run of blanks is a metadata problem, and holding songs back over it would starve the mix.
    /// </summary>
    public static bool SameArtist(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
