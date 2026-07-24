using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ManagedBass;
using ManagedBass.Aac;
using RadioPlayer.Models;
using RadioPlayer.Services;

// Dj Mode (harvest) — PoC 1: concurrent headless harvesting + fill-rate measurement.
//
// Opens N decode-only connections to real stations, cuts complete songs at ICY title
// boundaries using the app's real StreamRecorder, copies each kept segment to a scratch
// folder for by-ear inspection, and reports the aggregate fill rate. No playback, no UI.
//
//   dotnet run --project tools/DjHarvest
//   dotnet run --project tools/DjHarvest -- --seconds 1800 --out C:\harvest
//   dotnet run --project tools/DjHarvest -- "http://a/stream|aac" "http://b/stream|mp3"
//
// Each station arg is "url" (codec inferred from the URL) or "url|aac" / "url|mp3" explicit.

var seconds = ArgInt("--seconds", 900);
var offset = ArgDouble("--offset", 6.0);
var outDir = ArgStr("--out", null) ?? Path.Combine(Path.GetTempPath(), "DjHarvest");

// Positional station specs = everything that isn't a flag or a flag's value.
var valueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--seconds", "--offset", "--out" };
var stationArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        if (valueFlags.Contains(args[i])) i++; // skip this flag's value too
        continue;
    }
    stationArgs.Add(args[i]);
}

if (stationArgs.Count == 0)
{
    // Default mix (AAC + MP3). Override by passing your own station URLs.
    stationArgs =
    [
        "http://stream.radioparadise.com/aac-128|aac",
        "http://ice1.somafm.com/groovesalad-128-mp3|mp3",
        "http://ice1.somafm.com/indiepop-128-mp3|mp3",
        "http://ice1.somafm.com/dronezone-128-mp3|mp3",
    ];
}

Directory.CreateDirectory(outDir);

// BASS on the "no sound" device (0): streams still download + fire metadata syncs at
// real-time when played, but produce no audio and don't contend for a real output device.
if (!Bass.Init(0) && Bass.LastError != Errors.Already)
{
    Console.WriteLine($"BASS init failed: {Bass.LastError}");
    return 1;
}

// A dispatcher must pump on this thread: StreamRecorder raises SegmentCompleted through the
// dispatcher captured at its construction, and harvesters marshal reconnects here too.
var dispatcher = Dispatcher.CurrentDispatcher;

var harvesters = new List<Harvester>();
foreach (var spec in stationArgs)
{
    var (url, format) = ParseSpec(spec);
    var label = LabelFor(url);
    harvesters.Add(new Harvester(label, url, format, outDir, offset, dispatcher));
}

Console.WriteLine($"Dj harvest PoC — {harvesters.Count} stations, {seconds}s, offset {offset}s");
Console.WriteLine($"Segments → {outDir}");
Console.WriteLine(new string('-', 60));

var runStart = DateTime.Now;
foreach (var h in harvesters)
    h.Start();

// Periodic status.
var status = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(20) };
status.Tick += (_, _) => PrintStatus(harvesters, runStart, final: false);
status.Start();

// Auto-stop after the requested duration.
var stopTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(seconds) };
stopTimer.Tick += (_, _) => Shutdown();
stopTimer.Start();

// Graceful Ctrl+C.
Console.CancelKeyPress += (_, e) => { e.Cancel = true; dispatcher.BeginInvoke(Shutdown); };

Dispatcher.Run();
return 0;

void Shutdown()
{
    status.Stop();
    stopTimer.Stop();
    foreach (var h in harvesters)
        h.Stop();
    PrintStatus(harvesters, runStart, final: true);
    Bass.Free();
    dispatcher.InvokeShutdown();
}

