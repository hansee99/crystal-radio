using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RadioPlayer.Tests;

/// <summary>For tests that check the source tree rather than a build: found from this file's own
/// compile-time path, so it works from any output directory and on any OS.</summary>
internal static class Repo
{
    public static string Root([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>The product version — crystal-radio.csproj's &lt;Version&gt;, which the release
    /// scripts read and src/Directory.Build.props copies to every project under src/.</summary>
    public static Version ProductVersion()
    {
        var csproj = File.ReadAllText(Path.Combine(Root(), "crystal-radio.csproj"));
        return Version.Parse(Regex.Match(csproj, "<Version>([^<]+)</Version>").Groups[1].Value);
    }
}
