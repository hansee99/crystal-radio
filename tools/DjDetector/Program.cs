using System.IO;
using RadioPlayer.Services;
using ManagedBass;
using ManagedBass.Aac;

// DJ Mode — offline music / speech detector harness (DJ-MODE-SPEC.md PoC 1, reused by the
// harvest design as the segment QC + edge-trim detector).
//
// Decodes each audio file to mono PCM, slides a ~1 s window computing speech/music features,
// and emits a per-window music-confidence, a whole-file verdict, an edge-trim suggestion, a
// suspect-span report (candidate talk timestamps to spot-check by ear), and a CSV of every
// window's raw features for tuning. No realtime constraint, no UI.
//
//   dotnet run --project tools/DjDetector -- <folder-or-files...>
//   dotnet run --project tools/DjDetector -- C:\harvest --csv features.csv
//   dotnet run --project tools/DjDetector -- C:\clips\music --label music
//
// Labelling (for the accuracy readout): pass --label, or drop files under a folder named
// music / talk / ads / news / speech and the label is inferred from that folder.
//
// Ground-truthing a harvested, unlabelled folder efficiently (no whole-file relabelling, no
// audio editing): run once to get the suspect spans printed/written to --suspects-csv, seek to
// those timestamps in any player, then note confirmed spans in a --corrections CSV
// (file,startSec,endSec,label — label is music|nonmusic) and re-run with --corrections applied.
// Only windows inside a confirmed span get that label in the output feature CSV; the rest of
// that file stays unlabelled (not assumed) until you confirm it too. Feed the resulting CSV rows
// into fit_logreg.py alongside the original corpus to re-fit.
//
// If a harvested folder produces a suspect-span AVALANCHE (hundreds of tiny "interior" spans,
// often back-to-back into one long run) that's usually NOT real talk — it's a corpus gap: the
// detector under-represents whatever genre those songs are (ambient/downtempo and sparse indie
// are the classic case) and mistakes "quiet/sparse" for "speech-like". Span-by-span review
// doesn't scale for that and isn't the right fix anyway. --template-corrections writes one
// whole-file "music" row per analyzed file (0..duration) for you to thin out: delete/edit only
// the rows for songs you know had real audible talk, then use the result as --corrections. That
// directly injects the missing genre diversity as clean examples, which is the actual fix.

var forcedLabel = ArgStr("--label");
var csvPath = ArgStr("--csv") ?? Path.Combine("tools", "DjDetector", "corpus", "djdetector-features.csv");
var correctionsPath = ArgStr("--corrections");
var suspectsCsvPath = ArgStr("--suspects-csv");
var templateCorrectionsPath = ArgStr("--template-corrections");
// Separate from MusicDetector.MusicThreshold (0.5, the classification boundary) on purpose: a
// window just under 0.5 in an otherwise-fine file isn't necessarily a real QC concern — the
// harvest pipeline's actual accept/reject bar is --qc-reject (0.30 by default in DjHarvest), and
// reporting suspects at 0.5 manufactures spans for genres that sit comfortably above 0.30 but
// below 0.5 (quiet/sparse music the fit is still lukewarm on). Default this to the same 0.30 so
// the report reflects "would this actually concern the harvest QC," not just "under 0.5".
var suspectThreshold = ArgDouble("--suspect-threshold", 0.30);

// Positional inputs = everything that isn't a flag or a flag's value.
var valueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "--label", "--csv", "--corrections", "--suspects-csv", "--template-corrections", "--suspect-threshold" };
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

// file → confirmed [start,end) spans with a verified label, from --corrections.
var corrections = LoadCorrections(correctionsPath);

var detector = new MusicDetector();
var csvDir = Path.GetDirectoryName(Path.GetFullPath(csvPath));
if (!string.IsNullOrEmpty(csvDir)) Directory.CreateDirectory(csvDir);
using var csv = new StreamWriter(csvPath);
csv.WriteLine(MusicDetector.CsvHeader);
using var suspectsCsv = suspectsCsvPath is null ? null : new StreamWriter(suspectsCsvPath);
suspectsCsv?.WriteLine("file,startSec,endSec,durationSec,kind,avgConfidence");
using var templateCsv = templateCorrectionsPath is null ? null : new StreamWriter(templateCorrectionsPath);
templateCsv?.WriteLine("file,startSec,endSec,label");

// Per-window accuracy tallies, keyed by label.
var labelWindows = new Dictionary<string, (int correct, int total)>();

Console.WriteLine($"Analyzing {files.Count} file(s)…  CSV → {csvPath}" +
                  (suspectsCsvPath is null ? "" : $"  suspects → {suspectsCsvPath}") +
                  (templateCorrectionsPath is null ? "" : $"  template → {templateCorrectionsPath}"));
Console.WriteLine(new string('-', 78));

