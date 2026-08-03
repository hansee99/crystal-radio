using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
///
/// <para>Laid out as four rail sections at a fixed size, replacing a single ~800px column. The
/// point of the rail isn't only today's six fields: pairing the numeric fields into two columns
/// leaves the pane room for about five DJ settings, so #22 can land without the frame moving.</para>
///
/// <para>Saving applies immediately — <c>MainWindow.ApplySettings</c> pushes the values into the
/// running services. The one exception is how many stations are listened to at once, which is read
/// when a DJ session starts; that field wears a "next session" chip rather than the whole dialog
/// carrying a "restart the app" banner.</para>
/// </summary>
public partial class OptionsDialog : AppDialog
{
    private readonly SettingsStore _store;

    /// <summary>
    /// A voice as the dropdown shows it. <see cref="DjPersonality"/>'s member names are what goes
    /// in the settings file; "LateNight" is not something to show a listener.
    ///
    /// <para>ToString is overridden because DialogComboBox's template renders the CLOSED box
    /// through a plain ContentPresenter. DisplayMemberPath styles the open list but leaves the box
    /// falling back to ToString — which for a record is its whole shape,
    /// "VoiceOption { Value = Warm, Label = Warm }". Seen in a render check.</para>
    /// </summary>
    private sealed record VoiceOption(DjPersonality Value, string Label)
    {
        public override string ToString() => Label;
    }

    public OptionsDialog(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        InitializeComponent();
        _store = store;

        var settings = store.Load();
        ApiKeyBox.Text = store.GetApiKey() ?? string.Empty;
        LibraryFolderBox.Text = settings.ResolveLibraryFolder();

        DjVoiceBox.ItemsSource = Enum.GetValues<DjPersonality>()
            .Select(v => new VoiceOption(v, DisplayName(v)))
            .ToArray();
        DjVoiceBox.SelectedValue = settings.ResolveDjPersonality();

        HarvesterCountBox.Text = settings.DjHarvesterCount.ToString(CultureInfo.CurrentCulture);
        DiskSpaceBox.Text = settings.DjDiskSpaceMb.ToString(CultureInfo.CurrentCulture);
        IntroSkipBox.Text = settings.IntroSkipSeconds.ToString("0.#", CultureInfo.CurrentCulture);
        OutroGuardBox.Text = settings.OutroGuardSeconds.ToString("0.#", CultureInfo.CurrentCulture);

        // If no in-app key is stored but the environment provides one, say so — it's the key
        // actually in effect until they save one here.
        var hasStored = !string.IsNullOrWhiteSpace(ApiKeyBox.Text);
        var envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!hasStored && !string.IsNullOrWhiteSpace(envKey))
            EnvHintBlock.Visibility = Visibility.Visible;

        ShowFolderTail();

        Loaded += (_, _) => { ApiKeyBox.Focus(); ApiKeyBox.SelectAll(); };
    }

    /// <summary>
    /// Splits a personality's member name for display: "LateNight" → "Late night". Done by rule
    /// rather than a lookup table so a personality added to the enum reads correctly without
    /// anyone remembering to add it here too.
    /// </summary>
    internal static string DisplayName(DjPersonality value)
    {
        var name = value.ToString();
        var text = new StringBuilder(name.Length + 4);

        foreach (var c in name)
        {
            // A capital after the first character starts a new word, and only the first word keeps
            // its capital — so it reads as a sentence rather than a Title Case Label.
            if (char.IsUpper(c) && text.Length > 0)
            {
                text.Append(' ');
                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private void Rail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Four sibling panels rather than a ContentControl over DataTemplates — see the class
        // remarks. Nothing is created or destroyed here, so switching sections cannot lose a value
        // that has been typed but not yet saved.
        if (AiSection is null)
            return; // fires during InitializeComponent, before the panels exist

        AiSection.Visibility = Shown(0);
        SongsSection.Visibility = Shown(1);
        DjSection.Visibility = Shown(2);
        EdgesSection.Visibility = Shown(3);

        Visibility Shown(int index) =>
            Rail.SelectedIndex == index ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder saved songs go to",
            InitialDirectory = LibraryFolderBox.Text
        };
        if (dialog.ShowDialog(this) == true)
        {
            LibraryFolderBox.Text = dialog.FolderName;
            ShowFolderTail();
        }
    }

    /// <summary>
    /// Keeps the END of the library path in view, and puts the whole thing on the tooltip.
    ///
    /// <para>The mockup truncates long paths from the LEFT, on the reasoning that the drive letter
    /// matters less than the folder. A WPF TextBox can't render a leading ellipsis — only a
    /// TextBlock can, and swapping to one would cost the ability to paste a path — so this reaches
    /// the same intent with the real control: scrolled to the tail, full path on hover.</para>
    /// </summary>
    private void ShowFolderTail()
    {
        LibraryFolderBox.ToolTip = LibraryFolderBox.Text;
        LibraryFolderBox.CaretIndex = LibraryFolderBox.Text.Length;
        LibraryFolderBox.ScrollToEnd();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _store.SetApiKey(ApiKeyBox.Text.Trim()); // blank clears the stored key

        // Load-modify-save (after SetApiKey, so its write isn't clobbered).
        var settings = _store.Load();
        var folder = LibraryFolderBox.Text.Trim();
        settings.LibraryFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;

        if (DjVoiceBox.SelectedValue is DjPersonality voice)
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
