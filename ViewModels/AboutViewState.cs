namespace RadioPlayer.ViewModels;

/// <summary>
/// State of the right pane's upper region: it swaps between the Now Playing block (Home) and the
/// "About this track" reading view (Loading / Result / Error). The transport + volume row below
/// is never affected.
/// </summary>
public enum AboutViewState
{
    Home,
    Loading,
    Result,
    Error
}
