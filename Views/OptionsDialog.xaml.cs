using System.Windows;
using RadioPlayer.Services;

namespace RadioPlayer;

/// <summary>
/// Modal settings dialog. Currently just the Anthropic API key; the layout is structured so
/// future settings slot in as additional labelled fields. The key is persisted encrypted via
/// <see cref="SettingsStore"/> (DPAPI) — this dialog never writes it anywhere in plain text.
/// </summary>
public partial class OptionsDialog : Window
{
    private readonly SettingsStore _store;

    public OptionsDialog(SettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        InitializeComponent();
        _store = store;

        ApiKeyBox.Text = store.GetApiKey() ?? string.Empty;

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _store.SetApiKey(ApiKeyBox.Text.Trim()); // blank clears the stored key
        DialogResult = true;
    }
}