static void PrintStatus(List<Harvester> harvesters, DateTime runStart, bool final)
{
    var elapsedHr = Math.Max(1e-4, (DateTime.Now - runStart).TotalHours);
    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"{(final ? "FINAL" : "status")} @ {(DateTime.Now - runStart).TotalMinutes:0.0} min");
    var totalKept = 0;
    foreach (var h in harvesters)
    {
        totalKept += h.Kept;
        Console.WriteLine($"  {h.Label,-24} kept {h.Kept,3}  titles {h.Titles,3}  " +
                          $"{h.Kept / elapsedHr,5:0.0}/hr{(h.Dead ? "  [DEAD]" : "")}");
    }
    var aggPerHr = totalKept / elapsedHr;
    // A single continuous playback consumes ~1 song per average song length (~3.5 min → ~17/hr).
    const double starvationLine = 17.0;
    Console.WriteLine($"  {"AGGREGATE",-24} kept {totalKept,3}           {aggPerHr,5:0.0}/hr  " +
                      $"(need > ~{starvationLine:0}/hr to sustain playback → " +
                      $"{(aggPerHr > starvationLine ? "SUSTAINABLE" : "below line")})");
    Console.WriteLine(new string('-', 60));
}

(string url, StreamFormat format) ParseSpec(string spec)
{
    var parts = spec.Split('|', 2);
    var url = parts[0].Trim();
    StreamFormat fmt;
    if (parts.Length == 2)
        fmt = parts[1].Trim().Equals("aac", StringComparison.OrdinalIgnoreCase) ? StreamFormat.Aac : StreamFormat.Mp3;
    else
        fmt = url.Contains("aac", StringComparison.OrdinalIgnoreCase) ? StreamFormat.Aac : StreamFormat.Mp3;
    return (url, fmt);
}

static string LabelFor(string url)
{
    // A short, human-ish label from the URL's host + last path segment.
    try
    {
        var u = new Uri(url);
        var host = u.Host.Replace("www.", "");
        var seg = u.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrEmpty(seg) ? host : $"{host}/{seg}";
    }
    catch { return url; }
}

int ArgInt(string name, int def) => int.TryParse(ArgStr(name, null), out var v) ? v : def;
double ArgDouble(string name, double def) => double.TryParse(ArgStr(name, null), out var v) ? v : def;
string? ArgStr(string name, string? def)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

/// <summary>One headless harvesting connection: downloads a station into a real StreamRecorder,
/// cuts complete songs at ICY boundaries, and copies each kept segment to the scratch folder.</summary>
sealed class Harvester
{
    private readonly string _outDir;
    private readonly string _url;
    private readonly StreamFormat _format;
    private readonly StreamRecorder _recorder;
    private readonly Dispatcher _dispatcher;
    private readonly DownloadProcedure _dl;   // rooted so BASS's native pointer stays valid
    private SyncProcedure? _metaSync, _endSync, _stallSync;

    private int _handle;
    private int _session;
    private int _titles;
    private int _kept;
    private int _reconnects;
    private volatile bool _dead;
    private const int MaxReconnects = 5;

    public string Label { get; }
    public int Titles => Volatile.Read(ref _titles);
    public int Kept => Volatile.Read(ref _kept);
    public bool Dead => _dead;

    public Harvester(string label, string url, StreamFormat format, string outDir, double offsetSeconds,
        Dispatcher dispatcher)
    {
        Label = label;
        _url = url;
        _format = format;
        _outDir = outDir;
        _dispatcher = dispatcher;
        _recorder = new StreamRecorder(offsetSeconds); // captures `dispatcher` for SegmentCompleted
        _recorder.SegmentCompleted += OnSegmentCompleted;
        // Download callback runs on a BASS network thread; Write is designed for that.
        _dl = (buffer, length, _) => _recorder.Write(_session, buffer, length);
    }

    /// <summary>Connect and begin harvesting. Runs on the dispatcher thread.</summary>
    public bool Start()
    {
        _session = _recorder.BeginSession(new Station(Label, _url, _format), _format);
        _handle = _format == StreamFormat.Aac
            ? BassAac.CreateStream(_url, 0, BassFlags.Default, _dl)
            : Bass.CreateStream(_url, 0, BassFlags.Default, _dl);
        if (_handle == 0)
        {
            Console.WriteLine($"  [{Label}] connect failed: {Bass.LastError}");
            _recorder.EndSession(_session);
            return false;
        }

        // Syncs fire on BASS threads → marshal to the dispatcher so all BASS lifecycle calls stay
        // single-threaded (mirrors RadioEngine), and reading the META tag happens off the callback.
        _metaSync = (_, _, _, _) => _dispatcher.BeginInvoke(OnMeta);
        Bass.ChannelSetSync(_handle, SyncFlags.MetadataReceived, 0, _metaSync);
        _endSync = (_, _, _, _) => _dispatcher.BeginInvoke(OnEnd);
        Bass.ChannelSetSync(_handle, SyncFlags.End, 0, _endSync);
        _stallSync = (_, _, data, _) =>
        {
            if (data == 0) _dispatcher.BeginInvoke(() => Console.WriteLine($"  [{Label}] stalled"));
        };
        Bass.ChannelSetSync(_handle, SyncFlags.Stalled, 0, _stallSync);

        // Play to the no-sound device: drives download + metadata syncs at real-time, silently.
        Bass.ChannelPlay(_handle);
        return true;
    }

