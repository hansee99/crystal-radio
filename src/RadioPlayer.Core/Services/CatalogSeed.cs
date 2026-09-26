using System.IO;

namespace RadioPlayer.Services;

/// <summary>
/// Gives a fresh install a local station catalog to start from (#40, #43).
///
/// <para>Everything built on the enrichment database is cold on a new machine: semantic recall
/// contributes nothing to the search pool, and the offline fallback (#26) has nothing to fall back
/// on. That is worst precisely when it matters most — the first sessions are the ones most likely
/// to meet "Couldn't reach the station directory", and they are the ones with no cache.</para>
///
/// <para>So the installer ships a pre-built catalog next to the executable and this copies it into
/// the user's profile the first time they run. Chosen over seeding from the network on first run
/// because that would reintroduce the very dependency it exists to remove: if the directory is down
/// on a fresh install, a network seed fails too, while a shipped file works offline from the first
/// launch.</para>
/// </summary>
public static class CatalogSeed
{
    /// <summary>Where the installer puts the shipped catalog, relative to the executable.</summary>
    public const string SeedFileName = "seed/enrichment.db";

    /// <summary>
    /// Copies the shipped catalog into place if the user has none yet. Never overwrites: a
    /// returning user's own catalog is better than the shipped snapshot — it is larger, it matches
    /// what they actually listen to, and it may hold descriptions this build's seed does not.
    /// <para>Best-effort. A missing or unreadable seed is normal (a dev build has no installer
    /// behind it), and nothing here may stop the app starting.</para>
    /// </summary>
    /// <returns>True when a catalog was seeded, for logging and tests.</returns>
    public static bool EnsureSeeded(string? seedPath = null, string? databasePath = null)
    {
        try
        {
            var target = databasePath ?? EnrichmentStore.DefaultDatabasePath();
            if (File.Exists(target))
                return false;   // the user already has one — leave it alone

            var source = seedPath ?? Path.Combine(AppContext.BaseDirectory, SeedFileName);
            if (!File.Exists(source))
                return false;   // no seed shipped with this build

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            AppLog.Info($"[Catalog] seeded the station catalog from {source} "
                        + $"({new FileInfo(target).Length / 1024} KB)");
            return true;
        }
        catch (Exception ex)
        {
            // A cold catalog costs quality, not correctness: the app still searches the directory.
            AppLog.Debug($"[Catalog] could not seed the station catalog: {ex.Message}");
            return false;
        }
    }
}
