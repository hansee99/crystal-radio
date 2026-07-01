using RadioPlayer.Models;
using RadioPlayer.Mvvm;

namespace RadioPlayer.ViewModels;

/// <summary>
/// A search result for the UI: the playable station plus an optional one-line rationale
/// (Pattern B supplies a reason; Pattern A leaves it null). <see cref="IsAdded"/> tracks
/// whether the station is already in the fixed list so the row's add affordance can show a
/// confirmed/disabled state; it is observable so the button updates the moment it's added.
/// </summary>
public sealed class SearchResultItem : ObservableObject
{
    private bool _isAdded;

    public SearchResultItem(Station station, string? reason, string? country = null, bool isAdded = false)
    {
        Station = station;
        Reason = reason;
        Country = country;
        _isAdded = isAdded;
    }

    public Station Station { get; }
    public string? Reason { get; }

    /// <summary>Origin country (from Radio Browser), when known — fed to the ranker so it can
    /// honour or diversify by country per the prompt's intent. Null for web finds.</summary>
    public string? Country { get; }

    /// <summary>Display name with technical noise (bitrate/codec) stripped; the raw
    /// <see cref="Station"/> name is kept for playback identity and dedup.</summary>
    public string DisplayName => StationNameFormatter.Clean(Station.Name);

    public bool IsAdded
    {
        get => _isAdded;
        set => SetProperty(ref _isAdded, value);
    }
}
