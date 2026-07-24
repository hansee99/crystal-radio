namespace RadioPlayer.ViewModels;

/// <summary>Which player is active. Either/or — one BASS output, one now-playing at a time.</summary>
public enum PlayerMode
{
    /// <summary>Internet radio streams (Stations / Search / History).</summary>
    Radio,

    /// <summary>The local, AI-curated library player.</summary>
    Library
}
