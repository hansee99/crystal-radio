# Aurora Radio

A lightweight Windows desktop app for playing internet radio streams
(Icecast/Shoutcast), with playback surfaced natively in the OS:

- **System Media Transport Controls (SMTC)** — the now-playing widget in the Windows 11
  volume/quick-settings flyout and lock screen, plus hardware media-key handling.
- **Taskbar thumbnail buttons** — play/stop shown in the live preview when hovering the
  app's taskbar icon.

It also includes an optional, online-only **AI-assisted station search** that turns a
plain-language prompt ("something mellow for late-night coding") into real, playable
stations — see [AI-assisted station search](#ai-assisted-station-search) below.

Built with WPF on .NET, using the [BASS](https://www.un4seen.com/) audio library.

![Aurora Radio — now playing, with the AI search panel](doc/screenshot.png)

## Features

- Stream AAC and MP3 internet radio (Icecast/Shoutcast).
- **Live track titles** via ICY metadata.
- **Station management** — add / edit / delete stations (friendly name + stream URL +
  format + optional description), persisted as JSON under
  `%AppData%\RadioPlayer\stations.json`.
- **AI-assisted search** — describe a vibe and get real, playable stations (optional;
  requires an Anthropic API key).
- **Next-station** button that cycles through the list (also mapped to the media-key /
  SMTC "next track" button).
- Double-click a station to play it; single running instance (a second launch surfaces
  the existing window).
- Volume control (persisted), automatic **reconnection** on stalls / dropped streams.
- Custom borderless, drag-anywhere UI with a teal theme and a generated app icon.

## AI-assisted station search

You describe what you want to hear — "something mellow for late-night coding" or "upbeat
80s synthpop without talk segments" — and the app finds real, playable stations that
match. This feature was deliberately built as a learning project in AI engineering, and
the architecture reflects a set of practical decisions worth understanding before reading
the code.

It is **optional and online-only**: the player works fully without it. See
[Enabling AI search](#enabling-ai-search) for setup.

### The core principle: the LLM is not the database

The obvious approach — ask a language model to name some stations and play whatever it
suggests — doesn't work. Language models have no reliable, current knowledge of which
internet radio streams exist, what their URLs are, or whether those streams are live.
They will invent plausible-sounding station names and confidently produce stream URLs
that are dead, wrong, or entirely fabricated. Stream URLs in particular churn constantly,
so even a model with recent training data can't be trusted as a source of them.

This leads to the rule that governs the entire feature:

> **The LLM is not the database.** All real station data — names, stream URLs, codecs —
> comes from a verified directory. The model's only job is to bridge the gap between what
> a user describes in natural language and what that directory can actually look up.

### The data layer: Radio Browser

The station directory used throughout is [Radio Browser](https://api.radio-browser.info),
a free, community-maintained catalog of internet radio stations with no authentication
requirement. It covers tens of thousands of stations and, critically, actively checks
whether streams are alive — so results can be filtered to stations that are actually
reachable. Every station that ever reaches the player has been validated through this
source first (and, as a final guard, the app probes each stream with the audio engine
before showing it in results).

The limitation of Radio Browser is also the reason this feature exists: its metadata is
thin. Each station carries a handful of human-applied genre tags, a country, and a
bitrate — but no mood, no era, no description of what the station actually sounds like.
A search for "melancholy post-rock" or "Berlin techno" will produce either nothing or
something generic. Bridging that gap is what the AI layers are for.

### A layered design: from simple to capable

The search is built as a stack of layers, each adding capability and teaching a distinct
AI engineering pattern. A cheap LLM **classifier** routes each prompt to the right path:
literal lookups take the fast path; fuzzy/vibe prompts take the semantic + web path.

#### Pattern A — structured output (literal queries)

For queries that map cleanly to catalog fields — "German news radio", "jazz from France",
"AAC streams above 128kbps" — the model functions as a query translator. It reads the
prompt and returns a small, strict JSON object describing what to search for (genre tags,
country, language, minimum bitrate). The app runs that structured query against Radio
Browser directly and presents the results.

This is the simplest possible use of a language model — one call, one structured response,
no conversation — and it teaches the most important skill in working with LLMs: asking
for structured output rather than prose, and validating what comes back before acting on
it.

#### Pattern B — web-search discovery (fuzzy and semantic queries)

Structured queries fail for soft, associative requests: "the station someone recommended
for night drives", "dreamy shoegaze", "whatever Radio Paradise-adjacent means". No set of
tags captures those, because the relevant knowledge — curated lists, community
recommendations, station reputations — lives in human-written text on the web, not in a
structured catalog.

Pattern B uses the model as an **agentic search agent** with two tools: a server-side web
search tool the model calls autonomously to find station names and descriptions, and a
client-side Radio Browser lookup tool it uses to verify and retrieve those stations from
the directory. The model decides how many searches to run, how to refine, and when it has
enough to answer.

The safety constraint is unchanged: the model's final answer is a list of `stationuuid`
values — identifiers from Radio Browser — not URLs. The app resolves each identifier back
to a stream URL it fetched itself. A URL produced by the model in prose or by a web page
is never played directly.

This pattern teaches the agentic loop: registering tools, driving a conversation to
completion across multiple model turns, and executing the right kind of tool call in the
right place (server-side and client-side tools have different execution models).

#### Phase 1 — local enrichment (building a better data asset)

Patterns A and B are online and stateless, and both are ultimately limited by the same
root cause: thin catalog metadata. Phase 1 asks: what if the app could build its own
richer description of each station over time?

When a station appears in a search result or is played, the app fetches its homepage,
extracts readable text, and asks a language model to distill a short profile from it —
genre, mood, era, what it actually sounds like — caching that profile locally in a SQLite
database keyed by the station's unique identifier. The next time that station is a
candidate, the re-ranking step has a real description to reason over, not three generic
tags.

Enrichment happens lazily and in the background. The player never waits for it, and search
and playback function identically without it. Over time the local catalog fills with
descriptions of stations the user has actually explored. Fallbacks matter: if a homepage
is dead or JavaScript-rendered and unreadable, the pipeline falls back to the station
name, its Radio Browser tags, and optionally a web snippet — something is always better
than nothing.

#### Phase 2 — semantic search (embeddings and vector retrieval)

With a growing set of enriched descriptions, it becomes possible to find stations that
*feel* like a query without sharing a single keyword with it. An embedding is a compact
numerical representation of text that captures semantic meaning — two descriptions of
"melancholy music for rainy evenings" produce vectors close to each other even if the
underlying words differ. By embedding both the station descriptions and the user's query
with the same model, the app ranks stations by conceptual similarity rather than keyword
overlap.

The implementation deliberately avoids a purpose-built vector database: embeddings are
stored as binary blobs in the same SQLite table as the descriptions, and similarity search
is brute-force cosine similarity in memory. At the scale of a personal enriched catalog
this is fast enough and keeps the dependency footprint minimal.

The embedding model runs locally via ONNX Runtime — a small sentence-transformer
(`all-MiniLM-L6-v2`) that ships with the app and works offline, keeping semantic search
fast and cost-free. It's kept behind an interface so a hosted alternative could be
substituted. One nuance: the model used to embed descriptions and the model used to embed
the query must be identical — vectors from different models aren't comparable — so the
model identifier is stored alongside every vector.

### How the layers combine: a self-improving loop

A **literal query** bypasses the AI machinery and goes straight to a Radio Browser search.

A **fuzzy/vibe query** runs the local semantic index **and** Pattern B (web discovery)
together, then merges and de-duplicates the results — the enriched catalog *augments* web
discovery rather than replacing it. An LLM re-ranking step scores every candidate's
description against the prompt so local and web results compete on one signal and weak
matches are dropped.

Crucially, Pattern B's finds then flow through the enrichment pipeline: their homepages are
fetched, descriptions generated, embeddings stored. The next time someone asks for
something similar, the local index already has it. This loop —
**discover → enrich → embed → rely more on local** — means the feature improves with use
without any explicit training step. It's a small-scale instance of the retrieval-augmented
generation pattern used in production AI systems, built from first principles on a desktop
app.

### Enabling AI search

AI search is off until an Anthropic API key is provided. Either:

- click the **gear (Options)** button and paste your key — it's stored **encrypted on your
  PC** (Windows DPAPI, current user) and never committed; or
- set the `ANTHROPIC_API_KEY` environment variable.

Get a key at [console.anthropic.com](https://console.anthropic.com). The in-app key takes
precedence over the environment variable; changes apply on restart.

Semantic search (Phase 2) additionally needs the local embedding model under `MlAssets/`
(`all-MiniLM-L6-v2.onnx`), which is git-ignored due to its size — see
[`MlAssets/README.md`](MlAssets/README.md). Without it, fuzzy search degrades gracefully to
Pattern B. Optionally, pre-fill the local catalog with popular stations using the
[`tools/SeedEnrichment`](tools/SeedEnrichment/README.md) console tool.

> Pattern B uses Anthropic's server-side web search tool (enabled per API-key org in the
> Anthropic Console) and incurs per-search cost; the local layers (embeddings) are free.

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
  so the engine can be swapped without touching the rest of the app. The AI services are
  isolated behind interfaces and never touch playback directly.
- **Platform:** **x64** (must match the native BASS DLLs).

## Building & running

Requires the **.NET 10 SDK** and Windows 11 (for Segoe Fluent Icons and SMTC).

```sh
dotnet build
dotnet run
```

The core player runs with no extra setup. To use AI search, provide an Anthropic API key
as described in [Enabling AI search](#enabling-ai-search).

### Native dependencies

The native BASS DLLs are tracked in the repo under `native/x64/` and copied to the
output directory at build time:

- `bass.dll` — BASS core (MP3/OGG)
- `bass_aac.dll` — AAC add-on (required for AAC streams, e.g. the default
  Radio Paradise station)

> Build **x64** only — AnyCPU is not supported, because the native DLLs are 64-bit.

### Background artwork

The window background is `Assets/player-bg.png` (compiled in as a WPF resource).
Swap in your own ~16:9 image (≈1920×1080) to retheme the player.

## Project structure

| Path | Responsibility |
| --- | --- |
| `Services/RadioEngine.cs` | The only class that talks to BASS: play/pause/stop/volume, ICY metadata, reconnection, stream probing. Marshals BASS callbacks to the UI thread. |
| `Services/SmtcController.cs` | Owns the SMTC instance; pushes now-playing info and maps the flyout/media-key buttons (incl. next-track) onto the app. |
| `Services/StationStore.cs` | Loads/saves the station list as JSON. |
| `Services/SettingsStore.cs` | Persists volume and the DPAPI-encrypted Anthropic API key. |
| `Services/StationSearchService.cs` | Radio Browser API client; maps results to playable `Station` candidates. |
| `Services/PromptInterpreter.cs` | Pattern A — translates a prompt into Radio Browser search parameters. |
| `Services/AgenticSearchService.cs` | Pattern B — two-tool agentic loop (web search + Radio Browser lookup). |
| `Services/LlmQueryClassifier.cs` | Routes a prompt to the literal (Pattern A) vs fuzzy (semantic + web) path. |
| `Services/LlmSearchRanker.cs` | LLM relevance re-rank of merged candidates. |
| `Services/EnrichmentService.cs`, `EnrichmentStore.cs` | Phase 1 — distil + cache per-station descriptions in SQLite. |
| `Services/SemanticSearchService.cs`, `MiniLmEmbeddingProvider.cs` | Phase 2 — local ONNX embeddings + brute-force cosine search. |
| `ViewModels/MainViewModel.cs` | Playback state, commands, station list, now-playing, search orchestration. |
| `StationDialog.xaml(.cs)` | Add/edit station editor. |
| `OptionsDialog.xaml(.cs)` | Settings dialog (Anthropic API key). |
| `MainWindow.xaml(.cs)` | UI, taskbar thumb buttons, custom window chrome, HWND/SMTC bootstrap. |
| `App.xaml(.cs)` | App entry point + single-instance guard. |
| `Models/Station.cs` | `{ Name, Url, Format, Description }` station record. |
| `tools/SeedEnrichment/` | Console tool to pre-fill the enrichment cache from the popularity ranking. |

## License

This project is licensed under the **PolyForm Noncommercial License 1.0.0** — you may
use, modify, and share it for any **non-commercial** purpose. See [LICENSE](LICENSE.md).

**Commercial use is not permitted**, which also aligns with the bundled audio engine:
BASS (un4seen) is **free for non-commercial use only**. Shipping this app commercially
would require a paid BASS license (or switching the engine to an alternative such as
[LibVLCSharp](https://github.com/videolan/libvlcsharp), LGPL — the engine is kept behind
the `RadioEngine` abstraction to make that swap feasible).

The bundled BASS DLLs and other third-party components are **not** covered by this
project's license and keep their own terms — see [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md).
