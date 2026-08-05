namespace RadioPlayer.ViewModels;

/// <summary>
/// Which reading view the right pane's upper region is showing instead of Now Playing. The
/// transport + volume row below is never affected.
///
/// <para>Two overlays share one set of chrome — back button, subject header, scroll region,
/// loading skeleton, error line, bottom fade. Only the result block differs. That is deliberate:
/// issue #20 wants this swap replaced by a rail or an inline expansion, and one mechanism is one
/// conversion rather than two near-identical ones.</para>
/// </summary>
public enum OverlayContent
{
    /// <summary>Nothing open — Now Playing is visible.</summary>
    None,
    /// <summary>The AI-generated "About this song" briefing.</summary>
    About,
    /// <summary>Lyrics for the currently playing track.</summary>
    Lyrics
}

/// <summary>How far along the open overlay is. Orthogonal to <see cref="OverlayContent"/>.</summary>
public enum OverlayViewState
{
    Home,
    Loading,
    Result,
    Error
}
