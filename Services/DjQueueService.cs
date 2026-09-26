using System.IO;
using RadioPlayer.Threading;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// The self-refilling DJ-mode play queue (DJ-MODE-SPEC-HARVEST.md §6.1/§6.7): warm-starts from
/// the existing library via <see cref="SongCurator"/> for the prompt, then keeps the queue full
/// two ways as it plays — appending freshly harvested songs as <see cref="DjHarvestService"/>
/// indexes them, and (the spec's explicit "pull more from the existing library" degrade lever)
/// pulling additional library matches whenever the queue drops below a low watermark, in case
/// harvesting alone can't keep up. Dedup by artist+title across both sources. Feeds
/// <see cref="LocalPlaybackEngine"/> exactly as the PoC (tools/DjQueue) validated in a 37-minute
/// real listening test — same warm-start/append logic, just wired to in-process events instead
/// of polling a manifest file written by a separate process.
///
/// Threading: constructed on the UI thread; captures its own <see cref="Dispatcher"/> so harvest
/// events (which fire on a background thread — see <see cref="DjHarvestService"/>) are always
/// marshalled back before touching <see cref="LocalPlaybackEngine"/>, matching
/// StreamRecorder/LocalPlaybackEngine/SmtcController's existing idiom.
/// </summary>
public sealed class DjQueueService : IDisposable
{
    private readonly ILocalQueuePlayer _local;
    private readonly ISongCurator _curator;
    private readonly IDjHarvestSource _harvest;
    private readonly IDispatcher _dispatcher;
    private readonly int _maxSeed;
    private readonly int _lowWatermark;

    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private string _prompt = "";
    private bool _topUpInFlight;

    // Set when a top-up came back with nothing new — the library is exhausted for this prompt,
    // so further top-ups (each a real curator/LLM call) are pointless until the library actually
    // grows. The only in-session growth is a harvested song landing (OnSegmentIndexed), which
    // clears the flag.
    private bool _libraryExhausted;


    public DjQueueService(ILocalQueuePlayer local, ISongCurator curator, IDjHarvestSource harvest,
        int maxSeed = 20, int lowWatermark = 5)
    {
        _local = local;
        _curator = curator;
        _harvest = harvest;
        _dispatcher = DispatcherContext.Current;
        _maxSeed = maxSeed;
        _lowWatermark = lowWatermark;
    }

    /// <summary>
    /// Points the queue at a new vibe without disturbing what's playing.
    ///
    /// Deliberately NOT <see cref="StartAsync"/>: that calls SetQueue, which stops the current
    /// track dead. The whole point of a mid-session vibe change is that the mix carries on — every
    /// top-up from here uses the new prompt, so the queue crosses over as the old songs play out.
    /// In practice that's quick: harvest barely keeps ahead of playback, so there are usually only
    /// one or two songs queued in front of the listener.
    ///
    /// <para><see cref="_seen"/> is kept on purpose — a song already heard this session shouldn't
    /// come back just because it also fits the new vibe. <see cref="_libraryExhausted"/> is cleared,
    /// because "the library has nothing more" was a judgement about the OLD prompt.</para>
    /// </summary>
    public void ChangeVibe(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;
        _prompt = prompt;
        _libraryExhausted = false;
        // Held-back songs were harvested for the old vibe; releasing them later would put the very
        // thing the change was meant to end back into the mix (#51 meeting #30).
        _deferred.Clear();
        // Deliberately no generation bookkeeping here: at this moment the harvest hasn't sourced
        // the new pool yet, so its counter still reads the OLD vibe. See OnSegmentIndexed.

        // Everything queued behind the current track was chosen for the old vibe. Letting it play
        // out means the change isn't audible for several minutes, which is the whole complaint in
        // #30. The playing track survives — cutting it off mid-song is the one thing not allowed.
        // If that leaves nothing, the queue runs dry and the caller bridges to live radio, which
        // is the correct answer to "no songs match this vibe yet".
        _local.TruncateAfterCurrent();
    }

