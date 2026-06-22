using System.Windows;
using RadioPlayer.Models;
using RadioPlayer.ViewModels;

namespace RadioPlayer;

/// <summary>View-layer implementation of <see cref="IStationDialog"/>.</summary>
public sealed class StationDialogService : IStationDialog
{
    private readonly Window _owner;

    public StationDialogService(Window owner) => _owner = owner;

    public Station? Show(Station? existing)
    {
        var dialog = new StationDialog(existing) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
