using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RadioPlayer.Services;

/// <summary>Persisted application settings.</summary>
public sealed class AppSettings
{
    /// <summary>Output volume, 0.0–1.0.</summary>
    public double Volume { get; set; } = 0.5;

    /// <summary>
    /// Anthropic API key, DPAPI-encrypted (CurrentUser) and Base64-encoded. Never stored in
    /// plaintext and never the raw key — read/write it via <see cref="SettingsStore.GetApiKey"/>
    /// / <see cref="SettingsStore.SetApiKey"/>. Null when no key has been set in-app.
    /// </summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>Folder saved songs are written to. Null → the default (Music\Crystal Radio).</summary>
    public string? LibraryFolder { get; set; }

    /// <summary>Rolling-cache size cap in megabytes (oldest segments are evicted beyond it).</summary>
    public int CacheCapMb { get; set; } = 200;

    /// <summary>
    /// Seconds by which a station's ICY title change leads its audio (studio playout announces
    /// the title while the audio is still in the encoder pipeline). Segment cuts are delayed by
    /// this much so saved songs start/end on the real boundary. Station encoders differ; tune
    /// here if saved songs consistently start late / end early (raise) or contain the previous /
    /// next song (lower).
    /// </summary>
    public double CaptureBoundaryOffsetSeconds { get; set; } = 6.0;

    /// <summary>Concurrent DJ-mode harvesting connections. Cheaper per-stream than a live
    /// standby (raw-byte capture, no continuous decode/FFT) — drives fill rate.</summary>
    public int DjHarvesterCount { get; set; } = 4;

    /// <summary>Validated station URLs held in reserve to replace a dead DJ-mode harvester
    /// without another search round-trip.</summary>
    public int DjHarvestReserveCount { get; set; } = 15;

    /// <summary>Refill the DJ-mode queue (from the library) when it drops to this many
    /// remaining songs, in case harvesting alone isn't keeping up.</summary>
    public int DjQueueLowWatermark { get; set; } = 5;

    /// <summary>Rolling-cache cap in megabytes for DJ-mode's harvested (unsaved) songs — a
    /// separate cap from <see cref="CacheCapMb"/>, since harvested songs live in their own
    /// folder, distinct from both the live-recording cache and the user's saved Library folder.</summary>
    public int DjMaxHarvestCacheMb { get; set; } = 500;

    /// <summary>
    /// Cap in megabytes for QC-rejected segments, kept in a <c>_rejected</c> subfolder instead of
    /// being deleted. They are the only evidence of a misclassification: the music detector was
    /// fitted on pop/rock/disco plus ambient/indie and has no electronic dance music in its
    /// corpus at all, so whole genres can be rejected wholesale with nothing left to inspect.
    /// Keeping them makes that diagnosable and feeds the corpus a re-fit needs. 0 disables.
    /// </summary>
    public int DjRejectedCacheMb { get; set; } = 250;

    /// <summary>
    /// A harvested segment must have at least this many seconds of audio left after the edge-trim
    /// to count as a song. This is the primary QC gate: it uses the trim, which measures a LOCAL
    /// run of non-music and does that well, rather than a whole-file music fraction, which does
    /// not (see <see cref="DjMusicFractionFloor"/>). An ad break trims away to nothing and is
    /// rejected; a track with a talk outro keeps its music and loses the outro.
    /// </summary>
    public int DjMinSongSeconds { get; set; } = 60;

    /// <summary>
    /// Reject a segment whose whole-file music fraction is below this. <b>0 disables it, which is
    /// the current default.</b> Listening tests found confirmed-good deep house scoring 0.00 —
    /// the same as a confirmed ad break — so the two classes are not separable by this number and
    /// no threshold on it can be right. Re-enable once the detector gains a pulse-strength
    /// feature and is re-fitted; the knob stays so that can be A/B'd without a rebuild.
    /// </summary>
    public double DjMusicFractionFloor { get; set; }

    /// <summary>Resolved library folder (the stored value or the default).</summary>
    public string ResolveLibraryFolder() =>
        string.IsNullOrWhiteSpace(LibraryFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Crystal Radio")
            : LibraryFolder;
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under
/// %AppData%\RadioPlayer\settings.json. Best-effort: failures fall back to defaults
/// rather than disrupting playback.
/// </summary>
public sealed class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RadioPlayer");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    settings.Volume = Math.Clamp(settings.Volume, 0.0, 1.0);
                    settings.CacheCapMb = Math.Clamp(settings.CacheCapMb, 20, 10_000);
                    settings.CaptureBoundaryOffsetSeconds = Math.Clamp(settings.CaptureBoundaryOffsetSeconds, 0.0, 30.0);
                    settings.DjHarvesterCount = Math.Clamp(settings.DjHarvesterCount, 1, 16);
                    settings.DjHarvestReserveCount = Math.Clamp(settings.DjHarvestReserveCount, 0, 100);
                    settings.DjQueueLowWatermark = Math.Clamp(settings.DjQueueLowWatermark, 1, 50);
                    settings.DjMaxHarvestCacheMb = Math.Clamp(settings.DjMaxHarvestCacheMb, 50, 20_000);
                    settings.DjRejectedCacheMb = Math.Clamp(settings.DjRejectedCacheMb, 0, 20_000);
                    settings.DjMinSongSeconds = Math.Clamp(settings.DjMinSongSeconds, 0, 600);
                    settings.DjMusicFractionFloor = Math.Clamp(settings.DjMusicFractionFloor, 0, 1);
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Decrypts and returns the stored API key, or null if none is set / undecryptable.</summary>
    public string? GetApiKey() => Unprotect(Load().ApiKeyProtected);

    /// <summary>
    /// Encrypts and persists the API key (or clears it when null/blank), leaving other
    /// settings untouched (load-modify-save so it never clobbers Volume).
    /// </summary>
    public void SetApiKey(string? plaintext)
    {
        var settings = Load();
        settings.ApiKeyProtected = string.IsNullOrWhiteSpace(plaintext) ? null : Protect(plaintext);
        Save(settings);
    }

    // DPAPI under the current Windows user: the encrypted blob is only readable by this user
    // on this machine, and is kept out of source control (it lives in %AppData%).
    private static string Protect(string plaintext)
    {
        var blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    private static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Corrupt, or encrypted by a different user — treat as no key.
            return null;
        }
    }
}