    /// <summary>Warm-starts the queue for <paramref name="prompt"/> and starts listening for
    /// freshly harvested songs. Call after <see cref="DjHarvestService.StartAsync"/>. Returns
    /// true when the warm-start seed had at least one song (playback starts immediately); false
    /// on a cold start (empty library match) — the caller uses this to decide whether to bridge
    /// the gap with live playback (<c>MainViewModel.BeginDjWarmupLivePlayback</c>) while waiting
    /// for the first harvested song.</summary>
    public async Task<bool> StartAsync(string prompt, CancellationToken ct = default)
    {
        Stop();
        _prompt = prompt;
        _seen.Clear();
        _libraryExhausted = false;

        // requireRelevance: the library holds harvested songs from every previous session, so
        // without it a deep-house prompt happily seeds itself with last week's happy hardcore.
        // An empty seed is a fine answer — StartAsync returns false and the caller bridges live.
        var seed = await _curator
            .CurateAsync(prompt, _maxSeed, requireRelevance: true, ct: ct)
            .ConfigureAwait(true);

        // Kept so the caller can explain the bridge instead of leaving it looking like a normal
        // cold start. A mistyped prompt used to be silently indistinguishable from one.
        SeedOutcome = seed.Outcome;

        var seedTracks = new List<LocalTrack>();
        foreach (var song in seed.Songs)
        {
            if (!File.Exists(song.Path)) continue; // library entry moved/deleted since save
            if (!_seen.Add(DedupKey(song.Artist, song.Title))) continue;
            seedTracks.Add(new LocalTrack(song.Path, song.Title, song.Artist, FormatFromExtension(song.Path), song.Reason));
        }
        // SetQueue starts playback immediately, so check the token one last time: the session can
        // be stopped (or the user can switch modes) while CurateAsync is in flight, and starting
        // to play after that puts the DJ mix under someone else's panel.
        ct.ThrowIfCancellationRequested();

        // Spread the seed so the opening minutes don't stack one artist (#51). The curator ordered
        // it by fit; this only breaks ties between equally-fitting neighbours.
        seedTracks = ArtistSpread.Spread(seedTracks, t => t.Artist);

        _local.SetQueue(seedTracks); // empty seed → cold start; Append below plays the first harvested song

        _harvest.SegmentIndexed += OnSegmentIndexed;
        _local.TrackChanged += OnLocalTrackChanged;
        return seedTracks.Count > 0;
    }

    /// <summary>
    /// Why the warm-start seed came out as it did, for the caller's status message. Set by
    /// <see cref="StartAsync"/>; meaningful immediately after it returns.
    /// </summary>
    public CurationOutcome SeedOutcome { get; private set; }

    public void Stop()
    {
        _harvest.SegmentIndexed -= OnSegmentIndexed;
        _local.TrackChanged -= OnLocalTrackChanged;
        _deferred.Clear();
    }

    public void Dispose() => Stop();

    private void OnSegmentIndexed(object? sender, HarvestedSong harvested)
    {
        // Fires on a background thread (see DjHarvestService) — marshal before touching _local.
        _dispatcher.Post(() =>
        {
            var song = harvested.Song;
            if (!File.Exists(song.Path)) return;

            // The library grew either way, so a top-up can find something new — even if this
            // particular song is stale for the current vibe.
            _libraryExhausted = false;

            // Arrivals used to be appended unconditionally, which is how songs that fit no
            // current vibe reached the mix (#30). The warm-start seed and the low-watermark
            // top-up both went through the curator with requireRelevance; this path — the main
            // source of songs in a session — went through nothing.
            //
            // Relevance here is the STATION, not the song: the harvest pool was sourced and
            // ranked for the vibe, so anything a current-vibe station plays qualifies, and a
            // per-song judgement would be an LLM call in the arrival path for no better answer.
            //
            // Compared against the harvester's counter live, never a copy taken earlier. A copy
            // is wrong in both directions: it is stale across sessions (the counter does not
            // reset), and it is stale across a vibe change, because ChangeVibe runs BEFORE the
            // harvest has sourced the new pool and bumped the counter. That second case shipped:
            // every song of the new vibe arrived stamped 1 against a copy of 0, the queue dropped
            // all of them, and the session bridged live indefinitely with songs on disk.
            var current = _harvest.VibeGeneration;
            if (harvested.VibeGeneration != current)
            {
                AppLog.Debug($"[DjQueue] dropping \"{song.Artist} - {song.Title}\" — harvested for "
                             + $"an earlier vibe (gen {harvested.VibeGeneration}, now {current})");
                return;
            }

            if (!_seen.Add(DedupKey(song.Artist, song.Title))) return;

            var track = new LocalTrack(song.Path, song.Title, song.Artist, FormatFromExtension(song.Path));

            // Two songs by one artist back to back reads as the mix running out of ideas (#51).
            // Hold this one back instead — but only while there is something else to play, which is
            // what keeps a single-artist prompt working without having to recognise one.
            if (ArtistSpread.SameArtist(track.Artist, LastQueuedArtist()) && _deferred.Count < MaxDeferred)
            {
                _deferred.Add(track);
                AppLog.Debug($"[DjQueue] holding \"{track.Artist} - {track.Title}\" back — "
                             + "it would follow the same artist");
                return;
            }

            _local.Append([track]);
            ReleaseDeferred();
        });
    }

