using System.IO;
using DjDetector;
using ManagedBass;
using ManagedBass.Aac;

// DJ Mode — offline music / speech detector harness (DJ-MODE-SPEC.md PoC 1, reused by the
// harvest design as the segment QC + edge-trim detector).
//
// Decodes each audio file to mono PCM, slides a ~1 s window computing speech/music features,
// and emits a per-window music-confidence, a whole-file verdict, an edge-trim suggestion, and a
// CSV of every window's raw features for tuning. No realtime constraint, no UI.
//
//   dotnet run --project tools/DjDetector -- <folder-or-files...>
//   dotnet run --project tools/DjDetector -- C:\harvest --csv features.csv
//   dotnet run --project tools/DjDetector -- C:\clips\music --label music
//
// Labelling (for the accuracy readout): pass --label, or drop files under a folder named
// music / talk / ads / news / speech and the label is inferred from that folder.

var forcedLabel = ArgStr("--label");
var csvPath = ArgStr("--csv") ?? "djdetector-features.csv";

// Positional inputs = everything that isn't a flag or a flag's value.
var valueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--label", "--csv" };
var paths = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        if (valueFlags.Contains(args[i])) i++; // skip this flag's value too
        continue;
    }
    paths.Add(args[i]);
}

if (paths.Count == 0)
{
    Console.WriteLine("Usage: DjDetector <folder-or-files...> [--label music|talk] [--csv out.csv]");
    return 1;
}

if (!Bass.Init(0) && Bass.LastError != Errors.Already)
{
    Console.WriteLine($"BASS init failed: {Bass.LastError}");
    return 1;
}

string[] audioExt = [".mp3", ".aac", ".m4a", ".wav", ".ogg"];
var files = new List<string>();
foreach (var p in paths)
{
    if (Directory.Exists(p))
        files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
            .Where(f => audioExt.Contains(Path.GetExtension(f).ToLowerInvariant())));
    else if (File.Exists(p))
        files.Add(p);
    else
        Console.WriteLine($"skip (not found): {p}");
}

if (files.Count == 0)
{
    Console.WriteLine("No audio files found.");
    return 1;
}

var detector = new MusicDetector();
using var csv = new StreamWriter(csvPath);
csv.WriteLine(MusicDetector.CsvHeader);

// Per-window accuracy tallies, keyed by label.
var labelWindows = new Dictionary<string, (int correct, int total)>();

Console.WriteLine($"Analyzing {files.Count} file(s)…  CSV → {csvPath}");
Console.WriteLine(new string('-', 78));

foreach (var file in files.OrderBy(f => f))
{
    var name = Path.GetFileName(file);
    var label = forcedLabel ?? InferLabel(file);

    var mono = DecodeMono(file, out var rate);
    if (mono is null || mono.Length == 0)
    {
        Console.WriteLine($"  {name,-40} decode failed ({Bass.LastError})");
        continue;
    }

    var result = detector.Analyze(mono, rate);
    foreach (var w in result.Windows)
        csv.WriteLine(MusicDetector.CsvRow(name, label, w));

    // Edge-trim readout: how much talk sits at the head/tail of a mostly-music segment.
    var trim = result.Verdict == "MUSIC" && (result.LeadTalkSeconds > 0 || result.TailTalkSeconds > 0)
        ? $"  trim: {result.LeadTalkSeconds:0.0}s lead / {result.TailTalkSeconds:0.0}s tail"
        : "";
    Console.WriteLine($"  {name,-40} {result.Verdict,-6} music {result.MusicFraction * 100,3:0}%" +
                      $"  ({result.Windows.Count}w){trim}{(label.Length > 0 ? $"  [{label}]" : "")}");

    // Accuracy: for a labelled clip, every window should match the label.
    if (label is "music" or "nonmusic")
    {
        var want = label == "music";
        var correct = result.Windows.Count(w => (w.Confidence >= 0.5) == want);
        var t = labelWindows.GetValueOrDefault(label);
        labelWindows[label] = (t.correct + correct, t.total + result.Windows.Count);
    }
}

Bass.Free();

if (labelWindows.Count > 0)
{
    Console.WriteLine(new string('-', 78));
    Console.WriteLine("Per-window accuracy (labelled clips):");
    var allC = 0; var allT = 0;
    foreach (var (label, (c, t)) in labelWindows)
    {
        Console.WriteLine($"  {label,-10} {(t == 0 ? 0 : 100.0 * c / t),5:0.0}%  ({c}/{t} windows)");
        allC += c; allT += t;
    }
    Console.WriteLine($"  {"overall",-10} {(allT == 0 ? 0 : 100.0 * allC / allT),5:0.0}%  " +
                      $"(target ≳ 90% — DJ-MODE-SPEC PoC 1 exit criterion)");
}

return 0;

// --- helpers ---

static float[]? DecodeMono(string file, out int sampleRate)
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
        if (bytes <= 0) break; // -1 = end/error, 0 = no more
        var got = bytes / sizeof(float);
        if (ch == 1)
        {
            for (var i = 0; i < got; i++) samples.Add(buf[i]);
        }
        else
        {
            for (var i = 0; i + ch <= got; i += ch)
            {
                double s = 0;
                for (var c = 0; c < ch; c++) s += buf[i + c];
                samples.Add((float)(s / ch));
            }
        }
    }
    Bass.StreamFree(h);
    return samples.ToArray();
}

static string InferLabel(string file)
{
    var parent = Path.GetFileName(Path.GetDirectoryName(file) ?? "")?.ToLowerInvariant() ?? "";
    return parent switch
    {
        "music" => "music",
        "talk" or "speech" or "ads" or "ad" or "news" or "jingle" => "nonmusic",
        _ => ""
    };
}

string? ArgStr(string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
