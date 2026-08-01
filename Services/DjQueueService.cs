using System.IO;
using System.Windows.Threading;
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
    private readonly Dispatcher _dispatcher;
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
        _dispatcher = Dispatcher.CurrentDispatcher;
        _maxSeed = maxSeed;
        _lowWatermark = lowWatermark;
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

        var seed = await _curator.CurateAsync(prompt, _maxSeed, null, ct).ConfigureAwait(true);
        var seedTracks = new List<LocalTrack>();
        foreach (var song in seed)
        {
            if (!File.Exists(song.Path)) continue; // library entry moved/deleted since save
            if (!_seen.Add(DedupKey(song.Artist, song.Title))) continue;
            seedTracks.Add(new LocalTrack(song.Path, song.Title, song.Artist, FormatFromExtension(song.Path), song.Reason));
        }
        _local.SetQueue(seedTracks); // empty seed → cold start; Append below plays the first harvested song

        _harvest.SegmentIndexed += OnSegmentIndexed;
        _local.TrackChanged += OnLocalTrackChanged;
        return seedTracks.Count > 0;
    }

    public void Stop()
    {
        _harvest.SegmentIndexed -= OnSegmentIndexed;
        _local.TrackChanged -= OnLocalTrackChanged;
    }

    public void Dispose() => Stop();

    private void OnSegmentIndexed(object? sender, SavedSong song)
    {
        // Fires on a background thread (see DjHarvestService) — marshal before touching _local.
        _dispatcher.BeginInvoke(() =>
        {
            if (!File.Exists(song.Path)) return;
            _libraryExhausted = false; // the library just grew — top-ups can find new songs again
            if (!_seen.Add(DedupKey(song.Artist, song.Title))) return;
            _local.Append([new LocalTrack(song.Path, song.Title, song.Artist, FormatFromExtension(song.Path))]);
        });
    }

    // Checked on every track change (auto-advance or crossfade) — cheap, and exactly the moment
    // "how much runway is left" changes. Re-queries the library for more matches when harvesting
    // alone might not keep up (spec §6.7's explicit degrade lever), independent of whether any
    // harvested songs have arrived recently.
    private void OnLocalTrackChanged(object? sender, (LocalTrack Track, int Index) e)
    {
        var remaining = _local.QueueCount - e.Index - 1;
        if (remaining >= _lowWatermark || _topUpInFlight || _libraryExhausted || string.IsNullOrEmpty(_prompt))
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
            var more = await _curator.CurateAsync(_prompt, _maxSeed, exclude).ConfigureAwait(true);
            var fresh = new List<LocalTrack>();
            foreach (var song in more)
            {
                if (!File.Exists(song.Path)) continue;
                if (!_seen.Add(DedupKey(song.Artist, song.Title))) continue;
                fresh.Add(new LocalTrack(song.Path, song.Title, song.Artist, FormatFromExtension(song.Path), song.Reason));
            }
            if (fresh.Count > 0)
                _local.Append(fresh);
            else
                _libraryExhausted = true; // nothing new for this prompt — pause top-ups until the library grows
        }
        finally
        {
            _topUpInFlight = false;
        }
    }

    private static string DedupKey(string artist, string title) => $"{artist}|{title}";

    private static StreamFormat FormatFromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".aac" or ".m4a" ? StreamFormat.Aac : StreamFormat.Mp3;
}