    private void OnMeta()
    {
        var title = ParseStreamTitle(ReadTag(_handle, TagType.META));
        if (string.IsNullOrWhiteSpace(title)) return;

        var trackTitle = title;
        string? artist = null;
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            artist = title[..dash].Trim();
            trackTitle = title[(dash + 3)..].Trim();
        }

        Interlocked.Increment(ref _titles);
        Console.WriteLine($"  [{Label}] ICY: {title}");

        double bps = 0;
        if (Bass.ChannelGetAttribute(_handle, ChannelAttribute.Bitrate, out var kbps) && kbps > 0)
            bps = kbps * 1000.0 / 8.0;
        _recorder.OnTrackChanged(_session, trackTitle, artist, Label, bps);
    }

    // Raised on the dispatcher thread by StreamRecorder for each complete, song-like segment.
    private void OnSegmentCompleted(object? sender, CompletedSegment seg)
    {
        try
        {
            var src = StreamRecorder.PathFor(seg.FileName);
            var baseName = Sanitize($"{Label} · {seg.Artist} - {seg.Title}");
            var ext = Path.GetExtension(seg.FileName);
            var dst = Path.Combine(_outDir, baseName + ext);
            for (var n = 2; File.Exists(dst); n++)
                dst = Path.Combine(_outDir, $"{baseName} ({n}){ext}");
            File.Copy(src, dst);
            StreamRecorder.TryDelete(src); // don't pollute the app's real cache
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{Label}] copy failed: {ex.Message}");
        }
        Interlocked.Increment(ref _kept);
        Console.WriteLine($"  [{Label}] ♪ {seg.Artist} — {seg.Title}  ({seg.Bytes / 1024} KB)");
    }

    private void OnEnd()
    {
        if (_dead) return;
        Console.WriteLine($"  [{Label}] stream ended");
        if (_handle != 0) { Bass.StreamFree(_handle); _handle = 0; }
        _recorder.EndSession(_session);

        if (++_reconnects > MaxReconnects)
        {
            Console.WriteLine($"  [{Label}] giving up after {MaxReconnects} reconnects");
            _dead = true;
            return;
        }
        // Bounded reconnect after a short delay, back on the dispatcher thread.
        Task.Delay(3000).ContinueWith(_ => _dispatcher.BeginInvoke(() =>
        {
            if (_dead) return;
            Console.WriteLine($"  [{Label}] reconnecting ({_reconnects}/{MaxReconnects})…");
            if (!Start()) _dead = true;
        }));
    }

    public void Stop()
    {
        _dead = true;
        if (_handle != 0) { Bass.StreamFree(_handle); _handle = 0; }
        _recorder.EndSession(_session);
        _recorder.Dispose();
    }

    private static string? ReadTag(int handle, TagType type)
    {
        var ptr = Bass.ChannelGetTags(handle, type);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    // ICY terminates StreamTitle with '; (apostrophe-semicolon) — match the app's parser so a
    // title containing an apostrophe ("Don't...") isn't truncated.
    private static string? ParseStreamTitle(string? meta)
    {
        if (string.IsNullOrEmpty(meta)) return null;
        const string key = "StreamTitle='";
        var start = meta.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = meta.IndexOf("';", start, StringComparison.Ordinal);
        return end < 0 ? meta[start..].TrimEnd('\'') : meta[start..end];
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length > 120 ? name[..120] : name;
    }
}
