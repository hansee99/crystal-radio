namespace RadioPlayer.Services;

/// <summary>
/// Shows Windows toast notifications. Best-effort by construction: a notification that fails to
/// appear must never disturb playback, so every failure is swallowed and logged.
/// </summary>
public interface INotificationService
{
    /// <summary>False when toasts can't be shown at all (no app identity, unsupported OS, the user
    /// has turned them off in Windows). Callers can skip the work of composing one.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Announces a track the DJ has just introduced: what is playing, and what the DJ said about
    /// it. Returns immediately; the toast is fire-and-forget.
    /// </summary>
    void ShowDjTrack(string title, string? artist, string? remark);
}
