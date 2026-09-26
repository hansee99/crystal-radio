using RadioPlayer.ViewModels;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Tapping a curated-playlist row jumps by position only when the engine's queue IS that playlist.
/// It used to jump whenever any queue existed: curate a playlist, play a song from the Songs tab
/// (which replaces the queue with the whole library), tap playlist track 3 — and track 3 of the
/// whole library played. MainViewModel.PlayQueueItem decides with IsSameQueue.
/// </summary>
public class CuratedQueuePlaybackTests
{
    private static readonly string[] Playlist = ["/m/b.mp3", "/m/d.mp3", "/m/a.mp3"];

    [Fact]
    public void ThePlaylistItselfIsTheSameQueue()
        => Assert.True(MainViewModel.IsSameQueue(Playlist, Playlist.ToArray()));

    [Fact]
    public void TheWholeLibraryIsNotThePlaylist()
    {
        // The reported case: the Songs tab loaded every saved song, in library order.
        string[] library = ["/m/a.mp3", "/m/b.mp3", "/m/c.mp3", "/m/d.mp3"];
        Assert.False(MainViewModel.IsSameQueue(library, Playlist));
    }

    [Fact]
    public void SameSongsInAnotherOrderAreNotTheSameQueue()
        => Assert.False(MainViewModel.IsSameQueue(["/m/a.mp3", "/m/b.mp3", "/m/d.mp3"], Playlist));

    [Fact]
    public void AnEmptyQueueNeverMatches()
    {
        // Nothing loaded yet (or DJ mode cleared it on the way out): the first tap must load the
        // playlist, including when the playlist is itself empty.
        Assert.False(MainViewModel.IsSameQueue([], Playlist));
        Assert.False(MainViewModel.IsSameQueue([], []));
    }

    [Fact]
    public void PathsCompareExactly()
        // Linux paths are case-sensitive; two files differing only by case are different files.
        => Assert.False(MainViewModel.IsSameQueue(["/m/B.mp3", "/m/d.mp3", "/m/a.mp3"], Playlist));
}
