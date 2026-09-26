using System.IO;
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

    /// <summary>
    /// Total disk DJ mode may use, in megabytes — harvested songs plus the QC quarantine.
    ///
    /// One number on purpose. These were two separate caps (500 + 250) and the folder therefore
    /// grew past whichever one you had set, which is confusing enough that it was reported as a
    /// bug. Now the figure you set is the figure on disk; the split between the two folders is an
    /// implementation detail (see <see cref="ResolveHarvestCacheBytes"/>).
    /// </summary>
    public int DjDiskSpaceMb { get; set; } = 750;

    /// <summary>Share of <see cref="DjDiskSpaceMb"/> for harvested songs — the mix's material.</summary>
    public long ResolveHarvestCacheBytes() => (long)(DjDiskSpaceMb * (2.0 / 3.0)) * 1024 * 1024;

    /// <summary>
    /// Share for QC-rejected segments, kept rather than deleted because they are the only evidence
    /// a rejection was wrong — whole genres were once rejected wholesale with nothing left to
    /// inspect. A third is enough to diagnose without crowding out the mix itself.
    /// </summary>
    public long ResolveRejectedCacheBytes() => (long)(DjDiskSpaceMb * (1.0 / 3.0)) * 1024 * 1024;

    /// <summary>
    /// Seconds skipped at the start of every locally-played track. 0 = off.
    ///
    /// A boundary cut slightly early leaves the tail of the previous song at the head of this one,
    /// and the edge-trim only catches it when the detector recognises it as non-music. This is the
    /// blunt backstop for when it doesn't. Engine-wide rather than DJ-only: saved songs come from
    /// the same boundary mechanism and currently get no trim at all, so they carry the artefact
    /// too. Per CLAUDE.md, losing a couple of seconds beats hearing the previous track.
    /// </summary>
    public double IntroSkipSeconds { get; set; }

    /// <summary>
    /// Seconds a locally-played track stops short of its end. 0 = off.
    ///
    /// The mirror of <see cref="IntroSkipSeconds"/>: a boundary cut slightly late leaves the head
    /// of the NEXT song at the end of this one. Where a crossfade follows, this brings the fade
    /// forward so the outgoing track is already silent before the residue; where one doesn't, the
    /// track simply ends early.
    /// </summary>
    public double OutroGuardSeconds { get; set; }

    /// <summary>
    /// A harvested segment must have at least this many seconds of audio left after the edge-trim
    /// to count as a song. This is the primary QC gate: it uses the trim, which measures a LOCAL
    /// run of non-music and does that well, rather than a whole-file music fraction, which does
    /// not (see <see cref="DjMusicFractionFloor"/>). An ad break trims away to nothing and is
    /// rejected; a track with a talk outro keeps its music and loses the outro.
    /// </summary>
    public int DjMinSongSeconds { get; set; } = 60;

    /// <summary>
    /// How long a DJ-mode harvester may go without completing a segment before its slot is given
    /// to a reserve station. Catches stations playing long DJ sets or extended mixes — one ICY
    /// title announced for an hour means no song boundaries and nothing for the curator — as well
    /// as streams that quietly stall without erroring. Measured on segments rather than title
    /// changes, since some stations re-announce the same title mid-track.
    ///
    /// <para>10 minutes rather than 15: the longest real track measured across five sessions was
    /// 10:04, and a station whose tracks routinely run longer than that isn't a good fit for the
    /// harvest-and-curate model anyway — the queue wants songs, not sets. Waiting longer only
    /// delays giving the slot to a station that will actually produce.</para>
    /// </summary>
    public int DjStationIdleMinutes { get; set; } = 10;

    /// <summary>
    /// The voice DJ mode introduces tracks in: <c>Warm</c> (default), <c>Upbeat</c>,
    /// <c>LateNight</c>, <c>Wry</c>, <c>Professional</c>. Stored as text so a typo costs one
    /// setting rather than the whole file — an unrecognised value falls back to Warm, whereas an
    /// enum property would throw during deserialization and reset EVERYTHING to defaults.
    /// </summary>
    public string DjPersonality { get; set; } = nameof(Services.DjPersonality.Warm);

    /// <summary>
    /// How large the DJ's remark reads: <c>Small</c>, <c>Medium</c> (default), <c>Large</c>.
    /// Text for the same reason as <see cref="DjPersonality"/> — an unrecognised value costs one
    /// setting instead of resetting the whole file.
    /// </summary>
    public string DjRemarkSize { get; set; } = nameof(Services.DjRemarkSize.Medium);

    /// <summary>The parsed form of <see cref="DjRemarkSize"/>, or Medium if it isn't recognised.
    /// Name-matched rather than <c>Enum.TryParse</c>, for the reason given on
    /// <see cref="ResolveDjPersonality"/>.</summary>
    public DjRemarkSize ResolveDjRemarkSize()
    {
        var wanted = DjRemarkSize?.Trim();
        foreach (var name in Enum.GetNames<DjRemarkSize>())
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<DjRemarkSize>(name);
        return Services.DjRemarkSize.Medium;
    }

    /// <summary>
    /// The parsed form of <see cref="DjPersonality"/>, or Warm if it isn't recognised.
    ///
    /// Matched against the member NAMES rather than via a bare <c>Enum.TryParse</c>, which also
    /// accepts numeric strings: <c>"3"</c> parses happily into whichever member has that value, so
    /// a stray number in a hand-edited file would silently pick an arbitrary persona instead of
    /// falling back.
    /// </summary>
    public DjPersonality ResolveDjPersonality()
    {
        var wanted = DjPersonality?.Trim();
        foreach (var name in Enum.GetNames<DjPersonality>())
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<DjPersonality>(name);
        return Services.DjPersonality.Warm;
    }

    /// <summary>
    /// Reject a harvested segment whose whole-file music fraction is below this. 0 disables it.
    ///
    /// <para>Was 0 for a long time, and correctly so: before the detector gained pulse strength,
    /// confirmed-good deep house scored 0.00 — the same as a confirmed ad break — so no threshold
    /// on this number could be right.</para>
    ///
    /// <para>0.15 now, and deliberately modest. Across every session since the re-fit, the
    /// sub-40% keeps were:</para>
    /// <code>
    ///  5.8%  THIS STATION WILL CONTINUE AFTER THIS BREAK   ad
    /// 11.3%  ADBREAK_120000 2                              ad
    /// 21.6%  All I Need                                    song (hip hop)
    /// 29.7%  HOUSE OF 1,000 PLEASURES
    /// 36.6%  Cry Baby Cry                                  song
    /// 37.4%  ADBREAK_120000 4                              ad
    /// 38.0%  Valley Of The Kings                           song
    /// </code>
    /// <para>An ad at 37.4% sits between two songs, so <b>the classes still overlap and no
    /// threshold separates them.</b> The floor is therefore only a backstop for the obviously
    /// non-musical, not a classifier: 0.15 clears both low ads while leaving 6.6 points under the
    /// lowest genuine song. It buys little on its own — the title filter and the enrichment
    /// <c>is_song</c> verdict catch all three of those ads by name — and it is set this low
    /// because the one song near it is hip hop, a beat-driven genre the detector has always
    /// underrated. Raise it only against a corpus that includes such genres.</para>
    /// </summary>
    public double DjMusicFractionFloor { get; set; } = 0.15;

    /// <summary>
    /// Show a Windows notification when the DJ introduces a track — title, artist and the DJ's
    /// remark. On by default because it is the point of the feature, and one click to turn off:
    /// the alternative is shipping something the listener has to go and find.
    ///
    /// <para>Only fires for a track introduction, never for the DJ's between-track patter, which
    /// is session chatter rather than an announcement about a song.</para>
    /// </summary>
    public bool DjNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Whether to ask before a mode switch stops what is playing (#46).
    ///
    /// <para>On by default because the person it protects is the one who does not yet know that
    /// the pills are exclusive — and off after they say so, because by then the dialog is pure
    /// friction on the app's most-used control.</para>
    /// </summary>
    public bool ConfirmModeSwitch { get; set; } = true;

    /// <summary>
    /// Set once the welcome has been shown (#50). Deliberately not a version number: this is an
    /// introduction for someone who has never seen the app, not release notes, and showing it
    /// again after an upgrade would be showing it to the wrong person.
    /// </summary>
    public bool HasSeenWelcome { get; set; }

    /// <summary>
    /// Bumped whenever a default changes in a way an existing settings file should adopt. Without
    /// it a new default only reaches new installs: every existing file already has the old value
    /// written out, so the change is invisible to exactly the people running the app.
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>Resolved library folder (the stored value or the default).</summary>
    public string ResolveLibraryFolder() =>
        string.IsNullOrWhiteSpace(LibraryFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic, Environment.SpecialFolderOption.Create), "Crystal Radio")
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "RadioPlayer");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly ISecretProtector _secrets;

    /// <param name="secrets">How the API key is protected at rest — the head's choice
    /// (<see cref="DpapiSecretProtector"/> on Windows).</param>
    public SettingsStore(ISecretProtector secrets) => _secrets = secrets;

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
                    settings.DjDiskSpaceMb = Math.Clamp(settings.DjDiskSpaceMb, 100, 30_000);
                    settings.IntroSkipSeconds = Math.Clamp(settings.IntroSkipSeconds, 0, 10);
                    settings.OutroGuardSeconds = Math.Clamp(settings.OutroGuardSeconds, 0, 10);
                    settings.DjMinSongSeconds = Math.Clamp(settings.DjMinSongSeconds, 0, 600);
                    settings.DjStationIdleMinutes = Math.Clamp(settings.DjStationIdleMinutes, 1, 240);
                    settings.DjMusicFractionFloor = Math.Clamp(settings.DjMusicFractionFloor, 0, 1);
                    return Migrate(settings);
                }
            }
        }
        catch
        {
            // Corrupt/unreadable — fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>Current settings schema. Bump when a default change must reach existing files.</summary>
    private const int CurrentSettingsVersion = 1;

    /// <summary>
    /// Brings an older settings file up to date. Needed because a changed default is invisible to
    /// anyone who already has a settings file: their old value is written out explicitly, so they
    /// keep it forever. The migration is applied in memory on every load and persisted by the next
    /// Save, so it is safe to run repeatedly.
    /// </summary>
    internal static AppSettings Migrate(AppSettings settings)
    {
        if (settings.SettingsVersion < 1)
        {
            // The music-fraction floor was 0 (off) while the detector could not separate music
            // from speech. It can now, and two ad breaks reached a real mix while it was off — so
            // existing installs should pick up the new default rather than keep the disabled value.
            // Only if it is still at the old default: a deliberate non-zero choice is left alone.
            if (settings.DjMusicFractionFloor == 0)
                settings.DjMusicFractionFloor = 0.15;
        }

        settings.SettingsVersion = CurrentSettingsVersion;
        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, Options);
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(FilePath, json);
                return;
            }

            // Off Windows the key is only as private as this file (PlainSecretProtector is base64),
            // so the file is owner-only: created 0600, and an older file narrowed BEFORE the key is
            // written into it rather than after.
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.Exists(FilePath))
                File.SetUnixFileMode(FilePath, OwnerOnly);
            using var stream = new FileStream(FilePath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = OwnerOnly,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Decrypts and returns the stored API key, or null if none is set / undecryptable.</summary>
    public string? GetApiKey()
    {
        var stored = Load().ApiKeyProtected;
        return string.IsNullOrWhiteSpace(stored) ? null : _secrets.Unprotect(stored);
    }

    /// <summary>
    /// Encrypts and persists the API key (or clears it when null/blank), leaving other
    /// settings untouched (load-modify-save so it never clobbers Volume).
    /// </summary>
    public void SetApiKey(string? plaintext)
    {
        var settings = Load();
        settings.ApiKeyProtected = string.IsNullOrWhiteSpace(plaintext) ? null : _secrets.Protect(plaintext);
        Save(settings);
    }
}
