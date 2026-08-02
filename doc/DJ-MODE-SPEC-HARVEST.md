# DJ Mode (harvest-and-replay) — feature specification

An alternative design for DJ mode. Instead of crossfading between *live* streams and racing
a detector to fade out before ads hit the ear, this version **harvests** clean songs from
several streams at once into the rolling cache and plays back from those **local files** —
reusing the Library / `SongCurator` side of the app rather than building a live multi-stream
mixing engine.

This is the companion to [DJ-MODE-SPEC.md](DJ-MODE-SPEC.md) (the live-crossfade design).
Both are kept for now. Read [doc/TECHNICAL.md](TECHNICAL.md) first — this spec leans heavily
on the existing song-library pipeline (`StreamRecorder`, `SongHistoryFilter`,
`SongLibraryService`, `LocalPlaybackEngine`, `SongCurator`).

---

## 1. The core idea (and why it's attractive)

The app already records each aired song into a rolling cache: `StreamRecorder` cuts the raw
stream bytes at ICY title boundaries, keeps only complete segments, and `SongHistoryFilter`
keeps ads / jingles / idents out. **So the cache is clean music by construction.**

If DJ mode runs several harvesters at once, the cache fills with playable songs quickly, and
playback becomes "play clean local songs in a good order, refilling continuously" — which is
almost exactly what `SongCurator` + `LocalPlaybackEngine` already do.

What this design **deletes** relative to the live-crossfade spec:

- **The real-time music detector as a playback gate.** Segments are already ad-free by
  boundary-cut + metadata filter; nothing has to decide "is this still music?" *while
  playing*.
- **The delay buffer + preemptive-fade timing race.** You're never trying to fade out before
  a live ad reaches the ear, because you never play the ad in the first place.
- **The live BASSmix-over-streams engine.** Crossfading local files of known length with no
  connect latency is easy, and largely already handled by the Library playback path.

Net: a large part of the live-crossfade spec collapses into "reuse the Library side." The ad
avoidance is also *more robust* — clean-by-construction beats racing a detector, which can
always leak a few seconds on a bad call.

---

## 2. Goals and non-goals

### Goals

- From a vibe prompt (or genre / station list), harvest clean songs from several streams
  concurrently and play a continuous, ad-free, well-ordered queue.
- Reuse the existing pipeline: `StreamRecorder` capture, `SongHistoryFilter`,
  `SongLibraryService`, `LocalPlaybackEngine`, `SongCurator`.
- Self-refill: keep the play queue topped up from freshly harvested songs (and the existing
  library) so it never runs dry.
- Warm start from the existing library so the mode isn't a cold-start dead-end.
- Degrade gracefully (no key, empty library, dead harvesters).

### Non-goals (v1)

- **Live-ness.** This is explicitly a *jukebox built from radio*, not radio itself. Losing
  "what's on air right now" is an accepted product tradeoff of this design (see §8). If
  live-ness matters, that's the other spec.
- **Beat-matched mixing.** Volume-envelope crossfade only, same as the Library side.
- **Perfect segment purity.** Boundary-cut + metadata filter is the primary guarantee; the
  optional detector QC (§6.4) is a backstop, not a hard gate.

---

## 3. What's genuinely new to build

Most of this design is reuse. The one real new piece:

> **Headless, decode-/download-only harvesting connections, decoupled from playback.**

Today `StreamRecorder` captures on the **same** connection as playback, coupled through
`RadioEngine`. Harvesting streams you are *not* listening to means each harvester opens its
own connection purely to pull bytes and cut segments — no output device, no playback
coupling. This is "more of the same," not a new hard problem, and it's *cheaper per stream
than the live-crossfade standbys were*: a harvester only needs the raw-byte download callback
(the same one `StreamRecorder` already uses), not continuous PCM decode + FFT. PCM decode is
only needed if the optional QC detector (§6.4) is enabled.

Everything else — cutting at ICY boundaries, discarding partial/joined segments, the deferred
`CaptureBoundaryOffsetSeconds` cut, enrichment, embeddings, curation, local playback — already
exists.

---

## 4. Key concepts

| Term | Meaning |
| --- | --- |
| **Harvester** | A headless connection to one station that pulls raw bytes and cuts complete songs at ICY boundaries into the cache. No playback, no output device. |
| **Harvest pool** | The set of stations currently being harvested (several at once). |
| **Play queue** | The ordered list currently feeding `LocalPlaybackEngine`. Continuously refilled — a self-replenishing evolution of today's ephemeral `CuratedQueue`. |
| **Fill rate** | How fast clean songs arrive = roughly (harvester count) ÷ (average song length), minus dedup collisions. |
| **QC detector** | *Optional* offline, whole-file music/non-music check on completed segments — a backstop for stations with poor ICY hygiene. No real-time constraint. |

---

## 5. Proof of concept

PoC-first again, but the risks are different from the live-crossfade design — detection and
crossfade timing are no longer on the critical path. The three things to retire:

1. **Concurrent headless harvesting works** — can we run N decode-/download-only recording
   connections in parallel, cutting clean segments, without disturbing playback or the one
   output device?
