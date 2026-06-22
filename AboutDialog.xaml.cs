using System.Reflection;
using System.Windows;

namespace RadioPlayer;

/// <summary>Minimal about box: name, version, short description.</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {GetVersion()}";
    }

    private static string GetVersion()
    {
        // Prefer the informational version (set from <Version> in the csproj); strip any
        // build metadata (e.g. a "+<githash>" suffix) that tooling might append.
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    }
}
