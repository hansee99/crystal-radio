using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using ManagedBass;
using RadioPlayer.Models;
using RadioPlayer.Services;

// Dj Mode (harvest) — PoC 2: warm-start + self-refilling queue.
//
// Seeds an opening queue from the existing local library via SongCurator, plays it through the
// real LocalPlaybackEngine, then watches a DjHarvest (PoC 1) output folder's manifest.csv for
// newly-kept segments and appends them to the queue — so it never runs dry. Reuses the app's
// real playback path unchanged; this tool only owns the queue's fill logic.
//
// Run DjHarvest in one terminal (writing into --harvest-dir), then this in another:
//   dotnet run --project tools/DjHarvest -- --out C:\harvest
//   dotnet run --project tools/DjQueue -- --prompt "mellow electronic for coding" --harvest-dir C:\harvest
//
// Ctrl+C to stop, or pass --seconds to auto-stop.

var prompt = ArgStr("--prompt", null);
if (string.IsNullOrWhiteSpace(prompt))
{
    Console.WriteLine("usage: dotnet run --project tools/DjQueue -- --prompt \"<text>\" [--harvest-dir <dir>] [--max-seed N] [--seconds N]");
    return 1;
}

var harvestDir = ArgStr("--harvest-dir", null) ?? Path.Combine(Path.GetTempPath(), "DjHarvest");
var maxSeed = ArgInt("--max-seed", 20);
var seconds = ArgInt("--seconds", 0); // 0 = run until Ctrl+C

var dispatcher = Dispatcher.CurrentDispatcher;

var engine = new LocalPlaybackEngine();
engine.TrackChanged += (_, t) => Console.WriteLine($"\n▶ now playing: {t.Track.Artist} — {t.Track.Title}  (queue {engine.QueueCount})");
engine.ErrorOccurred += (_, msg) => Console.WriteLine($"  [engine] {msg}");

var libraryStore = new LibraryStore();
var mlDir = Path.Combine(AppContext.BaseDirectory, "MlAssets");
var embeddings = new MiniLmEmbeddingProvider(
    Path.Combine(mlDir, "all-MiniLM-L6-v2.onnx"), Path.Combine(mlDir, "vocab.txt"));
var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (apiKey is null)
    Console.WriteLine("Note: ANTHROPIC_API_KEY not set — curation falls back to semantic order without an LLM arc.");
if (!embeddings.IsAvailable)
    Console.WriteLine("Note: local embedding model not found under MlAssets/ — curation falls back to recency order.");

var curator = new SongCurator(new HttpClient(), libraryStore, embeddings, apiKey);

// Dedup by artist+title (case-insensitive) across both the warm-start seed and every
// freshly-harvested song appended later, per the PoC's exit criterion (no repeats back-to-back).
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
string DedupKey(string artist, string title) => $"{artist}|{title}";

Console.WriteLine($"Dj queue PoC — prompt: \"{prompt}\"");
Console.WriteLine($"Harvest folder (watched): {harvestDir}");
Console.WriteLine(new string('-', 60));

// Blocking, not awaited: this is the only async work in the whole script, done before
// Dispatcher.Run() starts pumping. A plain console app installs no SynchronizationContext, so
// an `await` here would resume on a thread-pool thread — leaving Dispatcher.Run() later pump
// a *different* dispatcher than the one the timers below are bound to (they'd never fire).
// Blocking keeps everything on this one thread, which is what the rest of the script assumes.
var seed = curator.CurateAsync(prompt!, maxSeed).GetAwaiter().GetResult();
var seedTracks = new List<LocalTrack>();
foreach (var song in seed)
{
    if (!File.Exists(song.Path)) continue; // library entry moved/deleted since save
    if (!seen.Add(DedupKey(song.Artist, song.Title))) continue;
    seedTracks.Add(new LocalTrack(song.Path, song.Title, song.Artist, FormatFromExtension(song.Path)));
}