    /// <summary>
    /// Songs held back because they would have followed the same artist. Capped: past this the mix
    /// is clearly dominated by one act, and holding more would starve it rather than vary it.
    /// </summary>
    private readonly List<LocalTrack> _deferred = [];
    private const int MaxDeferred = 4;

    /// <summary>The artist at the end of the queue — what a newly appended track would follow.</summary>
    private string? LastQueuedArtist()
    {
        var queue = _local.Queue;
        return queue.Count == 0 ? null : queue[^1].Artist;
    }

    /// <summary>
    /// Appends one held-back song if it no longer repeats. One at a time on purpose: releasing the
    /// whole buffer would just rebuild the run it was holding apart.
    /// </summary>
    private void ReleaseDeferred()
    {
        if (_deferred.Count == 0)
            return;

        var last = LastQueuedArtist();
        var at = _deferred.FindIndex(t => !ArtistSpread.SameArtist(t.Artist, last));
        if (at < 0)
            return;

        var track = _deferred[at];
        _deferred.RemoveAt(at);
        _local.Append([track]);
    }

    /// <summary>
    /// Lets everything held back through, in order. Called when the queue is short enough that
    /// variety is no longer the problem — silence is. Nothing is ever dropped for being a repeat;
    /// it is only ever postponed.
    /// </summary>
    private void FlushDeferred()
    {
        if (_deferred.Count == 0)
            return;

        AppLog.Debug($"[DjQueue] releasing {_deferred.Count} held-back song(s) — the queue is short");
        _local.Append(ArtistSpread.Spread(_deferred, t => t.Artist, LastQueuedArtist()));
        _deferred.Clear();
    }

    // Checked on every track change (auto-advance or crossfade) — cheap, and exactly the moment
    // "how much runway is left" changes. Re-queries the library for more matches when harvesting
    // alone might not keep up (spec §6.7's explicit degrade lever), independent of whether any
    // harvested songs have arrived recently.
    private void OnLocalTrackChanged(object? sender, (LocalTrack Track, int Index) e)
    {
        var remaining = _local.QueueCount - e.Index - 1;
        if (remaining >= _lowWatermark)
            return;

        // Below the watermark, variety stops being the problem and silence starts being one, so
        // anything held back for repeating an artist goes in now (#51). Before the library top-up,
        // because these are already on disk and it is a network round trip.
        FlushDeferred();

        if (_topUpInFlight || _libraryExhausted || string.IsNullOrEmpty(_prompt))
            return;
        _ = TopUpFromLibraryAsync();
    }

    private async Task TopUpFromLibraryAsync()
    {
        _topUpInFlight = true;
        try
        {
            // Snapshot _seen on this (dispatcher) thread — the curator reads the exclusion set
            // on a worker thread, and _seen is mutated here whenever a harvested song lands.
            var exclude = _seen.ToArray();
            var more = await _curator
                .CurateAsync(_prompt, _maxSeed, exclude, requireRelevance: true)
                .ConfigureAwait(true);
            var fresh = new List<LocalTrack>();
            foreach (var song in more.Songs)
            {
                if (!File.Exists(song.Path)) continue;
                if (!_seen.Add(DedupKey(song.Artist, song.Title))) continue;
                fresh.Add(new LocalTrack(song.Path, song.Title, song.Artist, FormatFromExtension(song.Path), song.Reason));
            }
            if (fresh.Count > 0)
                _local.Append(ArtistSpread.Spread(fresh, t => t.Artist, LastQueuedArtist()));
            else
                _libraryExhausted = true; // nothing new for this prompt — pause top-ups until the library grows
        }
        finally
        {
            _topUpInFlight = false;
        }
    }

    /// <summary>
    /// Records a track the listener has already heard live, so its harvested copy never reaches the
    /// queue (#54).
    ///
    /// <para>The station covering a bridge is one of the pool's own, and its harvester keeps
    /// recording throughout — so a song heard during the bridge was captured at the same time and
    /// would be queued minutes later, playing the same track twice. From the listener's side it was
    /// already part of the mix.</para>
    ///
    /// <para>Handled here rather than by taking the bridging station out of the pool, which would
    /// cost a harvester exactly when the mix is starving. The song is still captured, described and
    /// added to the library — it just doesn't play again this session, which is what the existing
    /// <see cref="_seen"/> set already means.</para>
    /// </summary>
    public void NoteHeardLive(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return;   // a station name or an empty announce, not a track

        var key = DedupKey(artist ?? "", title);
        if (_seen.Add(key))
            AppLog.Debug($"[DjQueue] heard live during the bridge: \"{artist} - {title}\" — "
                         + "its harvested copy will be skipped");
    }

    private static string DedupKey(string artist, string title) => $"{artist}|{title}";

    private static StreamFormat FormatFromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".aac" or ".m4a" ? StreamFormat.Aac : StreamFormat.Mp3;
}