2. **Fill rate is sustainable** — do songs arrive fast enough (across N harvesters) to keep a
   queue playing continuously after warm-up?
3. **Segment cleanliness at scale** — is boundary-cut + `SongHistoryFilter` clean enough
   across many stations, or is the QC backstop actually needed?

### PoC 1 — Concurrent headless harvest (the minimal PoC)

**A standalone console tool**, following the `tools/SeedEnrichment/` convention.

**Location:** `tools/DjHarvest/`

**What it does:**
1. Take a handful of station URLs (mix MP3 + AAC).
2. For each, open a headless connection reusing the existing download-callback + ICY-boundary
   cut logic (factor the reusable core out of `StreamRecorder` if needed — see §6.1).
3. Write complete segments to a scratch folder; log per-harvester: songs/hour, discarded
   partials, ICY title stream, and any obviously-bad cuts.
4. Run for a while against real stations and report the aggregate fill rate + a sample of
   segments to inspect by ear.

**Exit criterion:** N harvesters run stably in parallel, produce clean complete-song
segments, and the aggregate fill rate comfortably exceeds one song per average-song-length
(i.e. the queue would grow, not starve) after warm-up. Confirms the one new engineering piece
*and* the fill-rate assumption at once.

### PoC 2 — Warm-start + self-refilling queue

**A small spike** proving continuous playback, reusing the Library path.

**What it does:**
1. Seed an opening queue from the existing library via `SongCurator` for the prompt.
2. Play it through `LocalPlaybackEngine`.
3. As `DjHarvest` (PoC 1) drops new segments in, index them and append fitting songs to the
   queue so it never empties; dedup by artist+title.

**Exit criterion:** playback runs continuously from warm start through the transition to
freshly-harvested songs, with no gap and no starvation, and no duplicate songs back-to-back.

### PoC 3 (optional) — Segment QC backstop

Only if PoC 1 shows some stations leak ads/talk into segments (poor ICY hygiene).

Reuse the detector harness from the live-crossfade spec ([DJ-MODE-SPEC.md](DJ-MODE-SPEC.md)
PoC 1) as a **whole-file verdict** on completed segments — no timing pressure, just
music/not-music per file — and reject failed segments before they reach the queue.

**Exit criterion:** the QC pass removes the leaked-ad segments PoC 1 surfaced without
rejecting real songs. If PoC 1 was already clean, skip this entirely.

---

## 6. Full implementation — big picture

### 6.1 New / changed components

| Path | Responsibility |
| --- | --- |
| `Services/SegmentCapture.cs` *(refactor)* | The reusable core of `StreamRecorder` — download callback + ICY-boundary cut + complete-segment rule — factored out so both the live playback recorder *and* headless harvesters share it. `StreamRecorder` becomes a thin caller. |
| `Services/StreamHarvester.cs` | One headless harvesting connection: opens a download-only stream, drives `SegmentCapture`, emits completed segments. No output device, no playback coupling. |
| `Services/DjHarvestService.cs` | Owns the harvest pool: seeds stations from search, runs N `StreamHarvester`s, indexes completed segments via `SongLibraryService.AddAndEnrich`, replaces dead harvesters, enforces dedup + rolling-cache eviction. |
| `Services/DjQueueService.cs` | The self-refilling play queue: warm-starts via `SongCurator` from the existing library, then continuously appends fitting freshly-harvested songs; feeds `LocalPlaybackEngine`. |
| `Services/SegmentQualityChecker.cs` *(optional)* | The offline whole-file QC detector (§6.4), gating segments before they reach the queue. |
| `ViewModels/` (extend `MainViewModel`) | DJ mode entry, prompt, "now playing (auto) + N harvesting" status, stop. |
| `tools/DjHarvest/` | The PoC-1 harness, kept as a fill-rate / stability tool. |

Playback itself reuses **`LocalPlaybackEngine` unchanged** — DJ mode (harvest) is an ingest
service + a self-refilling queue on top of the existing local transport, not a new engine.

### 6.2 How it fits the architecture

- **Playback** goes through `LocalPlaybackEngine`, so the "one BASS device, one now-playing
  identity" invariant and all SMTC / media-key / flyout wiring already work via
  `SmtcController.SetActiveEngine` — same as Library mode today.
- **Harvesters** are decode-/download-only: they don't open the output device, so they don't
  contend with playback for the single device. They're background ingest, not a second
  playback path.
- **Search** reuses the headless `RunUnifiedSearchAsync` variant (same as the other spec,
  §6.2 there) to pick which stations to harvest.
- **Curation / ordering** reuses `SongCurator` — recall by embedding, arrange into a
  mood/energy arc with Haiku, degrade gracefully — exactly as the offline curated-playlist
  feature already does.

### 6.3 Cold start vs. warm start

