using RadioPlayer.Models;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Which save affordance a history row offers (#37).
///
/// <para>There used to be two buttons for one song: "Save this song when it finishes" in the Now
/// Playing header, and — on that same song's row in the list below — a DISABLED button insisting
/// "Can't save this one, it wasn't recorded while it played". The second was simply untrue: the
/// audio was being captured at that moment. The header button is gone and the row owns saving for
/// every song, so the row has to distinguish "audio genuinely missed" from "still recording".</para>
/// </summary>
public class SaveAffordanceTests
{
    private static SongHistoryEntry Entry(string? segment = null, string? saved = null, bool current = false) =>
        new() { Title = "T", Artist = "A", SegmentFile = segment, SavedPath = saved, IsCurrent = current };

    [Fact]
    public void ARowWithCapturedAudioCanBeSaved()
    {
        var entry = Entry(segment: "seg.mp3");

        Assert.True(entry.CanSave);
        Assert.True(entry.CanSaveOrMark);
    }

    /// <summary>The playing song: no completed recording yet, but the click means "when it
    /// finishes". This is the case that used to render as a disabled button with a false message.</summary>
    [Fact]
    public void ThePlayingRowCanBeMarkedEvenWithNoSegmentYet()
    {
        var entry = Entry(current: true);

        Assert.False(entry.CanSave);        // nothing to copy yet
        Assert.True(entry.CanSaveOrMark);   // but the button must still do something
    }

    /// <summary>The genuinely-missed case, which keeps its honest explanation: the song played
    /// before recording covered it, and no click will ever help.</summary>
    [Fact]
    public void AnOlderRowWithNoAudioStaysUnsaveable()
    {
        var entry = Entry();

        Assert.False(entry.CanSave);
        Assert.False(entry.CanSaveOrMark);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadySavedIsNeverSaveableAgain(bool current)
    {
        var entry = Entry(segment: "seg.mp3", saved: @"C:\lib\A - T.mp3", current: current);

        Assert.True(entry.IsSaved);
        Assert.False(entry.CanSave);
        Assert.False(entry.CanSaveOrMark);
    }

    // --- Notifications: the button binds to these, so a missed one leaves it stale --------------

    /// <summary>The exact trap the XAML's IsEnabled comment warns about: RelayCommand only
    /// re-evaluates on RaiseCanExecuteChanged, so the button tracks this property instead. If the
    /// property doesn't announce itself the button stays disabled after the recording lands.</summary>
    [Fact]
    public void CompletingTheRecordingAnnouncesTheButtonCanNowWork()
    {
        var entry = Entry(current: true);
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.SegmentFile = "seg.mp3";

        Assert.Contains(nameof(SongHistoryEntry.CanSave), raised);
        Assert.Contains(nameof(SongHistoryEntry.CanSaveOrMark), raised);
    }

    [Fact]
    public void SavingAnnouncesThatTheButtonShouldGiveWayToTheMark()
    {
        var entry = Entry(segment: "seg.mp3");
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.SavedPath = @"C:\lib\A - T.mp3";

        Assert.Contains(nameof(SongHistoryEntry.IsSaved), raised);
        Assert.Contains(nameof(SongHistoryEntry.CanSaveOrMark), raised);
    }

    /// <summary>Playback moving on has to re-evaluate the row that just stopped being current, or
    /// an older row keeps offering to save a song that has already finished.</summary>
    [Fact]
    public void BecomingOrCeasingToBeCurrentAnnouncesItself()
    {
        var entry = Entry();
        var raised = new List<string?>();
        entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        entry.IsCurrent = true;
        Assert.Contains(nameof(SongHistoryEntry.CanSaveOrMark), raised);

        raised.Clear();
        entry.IsCurrent = false;
        Assert.Contains(nameof(SongHistoryEntry.CanSaveOrMark), raised);
    }

    /// <summary>IsCurrent is transient UI state, not part of the persisted history file.</summary>
    [Fact]
    public void IsCurrentIsNotSerialised()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Entry(current: true));

        Assert.DoesNotContain("IsCurrent", json);
        Assert.DoesNotContain("CanSaveOrMark", json);
    }
}
