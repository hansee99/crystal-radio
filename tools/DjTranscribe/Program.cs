// DjTranscribe — does speech recognition separate music from talk where the acoustic detector
// can't? (Side-project PoC; see doc/KNOWN-LIMITATIONS.md and issue #8 for why this exists.)
//
// The premise: MusicDetector fails because electronic music and speech aren't separable in the
// spectral/modulation domain. An ASR model asks a different question — "is anyone speaking, and
// what are they saying" — and its OWN confidence metrics may answer it without reading the
// transcript at all. Whisper reports NoSpeechProbability per segment, which is effectively a
// speech detector trained on orders of magnitude more audio than our 4-feature logistic regression.
//
// Three signatures are expected to differ:
//   talk         → clean, coherent, high-confidence transcript, low no-speech probability
//   sung lyrics  → partial and poor, middling confidence
//   instrumental → empty, or hallucinated repeats (whisper.cpp is known for this on non-speech)
//
// Deliberately samples a few short windows rather than transcribing whole files: the decision is
// one bit, and sampling is what would make this affordable in the app. If it only works on whole
// files it isn't viable during a live harvest.
//
// Usage:
//   dotnet run --project tools/DjTranscribe -- <model.bin> <files-or-folders...> [options]
//     --label music|talk     force a label for scoring (else inferred from the filename)
//     --windows N            windows per file (default 3)
//     --window-seconds N     length of each window (default 10)
//     --language xx          force a language (default: auto-detect per window)
//     --csv <path>           write per-window rows
//     --transcripts          print what was actually heard

using System.Diagnostics;
using System.Globalization;
using System.Text;
using ManagedBass;
using ManagedBass.Aac;
using Whisper.net;

const int WhisperRate = 16_000; // whisper.cpp expects 16 kHz mono float

var argv = args.ToList();
string? Opt(string name)
{
    var i = argv.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}
bool Flag(string name)
{
    var i = argv.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (i < 0) return false;
    argv.RemoveAt(i);
    return true;
}

var forcedLabel = Opt("--label");
var windows = int.TryParse(Opt("--windows"), out var w) ? w : 3;
var windowSeconds = double.TryParse(Opt("--window-seconds"), NumberStyles.Any, CultureInfo.InvariantCulture, out var ws) ? ws : 10;
var language = Opt("--language");
var csvPath = Opt("--csv");
var showTranscripts = Flag("--transcripts");

if (argv.Count < 2)
{
    Console.WriteLine("Usage: DjTranscribe <model.bin> <files-or-folders...> [--label music|talk] "
                      + "[--windows N] [--window-seconds N] [--language xx] [--csv out.csv] [--transcripts]");
    return 1;
}

var modelPath = argv[0];
if (!File.Exists(modelPath))
{
    Console.WriteLine($"model not found: {modelPath}");
    return 1;
}

var files = new List<string>();
foreach (var input in argv.Skip(1))
{
    if (Directory.Exists(input))
        files.AddRange(Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)));
    else if (File.Exists(input))
        files.Add(input);
}
files.Sort(StringComparer.OrdinalIgnoreCase);
if (files.Count == 0) { Console.WriteLine("no audio files found"); return 1; }

if (!Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero))
{
    // device 0 = "no sound"; decoding needs no output device but Init must still succeed.
    Console.WriteLine($"Bass.Init failed: {Bass.LastError}");
    return 1;
}

using var factory = WhisperFactory.FromPath(modelPath);

using var csv = csvPath is null ? null : new StreamWriter(csvPath);
csv?.WriteLine("file,label,windowStart,noSpeechProb,probability,minProbability,textChars,wordCount,language,text");

Console.WriteLine($"model {Path.GetFileName(modelPath)} · {files.Count} file(s) · "
                  + $"{windows}×{windowSeconds:0.#}s windows{(language is null ? " · auto language" : $" · {language}")}");
Console.WriteLine(new string('-', 100));

var rows = new List<Row>();
var clock = Stopwatch.StartNew();

