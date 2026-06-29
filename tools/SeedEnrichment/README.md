# SeedEnrichment — pre-fill the enrichment cache

A small console tool that pre-fills the enrichment database with the most popular stations
(by Radio Browser `clickcount`) so **semantic search works immediately** instead of
cold-starting. It reuses the app's services (`StationSearchService`, `EnrichmentService`,
`EnrichmentStore`, `MiniLmEmbeddingProvider`) and writes to the same cache the app uses:
`%LocalAppData%\RadioPlayer\enrichment.db`.

## Usage

Run it (the app project is built automatically as a dependency):

```sh
# Top 200, full distillation (homepage -> Haiku -> embed). Needs ANTHROPIC_API_KEY.
set ANTHROPIC_API_KEY=sk-ant-...        # PowerShell: $env:ANTHROPIC_API_KEY="sk-ant-..."
dotnet run --project tools/SeedEnrichment -- --count 200

# Free / fast: descriptions from name + tags only, no LLM calls.
dotnet run --project tools/SeedEnrichment -- --count 500 --tags-only

# Only stations that publish extended metadata.
dotnet run --project tools/SeedEnrichment -- --count 200 --extended
```

| Arg | Default | Meaning |
|---|---|---|
| `--count N` | 200 | How many popular stations to seed |
| `--tags-only` | off | Skip the LLM; build descriptions from name + tags (free) |
| `--extended` | off | Restrict to `has_extended_info=true` stations |
| `--db <path>` | app cache | Override the SQLite path (e.g. for testing) |

## Notes

- **Run with the app closed.** The DB uses WAL so concurrent access won't corrupt, but
  closing the app avoids lock contention.
- **Idempotent / resumable.** Re-running only enriches missing or stale rows (fresh rows
  are skipped), so you can stop (Ctrl+C) and resume, or bump `--count` later.
- **Embeddings are local & free** (all-MiniLM-L6-v2 via ONNX); only the optional homepage
  distillation uses the Anthropic API.
- Not part of `crystal-radio.sln` on purpose — it's a separate, on-demand utility. The app's
  own build excludes `tools/**`.
