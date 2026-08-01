using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ManagedBass;
using ManagedBass.Aac;
using RadioPlayer.Models;
using RadioPlayer.Services;

// Dj Mode (harvest) — concurrent headless harvesting + segment QC + fill-rate measurement.
//
// Opens N decode-only connections to real stations, cuts complete songs at ICY title
// boundaries using the app's real StreamRecorder, then runs each completed segment through the
// music/speech detector: clear talk is REJECTED, and kept songs get their head/tail talk
// (edge-trim seconds) recorded to manifest.csv. Produces a clean harvest folder + fill rate.
//
//   dotnet run --project tools/DjHarvest
//   dotnet run --project tools/DjHarvest -- --seconds 1800 --out C:\harvest
//   dotnet run --project tools/DjHarvest -- --qc-reject 0.3 "http://a/stream|aac"
//
// Each station arg is "url" (codec inferred from the URL) or "url|aac" / "url|mp3" explicit.

// --probe <folder>: diagnostic-only. Decodes each file to get its TRUE duration (via BASS) and
// compares fileSize/trueDuration (the file's real bytes/sec) against the nominal rate implied by
// its "-NNN-" bitrate tag in the filename (SomaFM/Icecast convention) — the most direct way to
// check whether the byte-rate assumption StreamRecorder's deferred cut relies on is actually
// correct for these streams, without guessing.
var probeDir = ArgStr("--probe", null);

var seconds = ArgInt("--seconds", 900);
var offset = ArgDouble("--offset", 6.0);
var qcReject = ArgDouble("--qc-reject", 0.30); // reject a segment below this music fraction
var outDir = ArgStr("--out", null) ?? Path.Combine(Path.GetTempPath(), "DjHarvest");
// Applies the detector's lead/tail edge-trim to kept segments by default (see Qc.TryTrimAndCopy);
// pass --no-edge-trim to compare against the old untrimmed behavior.
var edgeTrim = !args.Any(a => a.Equals("--no-edge-trim", StringComparison.OrdinalIgnoreCase));

// Positional station specs = everything that isn't a flag or a flag's value.
var valueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "--seconds", "--offset", "--out", "--qc-reject", "--probe" };
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
Qc.Configure(outDir, qcReject, edgeTrim);

// BASS on the "no sound" device (0): streams still download + fire metadata syncs at
// real-time when played, but produce no audio and don't contend for a real output device.
if (!Bass.Init(0) && Bass.LastError != Errors.Already)
{
    Console.WriteLine($"BASS init failed: {Bass.LastError}");
    return 1;
}

if (probeDir is not null)
    return Probe(probeDir);

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

Console.WriteLine($"Dj harvest PoC — {harvesters.Count} stations, {seconds}s, offset {offset}s, " +
                  $"QC reject < {qcReject:0.00} music, edge-trim {(edgeTrim ? "ON" : "off")}");
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
    // Give any in-flight QC decodes a moment to finish before tearing down BASS.
    Thread.Sleep(1500);
    PrintStatus(harvesters, runStart, final: true);
    Qc.Close();
    Bass.Free();
    dispatcher.InvokeShutdown();
}

static void PrintStatus(List<Harvester> harvesters, DateTime runStart, bool final)
{
    var elapsedHr = Math.Max(1e-4, (DateTime.Now - runStart).TotalHours);
    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"{(final ? "FINAL" : "status")} @ {(DateTime.Now - runStart).TotalMinutes:0.0} min");
    var totalKept = 0;
    var totalRejected = 0;
    foreach (var h in harvesters)
    {
        totalKept += h.Kept;
        totalRejected += h.Rejected;
        Console.WriteLine($"  {h.Label,-24} kept {h.Kept,3}  rej {h.Rejected,2}  titles {h.Titles,3}  " +
                          $"{h.Kept / elapsedHr,5:0.0}/hr{(h.Dead ? "  [DEAD]" : "")}");
    }
    // Fill rate is measured on QC-KEPT songs (the ones that reach the queue).
    var aggPerHr = totalKept / elapsedHr;
    // A single continuous playback consumes ~1 song per average song length (~3.5 min → ~17/hr).
    const double starvationLine = 17.0;
    Console.WriteLine($"  {"AGGREGATE",-24} kept {totalKept,3}  rej {totalRejected,2}  " +
                      $"     {aggPerHr,5:0.0}/hr  " +
                      $"(need > ~{starvationLine:0}/hr → {(aggPerHr > starvationLine ? "SUSTAINABLE" : "below line")})");
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

