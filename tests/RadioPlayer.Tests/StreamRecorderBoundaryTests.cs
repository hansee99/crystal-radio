using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using RadioPlayer.Models;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The segment-cutting rules, driven through the real <see cref="StreamRecorder"/> with synthetic
/// bytes. Chiefly here for the discard reporting: a station announcing nothing and a station whose
/// every announcement is filtered out used to be indistinguishable in a session log, which is what
/// made the 2026-08-02 "one of four harvesters producing" session need a live ICY probe to
/// diagnose. Also pins the keep/drop rules themselves, which had no coverage.
///
/// Offset 0 throughout (the DJ harvest path's setting), so each boundary cuts immediately rather
/// than after a byte countdown.
/// </summary>
public sealed class StreamRecorderBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crystal-radio-tests",
        Guid.NewGuid().ToString("N"));
    private readonly StreamRecorder _recorder;
    private readonly List<CompletedSegment> _completed = [];
    private readonly List<DiscardedSegment> _discarded = [];
    private readonly int _session;

    public StreamRecorderBoundaryTests()
    {
        Directory.CreateDirectory(_dir);
        _recorder = new StreamRecorder(boundaryOffsetSeconds: 0, cacheDir: _dir);
        _recorder.SegmentCompleted += (_, s) => _completed.Add(s);
        _recorder.SegmentDiscarded += (_, d) => _discarded.Add(d);
        _session = _recorder.BeginSession(new Station("Test FM", "http://x", StreamFormat.Mp3), StreamFormat.Mp3);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A title change. Station name matches the one the session was opened with, so the
    /// filter's station-name rule is live exactly as it is in production.</summary>
    private void Boundary(string title, string? artist = "Some Artist") =>
        _recorder.OnTrackChanged(_session, title, artist, "Test FM", bytesPerSecond: 16_000);

    private void Feed(int bytes)
    {
        var buffer = Marshal.AllocHGlobal(bytes);
        try { _recorder.Write(_session, buffer, bytes); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const int SongSized = 300 * 1024;   // over the 200 KB floor
    private const int SliverSized = 8 * 1024;

    /// <summary>Events are marshalled through the captured dispatcher, so nothing arrives until
    /// the queue is pumped.</summary>
    private void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void ASegmentBetweenTwoBoundariesIsAnnounced()
    {
        Boundary("Head");                 // opens the mid-song head
        Boundary("Real Song");            // closes the head, opens the first true segment
        Feed(SongSized);
        Boundary("Next Song");            // closes it

        Pump();

        var seg = Assert.Single(_completed);
        Assert.Equal("Real Song", seg.Title);
        Assert.Equal("Some Artist", seg.Artist);
    }

    [Fact]
    public void TheFirstSegmentOfAConnectionIsReportedAsAMidSongHead()
    {
        // The single most informative discard: it proves the station announced a title at all,
        // which is otherwise unknowable from the log.
        Boundary("Whatever Was Playing");
        Feed(SongSized);
        Boundary("Real Song");

        Pump();

        Assert.Empty(_completed);
        Assert.Equal(DiscardReason.MidSongHead, Assert.Single(_discarded).Reason);
    }

    [Fact]
    public void ARepeatedTitleLeavesASliverReportedAsTooShort()
    {
        // Metadata bounce: some stations re-announce the current title mid-track, and every
        // re-announcement is treated as a boundary. Observed producing a 9-second sliver
        // 2 seconds after a 5:20 segment of the same title.
        Boundary("Head");
        Boundary("UK Hardcore Mix");
        Feed(SliverSized);
        Boundary("UK Hardcore Mix");      // same title again, moments later

        Pump();

        Assert.Empty(_completed);
        Assert.Equal([DiscardReason.MidSongHead, DiscardReason.TooShort],
            _discarded.Select(d => d.Reason));
    }

    [Fact]
    public void ATitleWithNoArtistIsReportedAsNotSongLike()
    {
        // ICY without the "Artist - Title" convention. Common on small stations, and it silently
        // costs them every segment.
        Boundary("Head");
        Boundary("LIQUID DNB RADIO", artist: null);
        Feed(SongSized);
        Boundary("Something Else");

        Pump();

        Assert.Empty(_completed);
        Assert.Contains(_discarded, d => d.Reason == DiscardReason.NotSongLike);
    }

    /// <summary>The hazard behind the 2026-08-02 session: the filter drops any title carrying the
    /// station's own name, which for a genre-named station collides with real track titles.</summary>
    [Fact]
    public void ATitleContainingTheStationNameIsReportedAsNotSongLike()
    {
        Boundary("Head");
        Boundary("Test FM Anthem");
        Feed(SongSized);
        Boundary("Something Else");

        Pump();

        Assert.Empty(_completed);
        var dropped = _discarded.Last();
        Assert.Equal(DiscardReason.NotSongLike, dropped.Reason);
        Assert.Equal("Test FM Anthem", dropped.Title);
    }

    [Fact]
    public void AStationAnnouncingOneTitleProducesNoEventsAtAll()
    {
        // The long-mix case, and the reason the session log also needs the per-station title
        // count: with no second boundary nothing is ever closed, so there is nothing to report.
        Boundary("Creative Source (Part 86)", "Arythmatik");
        Feed(SongSized * 4);

        Pump();

        Assert.Empty(_completed);
        Assert.Empty(_discarded);
    }

    [Fact]
    public void EndingTheSessionDoesNotReportItsPartialTail()
    {
        // A tail cut short by stop/reconnect says nothing about the station; reporting it would
        // add a spurious "skipped" to every session.
        Boundary("Head");
        Boundary("Real Song");
        Feed(SongSized);
        _recorder.EndSession(_session);

        Pump();

        Assert.Empty(_completed);
        Assert.Equal([DiscardReason.MidSongHead], _discarded.Select(d => d.Reason));
    }

    [Fact]
    public void DiscardedSegmentFilesAreDeleted()
    {
        Boundary("Head");
        Boundary("Test FM Anthem");   // will be filtered out
        Feed(SongSized);
        Boundary("Next");
        Pump();

        // Only the still-open current segment should remain on disk.
        Assert.Single(Directory.GetFiles(_dir));
    }
}
