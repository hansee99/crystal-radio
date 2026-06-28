# Aurora Radio — technical reference

Architecture, AI search design, and project structure for contributors and developers.

## Tech stack

- **C# / .NET 10**, **WPF** (`net10.0-windows10.0.19041.0`; the Windows SDK version in
  the TFM is required to project the WinRT/SMTC types).
- **Audio engine:** [BASS](https://www.un4seen.com/) via
  [ManagedBass](https://github.com/ManagedBass/ManagedBass) + `ManagedBass.Aac`.
- **OS media integration:** SMTC via the projected
  `Windows.Media.SystemMediaTransportControlsInterop`.
- **AI search:** the [Anthropic API](https://docs.claude.com/) called directly over REST
  (no SDK), the [Radio Browser](https://api.radio-browser.info) directory, **SQLite**
  (`Microsoft.Data.Sqlite`) for the enrichment cache, and local embeddings via
  **ONNX Runtime** (`Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers`).
- **Architecture:** MVVM, with all BASS access funnelled through a single `RadioEngine`
  so the engine can be swapped without touching the rest of the app. AI services are
  isolated behind interfaces and never touch playback directly.
- **Platform:** **x64** (must match the native BASS DLLs).

## Project structure

| Path | Responsibility |
| --- | --- |
| `Services/RadioEngine.cs` | The only class that talks to BASS: play/pause/stop/volume, ICY metadata, reconnection, stream probing. Marshals BASS callbacks to the UI thread. |
| `Services/SmtcController.cs` | Owns the SMTC instance; pushes now-playing info and maps the flyout/media-key buttons (incl. next-track) onto the app. |
| `Services/StationStore.cs` | Loads/saves the station list as JSON under `%AppData%\RadioPlayer\`. |
| `Services/SettingsStore.cs` | Persists volume and the DPAPI-encrypted Anthropic API key. |
| `Services/StationSearchService.cs` | Radio Browser API client; maps results to playable `Station` candidates. |
| `Services/PromptInterpreter.cs` | Pattern A — translates a prompt into Radio Browser search parameters (structured output). |
| `Services/AgenticSearchService.cs` | Pattern B — two-tool agentic loop (web search + Radio Browser lookup). |
| `Services/LlmSearchRanker.cs` | LLM relevance re-rank of the merged candidate pool; its strictness also drives the web-escalation decision. |
| `Services/EnrichmentService.cs`, `EnrichmentStore.cs` | Phase 1 — distil + cache per-station descriptions in SQLite under `%LocalAppData%\RadioPlayer\`. |
| `Services/SemanticSearchService.cs`, `MiniLmEmbeddingProvider.cs` | Phase 2 — local ONNX embeddings + brute-force cosine search. |
| `ViewModels/MainViewModel.cs` | Playback state, commands, station list, now-playing, search orchestration. |
| `Views/MainWindow.xaml(.cs)` | UI layout, taskbar thumb buttons, custom window chrome, HWND/SMTC bootstrap. |
| `Views/Theme.xaml` | Aurora theme: brushes, icon geometries, all control styles. |
| `Views/StationDialog.xaml(.cs)` | Add/edit station editor. |
| `Views/OptionsDialog.xaml(.cs)` | Settings dialog (Anthropic API key). |
| `Views/AboutDialog.xaml(.cs)` | About dialog. |
| `Views/StationDialogService.cs` | Spawns the station dialog from the view model (keeps VM free of Window references). |
| `App.xaml(.cs)` | App entry point + single-instance guard (named mutex + EventWaitHandle). |
| `Models/Station.cs` | `{ Name, Url, Format, Description }` station record. |
| `MlAssets/` | BERT vocab (`vocab.txt`, tracked) and the `all-MiniLM-L6-v2.onnx` model (git-ignored, ~90 MB — see [MlAssets/README.md](../MlAssets/README.md)). |
| `tools/SeedEnrichment/` | Console tool to pre-fill the enrichment cache from the Radio Browser popularity ranking. |

## AI-assisted station search

### The core principle: the LLM is not the database

An LLM has no reliable, current knowledge of which streams exist or what their URLs are,
and will hallucinate dead or invented stream URLs if asked directly. The rule that governs
the entire feature:

> **All real station data — names, stream URLs, codecs — comes from Radio Browser.**
> The model's only job is to bridge the gap between a natural-language prompt and what the
> directory can actually look up.

### Data source: Radio Browser

[Radio Browser](https://api.radio-browser.info) is a free, community-maintained catalog
with no authentication requirement. It actively checks whether streams are alive, so
results can be filtered to stations that are actually reachable. Each result carries
`stationuuid`, `url_resolved` (follows redirects), `codec`, `hls`, `lastcheckok`,
`homepage`, and lightweight genre tags.

Only stations with `codec` ∈ {MP3, AAC, AAC+}, `hls == false`, and `lastcheckok == true`
reach the player.

### One unified pipeline (no literal-vs-fuzzy routing)

There is no classifier and no A-vs-B branch. Every prompt runs one pipeline — several cheap
recall sources feed one ranker, and web search is the only escalation, fired by result
quality rather than a guess about intent (`MainViewModel.RunUnifiedSearchAsync`):

1. **Cheap recall, in parallel** — Pattern A (structured Radio Browser lookup) and local
   semantic search. Pool their candidates, deduped on the resolved stream URL.
2. **Re-rank** the pool with the strict LLM ranker (drops loosely-related stations; this
   replaces the old `Broaden` fallback).
3. **Escalate to web** (Pattern B) only when the re-ranked cheap pool can't fill a page
   (`shortlist.Count < MaxResults`), then re-rank the combined pool. A thin shortlist is the
   signal that the directory tags + local catalog genuinely don't cover the prompt — a niche
   genre, a multi-country region ("Scandinavia"), a stylistic qualifier ("contemporary") —
   exactly when web discovery earns its per-search cost.
4. **Validate** streams and present.

So "BBC Radio 1" is filled by the cheap sources and never pays for web, while "contemporary
metal from scandinavia" comes back thin and escalates automatically. As the enrichment DB
and vector index grow (Pattern B's finds get enriched + embedded), the cheap pool covers more
and escalates less over time.

### Pattern A — structured output

The model returns a strict JSON object (`tags`, `name`, `country`, `language`,
`bitrateMin`, `order`). The app queries Radio Browser with those fields. One call, no
conversation, no state.

### Pattern B — agentic two-tool loop

Two tools, two execution models:

- `web_search` — Anthropic's **server-executed** tool. Added to the `tools` array; the
  model decides when to search, Anthropic runs it, cited result blocks come back
  automatically. Not executed by the app.
- `search_radio_browser` — **client-executed** tool wrapping `StationSearchService`.
  The model emits a `tool_use` block; the app runs the query and returns a `tool_result`
  with codec/HLS-filtered candidates.

The loop drives `messages.create` to `stop_reason == "end_turn"`, executing client tool
calls and passing back encrypted web-search result blocks unmodified. The final answer is
a JSON list of `stationuuid` values — each validated against tool-fetched Radio Browser
data before playback.

### Phase 1 — local enrichment

Radio Browser's tags are thin. Phase 1 builds a richer, persistent description per
station: fetch the homepage, extract text, distil with a cheap model (Haiku) into a
short profile (genre, mood, era), cache in SQLite keyed by `stationuuid`.

Enrichment is lazy (triggered when a station appears in results or is played) and
best-effort (never blocks search or playback; fallbacks: name + tags + web snippet when
the homepage is dead or unreadable).

These descriptions feed the re-ranking step in both Pattern A and Pattern B immediately,
making results smarter as the local catalog fills.

### Phase 2 — semantic search

With enriched descriptions in SQLite, each description is embedded via `all-MiniLM-L6-v2`
(384-dim, ONNX, offline) and stored as a BLOB alongside the description row. At query
time: embed the query, brute-force cosine similarity against all embedded rows, hard-filter
to playable, return top-K.

The query and document embeddings **must use the same model** — the model identifier is
stored with every vector. Switching models requires re-embedding existing rows.

Key implementation note: the ONNX model returns per-token outputs. You must **mean-pool
over tokens using the attention mask, then L2-normalize** — do not use the CLS token and
do not skip pooling or similarity scores will be garbage.

### The self-improving loop

Pattern B's finds flow through the enrichment pipeline automatically: homepage fetched,
description generated, embedding stored. The next similar query finds those stations via
the cheap sources (Pattern A tags + the local semantic index), so the pool fills the page
without escalating to web. Over time the local side contributes increasingly more and web
search fires less — the enrichment layer augments web discovery rather than replacing it.

## Key gotchas

- **AAC needs the add-on.** BASS core does not decode AAC. Use
  `BassAac.CreateStream(url, ...)` for AAC/AAC+ stations, `Bass.CreateStream(url, ...)`
  for MP3/OGG only.
- **SMTC in WPF.** `GetForCurrentView()` throws. Use the projected static class
  `Windows.Media.SystemMediaTransportControlsInterop.GetForWindow(hwnd)` after
  `OnSourceInitialized` (HWND must exist).
- **BASS callbacks fire on BASS-owned threads.** Marshal to the UI thread via
  `Dispatcher` before touching the view model, UI, or SMTC.
- **Pack URI for bundled font.** The Hanken Grotesk TTFs are embedded as WPF resources
  and referenced as `/aurora-radio;component/Fonts/static/#Hanken Grotesk`. If the
  assembly name changes, update `Views/Theme.xaml`.
- **`ClipToBounds` is rectangular.** WPF's `ClipToBounds` clips to layout bounds, not
  the visual rounded shape of a `Border`. The shell border uses `UIElement.Clip` (set in
  code-behind via `SizeChanged`) to clip children to the rounded-rectangle shape.
