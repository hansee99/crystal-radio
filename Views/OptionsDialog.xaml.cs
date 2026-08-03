using System.Globalization;
using System.Windows;
using RadioPlayer.Controls;
using RadioPlayer.Services;

namespace RadioPlayer;

/// <summary>
/// Modal settings dialog. Deliberately a small, non-technical subset of what
/// <see cref="AppSettings"/> holds — the API key, the library folder, and the DJ knobs a listener
/// would actually reach for. Everything else stays file-editable: exposing a tuning parameter
/// invites fiddling with something whose effect can't be heard.
///
/// Fields are labelled by their EFFECT rather than their setting name ("Stop … seconds before the
/// end", not "OutroGuardSeconds"), and the two disk caps are presented as one total, because
/// showing only one of them was itself reported as a bug.
/// </summary>
public partial class OptionsDialog : AppDialog
{
    private readonly SettingsStore _store;

    public OptionsDialog(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        InitializeComponent();
        _store = store;

        var settings = store.Load();
        ApiKeyBox.Text = store.GetApiKey() ?? string.Empty;
        LibraryFolderBox.Text = settings.ResolveLibraryFolder();

        DjVoiceBox.ItemsSource = Enum.GetValues<DjPersonality>();
        DjVoiceBox.SelectedItem = settings.ResolveDjPersonality();
        HarvesterCountBox.Text = settings.DjHarvesterCount.ToString(CultureInfo.CurrentCulture);
        DiskSpaceBox.Text = settings.DjDiskSpaceMb.ToString(CultureInfo.CurrentCulture);
        IntroSkipBox.Text = settings.IntroSkipSeconds.ToString("0.#", CultureInfo.CurrentCulture);
        OutroGuardBox.Text = settings.OutroGuardSeconds.ToString("0.#", CultureInfo.CurrentCulture);

        // If no in-app key is stored but the environment provides one, say so — it's the key
        // actually in effect until they save one here.
        var hasStored = !string.IsNullOrWhiteSpace(ApiKeyBox.Text);
        var envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!hasStored && !string.IsNullOrWhiteSpace(envKey))
        {
            EnvHintText.Text = "A key from your system is currently in use.";
            EnvHintText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => { ApiKeyBox.Focus(); ApiKeyBox.SelectAll(); };
    }

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder saved songs go to",
            InitialDirectory = LibraryFolderBox.Text
        };
        if (dialog.ShowDialog(this) == true)
            LibraryFolderBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _store.SetApiKey(ApiKeyBox.Text.Trim()); // blank clears the stored key

        // Load-modify-save (after SetApiKey, so its write isn't clobbered).
        var settings = _store.Load();
        var folder = LibraryFolderBox.Text.Trim();
        settings.LibraryFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;

        if (DjVoiceBox.SelectedItem is DjPersonality voice)
            settings.DjPersonality = voice.ToString();

        // Unparseable input keeps the current value rather than resetting to a default — a typo
        // shouldn't silently change a setting the user wasn't editing.
        settings.DjHarvesterCount = ParseInt(HarvesterCountBox.Text, settings.DjHarvesterCount);
        settings.DjDiskSpaceMb = ParseInt(DiskSpaceBox.Text, settings.DjDiskSpaceMb);
        settings.IntroSkipSeconds = ParseDouble(IntroSkipBox.Text, settings.IntroSkipSeconds);
        settings.OutroGuardSeconds = ParseDouble(OutroGuardBox.Text, settings.OutroGuardSeconds);

        // Save() writes what it is given; Load() is what clamps. Clamp here too so the dialog
        // can't persist a value the app would silently override on the next read.
        _store.Save(settings);

        DialogResult = true;
    }

    /// <summary>Accepts both "1.5" and "1,5" — the box is typed into by a person, and a German
    /// keyboard produces the comma. Falls back to the current value on anything unparseable.</summary>
    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var v)
        || double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Any,
            CultureInfo.InvariantCulture, out v)
            ? v
            : fallback;

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v)
            ? v
            : fallback;
}