/// <summary>
/// Diagnostic: for each audio file, decode it to get its TRUE duration (BASS), then compute
/// fileSize / trueDuration = the file's REAL bytes/sec — and compare against the nominal rate
/// implied by a "-NNN-" bitrate tag in the filename (e.g. "groovesalad-128-mp3" → 128 kbps).
/// StreamRecorder's deferred-cut math converts a configured offset in SECONDS to a byte count via
/// exactly this kind of bitrate figure; if the assumed rate is off, "6 seconds" silently becomes
/// a different real-world delay. A large mismatch here is a smoking gun; a close match rules out
/// bitrate error and points at the offset value itself (or the stream's true metadata timing)
/// as the real gap between configured and observed lead/lag.
/// </summary>
static int Probe(string dir)
{
    string[] audioExt = [".mp3", ".aac", ".m4a"];
    var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Where(f => audioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .OrderBy(f => f).ToList();

    Console.WriteLine($"Probing {files.Count} file(s) in {dir}");
    Console.WriteLine(new string('-', 90));
    foreach (var file in files)
    {
        var name = Path.GetFileName(file);
        var size = new FileInfo(file).Length;
        var isAac = Path.GetExtension(file).Equals(".aac", StringComparison.OrdinalIgnoreCase);
        var h = isAac
            ? BassAac.CreateStream(file, 0, 0, BassFlags.Decode)
            : Bass.CreateStream(file, 0, 0, BassFlags.Decode);
        if (h == 0) { Console.WriteLine($"  {name,-55} decode failed ({Bass.LastError})"); continue; }

        var lenBytes = Bass.ChannelGetLength(h, PositionFlags.Bytes);
        var trueDuration = Bass.ChannelBytes2Seconds(h, lenBytes);
        Bass.ChannelGetAttribute(h, ChannelAttribute.Bitrate, out var reportedKbps);
        Bass.StreamFree(h);
        if (trueDuration <= 0) { Console.WriteLine($"  {name,-55} zero duration?"); continue; }

        var trueBytesPerSec = size / trueDuration;
        var trueKbps = trueBytesPerSec * 8.0 / 1000.0;

        // Nominal rate from a "-NNN-" segment in the filename, if present (SomaFM/Icecast style).
        var m = System.Text.RegularExpressions.Regex.Match(name, @"-(\d{2,3})-(mp3|aac)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var nominalNote = m.Success ? $"  nominal={m.Groups[1].Value}kbps" : "";

        Console.WriteLine($"  {name,-55} dur={trueDuration,6:0.0}s  size={size,9}B  " +
                          $"trueRate={trueKbps,6:0.0}kbps  BASS-reported={reportedKbps,6:0.0}kbps{nominalNote}");
    }
    return 0;
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
    private long _downloadedBytes;            // diagnostic: raw bytes handed to Write() so far

    private int _handle;
    private int _session;
    private int _titles;
    private int _kept;
    private int _rejected;
    private int _reconnects;
    private volatile bool _dead;
    private const int MaxReconnects = 5;

    public string Label { get; }
    public int Titles => Volatile.Read(ref _titles);
    public int Kept => Volatile.Read(ref _kept);
    public int Rejected => Volatile.Read(ref _rejected);
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
        _dl = (buffer, length, _) =>
        {
            if (buffer != IntPtr.Zero && length > 0)
                Interlocked.Add(ref _downloadedBytes, length);
            _recorder.Write(_session, buffer, length);
        };
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

        double bps = 0;
        var gotAttr = Bass.ChannelGetAttribute(_handle, ChannelAttribute.Bitrate, out var kbps);
        if (gotAttr && kbps > 0)
            bps = kbps * 1000.0 / 8.0;

        // Diagnostic: how far behind the raw download position is the decoder's own read
        // position? Write() (and thus the deferred-cut countdown) tallies bytes as they arrive
        // off the network; if BASS's internal decode/read buffer keeps the decode-for-playback
        // position meaningfully behind that, the metadata sync itself is already "late" relative
        // to the bytes already sitting in the recorder — on top of the deliberate offset.
        // Both sides here are in COMPRESSED-stream byte terms (FileStreamPosition), unlike
        // ChannelGetPosition(Bytes) which is decoded-PCM bytes and not comparable.
        var decodePos = Bass.StreamGetFilePosition(_handle, FileStreamPosition.Current);
        var downloaded = Interlocked.Read(ref _downloadedBytes);
        var gapBytes = downloaded - decodePos;
        var gapSeconds = bps > 0 ? gapBytes / bps : double.NaN;

        var bassDownloadPos = Bass.StreamGetFilePosition(_handle, FileStreamPosition.Download);
        Console.WriteLine($"  [{Label}] ICY: {title}  " +
                          $"[diag: reported={(gotAttr ? kbps.ToString("0.#") : "none")}kbps bps={bps:0} " +
                          $"downloaded={downloaded} bassDownloadPos={bassDownloadPos} decodePos={decodePos} " +
                          $"gap={gapBytes}B (~{gapSeconds:0.0}s)]");
        _recorder.OnTrackChanged(_session, trackTitle, artist, Label, bps);
    }

    // Raised on the dispatcher thread by StreamRecorder for each complete, song-like segment.
    // QC (decode + classify) is offloaded so it never blocks the dispatcher / other harvesters.
    private void OnSegmentCompleted(object? sender, CompletedSegment seg)
    {
        var src = _recorder.PathFor(seg.FileName);
        Task.Run(() =>
        {
            var (kept, note) = Qc.Evaluate(src, _outDir, Label, seg);
            if (kept) Interlocked.Increment(ref _kept);
            else Interlocked.Increment(ref _rejected);
            Console.WriteLine($"  [{Label}] {note}");
        });
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
}

/// <summary>
/// Segment quality control on completed harvest segments (the harvest design's SegmentQualityChecker,
/// PoC form): decode the finished segment, classify it with the shared <see cref="MusicDetector"/>,
/// REJECT clear talk (music fraction below the threshold), and for kept songs record the head/tail
/// talk (edge-trim seconds) to manifest.csv. Runs on background threads; thread-safe.
/// </summary>
internal static class Qc
{
    private static readonly object _lock = new();
    private static readonly MusicDetector _detector = new();
    private static StreamWriter? _manifest;
    private static double _rejectBelow = 0.30;
    private static bool _trimEdges = true;

    public static void Configure(string outDir, double rejectBelow, bool trimEdges = true)
    {
        _rejectBelow = rejectBelow;
        _trimEdges = trimEdges;
        Directory.CreateDirectory(outDir);
        _manifest = new StreamWriter(Path.Combine(outDir, "manifest.csv")) { AutoFlush = true };
        _manifest.WriteLine("station,artist,title,verdict,musicPct,leadTrimSec,tailTrimSec,keptFile");
    }

    public static void Close()
    {
        lock (_lock) { _manifest?.Flush(); _manifest?.Dispose(); _manifest = null; }
    }

    /// <summary>Decode + classify one completed segment; keep or reject it. Returns (kept, log note).</summary>
    public static (bool Kept, string Note) Evaluate(string src, string outDir, string label, CompletedSegment seg)
    {
        try
        {
            var mono = DecodeMono(src, out var rate);
            if (mono is null || mono.Length == 0)
            {
                StreamRecorder.TryDelete(src);
                return (false, $"✗ decode failed: {seg.Artist} — {seg.Title}");
            }

            var res = _detector.Analyze(mono, rate);
            var pct = res.MusicFraction * 100;

            if (res.MusicFraction < _rejectBelow)
            {
                StreamRecorder.TryDelete(src); // clear talk — don't let it reach the queue
                WriteManifest(label, seg, res, "");
                return (false, $"✗ QC rejected {res.Verdict} {pct:0}%: {seg.Artist} — {seg.Title}");
            }

            var ext = Path.GetExtension(seg.FileName);
            var baseName = Sanitize($"{label} · {seg.Artist} - {seg.Title}");
            var dst = Path.Combine(outDir, baseName + ext);
            for (var n = 2; File.Exists(dst); n++)
                dst = Path.Combine(outDir, $"{baseName} ({n}){ext}");

            var didTrim = false;
            if (_trimEdges && (res.LeadTalkSeconds > 0 || res.TailTalkSeconds > 0))
                didTrim = TryTrimAndCopy(src, dst, res, seg.Bytes);
            if (!didTrim)
                File.Copy(src, dst);

            StreamRecorder.TryDelete(src);
            WriteManifest(label, seg, res, Path.GetFileName(dst));

            var trim = res.LeadTalkSeconds > 0 || res.TailTalkSeconds > 0
                ? $"  trim {res.LeadTalkSeconds:0.0}s/{res.TailTalkSeconds:0.0}s{(didTrim ? " (applied)" : " (suggested only)")}"
                : "";
            return (true, $"♪ {res.Verdict} {pct:0}%: {seg.Artist} — {seg.Title}{trim}");
        }
        catch (Exception ex)
        {
            return (false, $"✗ QC error: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the detector's edge-trim suggestion to the actual audio bytes: chops
    /// <see cref="FileResult.LeadTalkSeconds"/>/<see cref="FileResult.TailTalkSeconds"/> off the
    /// front/back of the captured (constant-bitrate) file and writes only the middle to
    /// <paramref name="dst"/>. This is the real fix for "song starts abruptly / has talk (or the
    /// next song) bled into the tail" — the offset that drives the *capture-time* cut can only
    /// delay a boundary, never pull one backward, so it can't correct a station whose metadata
    /// genuinely lags its own audio (a negative true lead). Byte-trimming after the fact can, since
    /// it uses the detector's own measurement of where the real content starts/ends, not a fixed
    /// assumption about the station's encoder timing.
    ///
    /// Byte rate is derived from this file's own captured size ÷ its analyzed duration (not a
    /// nominal/reported bitrate) — self-consistent for the constant-bitrate MP3/AAC streams these
    /// harvesters capture. The cut isn't frame-boundary-exact (no MP3/ADTS frame parsing here), so
    /// expect an occasional barely-audible micro-glitch right at the trim point — acceptable for
    /// this PoC; a production cut would snap to the nearest frame header.
    /// </summary>
    private static bool TryTrimAndCopy(string src, string dst, FileResult res, long totalBytes)
    {
        if (res.Windows.Count == 0 || totalBytes <= 0)
            return false;

        var totalSeconds = res.Windows[^1].TStart + MusicDetector.WindowSeconds;
        if (totalSeconds <= 0)
            return false;

        var bytesPerSecond = totalBytes / totalSeconds;
        var leadBytes = (long)(res.LeadTalkSeconds * bytesPerSecond);
        var tailBytes = (long)(res.TailTalkSeconds * bytesPerSecond);

        // Sanity guard: never trim away the whole file (a mis-measured all-talk file should have
        // been QC-rejected already, not land here trimmed to nothing).
        const long minKeptBytes = 32 * 1024;
        if (leadBytes + tailBytes + minKeptBytes >= totalBytes)
            return false;

        try
        {
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read);
            using var output = new FileStream(dst, FileMode.Create, FileAccess.Write);
            input.Seek(leadBytes, SeekOrigin.Begin);
            var remaining = totalBytes - leadBytes - tailBytes;
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                var chunk = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (chunk <= 0) break;
                output.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
            return true;
        }
        catch
        {
            try { File.Delete(dst); } catch { /* best effort */ }
            return false;
        }
    }

    private static void WriteManifest(string label, CompletedSegment seg, FileResult res, string keptFile)
    {
        lock (_lock)
            _manifest?.WriteLine(string.Join(',',
                Csv(label), Csv(seg.Artist), Csv(seg.Title), res.Verdict,
                (res.MusicFraction * 100).ToString("0"),
                res.LeadTalkSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                res.TailTalkSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                Csv(keptFile)));
    }

    private static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    private static float[]? DecodeMono(string file, out int sampleRate)
    {
        sampleRate = 0;
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var isAac = ext is ".aac" or ".m4a";
        var h = isAac
            ? BassAac.CreateStream(file, 0, 0, BassFlags.Decode | BassFlags.Float)
            : Bass.CreateStream(file, 0, 0, BassFlags.Decode | BassFlags.Float);
        if (h == 0) return null;
        if (!Bass.ChannelGetInfo(h, out var info)) { Bass.StreamFree(h); return null; }
        sampleRate = info.Frequency;
        var ch = Math.Max(1, info.Channels);

        var samples = new List<float>(1 << 20);
        var buf = new float[16384];
        while (true)
        {
            var bytes = Bass.ChannelGetData(h, buf, buf.Length * sizeof(float));
            if (bytes <= 0) break;
            var got = bytes / sizeof(float);
            if (ch == 1)
                for (var i = 0; i < got; i++) samples.Add(buf[i]);
            else
                for (var i = 0; i + ch <= got; i += ch)
                {
                    double s = 0;
                    for (var c = 0; c < ch; c++) s += buf[i + c];
                    samples.Add((float)(s / ch));
                }
        }
        Bass.StreamFree(h);
        return samples.ToArray();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length > 120 ? name[..120] : name;
    }
}
