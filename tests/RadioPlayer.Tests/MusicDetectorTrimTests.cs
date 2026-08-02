using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Covers the edge-trim measurement, which decides how much audio gets cut off the front and back
/// of a kept segment. Signals are synthesised rather than decoded from fixtures.
///
/// Note on what counts as "non-music" here: digital silence does NOT. With every feature at zero
/// the model is left with its +8.76 bias, so pure silence scores ~0.9998 — music. The speech-like
/// signal below drives the three features that actually carry negative weight (4 Hz envelope
/// modulation, low-energy-frame ratio, spectral flux) and is what the detector really rejects.
/// </summary>
public class MusicDetectorTrimTests
{
    private const int Rate = 22050;
    private const double Seconds = 24;

    private static float[] Buffer() => new float[(int)(Seconds * Rate)];

    /// <summary>Sustained tone: steady energy, low flux, no syllabic pulse — reads as music.</summary>
    private static void FillMusic(float[] buffer, double fromSec, double toSec)
    {
        var (from, to) = Range(buffer, fromSec, toSec);
        for (var i = from; i < to; i++)
            buffer[i] = 0.5f * MathF.Sin(2 * MathF.PI * 220f * i / Rate);
    }

    /// <summary>Noise bursting at ~4 Hz with gaps between: the syllabic rate, gappy, and
    /// spectrally churning — everything the model was fitted to call speech.</summary>
    private static void FillSpeechLike(float[] buffer, double fromSec, double toSec)
    {
        var (from, to) = Range(buffer, fromSec, toSec);
        var rng = new Random(1234); // deterministic
        var period = Rate / 4;      // 4 bursts per second
        for (var i = from; i < to; i++)
            buffer[i] = i % period < period / 2
                ? (float)(rng.NextDouble() * 2 - 1) * 0.5f
                : 0f;
    }

    private static (int From, int To) Range(float[] buffer, double fromSec, double toSec) =>
        ((int)(fromSec * Rate), Math.Min((int)(toSec * Rate), buffer.Length));

    [Fact]
    public void AnAllNonMusicFile_CannotReportTrimmingMoreThanItsOwnLength()
    {
        // The bug this guards: lead and tail were scanned independently, so a file read as
        // non-music THROUGHOUT reported lead = tail = the whole file — a trim of twice its own
        // duration. Seen live as a 3:20 segment claiming 196s + 196s.
        var audio = Buffer();
        FillSpeechLike(audio, 0, Seconds);

        var result = new MusicDetector().Analyze(audio, Rate);

        Assert.True(result.MusicFraction < 0.5, "signal should read as non-music for this to test anything");
        Assert.True(result.LeadTalkSeconds + result.TailTalkSeconds <= Seconds,
            $"trim ({result.LeadTalkSeconds}s + {result.TailTalkSeconds}s) exceeds the {Seconds}s file");
    }

    [Fact]
    public void LeadTrimStopsAtTheFirstMusic()
    {
        var audio = Buffer();
        FillSpeechLike(audio, 0, 6);
        FillMusic(audio, 6, Seconds);

        var result = new MusicDetector().Analyze(audio, Rate);

        Assert.InRange(result.LeadTalkSeconds, 3, 9); // ~6s, allowing for 1s windows at 50% overlap
        Assert.Equal(0, result.TailTalkSeconds);
    }

    [Fact]
    public void AFullyMusicalFileIsNotTrimmedAtAll()
    {
        var audio = Buffer();
        FillMusic(audio, 0, Seconds);

        var result = new MusicDetector().Analyze(audio, Rate);

        Assert.Equal(0, result.LeadTalkSeconds);
        Assert.Equal(0, result.TailTalkSeconds);
    }
}
