using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Environment.GetFolderPath returns "" when the folder doesn't exist yet, and on Windows these
/// folders always do — so the bug is invisible here. On a fresh Raspberry Pi ~/.local/share did
/// not exist: Path.Combine("", "RadioPlayer") went relative, and the databases and logs landed in
/// the app folder, which every update deletes. Every call in Core must pass
/// SpecialFolderOption.Create. This scans the source so a new store can't quietly bring it back.
/// </summary>
public class SpecialFolderUsageTests
{
    [Fact]
    public void EveryGetFolderPathCallInCoreCreatesTheFolder()
    {
        var core = Path.Combine(Repo.Root(), "src", "RadioPlayer.Core");
        Assert.True(Directory.Exists(core), $"Core source not found at {core}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                foreach (Match call in Regex.Matches(lines[i], @"GetFolderPath\([^)]*\)"))
                    if (!call.Value.Contains("SpecialFolderOption.Create"))
                        offenders.Add($"{Path.GetRelativePath(core, file)}:{i + 1}: {call.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "GetFolderPath without SpecialFolderOption.Create (returns \"\" on Linux when the folder is missing):\n"
            + string.Join("\n", offenders));
    }
}
