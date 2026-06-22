using System.Collections.ObjectModel;
using RadioPlayer.Models;
using RadioPlayer.Mvvm;
using RadioPlayer.Services;

namespace RadioPlayer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly RadioEngine _engine;
    private readonly StationStore _store;
    private readonly SettingsStore _settingsStore;
    private readonly IStationDialog _stationDialog;

    private Station? _selectedStation;
    private string _nowPlayingTitle = "Not playing";
    private string _nowPlayingArtist = string.Empty;
    private string _statusText = "Stopped";
    private bool _isPlaying;
    private bool _hasTrackInfo;
    private double _volume;

    // When set, changing SelectedStation won't auto-start playback. Used by the
    // add/edit/delete commands so managing the list doesn't yank what's playing.
    private bool _suppressAutoPlay;

    public MainViewModel(RadioEngine engine, StationStore store, SettingsStore settingsStore,
        IStationDialog stationDialog)
    {
        _engine = engine;
        _store = store;
        _settingsStore = settingsStore;
        _stationDialog = stationDialog;

        // Restore the persisted volume.
        _volume = settingsStore.Load().Volume;
        _engine.Volume = _volume;

        _engine.StateChanged += (_, state) => OnStateChanged(state);
        _engine.MetadataChanged += (_, meta) => OnMetadataChanged(meta);
        _engine.ErrorOccurred += (_, msg) => StatusText = msg;

        Stations = new ObservableCollection<Station>(_store.Load());
        _selectedStation = Stations.FirstOrDefault();

        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(_engine.Stop, () => _engine.State != PlaybackState.Stopped);
        NextStationCommand = new RelayCommand(NextStation, () => Stations.Count > 0);
        AddStationCommand = new RelayCommand(AddStation);
        EditStationCommand = new RelayCommand(EditStation, () => SelectedStation is not null);
        DeleteStationCommand = new RelayCommand(DeleteStation, () => SelectedStation is not null);
    }

    public ObservableCollection<Station> Stations { get; }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextStationCommand { get; }
    public RelayCommand AddStationCommand { get; }
    public RelayCommand EditStationCommand { get; }
    public RelayCommand DeleteStationCommand { get; }

    public Station? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (!SetProperty(ref _selectedStation, value)) return;

            EditStationCommand.RaiseCanExecuteChanged();
            DeleteStationCommand.RaiseCanExecuteChanged();
            if (value is null) return;

            // Switching station while already on-air restarts playback immediately.
            if (!_suppressAutoPlay
                && _engine.State is PlaybackState.Playing or PlaybackState.Paused
                    or PlaybackState.Buffering or PlaybackState.Reconnecting
                && !ReferenceEquals(value, _engine.CurrentStation))
            {
                _engine.Play(value);
            }
        }
    }

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            if (SetProperty(ref _nowPlayingTitle, value))
                OnPropertyChanged(nameof(NowPlayingClipboardText));
        }
    }

    public string NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set
        {
            if (SetProperty(ref _nowPlayingArtist, value))
                OnPropertyChanged(nameof(NowPlayingClipboardText));
        }
    }

    /// <summary>True while a stream is connecting/reconnecting (drives the loading spinner).</summary>
    public bool IsBusy => _engine.State is PlaybackState.Buffering or PlaybackState.Reconnecting;

    /// <summary>True when there's real now-playing info worth copying.</summary>
    public bool HasTrackInfo
    {
        get => _hasTrackInfo;
        private set
        {
            if (SetProperty(ref _hasTrackInfo, value))
                OnPropertyChanged(nameof(NowPlayingClipboardText));
        }
    }

    /// <summary>"Artist - Title" (or just the title) for the clipboard, or null if nothing to copy.</summary>
    public string? NowPlayingClipboardText =>
        !HasTrackInfo ? null
        : string.IsNullOrWhiteSpace(NowPlayingArtist) ? NowPlayingTitle
        : $"{NowPlayingArtist} - {NowPlayingTitle}";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
                OnPropertyChanged(nameof(PlayPauseLabel));
        }
    }

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
                _engine.Volume = value;
        }
    }

    private void NextStation()
    {
        if (Stations.Count == 0) return;

        // Cycle from whatever is currently playing (or just selected), wrapping around.
        var reference = _engine.CurrentStation ?? SelectedStation;
        var index = reference is null ? -1 : Stations.IndexOf(reference);
        var next = Stations[(index + 1) % Stations.Count];

        SelectWithoutAutoPlay(next); // update selection without double-triggering play
        _engine.Play(next);
    }

    private void AddStation()
    {
        var created = _stationDialog.Show(null);
        if (created is null) return;

        Stations.Add(created);
        SelectWithoutAutoPlay(created);
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();
    }

    private void EditStation()
    {
        var existing = SelectedStation;
        if (existing is null) return;

        var edited = _stationDialog.Show(existing);
        if (edited is null) return;

        var index = Stations.IndexOf(existing);
        var wasPlayingThis = ReferenceEquals(_engine.CurrentStation, existing);

        Stations[index] = edited;
        SelectWithoutAutoPlay(edited);
        _store.Save(Stations);

        // If we edited the station that's currently on-air, restart it (the URL or
        // format may have changed).
        if (wasPlayingThis)
            _engine.Play(edited);
    }

    private void DeleteStation()
    {
        var target = SelectedStation;
        if (target is null) return;

        if (ReferenceEquals(_engine.CurrentStation, target))
            _engine.Stop();

        var index = Stations.IndexOf(target);
        Stations.Remove(target);
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();

        // Move selection to a sensible neighbour without auto-starting it.
        SelectWithoutAutoPlay(Stations.Count == 0
            ? null
            : Stations[Math.Min(index, Stations.Count - 1)]);
    }

    /// <summary>Persist user settings (volume). Called when the app is closing.</summary>
    public void SaveSettings() => _settingsStore.Save(new AppSettings { Volume = _volume });

    private void SelectWithoutAutoPlay(Station? station)
    {
        _suppressAutoPlay = true;
        SelectedStation = station;
        _suppressAutoPlay = false;
    }

    private void TogglePlayPause()
    {
        if (_engine.State is PlaybackState.Stopped or PlaybackState.Error)
        {
            if (SelectedStation is not null)
                _engine.Play(SelectedStation);
        }
        else
        {
            _engine.TogglePause();
        }
    }

    private void OnStateChanged(PlaybackState state)
    {
        IsPlaying = state == PlaybackState.Playing;
        OnPropertyChanged(nameof(IsBusy));

        StatusText = state switch
        {
            PlaybackState.Stopped => "Stopped",
            PlaybackState.Buffering => "Buffering...",
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Reconnecting => "Reconnecting...",
            PlaybackState.Error => StatusText, // keep the error message
            _ => StatusText
        };

        switch (state)
        {
            case PlaybackState.Stopped:
                NowPlayingTitle = "Not playing";
                NowPlayingArtist = string.Empty;
                HasTrackInfo = false;
                break;
            case PlaybackState.Buffering:
            case PlaybackState.Reconnecting:
                // No track to copy yet; show which station we're connecting to.
                NowPlayingTitle = state == PlaybackState.Reconnecting ? "Reconnecting..." : "Connecting...";
                NowPlayingArtist = _engine.CurrentStation?.Name ?? string.Empty;
                HasTrackInfo = false;
                break;
        }

        StopCommand.RaiseCanExecuteChanged();
    }

    private void OnMetadataChanged(TrackMetadata meta)
    {
        NowPlayingTitle = string.IsNullOrWhiteSpace(meta.Title)
            ? (meta.StationName ?? "Live stream")
            : meta.Title;
        NowPlayingArtist = meta.Artist ?? meta.StationName ?? string.Empty;
        HasTrackInfo = !string.IsNullOrWhiteSpace(NowPlayingTitle);
    }
}
