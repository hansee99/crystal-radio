using System.Windows;
using RadioPlayer.Controls;
using RadioPlayer.Services;

namespace RadioPlayer;

/// <summary>
/// Modal settings dialog: the Anthropic API key (persisted encrypted via
/// <see cref="SettingsStore"/> (DPAPI) — never written anywhere in plain text) and the
/// library folder that saved songs are copied into.
/// </summary>
public partial class OptionsDialog : AppDialog
{
    private readonly SettingsStore _store;

    public OptionsDialog(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        InitializeComponent();
        _store = store;

        ApiKeyBox.Text = store.GetApiKey() ?? string.Empty;
        LibraryFolderBox.Text = store.Load().ResolveLibraryFolder();

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
        _store.Save(settings);

        DialogResult = true;
    }
}
