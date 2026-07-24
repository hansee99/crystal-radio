namespace DjDetector;

/// <summary>Per-analysis-window features + the derived music confidence (0 = speech/ad, 1 = music).</summary>
internal sealed record WindowFeatures(
    double TStart,
    double Mod4Hz,        // energy of the amplitude envelope near ~4 Hz (syllabic rate) — high for speech
    double ZcrMean,
    double ZcrVar,
    double LowEnergyRatio,
    double FluxMean,
    double FluxVar,
    double CentroidMean,
    double CentroidVar,
    double RolloffMean,
    double Flatness,
    double Confidence);

/// <summary>Whole-file result: per-window features + a verdict and the music span (edge-trim).</summary>
internal sealed record FileResult(
    IReadOnlyList<WindowFeatures> Windows,
    double MusicFraction,     // fraction of windows classified music
    double LeadTalkSeconds,   // non-music run at the start (trim this off the front)
    double TailTalkSeconds,   // non-music run at the end (trim this off the back)
    string Verdict);          // MUSIC / MIXED / TALK

/// <summary>
/// Offline music/speech discriminator (Scheirer–Slaney lineage). Operates on mono float PCM —
/// no BASS, no realtime constraint — so it's equally the harvest QC/edge-trim tool and, later,
/// the core of the live engine's detector. Features and weights are intentionally explicit and
/// tunable; the harness also dumps them to CSV so the weights below can be fitted to real data.
/// </summary>
internal sealed class MusicDetector
{
    // Framing.
    private const double FrameSeconds = 0.025;   // 25 ms analysis frame
    private const double HopSeconds = 0.010;      // 10 ms hop → ~100 frames/sec envelope
    private const double WindowSeconds = 1.0;     // 1 s decision window
    private const double WindowHopSeconds = 0.5;  // 50% overlap

    // Confidence rule (music = 1). A speech score is a weighted sum of normalized features;
    // music confidence = 1 - sigmoid(score - Bias). Higher on speechy features → lower music
    // confidence.
    //
    // Scales are centered on a real harvested-music corpus (11.7k windows) so a typical music
    // window normalizes each feature to ≈1; weights then lean on the cleanest discriminator,
    // low-energy-frame ratio (speech has inter-word pauses, music is sustained). These are still
    // provisional — verified to classify MUSIC correctly, but the speech/talk side of the
    // boundary needs labelled non-music clips (or a logistic fit on the CSV) to confirm.
    private const double W_Mod4Hz = 1.2;
    private const double W_ZcrVar = 0.6;
    private const double W_LowEnergy = 2.2;
    private const double W_FluxVar = 0.3;
    private const double W_CentroidVar = 0.4;
    private const double W_Flatness = 0.4;
    private const double Bias = 4.0;              // higher → more readily called music
    private const double MusicThreshold = 0.5;    // confidence ≥ this ⇒ music

    // Normalization scales — the observed music-corpus means (lowEnergy widened so ordinary
    // quiet music passages don't trip the speech side; only genuine pause-heavy windows do).
    private const double S_Mod4Hz = 0.15;
    private const double S_ZcrVar = 1.5e6;
    private const double S_LowEnergy = 0.15;
    private const double S_FluxVar = 0.010;
    private const double S_CentroidVar = 6.0e5;
    private const double S_Flatness = 0.015;

    public FileResult Analyze(float[] mono, int sampleRate)
    {
        var frameLen = Math.Max(1, (int)Math.Round(FrameSeconds * sampleRate));
        var hop = Math.Max(1, (int)Math.Round(HopSeconds * sampleRate));

        // --- Per-frame features ---
        var frameCount = mono.Length >= frameLen ? 1 + (mono.Length - frameLen) / hop : 0;
        var energy = new double[frameCount];
        var zcr = new double[frameCount];
        var centroid = new double[frameCount];
        var rolloff = new double[frameCount];
        var flatness = new double[frameCount];
        var flux = new double[frameCount];
        double[]? prevSpec = null;

        for (var f = 0; f < frameCount; f++)
        {
            var start = f * hop;
            var frame = mono.AsSpan(start, frameLen);

            // RMS energy.
            double sumSq = 0;
            for (var i = 0; i < frame.Length; i++) sumSq += frame[i] * (double)frame[i];
            energy[f] = Math.Sqrt(sumSq / frame.Length);

            // Zero-crossing rate (crossings per second).
            var crossings = 0;
            for (var i = 1; i < frame.Length; i++)
                if ((frame[i] >= 0) != (frame[i - 1] >= 0)) crossings++;
            zcr[f] = crossings * (double)sampleRate / frame.Length;

            // Magnitude spectrum → centroid, rolloff(85%), flatness, flux.
            var spec = Fft.Magnitude(frame, hann: true);
            var binHz = sampleRate / (double)Fft.NextPow2(frameLen);

            double magSum = 0, weighted = 0, logSum = 0;
            var flat = 0;
            for (var b = 0; b < spec.Length; b++)
            {
                magSum += spec[b];
                weighted += spec[b] * (b * binHz);
                logSum += Math.Log(spec[b] + 1e-12);
                if (spec[b] > 0) flat++;
            }
            centroid[f] = magSum > 0 ? weighted / magSum : 0;

            var roll = 0.85 * magSum;
            double acc = 0;
            var rb = 0;
            for (; rb < spec.Length && acc < roll; rb++) acc += spec[rb];
            rolloff[f] = rb * binHz;

            var geo = Math.Exp(logSum / spec.Length);
            var arith = magSum / spec.Length;
            flatness[f] = arith > 0 ? geo / arith : 0;

            if (prevSpec is not null)
            {
                double d = 0;
                var m = Math.Min(spec.Length, prevSpec.Length);
                for (var b = 0; b < m; b++)
                {
                    var diff = spec[b] - prevSpec[b];
                    if (diff > 0) d += diff;
                }
                flux[f] = magSum > 0 ? d / magSum : 0; // normalized positive flux
            }
            prevSpec = spec;
        }

        // --- Per-window aggregation ---
        var framesPerWindow = Math.Max(1, (int)Math.Round(WindowSeconds / HopSeconds));
        var windowHopFrames = Math.Max(1, (int)Math.Round(WindowHopSeconds / HopSeconds));
        var envelopeRate = 1.0 / HopSeconds; // Hz at which the energy envelope is sampled

        var windows = new List<WindowFeatures>();
        for (var w = 0; w + framesPerWindow <= frameCount; w += windowHopFrames)
        {
            var lo = w;
            var hi = w + framesPerWindow;

            var mod4 = Modulation4Hz(energy, lo, hi, envelopeRate);
            var (zMean, zVar) = MeanVar(zcr, lo, hi);
            var lowE = LowEnergyRatio(energy, lo, hi);
            var (fMean, fVar) = MeanVar(flux, lo, hi);
            var (cMean, cVar) = MeanVar(centroid, lo, hi);
            var (rMean, _) = MeanVar(rolloff, lo, hi);
            var (flMean, _) = MeanVar(flatness, lo, hi);

            var confidence = Confidence(mod4, zVar, lowE, fVar, cVar, flMean);
            windows.Add(new WindowFeatures(
                w * HopSeconds, mod4, zMean, zVar, lowE, fMean, fVar, cMean, cVar, rMean, flMean, confidence));
        }

        return Summarize(windows);
    }