- **Warm** (you've used the app): the opening queue is curated from the existing library for
  the vibe, so playback starts *immediately*; harvested songs blend in as they complete.
- **Cold** (empty library): you must wait for songs to finish airing before anything's
  playable — worst case ≈ one song length for the first track (you join mid-song; that
  partial is discarded). Running several harvesters raises the *fill rate* but not the
  *first-song* latency. Surface an honest "warming up, harvesting stations…" state rather
  than a silent stall.

### 6.4 Optional QC detector

The metadata boundary-cut is the primary purity guarantee. The detector from the
live-crossfade spec gets an easier second life here as a **whole-file, offline** check on
completed segments (no real-time constraint, no timing race): reject a segment if it isn't
confidently music, catching stations that fold an ad into a segment via poor ICY hygiene.
Enable only if PoC 1 shows it's needed.

### 6.5 Dedup and eviction (matter more here than today)

- **Dedup:** several same-genre harvesters *will* capture the same hits. Dedup on
  artist+title (already in `LibraryStore`) before queueing, and avoid back-to-back repeats in
  the arrangement.
- **Eviction:** the cache fills fast with N harvesters, so rolling-cache eviction matters
  more than in single-stream recording — cap total harvested size and evict oldest
  unplayed / lowest-value first. Don't evict library songs the user explicitly saved.

### 6.6 SMTC / UI integration

- **Now playing:** the current local song's title/artist via `LocalPlaybackEngine`, as
  Library mode already reports.
- **Next track:** trivial — advance the queue (already supported by `LocalPlaybackEngine`
  auto-advance). Maps straight onto the existing next-track wiring.
- **UI (minimal v1):** prompt box, a "now playing X · harvesting N stations · M songs
  queued" status line, stop.

### 6.7 Failure handling & degrade

- Harvester dies → drop it, promote another station from the search reserve; keep the pool at
  target count.
- No API key → warm-start / ordering falls back to cosine order or most-recently-harvested,
  same graceful-degrade ladder as `SongCurator`.
- Empty library **and** cold cache → honest "warming up" state; start playing the moment the
  first complete segment lands.
- Queue nearing empty faster than harvest fills it → widen the harvest pool and/or pull more
  from the existing library; never stall silently.

---

## 7. Configuration knobs

| Setting | Default | Notes |
| --- | --- | --- |
| `HarvesterCount` | 3–4 | Concurrent harvesting connections. Cheaper per stream than live standbys (byte capture, no continuous decode/FFT). Drives fill rate. |
| `HarvestReserveCount` | 15 | Validated station URLs held to replace dead harvesters. |
| `QueueLowWatermark` | 5 | Refill the queue when it drops to this many songs. |
| `MaxHarvestCacheSize` | tune | Rolling-cache cap for harvested (unsaved) segments; drives eviction. |
| `CaptureBoundaryOffsetSeconds` | 6 (existing) | Reused from `StreamRecorder`. |
| `EnableSegmentQc` | false | Turn on the §6.4 whole-file detector backstop if needed. |
| `DedupBackToBack` | true | Prevent the same song appearing twice in close succession. |

---

## 8. The one product tradeoff to decide

This design is a **personal jukebox assembled from radio**, not radio itself. You gain
clean-by-construction ad-free playback and a much smaller build; you lose the freshness and
serendipity of what's actually on air right now. That's a product call, not a technical one —
the two specs sit at opposite ends of it:

- **This spec (harvest):** cleaner, cheaper, more robustly ad-free, but not live.
- **[DJ-MODE-SPEC.md](DJ-MODE-SPEC.md) (live crossfade):** genuinely live, but needs the
  real-time detector, delay buffer, and live mixing engine, and can leak a few seconds of ad.

They're not mutually exclusive long-term: harvest could ship as v1, with live mode as a later
"true live" toggle.

---

## 9. Testing strategy

- **Harvest:** `tools/DjHarvest/` doubles as a fill-rate / stability soak tool — assert N
  harvesters stay up and fill rate stays above the starvation line over long runs.
- **Queue service:** `DjQueueService` takes segment-arrival events + library state and emits
  a queue — unit-test warm-start, refill at the low watermark, dedup, and the never-starve
  guarantee with synthetic arrival timelines, no BASS dependency.
- **Segment purity:** if QC is enabled, keep a labelled clip set (shared with the other
  spec's harness) as a regression check.
- **Playback:** reuses `LocalPlaybackEngine`; manual soak for continuity across the
  warm-start → harvested-songs transition.

---

## 10. Suggested build order

1. **PoC 1** — `tools/DjHarvest/`: concurrent headless harvest + fill-rate measurement.
   *(This is the minimal PoC — retires the one new engineering risk and the fill-rate
   assumption together.)*
2. **PoC 2** — warm-start via `SongCurator` + self-refilling queue on `LocalPlaybackEngine`.
3. **PoC 3** *(only if needed)* — segment QC backstop.
4. Refactor `SegmentCapture` out of `StreamRecorder`; build `StreamHarvester`.
5. `DjHarvestService` (pool, indexing, dedup, eviction) + `DjQueueService` (warm start,
   self-refill).
6. `MainViewModel` / `MainWindow` entry + status.
7. Config, degrade paths, soak testing.
