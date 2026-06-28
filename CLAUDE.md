# CLAUDE.md

Project context for Claude Code. Read this before working on audio playback or OS
media integration — it captures decisions and non-obvious gotchas that aren't
discoverable from the code alone.

## Project overview

A Windows desktop app that plays internet radio streams (Icecast/Shoutcast), with
playback surfaced in the OS in two places:

1. **Taskbar thumbnail buttons** — play/pause shown in the live preview when hovering
   the app's taskbar icon.
2. **System Media Transport Controls (SMTC)** — the now-playing widget in the Win11
   volume/quick-settings flyout, lock screen, and keyboard media-key handling.

Primary test stream: Radio Paradise AAC 128 — `http://stream.radioparadise.com/aac-128`.

A planned **AI-assisted station search** (natural-language prompt → real, playable
stations) is documented in its own section below. It is an optional, online-only
enhancement layered over the core player — the player must work fully without it.

## Tech stack

- **Language/runtime:** C# / .NET 8
- **UI:** WPF — chosen for first-class taskbar thumbnail button support via
  `TaskbarItemInfo.ThumbButtonInfos` (WinUI 3 has no built-in equivalent).
- **Audio engine:** BASS (un4seen) via **ManagedBass** + **ManagedBass.Aac**
- **OS media integration:** SMTC via WinRT interop (`ISystemMediaTransportControlsInterop`)
- **Target framework:** `net10.0-windows10.0.19041.0`
  (the Windows SDK version in the TFM is required to project the WinRT/SMTC types).
  This dev machine only has the .NET 10 SDK/runtime; net8.0 would build but not run
  without installing the .NET 8 runtime. The WPF/SMTC/taskbar code is identical either
  way — bump the major version in the TFM if you target a different runtime.
- **Architecture pattern:** MVVM
- **Platform target:** **x64** (must match the native BASS DLLs — see gotchas)

## Decisions & open questions

