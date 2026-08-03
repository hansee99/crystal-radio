using System.IO;
using ManagedBass;
using ManagedBass.Aac;

namespace RadioPlayer.Services;

/// <summary>The result of running one captured segment through <see cref="SegmentQualityChecker"/>.</summary>
public sealed record SegmentVerdict(
    bool Kept, string Verdict, double MusicPercent, double LeadTrimSeconds, double TailTrimSeconds, string? Note);

/// <summary>
/// Offline, whole-file segment QC backstop for DJ Mode harvesting (DJ-MODE-SPEC-HARVEST.md §6.4):
/// the boundary-cut + ICY-metadata filter is the primary purity guarantee, so this never runs
/// under real-time pressure — it decodes a completed segment, classifies it with
/// <see cref="MusicDetector"/>, edge-trims the non-music run at each end, and drops what isn't
/// a song. Promoted from tools/DjHarvest's <c>Qc</c> PoC class.
///
/// <para><b>Duration first, music fraction second.</b> The whole-file fraction was originally
/// the only gate, and it failed badly on everything percussive-electronic: 92% of a deep-house
/// session was rejected, and tracks confirmed by ear to be perfectly good scored 0.00 — the same
/// as a confirmed advert. Two classes occupying one point cannot be separated by any threshold,
/// so it was turned off. The detector has since gained pulse strength and been re-fitted
/// (electronic recall 9.5% → 81.0%), which pulls those two classes apart again, so the floor is
/// back on at a deliberately low 0.20 (see <c>DjMusicFractionFloor</c>) as a backstop under the
/// duration gate — not as the primary check.</para>
///
/// <para>The edge-trim, by contrast, is doing real work — it correctly stripped a talk outro
/// from a track a listener confirmed. It measures a LOCAL run of non-music rather than averaging
/// over the whole file, which is why it survives material the fraction can't judge. So the trim
/// now drives the decision too: whatever is left after trimming has to be long enough to be a
/// song. An ad break trims away to nothing; a song with a talk outro keeps its music.</para>
/// </summary>
public sealed class SegmentQualityChecker
{
    private readonly MusicDetector _detector = new();
    private readonly double _rejectBelow;
    private readonly double _minKeptSeconds;
    private readonly bool _trimEdges;

    /// <param name="rejectBelow">Reject a segment whose whole-file music fraction falls below
    /// this. 0 disables it; the shipping default is 0.20 — see the class remarks.</param>
    /// <param name="trimEdges">Apply the detector's lead/tail edge-trim to kept segments'
    /// audio bytes rather than just measuring it.</param>
    /// <param name="minKeptSeconds">Minimum audio remaining AFTER the edge-trim for a segment to
    /// count as a song. The primary gate.</param>
    public SegmentQualityChecker(double rejectBelow = 0, bool trimEdges = true,
        double minKeptSeconds = 60)
    {
        _rejectBelow = rejectBelow;
        _minKeptSeconds = minKeptSeconds;
        _trimEdges = trimEdges;
    }

    /// <summary>
    /// How much audio survives the edge-trim. This is what decides keep/reject: the trim measures
    /// a LOCAL run of non-music at each end and does it well, whereas the whole-file music
    /// fraction cannot tell a deep-house track from an advert (both have scored 0.00 on real
    /// material). An ad break trims away to nothing; a song with a talk outro keeps its music.
    /// </summary>
    internal static double KeptSeconds(FileResult res)
    {
        if (res.Windows.Count == 0)
            return 0;
        var total = res.Windows[^1].TStart + MusicDetector.WindowSeconds;
        return Math.Max(0, total - res.LeadTalkSeconds - res.TailTalkSeconds);
    }

