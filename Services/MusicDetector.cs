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
    double PulseStrength, // envelope-spectrum peak/mean over 0.5-8 Hz — high for a steady beat
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

    // Pulse/beat measurement. A longer context than the decision window is required — see
    // PulseStrength for why two beats can't establish a period. 0.5-8 Hz is 30-480 BPM.
    private const double PulseContextSeconds = 8.0;
    private const double PulseLowHz = 0.5;
    private const double PulseHighHz = 8.0;

    // Music confidence = sigmoid(bias + Σ wᵢ·featureᵢ). Class-weighted logistic regression over
    // 11 features, fitted on 226 files / ~84.5k windows (tools/DjDetector/corpus/): the original
    // 69 clips (mainstream pop/rock/disco vs radio ads, PSAs, jingle montages and real UK DJ links
    // — the last of which are talk OVER a music bed, the hardest negative), 121 whole harvested
    // songs from real sessions across 15 stations, 21 confirmed electronic tracks the previous fit
    // got wrong, and 15 confirmed Ö3 idents/news bulletins.
    //
    // This fit exists because PulseStrength was added (issue #8) and because the previous corpus
    // had almost no electronic music. Per-FILE verdicts, previous weights → these:
    //     original clips, music      92.6% → 96.3%
    //     original clips, non-music  92.9% → 97.6%
    //     121 real harvested songs   59.5% → 95.0%
    //     21 electronic tracks        9.5% → 81.0%
    //     15 Ö3 idents/news         100.0% → 100.0%
    // Nothing was traded away: talk rejection improved alongside music recognition.
    //
    // Generalisation, honestly: refitting with the electronic set held out ENTIRELY still moves it
    // from 10% to 52%, so the gain is the feature and not memorisation — but a genre absent from
    // the corpus is still recognised far worse than one present in it. Per-window 5-fold
    // file-grouped CV is 83.5% ± 2.9 (music 83.2% ± 3.4, non-music 83.9% ± 5.2): lower variance
    // and better balance than the previous fit's ± 3.8 / ± 7.3 / ± 7.5, though the headline number
    // is not comparable across different corpora. Per-window accuracy also understates per-file
    // verdicts, which is what QC actually uses.
    //
    // Class weighting (inverse label frequency) matters more than before: the corpus is now ~6:1
    // music:non-music by window count.
    // Re-fit with fit_logreg.py as the corpus grows — more non-music diversity is still the
    // highest-value addition.
    internal const double MusicThreshold = 0.5;   // confidence ≥ this ⇒ music

    /// <summary>Consecutive music windows the edge-trim scan needs before it stops. Measured; see
    /// the comment at the scan in <c>Summarize</c> for why it's 2 and not 1 or 6.</summary>
    private const int TrimStopRun = 2;

    private const double LrBias = 2.5001012;
    private const double Lr_Mod4Hz = -4.1045931;
    private const double Lr_ZcrMean = 0.00063936365;
    private const double Lr_ZcrVar = -1.0468556e-07;
    private const double Lr_LowEnergyRatio = -5.5053692;
    private const double Lr_FluxMean = -28.189889;
    private const double Lr_FluxVar = 66.569986;
    private const double Lr_CentroidMean = -0.00059962118;
    private const double Lr_CentroidVar = -3.3100546e-07;
    private const double Lr_RolloffMean = 0.00012923647;
    private const double Lr_Flatness = 5.5935045e-09;
    private const double Lr_PulseStrength = 1.1315929;

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

            var pulse = PulseStrength(energy, lo, hi, envelopeRate);

            var confidence = Confidence(mod4, zMean, zVar, lowE, fMean, fVar, cMean, cVar, rMean, flMean, pulse);
            windows.Add(new WindowFeatures(
                w * HopSeconds, mod4, zMean, zVar, lowE, fMean, fVar, cMean, cVar, rMean, flMean,
                pulse, confidence));
        }

        return Summarize(windows);
    }

    /// <summary>
    /// How strongly periodic the amplitude envelope is in the beat range — the peak of the
    /// envelope spectrum over 0.5–8 Hz (30–480 BPM) divided by that band's mean. Scale-free, so
    /// loudness doesn't enter. Music has a beat and should peak sharply; speech has syllables but
    /// no steady period and should stay flat.
    ///
    /// <para>The feature the existing set lacks. Every current feature measures how energy is
    /// <i>distributed</i> (spectral shape, zero crossings, how much sits near 4 Hz); none measures
    /// whether it <i>repeats</i>. That's why electronic music and speech aren't separable here —
    /// they can look alike on distribution while differing completely on periodicity. Measured:
    /// a five-minute news bulletin and an Ace of Base single both score 3% music.</para>
    ///
    /// <para><b>Uses a longer context than the decision window on purpose.</b> A 1-second window
    /// holds about two beats at 120 BPM, and periodicity can't be established from two cycles —
    /// the FFT peak would be noise. This takes <see cref="PulseContextSeconds"/> centred on the
    /// window, which at a 100 Hz envelope rate gives ~0.1 Hz bins and roughly sixteen beats to
    /// measure. Adjacent windows share most of that context, so the feature is smooth across them,
    /// which is right: a beat is a property of a passage, not of a one-second slice.</para>
    /// </summary>
    private static double PulseStrength(double[] energy, int lo, int hi, double envelopeRate)
    {
        var contextFrames = (int)Math.Round(PulseContextSeconds * envelopeRate);
        var centre = (lo + hi) / 2;
        var from = Math.Max(0, centre - contextFrames / 2);
        var to = Math.Min(energy.Length, from + contextFrames);
        from = Math.Max(0, to - contextFrames);   // pull back if we ran off the end
        var n = to - from;
        if (n < 16) return 0;                     // too little context to say anything

        var env = new double[n];
        double mean = 0;
        for (var i = 0; i < n; i++) { env[i] = energy[from + i]; mean += env[i]; }
        mean /= n;
        for (var i = 0; i < n; i++) env[i] -= mean; // DC would dominate the peak

        var mag = Fft.Magnitude(env);
        var binHz = envelopeRate / Fft.NextPow2(n);

        double peak = 0, sum = 0;
        var bins = 0;
        for (var b = 1; b < mag.Length; b++)
        {
            var hz = b * binHz;
            if (hz < PulseLowHz) continue;
            if (hz > PulseHighHz) break;
            sum += mag[b];
            bins++;
            if (mag[b] > peak) peak = mag[b];
        }
        if (bins == 0 || sum <= 0) return 0;
        return peak / (sum / bins);
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
        double fluxMean, double fluxVar, double centroidMean, double centroidVar, double rolloffMean,
        double flatness, double pulseStrength)
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
            Lr_Flatness * flatness +
            Lr_PulseStrength * pulseStrength;
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
        // A single music-looking window must not stop the scan. It used to: one window at 0.528
        // at the very end of a segment left a 70-second talk outro completely untrimmed, and the
        // same thing at the front is why Ö3 news bulletins reported lead trims of 0.5s and kept
        // five minutes of speech. The scan now needs TrimStopRun windows in a row before it
        // accepts that music has started.
        //
        // 2 is measured, not guessed: across 121 real songs and 15 confirmed talk segments it
        // doubles the talk caught (5 → 10 of 15 trimmed away entirely, median trim 23.5s → 35.5s)
        // while leaving the median song trim at 0.0s. Higher values buy almost no extra talk and
        // start eating real songs — at 6, seven songs lose over 30 seconds.
        var lead = LeadingNonMusicWindows(windows);
        var tail = Math.Min(TrailingNonMusicWindows(windows), windows.Count - lead);

        var verdict = frac >= 0.85 ? "MUSIC" : frac >= 0.4 ? "MIXED" : "TALK";
        // Windows overlap 50%, so each advances WindowHopSeconds of audio.
        return new FileResult(windows, frac, lead * WindowHopSeconds, tail * WindowHopSeconds, verdict);
    }

    /// <summary>
    /// Windows before music sustains itself — i.e. the index of the first run of
    /// <see cref="TrimStopRun"/> consecutive music windows. Returns the whole count when music
    /// never sustains, which the caller bounds against the tail scan.
    /// </summary>
    private static int LeadingNonMusicWindows(List<WindowFeatures> windows)
    {
        for (var i = 0; i + TrimStopRun <= windows.Count; i++)
            if (IsSustainedMusicAt(windows, i))
                return i;
        return windows.Count;
    }

    /// <summary>The same scan from the end: trailing windows before music sustains itself.</summary>
    private static int TrailingNonMusicWindows(List<WindowFeatures> windows)
    {
        for (var t = 0; t + TrimStopRun <= windows.Count; t++)
            if (IsSustainedMusicAt(windows, windows.Count - t - TrimStopRun))
                return t;
        return windows.Count;
    }

    private static bool IsSustainedMusicAt(List<WindowFeatures> windows, int start)
    {
        for (var j = 0; j < TrimStopRun; j++)
            if (windows[start + j].Confidence < MusicThreshold)
                return false;
        return true;
    }

    /// <summary>CSV header matching <see cref="WindowFeatures"/> (plus file + label columns added by the caller).</summary>
    public static string CsvHeader =>
        "file,label,tStart,mod4Hz,zcrMean,zcrVar,lowEnergyRatio,fluxMean,fluxVar,centroidMean,centroidVar,rolloffMean,flatness,pulseStrength,confidence";

    public static string CsvRow(string file, string label, WindowFeatures w) =>
        string.Join(',', file, label,
            F(w.TStart), F(w.Mod4Hz), F(w.ZcrMean), F(w.ZcrVar), F(w.LowEnergyRatio),
            F(w.FluxMean), F(w.FluxVar), F(w.CentroidMean), F(w.CentroidVar), F(w.RolloffMean),
            F(w.Flatness), F(w.PulseStrength), F(w.Confidence));

    private static string F(double v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}