foreach (var file in files)
{
    var name = Path.GetFileNameWithoutExtension(file);
    var label = forcedLabel ?? InferLabel(name);

    var mono = DecodeMono(file, out var rate);
    if (mono is null || rate <= 0)
    {
        Console.WriteLine($"  {Trim(name, 58),-58} decode failed");
        continue;
    }

    var resampled = Resample(mono, rate, WhisperRate);
    var perWindow = new List<WindowResult>();

    foreach (var (start, samples) in SampleWindows(resampled, WhisperRate, windows, windowSeconds))
    {
        var builder = factory.CreateBuilder().WithNoContext();
        builder = language is null ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        await using var processor = builder.Build();

        var text = new StringBuilder();
        double noSpeech = 0, prob = 0, minProb = 0;
        var segments = 0;
        string? lang = null;

        await foreach (var seg in processor.ProcessAsync(samples))
        {
            text.Append(seg.Text);
            noSpeech += seg.NoSpeechProbability;
            prob += seg.Probability;
            minProb += seg.MinProbability;
            lang ??= seg.Language;
            segments++;
        }

        // No segments at all is itself the strongest "not speech" signal whisper gives.
        var result = segments == 0
            ? new WindowResult(start, NoSpeech: 1.0, Probability: 0, MinProbability: 0, "", lang)
            : new WindowResult(start, noSpeech / segments, prob / segments, minProb / segments,
                Collapse(text.ToString()), lang);
        perWindow.Add(result);

        csv?.WriteLine(string.Join(',', Q(name), label, F(start), F(result.NoSpeech), F(result.Probability),
            F(result.MinProbability), result.Text.Length, WordCount(result.Text), result.Language ?? "", Q(result.Text)));
    }

    if (perWindow.Count == 0) continue;

    var row = new Row(name, label,
        perWindow.Average(p => p.NoSpeech),
        perWindow.Average(p => p.Probability),
        perWindow.Sum(p => WordCount(p.Text)) / (double)perWindow.Count,
        perWindow);
    rows.Add(row);

    Console.WriteLine($"  {Trim(name, 58),-58} {label,-5} noSpeech {row.NoSpeech:0.000}  "
                      + $"prob {row.Probability:0.000}  words/win {row.WordsPerWindow:0.0}");
    if (showTranscripts)
        foreach (var p in row.Windows)
            Console.WriteLine($"        {p.Start,5:0}s [{p.Language ?? "??"}] {Trim(p.Text, 84)}");
}

Bass.Free();
Console.WriteLine(new string('-', 100));
Console.WriteLine($"{rows.Count} file(s) in {clock.Elapsed.TotalSeconds:0}s "
                  + $"({clock.Elapsed.TotalSeconds / Math.Max(1, rows.Count):0.0}s per file)");

Report(rows);
if (csvPath is not null) Console.WriteLine($"CSV → {csvPath}");
return 0;

// ---------------------------------------------------------------------------------------------

// Separation is the whole question, so report it directly rather than leaving it to be eyeballed.
static void Report(List<Row> rows)
{
    var music = rows.Where(r => r.Label == "music").ToList();
    var talk = rows.Where(r => r.Label == "talk").ToList();
    if (music.Count == 0 || talk.Count == 0)
    {
        Console.WriteLine("\n(no labelled comparison — need both music and talk files)");
        return;
    }

    Console.WriteLine($"\n=== SEPARATION ===  music n={music.Count}  talk n={talk.Count}");
    foreach (var (metric, get) in new (string, Func<Row, double>)[]
             {
                 ("noSpeechProb", r => r.NoSpeech),
                 ("probability", r => r.Probability),
                 ("words/window", r => r.WordsPerWindow),
             })
    {
        var m = music.Select(get).OrderBy(x => x).ToList();
        var t = talk.Select(get).OrderBy(x => x).ToList();
        Console.WriteLine($"  {metric,-14} music median {Median(m),7:0.000}  [{m[0]:0.000}..{m[^1]:0.000}]   "
                          + $"talk median {Median(t),7:0.000}  [{t[0]:0.000}..{t[^1]:0.000}]");

        // The number that matters: the best single cut, and what it costs.
        var (cut, acc, fp, fn) = BestThreshold(music.Select(get).ToList(), talk.Select(get).ToList());
        Console.WriteLine($"  {"",14} best cut {cut,7:0.000} → {acc * 100,5:0.0}% accuracy "
                          + $"({fp} music misread as talk, {fn} talk misread as music)");
    }
}

