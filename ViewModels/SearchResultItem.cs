using RadioPlayer.Models;

namespace RadioPlayer.ViewModels;

/// <summary>
/// A search result for the UI: the playable station plus an optional one-line rationale
/// (Pattern B supplies a reason; Pattern A leaves it null).
/// </summary>
public sealed record SearchResultItem(Station Station, string? Reason);
