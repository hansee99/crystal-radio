using System.IO;
using System.Runtime.InteropServices;
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
    private static void Pump() => TestLoop.Pump();

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

    // --- Metadata bounce: a re-announced title is not a boundary --------------------------------
    // Some stations re-announce the track they're already playing. Treating each announcement as a
    // cut chopped one track into several: "UK Hardcore #13 Mix 2017" came out of a real session as
    // a 3:33 song and a 2:25 song, both passing QC, both ending mid-phrase.

    [Fact]
    public void ARepeatedTitleDoesNotCutTheTrack()
    {
        Boundary("Head");
        Boundary("UK Hardcore Mix");
        Feed(SongSized);
        Boundary("UK Hardcore Mix");      // re-announcement, not a new track
        Feed(SongSized);
        Boundary("Something Else");       // the real boundary

        Pump();

        // One whole song, not two fragments — and its length spans both halves.
        var seg = Assert.Single(_completed);
        Assert.Equal("UK Hardcore Mix", seg.Title);
        Assert.Equal(SongSized * 2, seg.Bytes);
    }

    [Fact]
    public void ARepeatedTitleDoesNotLeaveASliverEither()
    {
        // The other half of the same bug: a re-announcement arriving seconds after the last one
        // used to produce a discarded sliver, which is how it was first spotted in the logs.
        Boundary("Head");
        Boundary("UK Hardcore Mix");
        Feed(SliverSized);
        Boundary("UK Hardcore Mix");

        Pump();

        Assert.Empty(_completed);
        Assert.Equal([DiscardReason.MidSongHead], _discarded.Select(d => d.Reason));
    }

    [Fact]
    public void ARepeatedTitleWithADifferentArtistIsStillABoundary()
    {
        // Two different tracks sharing a title is entirely normal (covers, remakes). Only the
        // title AND artist together identify a track.
        Boundary("Head");
        Boundary("Wonderful Life", "Black");
        Feed(SongSized);
        Boundary("Wonderful Life", "Hurts");

        Pump();

        Assert.Equal("Black", Assert.Single(_completed).Artist);
    }

    [Fact]
    public void ARepeatedTitleIsMatchedIgnoringCaseAndSurroundingSpace()
    {
        Boundary("Head");
        Boundary("UK Hardcore Mix", "DJ Someone");
        Feed(SongSized);
        Boundary("  uk hardcore mix ", "dj someone");
        Feed(SongSized);
        Boundary("Something Else");

        Pump();

        Assert.Equal(SongSized * 2, Assert.Single(_completed).Bytes);
    }

    [Fact]
    public void ARepeatedTitleDuringThePendingCutWindowIsIgnoredToo()
    {
        // With a boundary offset the cut is queued rather than immediate, so a duplicate
        // announcement arriving inside that window has to be compared against the track being
        // entered, not the one still being written.
        var dir = Path.Combine(_dir, "offset");
        Directory.CreateDirectory(dir);
        using var rec = new StreamRecorder(boundaryOffsetSeconds: 6, cacheDir: dir);
        var completed = new List<CompletedSegment>();
        rec.SegmentCompleted += (_, s) => completed.Add(s);
        var session = rec.BeginSession(new Station("Test FM", "http://x", StreamFormat.Mp3), StreamFormat.Mp3);

        void Bound(string t) => rec.OnTrackChanged(session, t, "Some Artist", "Test FM", 16_000);
        void Bytes(int n)
        {
            var buf = Marshal.AllocHGlobal(n);
            try { rec.Write(session, buf, n); } finally { Marshal.FreeHGlobal(buf); }
        }

        Bound("Head");
        Bytes(SongSized);
        Bound("Real Song");     // queues a cut ~96 KB out
        Bound("Real Song");     // duplicate inside the offset window — must not force the cut early
        Bytes(SongSized);
        Bound("Next");
        Bytes(SongSized);       // a deferred cut only executes once its byte countdown runs out
        Pump();

        Assert.Single(completed);
        Assert.Equal("Real Song", completed[0].Title);
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