foreach (var file in files.OrderBy(f => f))
{
    var name = Path.GetFileName(file);
    var fileLabel = forcedLabel ?? InferLabel(file);
    var fileCorrections = corrections.GetValueOrDefault(name);

    var mono = DecodeMono(file, out var rate);
    if (mono is null || mono.Length == 0)
    {
        Console.WriteLine($"  {name,-40} decode failed ({Bass.LastError})");
        continue;
    }

    var result = detector.Analyze(mono, rate);

    if (templateCsv is not null && result.Windows.Count > 0)
    {
        var duration = result.Windows[^1].TStart + MusicDetector.WindowSeconds;
        templateCsv.WriteLine(string.Join(',', Csv(name), "0", F(duration), "music"));
    }

    foreach (var w in result.Windows)
    {
        // A confirmed correction span overrides the whole-file label for windows inside it;
        // windows in a corrected file but OUTSIDE any span are left unlabelled (unverified),
        // never assumed to be the file's nominal label.
        var rowLabel = fileLabel;
        if (fileCorrections is not null)
        {
            rowLabel = "";
            var mid = w.TStart + MusicDetector.WindowSeconds / 2;
            foreach (var c in fileCorrections)
                if (mid >= c.Start && mid < c.End) { rowLabel = c.Label; break; }
        }
        csv.WriteLine(MusicDetector.CsvRow(name, rowLabel, w));

        if (rowLabel is "music" or "nonmusic")
        {
            var want = rowLabel == "music";
            var correct = (w.Confidence >= MusicDetector.MusicThreshold) == want ? 1 : 0;
            var t = labelWindows.GetValueOrDefault(rowLabel);
            labelWindows[rowLabel] = (t.correct + correct, t.total + 1);
        }
    }

    // Edge-trim readout: how much talk sits at the head/tail of a mostly-music segment.
    var trim = result.Verdict == "MUSIC" && (result.LeadTalkSeconds > 0 || result.TailTalkSeconds > 0)
        ? $"  trim: {result.LeadTalkSeconds:0.0}s lead / {result.TailTalkSeconds:0.0}s tail"
        : "";
    Console.WriteLine($"  {name,-40} {result.Verdict,-6} music {result.MusicFraction * 100,3:0}%" +
                      $"  ({result.Windows.Count}w){trim}{(fileLabel.Length > 0 ? $"  [{fileLabel}]" : "")}");

    // Suspect spans: contiguous below-threshold runs — candidate talk to spot-check by ear and
    // turn into --corrections entries. "lead"/"tail" spans duplicate the trim readout above;
    // "interior" spans are the ones edge-trim can't see (mid-song talk-overs, DJ drop-ins).
    foreach (var (start, end, kind, avgConf) in FindSuspectSpans(result, suspectThreshold))
    {
        Console.WriteLine($"      suspect  {start,6:0.0}s – {end,6:0.0}s  ({end - start:0.0}s, {kind}, conf {avgConf:0.00})");
        suspectsCsv?.WriteLine(string.Join(',', Csv(name), F(start), F(end), F(end - start), kind, F(avgConf)));
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

/// <summary>
/// Finds contiguous below-<paramref name="threshold"/> window runs — candidate talk spans to
/// spot-check by ear. "lead"/"tail" spans touch the very first/last window (same thing the trim
/// readout reports); "interior" spans are mid-file dips edge-trim can't see — the useful new
/// information here. Threshold is a parameter (not MusicDetector.MusicThreshold) because
/// "worth a human glance" and "worth classifying as music" are different bars — see --suspect-
/// threshold's default of 0.30 (matching DjHarvest's QC reject bar) above.
/// </summary>
static IEnumerable<(double Start, double End, string Kind, double AvgConfidence)> FindSuspectSpans(
    FileResult result, double threshold)
{
    var w = result.Windows;
    var i = 0;
    while (i < w.Count)
    {
        if (w[i].Confidence >= threshold) { i++; continue; }
        var start = i;
        while (i < w.Count && w[i].Confidence < threshold) i++;
        var end = i - 1;
        var kind = start == 0 ? "lead" : end == w.Count - 1 ? "tail" : "interior";
        var avg = 0.0;
        for (var k = start; k <= end; k++) avg += w[k].Confidence;
        avg /= end - start + 1;
        yield return (w[start].TStart, w[end].TStart + MusicDetector.WindowSeconds, kind, avg);
    }
}

/// <summary>
/// Minimal CSV line splitter that respects double-quoted fields (comma-inside-quotes is not a
/// separator; "" inside a quoted field is an escaped quote). Needed because --template-corrections
/// / --suspects-csv always quote the filename field (via <see cref="Csv"/>), and a spreadsheet
/// editor re-saving the file will itself quote (only) fields that need it — e.g. a filename with
/// a literal comma, like "Drums, The - What You Were.mp3" — producing a file with mixed quoted
/// and unquoted rows. A naive Split(',') mis-splits the quoted-comma case.
/// </summary>
static List<string> SplitCsvLine(string line)
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
    return fields;
}

/// <summary>
/// Loads confirmed ground-truth spans from a "file,startSec,endSec,label" CSV (label is
/// music|nonmusic). Fields are split quote-aware (<see cref="SplitCsvLine"/>), then right-anchored
/// (last 3 fields) so a filename containing an UNQUOTED comma is still handled — belt and braces,
/// since either form can occur once a human has edited the file in a spreadsheet.
/// Missing/empty path → no corrections.
/// </summary>
static Dictionary<string, List<(double Start, double End, string Label)>> LoadCorrections(string? path)
{
    var byFile = new Dictionary<string, List<(double, double, string)>>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
        return byFile;

    foreach (var line in File.ReadLines(path).Skip(1)) // skip header
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var parts = SplitCsvLine(line);
        if (parts.Count < 4) continue;
        var label = parts[^1].Trim();
        if (label is not ("music" or "nonmusic")) continue;
        if (!double.TryParse(parts[^2], System.Globalization.CultureInfo.InvariantCulture, out var end)) continue;
        if (!double.TryParse(parts[^3], System.Globalization.CultureInfo.InvariantCulture, out var start)) continue;
        var file = string.Join(',', parts.Take(parts.Count - 3)).Trim();
        if (!byFile.TryGetValue(file, out var list))
            byFile[file] = list = [];
        list.Add((start, end, label));
    }
    return byFile;
}

static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

string? ArgStr(string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

double ArgDouble(string name, double def) =>
    double.TryParse(ArgStr(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
