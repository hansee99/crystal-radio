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