if (seedTracks.Count == 0)
    Console.WriteLine("Warming up — no seed songs (empty/cold library); playback starts the moment the first harvested song lands.");
else
    Console.WriteLine($"Seeded {seedTracks.Count} song(s) from the library.");
engine.SetQueue(seedTracks);

// --- Watch the harvest folder's manifest.csv for newly-kept segments ---

var manifestPath = Path.Combine(harvestDir, "manifest.csv");
var manifestLinesSeen = 0;
var harvestedAppended = 0;

var watchTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
watchTimer.Tick += (_, _) => PollManifest();
watchTimer.Start();

void PollManifest()
{
    if (!File.Exists(manifestPath))
        return;

    string[] lines;
    try
    {
        // DjHarvest's manifest writer is AutoFlush + append-only; a shared-read open is safe
        // even while that separate process keeps writing.
        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
    catch (IOException)
    {
        return; // transient share/lock — try again next tick
    }

    if (lines.Length <= manifestLinesSeen)
        return;

    var fresh = new List<LocalTrack>();
    for (var i = Math.Max(manifestLinesSeen, 1); i < lines.Length; i++) // skip header row 0
    {
        var cols = SplitCsvLine(lines[i].TrimEnd('\r'));
        // station,artist,title,verdict,musicPct,leadTrimSec,tailTrimSec,keptFile
        if (cols.Length < 8) continue;
        var artist = cols[1];
        var title = cols[2];
        var keptFile = cols[7];
        if (string.IsNullOrWhiteSpace(keptFile)) continue; // QC-rejected row — nothing to play

        if (!seen.Add(DedupKey(artist, title)))
        {
            Console.WriteLine($"  (skip dup) {artist} — {title}");
            continue;
        }

        var path = Path.Combine(harvestDir, keptFile);
        if (!File.Exists(path)) continue;
        fresh.Add(new LocalTrack(path, title, artist, FormatFromExtension(path)));
    }
    manifestLinesSeen = lines.Length;

    if (fresh.Count == 0)
        return;

    harvestedAppended += fresh.Count;
    engine.Append(fresh);
    foreach (var t in fresh)
        Console.WriteLine($"  + appended: {t.Artist} — {t.Title}");
}

// Periodic status.
var status = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
status.Tick += (_, _) => Console.WriteLine(
    $"status — queue {engine.QueueCount} (seed {seedTracks.Count} + harvested {harvestedAppended})  state {engine.State}");
status.Start();

if (seconds > 0)
{
    var stopTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
    stopTimer.Tick += (_, _) => Shutdown();
    stopTimer.Start();
}

Console.CancelKeyPress += (_, e) => { e.Cancel = true; dispatcher.BeginInvoke(Shutdown); };

Dispatcher.Run();
return 0;

void Shutdown()
{
    watchTimer.Stop();
    status.Stop();
    engine.Stop();
    engine.Dispose();
    embeddings.Dispose();
    libraryStore.Dispose();
    Bass.Free();
    dispatcher.InvokeShutdown();
}

static StreamFormat FormatFromExtension(string path) =>
    Path.GetExtension(path).ToLowerInvariant() is ".aac" or ".m4a" ? StreamFormat.Aac : StreamFormat.Mp3;

// Minimal quote-aware CSV split (mirrors tools/DjDetector's — manifest.csv quotes every field
// via Qc.Csv(), and artist/title can contain literal commas).
static string[] SplitCsvLine(string line)
{
    var fields = new List<string>();
    var sb = new System.Text.StringBuilder();
    var inQuotes = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (inQuotes)
        {
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = false;
            }
            else sb.Append(c);
        }
        else if (c == '"') inQuotes = true;
        else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
        else sb.Append(c);
    }
    fields.Add(sb.ToString());
    return fields.ToArray();
}

int ArgInt(string name, int def) => int.TryParse(ArgStr(name, null), out var v) ? v : def;
string? ArgStr(string name, string? def)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}
