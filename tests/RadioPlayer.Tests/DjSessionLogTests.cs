using System.IO;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The session summary is what a harvester-tuning decision will be based on, so the arithmetic
/// in it needs to be right — a wrong surplus figure is worse than no measurement at all.
/// Rates in songs/min depend on wall-clock and aren't assertable in a millisecond-long test, but
/// the surplus RATIO is fill/play, so elapsed time cancels out and it is.
/// </summary>
public sealed class DjSessionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "crystal-radio-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private DjSessionLog NewLog() =>
        DjSessionLog.Start("motorik krautrock", harvesters: 4, reserve: 15,
            cacheCapBytes: 500L * 1024 * 1024, directory: _dir)
        ?? throw new InvalidOperationException("log should have started");

    private static string Summary(DjSessionLog log)
    {
        log.Finish();
        var text = File.ReadAllText(log.FilePath);
        var marker = text.IndexOf("=== SUMMARY", StringComparison.Ordinal);
        Assert.True(marker >= 0, "summary block missing");
        return text[marker..];
    }

    [Fact]
    public void Summary_ReportsKeepRateAndSurplus()
    {
        var log = NewLog();

        // Six segments completed, four kept — then two of those actually played.
        log.SegmentCompleted("Radio A", "Hallogallo", kept: true, confidence: 0.91, seconds: 606);
        log.SegmentCompleted("Radio A", "Spoon", kept: true, confidence: 0.88, seconds: 182);
        log.SegmentCompleted("Radio A", "<ad break>", kept: false, confidence: 0.08, seconds: 40);
        log.SegmentCompleted("Radio B", "Moonshake", kept: true, confidence: 0.84, seconds: 184);
        log.SegmentCompleted("Radio B", "Autobahn", kept: true, confidence: 0.79, seconds: 571);
        log.SegmentCompleted("Radio B", "<station ID>", kept: false, confidence: 0.11, seconds: 15);

        log.SongPlayed(@"C:\harvest\a.mp3", "Hallogallo", "Neu!");
        log.SongPlayed(@"C:\harvest\b.mp3", "Moonshake", "Can");

        var summary = Summary(log);

        Assert.Contains("Segments completed  6", summary);
        Assert.Contains("kept              4  (66%)", summary);
        Assert.Contains("rejected          2  (33%)", summary);
        // 4 harvested / 2 played — the ratio the tuning decision turns on.
        Assert.Contains("Surplus             2.0x  (2 kept but not played)", summary);
    }

    [Fact]
    public void Summary_CountsEvictionsThatWereNeverPlayed()
    {
        var log = NewLog();
        log.SegmentCompleted("Radio A", "Kept one", kept: true, confidence: 0.9, seconds: 200);
        log.SongPlayed(@"C:\harvest\played.mp3", "Kept one", "Neu!");

        log.Evicted(@"C:\harvest\played.mp3");   // heard before it went
        log.Evicted(@"C:\harvest\wasted.mp3");   // paid for, never heard
        log.Evicted(@"C:\harvest\wasted2.mp3");

        Assert.Contains("Evicted             3 (2 never played)", Summary(log));
    }

    [Fact]
    public void Summary_RanksStationsByWhatTheyContributed()
    {
        var log = NewLog();
        log.HarvesterStarted("Busy Station");
        log.HarvesterStarted("Quiet Station");
        log.SegmentCompleted("Quiet Station", "One", kept: true, confidence: 0.9, seconds: 200);
        log.SegmentCompleted("Busy Station", "A", kept: true, confidence: 0.9, seconds: 200);
        log.SegmentCompleted("Busy Station", "B", kept: true, confidence: 0.9, seconds: 200);
        log.SegmentCompleted("Busy Station", "<talk>", kept: false, confidence: 0.1, seconds: 60);

        var summary = Summary(log);
        var busy = summary.IndexOf("Busy Station", StringComparison.Ordinal);
        var quiet = summary.IndexOf("Quiet Station", StringComparison.Ordinal);

        Assert.True(busy >= 0 && quiet >= 0, "both stations should be listed");
        Assert.True(busy < quiet, "the station contributing most should rank first");
        Assert.Contains("kept   2   rejected   1", summary);
    }

    /// <summary>Edge-trim is driven by the same per-window classification as the keep/reject
    /// decision, so on material the detector reads badly it shaves real music off the songs it
    /// keeps. That only becomes visible if the totals are reported.</summary>
    [Fact]
    public void Summary_ReportsHowMuchAudioTheEdgeTrimRemoved()
    {
        var log = NewLog();
        log.SegmentCompleted("Radio A", "Intro-heavy track", kept: true, confidence: 0.42,
            seconds: 300, verdict: "MIXED", leadTrim: 12.5, tailTrim: 3.5);
        log.SegmentCompleted("Radio A", "Clean track", kept: true, confidence: 0.95,
            seconds: 200, verdict: "MUSIC");

        var summary = Summary(log);

        Assert.Contains("Edge-trimmed        16 s total", summary);
        Assert.Contains("trimmed 1", summary); // only the one segment was trimmed
    }

    /// <summary>A rejected segment is discarded, so whatever trim was measured on it cost
    /// nothing. Counting it made the total look alarming for the wrong reason — the figure that
    /// matters is audio lost from songs that actually reached the mix.</summary>
    [Fact]
    public void Summary_CountsTrimOnlyOnKeptSegments()
    {
        var log = NewLog();
        log.SegmentCompleted("Radio A", "Discarded", kept: false, confidence: 0.02,
            seconds: 200, verdict: "TALK", leadTrim: 196, tailTrim: 0);
        log.SegmentCompleted("Radio A", "In the mix", kept: true, confidence: 0.60,
            seconds: 240, verdict: "MIXED", leadTrim: 4, tailTrim: 2);

        Assert.Contains("Edge-trimmed        6 s total", Summary(log));
    }

    [Fact]
    public void Summary_OmitsTheTrimLineWhenNothingWasTrimmed()
    {
        var log = NewLog();
        log.SegmentCompleted("Radio A", "Clean track", kept: true, confidence: 0.95, seconds: 200);

        Assert.DoesNotContain("Edge-trimmed", Summary(log));
    }

    [Fact]
    public void Finish_IsIdempotent()
    {
        var log = NewLog();
        log.SegmentCompleted("Radio A", "One", kept: true, confidence: 0.9, seconds: 200);

        log.Finish();
        log.Finish(); // DjHarvestService.Stop and the view model both call it

        var occurrences = File.ReadAllText(log.FilePath).Split("=== SUMMARY").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void EventsAreRecordedBeforeTheSummary_SoAnUncleanExitStillLeavesData()
    {
        var log = NewLog();
        log.SegmentCompleted("Radio A", "Hallogallo", kept: true, confidence: 0.91, seconds: 606);

        // No Finish() — simulating the app being killed mid-session.
        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("Hallogallo", text);
        Assert.DoesNotContain("=== SUMMARY", text);
    }
}