- **Engine = BASS, not libVLC.** Rationale: lightweight, simple streaming API,
  first-class ICY metadata (live song titles). The ManagedBass wrapper is MIT.
  **Tradeoff:** BASS itself is free for *non-commercial* use only; shipping
  commercially requires a paid un4seen license. If the project must be
  free-to-distribute commercially, switch to **LibVLCSharp** (LGPL, but a ~40MB+
  native payload and you'd wire ICY metadata via `Media.MetaChanged`). This is the
  one decision most likely to be revisited — keep the engine behind the
  `RadioEngine` abstraction so it can be swapped.

- **AI station search = learning playground.** A deliberately self-contained feature
  for learning AI integration. Online-only; grounded on the Radio Browser directory so
  the LLM never supplies station URLs (it only translates the prompt into search
  parameters). Provider-agnostic by design; starting with the Anthropic API called via
  direct REST so the mechanics are visible. See the dedicated section below.

## Architecture

Keep a clean seam between the audio engine and everything else.

- **`RadioEngine`** — the only class that talks to BASS. Exposes
  play/pause/stop/volume, raises events for playback state, buffering, errors, and
  ICY metadata. Nothing else in the app calls BASS directly.
- **`SmtcController`** — owns the `SystemMediaTransportControls` instance, pushes
  now-playing info via `DisplayUpdater`, and handles `ButtonPressed` (mapping
  play/pause/stop to `RadioEngine`). Bootstrapped from the main window once the
  HWND exists.
- **`MainWindow` (XAML)** — taskbar thumbnail buttons via `TaskbarItemInfo`; minimal
  code-behind (only view wiring + SMTC/HWND bootstrap).
- **`MainViewModel`** — playback state, current station, station list, commands.
- **`Station` model** — `{ Name, Url, Format }` so the engine knows whether to use
  the AAC add-on path or the core path.

### Threading model (important)

- BASS sync callbacks (metadata changes, stalls, end-of-stream) fire on
  **BASS-owned threads**. Marshal to the UI thread via `Dispatcher` before touching
  the view model, UI, or SMTC.
- Treat `RadioEngine`'s public surface as UI-thread-affine; do the marshalling inside
  the engine so callers don't have to think about it.

## Build & run

```
dotnet build
dotnet run
```

**Native dependencies** must be present in the output directory and match the process
architecture (x64):

- `bass.dll` (core)
- `bass_aac.dll` (AAC/MP4 decoding — required for the Radio Paradise stream)

Prefer the ManagedBass NuGet native packages, or add the DLLs as `Content` with
`CopyToOutputDirectory`. Do **not** build AnyCPU unless you guarantee the right
native DLLs are resolved.

## Key gotchas (read before writing audio/SMTC code)

1. **AAC needs the add-on.** BASS *core* does not decode AAC. The `aac-128` test
   stream requires `bass_aac` (ManagedBass.Aac). Create AAC URL streams with
   `BassAac.CreateStream(url, ...)`, not `Bass.CreateStream(...)`. Use plain
   `Bass.CreateStream(url, ...)` only for MP3/OGG stations. Verify the exact overload
   signature against the installed ManagedBass.Aac version.

2. **SMTC in WPF has no CoreWindow.** `SystemMediaTransportControls.GetForCurrentView()`
   throws in a Win32/WPF app, and the old hand-rolled `[ComImport]`
   `ISystemMediaTransportControlsInterop` cast **no longer works** in modern .NET —
   built-in WinRT support was removed from the runtime and `IInspectable`-based interop
   interfaces can't be declared that way anymore. Instead use the **projected** static
   class: `Windows.Media.SystemMediaTransportControlsInterop.GetForWindow(hwnd)` (it
   returns a typed `SystemMediaTransportControls`; available via the Windows SDK TFM).
   Get the HWND from `new WindowInteropHelper(window).Handle` *after* the window is shown
   (in `OnSourceInitialized`/`SourceInitialized`). Then set `IsPlayEnabled`/`IsPauseEnabled`,
   subscribe to `ButtonPressed` (fires on a background thread — marshal to the UI thread),
   set `PlaybackStatus`, and push title/artist via `DisplayUpdater` + `DisplayUpdater.Update()`.

3. **ICY metadata setup.** `Configuration.NetMetadata` does **not** exist in ManagedBass
   4.0.2 — ICY/Shoutcast metadata is requested by default, so no `Bass.Configure` call is
   needed to enable it. Read the initial station info from
   `Bass.ChannelGetTags(handle, TagType.ICY)`; subscribe to a metadata sync
   (`SyncFlags.MetadataReceived`) for live track changes and read
   `TagType.META`, then parse the `StreamTitle='...';` field. Feed parsed title/artist
   into both the view model and `SmtcController`.

4. **It's an endless HTTP stream.** The URL is plain `http://` (not HTTPS) and has no
   duration. Don't treat the channel as seekable; don't show/seek a position bar.

5. **Reconnection.** Radio streams drop. Handle stalls and end-of-stream syncs by
   recreating the stream, and reflect a buffering/reconnecting state in both the UI
   and SMTC `PlaybackStatus`.

6. **TFM must include the Windows SDK version** (`net10.0-windows10.0.19041.0`) or the
   WinRT `Windows.Media.*` types won't be available.

## AI-assisted station search

Natural-language station discovery: the user types a prompt ("mellow jazz for
late-night coding") and the app surfaces real, playable stations. Optional and
online-only — the core player must work without it.

**Status:** Pattern A (structured-output search), Pattern B (web-search discovery), and
Phase 1 (local catalog enrichment) are implemented and working. Current work is
**Phase 2 — embeddings / semantic search**, which turns the enriched descriptions into a
local vector index; see that section below.

**Core principle — the LLM is not the database.** An LLM has no reliable, current
knowledge of which streams exist or what their URLs are, and *will* hallucinate dead or
invented stream URLs if asked to name stations directly. Use the LLM only to translate
fuzzy language into structured search parameters (and optionally to rank/explain
results). Every piece of real station data — name, URL, codec — comes from a directory.

### Data source: Radio Browser API (`https://api.radio-browser.info`)

- Free, community-maintained, **no authentication**.
- **Server discovery:** resolve via DNS / `all.api.radio-browser.info` and retry the
  next mirror on failure. To start, hardcoding a mirror (e.g.
  `https://de1.api.radio-browser.info`) is fine.
- **Send a descriptive `User-Agent`** (e.g. `RadioPlayer/1.0`) — the maintainers ask
  for this and use it for usage stats.
- Search endpoint: `GET /json/stations/search` with params such as `tag` / `tagList`,
  `name`, `country`, `countrycode`, `language`, `codec`, `bitrateMin`, `order`
  (`votes`, `clickcount`, …), `reverse`, `limit`, and `hidebroken=true`.

### Flow

1. User prompt → LLM → structured JSON search params (Pattern A below).
2. App queries Radio Browser with those params.
3. Filter results to what the BASS engine can actually play (see codec tie-in).
4. *(Optional)* feed candidates back to the LLM to rank or write a one-line rationale.
5. Map the chosen result → existing `Station` model → `RadioEngine.CreateStream`.

### Two patterns

- **Pattern A — LLM as query translator (structured output).** *Implemented.* The model
  returns JSON matching a fixed schema; the app calls Radio Browser. Best for literal
  queries ("German news radio").
- **Pattern B — web-search discovery (agentic, two tools).** *In progress.* Adds a
  web-search layer for soft/semantic queries the thin Radio Browser tags can't satisfy,
  then resolves the findings to playable streams via Radio Browser. Detailed below.

### Structured-output schema (Pattern A)

The model must emit exactly this object and nothing else:

```json
{
  "tags": ["string"],      // genre/mood keywords mapped from the prompt
  "name": "string|null",   // only if the user named a specific station
  "country": "string|null",
  "language": "string|null",
  "bitrateMin": 0,          // 0 = no minimum
  "order": "votes"         // ranking hint for Radio Browser
}
```

Map non-null fields to Radio Browser query params; omit the rest.

### Pattern B — web-search discovery (agentic, two tools)

Handles "soft" queries (vibe, era, scene, "the station someone recommended") that the
thin Radio Browser tags can't satisfy. Mental model: **web search is the recall /
curation layer; Radio Browser is the playability / trust layer.** The web surfaces
station *names and descriptions* from blogs, forums, and "best of" lists; you ALWAYS
resolve those to a verified, playable stream via Radio Browser before playing. The
"never play a web- or model-supplied URL" rule from Pattern A still holds.

**No routing — Pattern B is a quality-gated escalation, not a separate "path".** The app
always runs the cheap recall sources (Pattern A + local semantic) first and only escalates to
web discovery when the re-ranked cheap pool can't fill a page. See **Routing — one unified
pipeline** under "Semantic search (Phase 2)" for the full flow. (Historical note: an earlier
design used an LLM classifier to route literal→A vs fuzzy→B; it was removed because borderline
prompts misroute and a hard A/B switch is lossy.)

**Two tools, two execution models (the key learning point).**

- `web_search` — Anthropic's **server-executed** tool. Add
  `{"type": "web_search_20250305", "name": "web_search", "max_uses": N}` to the `tools`
  array; the model decides when to search, Anthropic runs it, and cited
  `web_search_tool_result` blocks come back automatically. You do **not** execute it.
  Must be enabled in the Anthropic Console for the key's org. Roughly $10 per 1,000
  searches plus tokens. Optionally set `allowed_domains` to focus on quality
  radio-recommendation sites and cut cost/noise.
- `search_radio_browser` — your **client-executed** tool wrapping the existing
  `StationSearchService` (add a `byName` lookup if it isn't there yet). The model emits a
  `tool_use` block; YOUR code runs the query and returns a `tool_result` with candidates,
  already codec/HLS-filtered per the engine tie-in rules.

The loop must handle both kinds in one conversation: server results arrive on their own;
client tool calls you execute and feed back.

**Agentic loop (Messages API):**

1. `messages.create` with both tools registered and the user's prompt.
2. While `stop_reason == "tool_use"`: execute each client `tool_use`
   (`search_radio_browser`), append a `tool_result`, and re-call with the **full** message
   history — including the encrypted `web_search` result blocks, passed back unmodified
   for multi-turn continuity.
3. Stop at `stop_reason == "end_turn"`; parse the model's final structured answer.
4. **Bound the loop** with a max-iteration cap and `max_uses` on web_search to prevent
   runaway latency/cost.

**Final-answer validation (critical).** The model returns a JSON list of chosen stations,
each keyed by Radio Browser `stationuuid` plus a one-line rationale. Map each back to a
station you actually fetched via `search_radio_browser`, and play only those — discard
anything not traceable to a real tool result. Trust comes from your fetched data, never
from a station/URL the model produced in prose.

**Final-answer schema:**

```json
{
  "stations": [
    { "stationuuid": "string", "reason": "string" }
  ]
}
```

**Components (extend, don't duplicate):**

- `StationSearchService` — reused; also backs the `search_radio_browser` tool.
- `AgenticSearchService` (new, behind an interface) — owns the two-tool loop: registers
  tools, drives `messages.create` to `end_turn`, executes client tool calls, collects all
  fetched candidates, and returns the validated final list. No playback knowledge.
- View model still orchestrates: agentic search → validated candidates → present → user
  picks → play via `RadioEngine`.

**Model / cost.** Use a stronger model than Pattern A here (e.g. Sonnet, possibly Opus) —
it's doing multi-step reasoning over web + catalog, not a one-shot translation. Cache web
results, cap searches. Verify current models/pricing at
https://docs.claude.com/en/api/overview.

### Codec / engine tie-in (important)

Radio Browser returns `codec`, `bitrate`, `hls`, `lastcheckok`, and both `url` and
`url_resolved` per station.

- Feed **`url_resolved`** to the player (it follows playlists/redirects), not `url`.
- Keep only stations the engine can play: `codec` in {MP3, AAC, AAC+} **and
  `hls == false`** (the current BASS setup has no HLS add-on). Derive `Station.Format`
  from `codec` so the engine picks `BassAac.CreateStream` vs `Bass.CreateStream`.
- Always pass `hidebroken=true` and prefer `lastcheckok == true` to avoid dead streams.

### Provider / API

- Provider-agnostic pattern; Anthropic API is the natural fit here.
- For learning, call the REST endpoint directly with `HttpClient` before adopting a
  wrapper SDK.
- Model sizing: a small/fast model (e.g. Claude Haiku) is plenty for query translation;
  step up (e.g. Sonnet) only if you add result-ranking. Verify current model names and
  pricing at https://docs.claude.com/en/api/overview — don't hardcode from memory.
- **Never commit the API key.** Read it from an environment variable / user-secrets;
  keep it out of source control and out of this file.

### New components (keep AI isolated from playback)

- `StationSearchService` — talks to Radio Browser; returns mapped `Station` candidates.
  Knows nothing about LLMs.
- `PromptInterpreter` (a.k.a. `AiSearchService`) — talks to the LLM; prompt in,
  structured params out. Knows nothing about playback.
- Both behind interfaces; neither touches `RadioEngine` directly. The view model
  orchestrates: interpret → search → present → user picks → play.

### Gotchas

- Strip ```` ```json ```` fences before `JsonSerializer.Deserialize` — models sometimes
  wrap JSON in markdown.
- Handle empty results (broaden tags and retry, or tell the user) rather than failing.
- Do the HTTP and LLM round-trips async; never block the UI thread on them.
- For *literal* queries ("German news radio") the LLM is unnecessary — Radio Browser's
  tag/country search handles those directly. The LLM earns its place on fuzzy/semantic
  prompts.
- **(Pattern B)** Don't try to execute `web_search` yourself — it's server-side; only
  `search_radio_browser` is client-executed. Pass encrypted `web_search` result blocks
  back unmodified on each turn, and always cap the loop (`max_uses` + iteration limit).

## Local catalog enrichment (Phase 1 toward semantic search)

The quality ceiling on AI search is Radio Browser's thin, generic tags. Phase 1 fixes the
*data*, not the search: build a persistent, richer **description** per station that the
existing Pattern A/B re-ranking can reason over. This is a prerequisite for Phase 2
(embeddings / semantic search) — embeddings over three tags add little; over real
descriptions they shine. **Phase 1 is enrichment only; do not build embeddings yet.**

This is the app's **first persistent local state.** Patterns A and B are stateless and
online; enrichment introduces a derived dataset with a lifecycle (build, cache, refresh).

### Enrich lazily, never in bulk

There are tens of thousands of stations — do **not** fetch/summarize them all. Enrich a
station the first time it appears in a Pattern A/B result (or is played), then cache it
permanently until stale. The enriched set grows to match what the user actually explores.
Optional: seed with the top few hundred stations by votes so it isn't empty on first run.

### Pipeline (per station)

1. Fetch the station's `homepage` (Radio Browser provides it) with a **tight timeout**.
2. Extract readable text (strip nav/script/boilerplate) and **cap length** before sending
   to the model.
3. Distill with a **cheap model** (Haiku-class — this is summarization, not reasoning)
   into a short description paragraph plus optional structured facets
   (`genres`, `moods`, `era`).
4. **Fallbacks (expect to need them often):** many homepages are dead, JS-only, or junk.
   When extraction yields nothing useful, fall back to name + Radio Browser tags + a
   Pattern-B web snippet rather than failing.
5. Cache the result keyed by `stationuuid`.

### Storage

- **SQLite** (`Microsoft.Data.Sqlite`), database file under `%LocalAppData%`.
- Schema keyed by `stationuuid`: `description` (text), `facets` (JSON), `source`
  (homepage | web | tags-only), `enriched_at` (timestamp for staleness/refresh).
- Reserve space for Phase 2 but don't build it now: a future `embedding` BLOB column plus
  an `embedding_model` string. Leave them out or nullable for now.

### Immediate payoff (no embeddings required)

Feed the cached descriptions into the existing Pattern A/B re-ranking step. Both get
smarter the moment descriptions exist — this is the deliverable for Phase 1, and it's
independently useful even if Phase 2 never happens.

### Components

- `EnrichmentService` (new, behind an interface) — owns the fetch → extract → distill →
  cache pipeline. Knows nothing about playback or the search loop.
- `EnrichmentStore` (new) — the SQLite read/write layer (get-by-uuid, upsert, staleness
  check). The only thing that touches the DB.
- Wire-in point: when `StationSearchService` returns candidates, opportunistically
  enrich-and-cache them (async, fire-and-forget; never block returning results). Search
  reads any already-cached descriptions to enhance re-ranking.

### Gotchas

- Enrichment is best-effort and must **never** block or break search/playback — degrade
  to the existing tag-based behavior on any failure.
- Cap homepage fetch time and response size; treat homepage fetching as untrusted I/O
  (timeouts, size limits, content-type checks).
- Enrich each station once (until `enriched_at` is stale); never re-summarize on every
  search — that defeats the cache and burns tokens.
- This is real persisted state: handle DB location/creation, concurrent access, and a
  simple schema-version field so later migrations (e.g. adding the Phase 2 columns) are
  clean.

## Semantic search (Phase 2: embeddings)

Turns the Phase 1 enriched descriptions into a local **vector index** for true semantic
matching — "dreamy music for late-night coding" finds the right stations even with no
matching tag. Depends entirely on Phase 1: only stations that have an enriched description
get embedded, so the index is naturally bounded by the lazily-grown enriched set.

### Embedding provider (decision)

Put it behind an `IEmbeddingProvider` interface and pick one implementation:

- **Local ONNX (default, recommended).** A small sentence-transformer such as
  `all-MiniLM-L6-v2` (384-dim) via `Microsoft.ML.OnnxRuntime`. Fully offline, no per-call
  cost, fits the desktop ethos. Cost: you must run the tokenizer and pooling yourself
  (see gotchas).
- **Hosted Voyage (alternative).** Anthropic's recommended embeddings partner (e.g. a
  `voyage-3.5`-class model, 1024-dim); a simple HTTP call returning a normalized vector.
  Simplest to wire, but adds a second API key and an online dependency. Anthropic has no
  first-party embeddings endpoint, so this is the hosted route.

Whichever you choose, **store the model id with every vector** — vectors from different
models are not comparable.

### Indexing

Extend the Phase 1 enrichment write path: after a description is produced, compute its
embedding and store it. Fill the reserved columns (`embedding` BLOB, `embedding_model`),
bump `schema_version`, and **backfill** existing enriched rows that have no embedding yet.
No new store — this is the same SQLite table from Phase 1.

### Vector storage & search

- Store each embedding as a BLOB alongside its station row. No vector database.
- At query time, load the embedded rows and do **brute-force cosine similarity in memory**.
  Normalize vectors so cosine == dot product (fast). At this scale (the enriched subset,
  typically thousands) brute force is more than fast enough; `sqlite-vec` is a later
  optimization, not a starting point.

### Query flow

1. Embed the query with the **same** provider/model used for documents.
2. Cosine top-K against the index.
3. Hard-filter to playable (existing codec/HLS rules); resolve `url_resolved`.
4. *(Optional)* LLM re-rank/explain the top-K (reuse the existing re-ranking step).
5. Present; user picks; play via `RadioEngine`.

### Routing — one unified pipeline (no literal-vs-fuzzy split)

There is **no classifier and no literal-vs-fuzzy branch.** Every prompt runs one retrieval
pipeline: several cheap recall sources feed one ranker, and web search is the only
escalation — fired by *result quality*, not a guess about intent. Implemented in
`MainViewModel.RunUnifiedSearchAsync`:

1. **Cheap recall, in parallel** — Pattern A (structured Radio Browser lookup) **and** local
   semantic search. Both are ~free/fast. Pool their candidates, deduped on the resolved
   stream URL.
2. **Re-rank** the pool with `LlmSearchRanker` (a strict Haiku call that drops loosely-related
   stations). This *replaces* the old `Broaden` fallback — a query that matches nothing simply
   contributes nothing.
3. **Escalate to web** only when the re-ranked cheap pool can't fill a page
   (`shortlist.Count < MaxResults`, see `NeedsWebEscalation`). Then run Pattern B (web
   discovery), add its finds to the pool, and **re-rank the combined pool**. Because the ranker
   is strict, a thin shortlist is a genuine signal the directory/local catalog don't cover the
   prompt (niche genre, multi-country region like "Scandinavia", stylistic qualifier like
   "contemporary") — exactly when web search earns its cost.
4. **Validate** streams (`AddValidatedAsync`) and show.

Net effect: "BBC Radio 1" is satisfied by the cheap sources and never pays for web; "contemporary
metal from scandinavia" comes back thin from the cheap sources and escalates automatically. The
enrichment DB / vector index still **augments** web discovery — Pattern B's finds get enriched
(Phase 1) and embedded (Phase 2), so the cheap pool keeps improving and escalates less over time.

- *Design history:* earlier drafts used (a) a cheap LLM **classifier** to route literal→Pattern A
  vs fuzzy→semantic+web, then (b) "fuzzy = always run semantic AND web". Both were replaced: the
  classifier kept mis-routing borderline prompts (genre+region, genre+qualifier) that are *both*
  literal-ish and fuzzy, and a hard A/B switch is lossy by construction. The unified
  "many retrievers → one ranker → quality-gated web escalation" design removes the routing
  decision entirely. `SemanticThreshold` is now only the relevance floor in the ranker-unavailable
  heuristic fallback (`BuildHeuristicMerge`), not a routing switch.

### Components

- `IEmbeddingProvider` (+ the chosen implementation) — text in, vector out. Nothing else.
- `SemanticSearchService` (new, behind an interface) — embed query, cosine top-K over the
  store, return ranked candidates. No playback knowledge.
- `EnrichmentService` — extended to also embed descriptions at cache time.
- `EnrichmentStore` — gains vector read/write and a "rows missing embeddings" query for
  backfill.
- View model owns the unified pipeline above (`RunUnifiedSearchAsync`): pool the cheap
  sources, re-rank, quality-gate the web escalation, validate, present.

### Gotchas

- **Same model for query and documents.** Store `embedding_model` per vector; if you
  switch models, existing vectors are incomparable — re-embed (or filter to matching
  model). Track the dimension too (MiniLM 384 vs Voyage ~1024).
- **Local MiniLM pooling is the classic mistake.** ONNX gives per-token outputs; you must
  **mean-pool over tokens using the attention mask, then L2-normalize** — do not use the
  CLS token and do not skip pooling, or similarity will be garbage. (Hosted Voyage returns
  ready-normalized vectors, so this only applies to the local path.)
- **Only embed enriched rows.** No description → no embedding. Cold start is expected;
  lean on the Pattern B fallback until the index fills.
- Keep embedding compute and similarity off the UI thread; load/cache the index in memory.
- Tune the cosine threshold that decides "good enough" vs. falling back to Pattern B.

## Conventions

- MVVM throughout; keep code-behind to view wiring and the HWND/SMTC bootstrap only.
- All BASS access funnels through `RadioEngine`.
- Engine-agnostic abstraction so BASS can be swapped for libVLC without touching the
  view model.

## Testing

- **Primary:** `http://stream.radioparadise.com/aac-128` (AAC — exercises the
  `bass_aac` path + ICY metadata).
- **Add one MP3 station** to validate the non-AAC `Bass.CreateStream` path.
- Verify: taskbar play/pause toggles state; hardware media keys toggle playback via
  SMTC; the Win11 media flyout shows the current track and reflects play/pause.
- **AI search (once implemented):** a fuzzy prompt ("calm music for focus") returns
  playable AAC/MP3 stations; a literal prompt ("BBC") resolves sensibly; malformed or
  empty LLM output is handled gracefully; only non-HLS AAC/MP3 stations reach the player.
