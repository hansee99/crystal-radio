namespace DjDetector;

/// <summary>
/// Minimal in-place iterative radix-2 Cooley–Tukey FFT (power-of-two sizes only). Kept
/// dependency-free and self-contained so the same code can move into the live engine later
/// (DJ-MODE-SPEC §6.4 wants a managed FFT over the delay buffer, not BASS's consuming FFT).
/// </summary>
internal static class Fft
{
    /// <summary>Smallest power of two ≥ <paramref name="n"/>.</summary>
    public static int NextPow2(int n)
    {
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// In-place complex FFT. <paramref name="re"/>/<paramref name="im"/> are length N (a power
    /// of two); on return they hold the transform. Forward transform (no normalization).
    /// </summary>
    public static void Forward(double[] re, double[] im)
    {
        var n = re.Length;
        if (n <= 1) return;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Butterflies.
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;
            var wRe = Math.Cos(ang);
            var wIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1.0, curIm = 0.0;
                for (var k = 0; k < len / 2; k++)
                {
                    var aRe = re[i + k];
                    var aIm = im[i + k];
                    var bRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var bIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
                    re[i + k] = aRe + bRe;
                    im[i + k] = aIm + bIm;
                    re[i + k + len / 2] = aRe - bRe;
                    im[i + k + len / 2] = aIm - bIm;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    /// <summary>
    /// Magnitude spectrum (bins 0..N/2) of a real signal. The input is copied into a
    /// power-of-two buffer (zero-padded); a Hann window is applied first when
    /// <paramref name="hann"/> is set.
    /// </summary>
    public static double[] Magnitude(ReadOnlySpan<float> signal, bool hann = true)
    {
        var n = NextPow2(signal.Length);
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < signal.Length; i++)
        {
            var w = hann ? 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (signal.Length - 1)) : 1.0;
            re[i] = signal[i] * w;
        }
        Forward(re, im);

        var half = n / 2;
        var mag = new double[half + 1];
        for (var i = 0; i <= half; i++)
            mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return mag;
    }

    /// <summary>Magnitude spectrum of a real double signal (used for the amplitude envelope's
    /// modulation spectrum). No windowing — the envelope is already short and mean-removed.</summary>
    public static double[] Magnitude(double[] signal)
    {
        var n = NextPow2(signal.Length);
        var re = new double[n];
        var im = new double[n];
        Array.Copy(signal, re, signal.Length);
        Forward(re, im);

        var half = n / 2;
        var mag = new double[half + 1];
        for (var i = 0; i <= half; i++)
            mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return mag;
    }
}
