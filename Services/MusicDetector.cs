namespace RadioPlayer.Services;

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
/// no BASS, no realtime constraint — promoted from tools/DjDetector into the app as the DJ-mode
/// segment QC + edge-trim backstop (<see cref="SegmentQualityChecker"/>). Features and weights
/// are intentionally explicit and tunable; tools/DjDetector's harness still dumps them to CSV so
/// the weights below can be re-fitted as the labelled corpus grows.
/// </summary>
internal sealed class MusicDetector
{
    // Framing.
    private const double FrameSeconds = 0.025;   // 25 ms analysis frame
    private const double HopSeconds = 0.010;      // 10 ms hop → ~100 frames/sec envelope
    // Exposed (internal) so callers can turn WindowFeatures.TStart runs into real timestamps —
    // e.g. the harness's suspect-span report.
    internal const double WindowSeconds = 1.0;    // 1 s decision window
    private const double WindowHopSeconds = 0.5;  // 50% overlap

    // Music confidence = sigmoid(bias + Σ wᵢ·featureᵢ). A class-weighted logistic regression
    // fitted to a labelled corpus of 106 clips (~41.6k windows): the original 69-clip corpus
    // (mainstream pop/rock/disco + ads/talk/jingles) plus 37 whole harvested songs across
    // DroneZone/Groove Salad/indiepop/Radio Paradise, added specifically to close a genre gap —
    // quiet/sparse downtempo and indie tracks were misread as speech-like by the first fit.
    // Class weighting (inverse label frequency) keeps the now-3:1 music:nonmusic window ratio
    // from skewing the boundary toward "music" by default.
    // Honest estimate: 5-fold file-grouped CV, 85.4% overall ± 3.8 (music 85.3% ± 7.3, non-music
    // 84.2% ± 7.5) — a small corpus (106 files) means real fold-to-fold variance; comparable to
    // the prior fit (84.4% ± 4.9) but measurably more balanced across classes.
    // Re-fit with tools/DjDetector's fit_logreg.py as the corpus grows (more nonmusic diversity
    // — not just more music — is the highest-value next addition).
    internal const double MusicThreshold = 0.5;   // confidence ≥ this ⇒ music

    private const double LrBias = 8.7579313;
    private const double Lr_Mod4Hz = -0.91245206;
    private const double Lr_ZcrMean = 0.0013854667;
    private const double Lr_ZcrVar = -1.474332e-07;
    private const double Lr_LowEnergyRatio = -5.0558876;
    private const double Lr_FluxMean = -25.067923;
    private const double Lr_FluxVar = -15.693825;
    private const double Lr_CentroidMean = -0.0012438604;
    private const double Lr_CentroidVar = -8.0104721e-07;
    private const double Lr_RolloffMean = -0.00026307206;
    private const double Lr_Flatness = 9.9606074e-09;

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

            var confidence = Confidence(mod4, zMean, zVar, lowE, fMean, fVar, cMean, cVar, rMean, flMean);
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

    private static double Confidence(double mod4Hz, double zcrMean, double zcrVar, double lowEnergyRatio,
        double fluxMean, double fluxVar, double centroidMean, double centroidVar, double rolloffMean, double flatness)
    {
        var logit =
            LrBias +
            Lr_Mod4Hz * mod4Hz +
            Lr_ZcrMean * zcrMean +
            Lr_ZcrVar * zcrVar +
            Lr_LowEnergyRatio * lowEnergyRatio +
            Lr_FluxMean * fluxMean +
            Lr_FluxVar * fluxVar +
            Lr_CentroidMean * centroidMean +
            Lr_CentroidVar * centroidVar +
            Lr_RolloffMean * rolloffMean +
            Lr_Flatness * flatness;
        return 1.0 / (1.0 + Math.Exp(-logit)); // music confidence
    }

    private static FileResult Summarize(List<WindowFeatures> windows)
    {
        if (windows.Count == 0)
            return new FileResult(windows, 0, 0, 0, "EMPTY");

        var musicCount = windows.Count(w => w.Confidence >= MusicThreshold);
        var frac = musicCount / (double)windows.Count;

        // Leading / trailing non-music runs → edge-trim suggestion. The tail scan stops where the
        // lead scan finished: without that bound, a file the detector reads as non-music
        // THROUGHOUT reports lead = tail = the whole file, i.e. a trim of twice its own length.
        // (Observed: a 3:20 segment reporting 196s + 196s. TryTrimAndCopy's sanity guard caught
        // it and fell back to a plain copy, so nothing was damaged — but the measurement was
        // nonsense, and it fed the session log's trim totals.)
        var lead = 0;
        while (lead < windows.Count && windows[lead].Confidence < MusicThreshold) lead++;
        var tail = 0;
        while (tail < windows.Count - lead && windows[^(tail + 1)].Confidence < MusicThreshold) tail++;

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