// Exhaustive scan over candidate cuts — the corpus is tiny, so there's no reason to be clever.
static (double Cut, double Accuracy, int MusicAsTalk, int TalkAsMusic) BestThreshold(
    List<double> music, List<double> talk)
{
    var all = music.Concat(talk).Distinct().OrderBy(x => x).ToList();
    var best = (Cut: 0.0, Accuracy: -1.0, Fp: 0, Fn: 0);
    var total = music.Count + talk.Count;
    foreach (var cut in all)
    {
        // Convention: talk scores BELOW the cut for noSpeech, above for the others — try both and
        // keep whichever direction separates better, so each metric speaks for itself.
        foreach (var talkIsLower in new[] { true, false })
        {
            var fp = music.Count(v => talkIsLower ? v <= cut : v >= cut);   // music read as talk
            var fn = talk.Count(v => talkIsLower ? v > cut : v < cut);      // talk read as music
            var acc = (total - fp - fn) / (double)total;
            if (acc > best.Accuracy) best = (cut, acc, fp, fn);
        }
    }
    return best;
}

static double Median(List<double> sorted) =>
    sorted.Count == 0 ? 0
    : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
    : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

// Labels come from the harvest naming convention "station · artist - title". A station announcing
// ITSELF as the artist is an ident or a news bulletin — the same signal SongHistoryFilter uses.
static string InferLabel(string name)
{
    var lower = name.ToLowerInvariant();
    foreach (var marker in new[]
             {
                 "hitradio ö3 - livestream", "hitradio ö3 - nachrichten",
                 "1fm30 - 1fm30", "liquid dnb 2 - liquid dnb 2", "somafm begathon",
             })
        if (lower.Contains(marker)) return "talk";
    return "music";
}

static IEnumerable<(double Start, float[] Samples)> SampleWindows(
    float[] mono, int rate, int count, double seconds)
{
    var per = (int)(seconds * rate);
    if (mono.Length <= per)
    {
        yield return (0, mono);
        yield break;
    }

    // Evenly spaced across the file, skipping the very edges: the first and last seconds are where
    // a boundary cut leaves the previous/next item's bleed, which would label the wrong thing.
    var usable = mono.Length - per;
    for (var i = 0; i < count; i++)
    {
        var frac = count == 1 ? 0.5 : 0.1 + 0.8 * i / (count - 1.0);
        var offset = (int)(usable * frac);
        var window = new float[per];
        Array.Copy(mono, offset, window, 0, per);
        yield return (offset / (double)rate, window);
    }
}

// Linear interpolation. Crude for high-fidelity audio, fine here: whisper's front end is a log-mel
// spectrogram and the question is whether anyone is talking, not transcription accuracy.
static float[] Resample(float[] input, int from, int to)
{
    if (from == to) return input;
    var ratio = (double)from / to;
    var outLen = (int)(input.Length / ratio);
    var output = new float[outLen];
    for (var i = 0; i < outLen; i++)
    {
        var src = i * ratio;
        var i0 = (int)src;
        var i1 = Math.Min(i0 + 1, input.Length - 1);
        var frac = src - i0;
        output[i] = (float)(input[i0] * (1 - frac) + input[i1] * frac);
    }
    return output;
}

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
    var channels = Math.Max(1, info.Channels);

    var samples = new List<float>(1 << 20);
    var buffer = new float[16384];
    while (true)
    {
        var bytes = Bass.ChannelGetData(h, buffer, buffer.Length * sizeof(float));
        if (bytes <= 0) break;
        var got = bytes / sizeof(float);
        if (channels == 1)
            for (var i = 0; i < got; i++) samples.Add(buffer[i]);
        else
            for (var i = 0; i + channels <= got; i += channels)
            {
                double sum = 0;
                for (var c = 0; c < channels; c++) sum += buffer[i + c];
                samples.Add((float)(sum / channels));
            }
    }
    Bass.StreamFree(h);
    return samples.Count == 0 ? null : samples.ToArray();
}

static int WordCount(string s) =>
    s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

static string Collapse(string s) =>
    System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

internal record WindowResult(double Start, double NoSpeech, double Probability, double MinProbability,
    string Text, string? Language);

internal record Row(string Name, string Label, double NoSpeech, double Probability,
    double WordsPerWindow, List<WindowResult> Windows);
