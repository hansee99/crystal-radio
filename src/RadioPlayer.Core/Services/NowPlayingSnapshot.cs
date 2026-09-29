using System.Text.Json;
using RadioPlayer.ViewModels;

namespace RadioPlayer.Services;

/// <summary>
/// What the player is doing right now, as a plain value: the body of <c>GET /api/now</c> on both
/// heads — <see cref="InfoDisplayServer"/> in the WPF app, a mapped endpoint in the web head.
///
/// <para>It exists so an external info display can show the DJ's remark without knowing anything
/// about the player. Everything here is a string or a flag a panel can render directly; nothing
/// carries a handle, an id or a url the caller is expected to act on.</para>
///
/// <para><b>Build it on the thread that owns the view model.</b> <see cref="From"/> reads a dozen
/// UI-thread-affine properties, so callers off that thread marshal first (the HTTP handlers do)
/// and hand this value back — never the view model itself.</para>
/// </summary>
/// <param name="Playing">Audio is playing or about to be: also true while a stream buffers or
/// reconnects, which is a listener waiting rather than silence.</param>
/// <param name="Buffering">Connecting or reconnecting — a display can show a spinner instead of
/// implying the music stopped.</param>
/// <param name="Mode">radio, library or dj.</param>
/// <param name="Station">Friendly name of the station playing, null when none is.</param>
/// <param name="Format">Codec of the playing station ("AAC"/"MP3"), null off the radio engine.</param>
/// <param name="Title">The track title, or null when nothing is playing. Deliberately NOT the
/// app's idle placeholder: "Nothing playing" is 44px type in the player's own window, and a
/// remote panel that rendered it as a song title would be stating a falsehood.</param>
/// <param name="Artist">The artist, null when unknown — plenty of stations never announce one.</param>
/// <param name="DjRunning">A DJ session is on, even while paused.</param>
/// <param name="Vibe">The prompt the running session was started with.</param>
/// <param name="Remark">The DJ's current line — the point of this whole payload. Follows the same
/// rule the player's own panel does (<see cref="MainViewModel.ShowDjIntro"/>), so a display can
/// never show a remark the app itself has stopped showing. Covers both a track introduction and
/// the between-tracks patter; from the outside they are the same thing, the DJ talking.</param>
/// <param name="RemarkChangedAt">When <paramref name="Remark"/> last changed, so a display can
/// animate a new line in rather than diffing strings, and can tell a fresh remark from one that
/// has been up for twenty minutes. Null when there is no remark.</param>
/// <param name="UpNext">The next track in the mix, "Title — Artist", when there is one.</param>
/// <param name="ServedAt">When this snapshot was taken. A panel that polls can spot a feed that
/// has frozen — the player still answering but no longer advancing.</param>
public sealed record NowPlayingSnapshot(
    bool Playing,
    bool Buffering,
    string Mode,
    string? Station,
    string? Format,
    string? Title,
    string? Artist,
    bool DjRunning,
    string? Vibe,
    string? Remark,
    DateTimeOffset? RemarkChangedAt,
    string? UpNext,
    DateTimeOffset ServedAt)
{
    /// <summary>
    /// camelCase, and nulls are WRITTEN rather than skipped: a display binds <c>remark</c> by name,
    /// and a key that vanishes when the DJ has nothing to say is the same bug as a renamed one —
    /// the field silently reads as undefined instead of empty.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Reads the view model. Call on the thread that owns it.</summary>
    public static NowPlayingSnapshot From(MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        return new NowPlayingSnapshot(
            Playing: vm.IsPlaying || vm.IsBusy,
            Buffering: vm.IsBusy,
            Mode: vm.Mode.ToString().ToLowerInvariant(),
            Station: Blank(vm.NowPlayingStation),
            Format: Blank(vm.NowPlayingFormat),
            Title: vm.IsNowPlayingIdle ? null : Blank(vm.NowPlayingTitle),
            Artist: Blank(vm.NowPlayingArtist),
            DjRunning: vm.IsDjRunning,
            Vibe: Blank(vm.DjSessionVibe),
            Remark: vm.ShowDjIntro ? Blank(vm.DjIntroLine) : null,
            RemarkChangedAt: vm.ShowDjIntro ? vm.DjIntroLineChangedAt : null,
            UpNext: vm.HasUpNext ? Blank(vm.UpNext) : null,
            ServedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>An absent value is null, never "". The view model uses empty strings for the
    /// slots its own bindings collapse; across the wire that distinction is noise.</summary>
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
