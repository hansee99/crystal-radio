using System.Net.Http;
using RadioPlayer.Services;

// Pre-fills the enrichment DB with the most popular stations (by clickcount) so semantic
// search is useful on a fresh machine instead of cold-starting. Reuses the app's services
// and writes to the same %LocalAppData%\RadioPlayer\enrichment.db.
//
//   dotnet run --project tools/SeedEnrichment -- --count 200
//   dotnet run --project tools/SeedEnrichment -- --count 500 --tags-only
//   dotnet run --project tools/SeedEnrichment -- --count 200 --extended

var count = ArgInt(args, "--count", 200);
var tagsOnly = args.Contains("--tags-only", StringComparer.OrdinalIgnoreCase);
var extended = args.Contains("--extended", StringComparer.OrdinalIgnoreCase);
var dbPath = ArgStr(args, "--db", null);

// Full distill needs the key; --tags-only forces the free path (apiKey == null).
var apiKey = tagsOnly ? null : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (!tagsOnly && string.IsNullOrWhiteSpace(apiKey))
    Console.WriteLine("Note: ANTHROPIC_API_KEY not set — falling back to tags-only descriptions.");

// Graceful Ctrl+C.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nCancelling…"); };

using var store = new EnrichmentStore(dbPath);
var mlDir = Path.Combine(AppContext.BaseDirectory, "MlAssets");
using var embeddings = new MiniLmEmbeddingProvider(
    Path.Combine(mlDir, "all-MiniLM-L6-v2.onnx"), Path.Combine(mlDir, "vocab.txt"));
if (!embeddings.IsAvailable)
    Console.WriteLine("Warning: embedding model unavailable — descriptions will be cached but not embedded.");

var search = new StationSearchService(new HttpClient());
var enrich = new EnrichmentService(new HttpClient(), new HttpClient(), store, embeddings, apiKey);

Console.WriteLine($"Fetching top {count} stations by popularity{(extended ? " (extended info only)" : "")}…");
var candidates = await search.GetPopularAsync(count, extended, cts.Token);
Console.WriteLine($"Fetched {candidates.Count} playable stations. Enriching + embedding " +
                  $"({(apiKey is null ? "tags-only" : "homepage distill")})…");

var lastReport = 0;
var progress = new Progress<(int done, int total)>(p =>
{
    // Throttle console spam to ~every 10 stations.
    if (p.done - lastReport < 10 && p.done != p.total) return;
    lastReport = p.done;
    Console.WriteLine($"  [{p.done}/{p.total}]");
});

try
{
    await enrich.EnrichManyAsync(candidates, progress, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopped early (partial progress saved).");
}

var embedded = embeddings.IsAvailable ? store.GetEmbeddedRows(embeddings.ModelId).Count : 0;
Console.WriteLine($"Done. Embedded rows in cache (model {embeddings.ModelId}): {embedded}.");
return 0;

static int ArgInt(string[] a, string name, int fallback)
{
    var i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : fallback;
}

static string? ArgStr(string[] a, string name, string? fallback)
{
    var i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback;
}