    /// <summary>
    /// Decodes <paramref name="sourcePath"/>, classifies it, and — if kept — writes the
    /// (possibly edge-trimmed) audio to <paramref name="destPath"/>. Never throws; decode/IO
    /// failures come back as a not-kept verdict. Runs on a background thread (BASS decode is
    /// synchronous) — safe to await from the UI thread.
    /// </summary>
    public Task<SegmentVerdict> EvaluateAsync(string sourcePath, string destPath, CancellationToken ct = default) =>
        Task.Run(() => Evaluate(sourcePath, destPath), ct);

    private SegmentVerdict Evaluate(string sourcePath, string destPath)
    {
        try
        {
            var mono = DecodeMono(sourcePath, out var rate);
            if (mono is null || mono.Length == 0)
                return new SegmentVerdict(false, "EMPTY", 0, 0, 0, "decode failed");

            var res = _detector.Analyze(mono, rate);
            var pct = res.MusicFraction * 100;

            // Primary gate: is there enough music left once the non-music edges come off?
            var kept = KeptSeconds(res);
            if (kept < _minKeptSeconds)
                return new SegmentVerdict(false, res.Verdict, pct, res.LeadTalkSeconds,
                    res.TailTalkSeconds, $"only {kept:0}s of music");

            // Secondary gate, off by default: the whole-file fraction can't separate the classes
            // on real material, so it stays available for A/B work but doesn't ship enabled.
            if (_rejectBelow > 0 && res.MusicFraction < _rejectBelow)
                return new SegmentVerdict(false, res.Verdict, pct, res.LeadTalkSeconds,
                    res.TailTalkSeconds, $"below the {_rejectBelow:P0} music floor");

            var didTrim = _trimEdges
                && (res.LeadTalkSeconds > 0 || res.TailTalkSeconds > 0)
                && TryTrimAndCopy(sourcePath, destPath, res);
            if (!didTrim)
                File.Copy(sourcePath, destPath, overwrite: true);

            return new SegmentVerdict(true, res.Verdict, pct, res.LeadTalkSeconds, res.TailTalkSeconds,
                didTrim ? "trim applied" : null);
        }
        catch (Exception ex)
        {
            return new SegmentVerdict(false, "ERROR", 0, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// Applies the detector's edge-trim suggestion to the actual audio bytes: chops
    /// <see cref="FileResult.LeadTalkSeconds"/>/<see cref="FileResult.TailTalkSeconds"/> off the
    /// front/back of the captured (constant-bitrate) file. Byte rate is derived from this file's
    /// own size ÷ its analyzed duration — self-consistent for the constant-bitrate MP3/AAC
    /// streams harvesters capture; validated against a real captured file (exact expected
    /// duration reduction, clean re-decode, detector confirms the trimmed talk is gone). The cut
    /// isn't frame-boundary-exact (no MP3/ADTS frame parsing), so expect an occasional
    /// barely-audible micro-glitch right at the trim point — acceptable for this pass.
    /// </summary>
    private static bool TryTrimAndCopy(string src, string dst, FileResult res)
    {
        if (res.Windows.Count == 0)
            return false;
        var totalBytes = new FileInfo(src).Length;
        if (totalBytes <= 0)
            return false;

        var totalSeconds = res.Windows[^1].TStart + MusicDetector.WindowSeconds;
        if (totalSeconds <= 0)
            return false;

        var bytesPerSecond = totalBytes / totalSeconds;
        var leadBytes = (long)(res.LeadTalkSeconds * bytesPerSecond);
        var tailBytes = (long)(res.TailTalkSeconds * bytesPerSecond);

        // Sanity guard: never trim away the whole file (a mis-measured all-talk file should have
        // been rejected already, not land here trimmed to nothing).
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
            {
                for (var i = 0; i < got; i++) samples.Add(buf[i]);
            }
            else
            {
                for (var i = 0; i + ch <= got; i += ch)
                {
                    double sum = 0;
                    for (var c = 0; c < ch; c++) sum += buf[i + c];
                    samples.Add((float)(sum / ch));
                }
            }
        }
        Bass.StreamFree(h);
        return samples.ToArray();
    }
}