    /// <summary>Energy of the amplitude envelope in a 3–5 Hz band (the ~4 Hz syllabic rate),
    /// relative to the envelope's total AC energy. High for speech, low for sustained music.</summary>
    private static double Modulation4Hz(double[] energy, int lo, int hi, double envelopeRate)
    {
        var n = hi - lo;
        var env = new double[n];
        double mean = 0;
        for (var i = 0; i < n; i++) { env[i] = energy[lo + i]; mean += env[i]; }
        mean /= n;
        for (var i = 0; i < n; i++) env[i] -= mean; // remove DC so the ratio is AC-only

        var mag = Fft.Magnitude(env);
        var fftN = Fft.NextPow2(n);
        var binHz = envelopeRate / fftN;

        double band = 0, total = 0;
        for (var b = 1; b < mag.Length; b++) // skip DC
        {
            var hz = b * binHz;
            total += mag[b];
            if (hz is >= 3.0 and <= 5.0) band += mag[b];
        }
        return total > 0 ? band / total : 0;
    }

    private static double LowEnergyRatio(double[] energy, int lo, int hi)
    {
        double mean = 0;
        for (var i = lo; i < hi; i++) mean += energy[i];
        mean /= (hi - lo);
        var low = 0;
        for (var i = lo; i < hi; i++)
            if (energy[i] < 0.5 * mean) low++;
        return low / (double)(hi - lo);
    }

    private static (double mean, double var) MeanVar(double[] a, int lo, int hi)
    {
        double mean = 0;
        for (var i = lo; i < hi; i++) mean += a[i];
        mean /= (hi - lo);
        double v = 0;
        for (var i = lo; i < hi; i++) { var d = a[i] - mean; v += d * d; }
        v /= (hi - lo);
        return (mean, v);
    }

    private static double Confidence(double mod4, double zVar, double lowE, double fVar, double cVar, double flatness)
    {
        var speech =
            W_Mod4Hz * (mod4 / S_Mod4Hz) +
            W_ZcrVar * (zVar / S_ZcrVar) +
            W_LowEnergy * (lowE / S_LowEnergy) +
            W_FluxVar * (fVar / S_FluxVar) +
            W_CentroidVar * (cVar / S_CentroidVar) +
            W_Flatness * (flatness / S_Flatness) -
            Bias;
        return 1.0 - 1.0 / (1.0 + Math.Exp(-speech)); // music confidence
    }

    private static FileResult Summarize(List<WindowFeatures> windows)
    {
        if (windows.Count == 0)
            return new FileResult(windows, 0, 0, 0, "EMPTY");

        var musicCount = windows.Count(w => w.Confidence >= MusicThreshold);
        var frac = musicCount / (double)windows.Count;

        // Leading / trailing non-music runs → edge-trim suggestion.
        var lead = 0;
        while (lead < windows.Count && windows[lead].Confidence < MusicThreshold) lead++;
        var tail = 0;
        while (tail < windows.Count && windows[^(tail + 1)].Confidence < MusicThreshold) tail++;

        var verdict = frac >= 0.85 ? "MUSIC" : frac >= 0.4 ? "MIXED" : "TALK";
        // Windows overlap 50%, so each advances WindowHopSeconds of audio.
        return new FileResult(windows, frac, lead * WindowHopSeconds, tail * WindowHopSeconds, verdict);
    }

    /// <summary>CSV header matching <see cref="WindowFeatures"/> (plus file + label columns added by the caller).</summary>
    public static string CsvHeader =>
        "file,label,tStart,mod4Hz,zcrMean,zcrVar,lowEnergyRatio,fluxMean,fluxVar,centroidMean,centroidVar,rolloffMean,flatness,confidence";

    public static string CsvRow(string file, string label, WindowFeatures w) =>
        string.Join(',', file, label,
            F(w.TStart), F(w.Mod4Hz), F(w.ZcrMean), F(w.ZcrVar), F(w.LowEnergyRatio),
            F(w.FluxMean), F(w.FluxVar), F(w.CentroidMean), F(w.CentroidVar), F(w.RolloffMean),
            F(w.Flatness), F(w.Confidence));

    private static string F(double v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}
