using RadioPlayer.Models;

namespace RadioPlayer.ViewModels;

/// <summary>
/// Shows the add/edit station dialog. Implemented in the view layer so the view model
/// has no dependency on WPF windows.
/// </summary>
public interface IStationDialog
{
    /// <summary>
    /// Opens the editor. Pass an existing station to edit, or null to add a new one.
    /// Returns the resulting station, or null if the user cancelled.
    /// </summary>
    Station? Show(Station? existing);
}
