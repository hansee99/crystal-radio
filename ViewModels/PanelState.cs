namespace RadioPlayer.ViewModels;

/// <summary>
/// The four mutually-exclusive states any list panel can be in. Introduced per the UX audit:
/// previously each panel's empty message was gated on its own <c>Count == 0</c> trigger,
/// independent of whether a load was running — so "describe a vibe above" could sit directly
/// under a spinner already doing that, and there was no designed home for errors anywhere in
/// the app. One enum per panel makes the states exclusive by construction and gives every
/// panel an error state for free.
/// </summary>
public enum PanelState
{
    /// <summary>The list has rows — show it.</summary>
    Content,

    /// <summary>Nothing to show and nothing running: the panel's invitation/next step.</summary>
    Empty,

    /// <summary>A load is in flight. Never shows alongside Empty.</summary>
    Loading,

    /// <summary>The last operation failed; the panel shows why.</summary>
    Error
}
