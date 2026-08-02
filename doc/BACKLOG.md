# Backlog

Small, deferred, non-urgent items — things worth doing but not right now. Not a full spec each;
just enough context to pick back up later. Add new items at the top.

---

## Test coverage: the remaining untested seams

**Where we are:** 129 tests. The pure derivation logic (`StationNameFormatter`,
`TrackTitleCleaner`, `MusicDetector` trim, `SegmentQualityChecker`, `StagedProgress`,
`DjSessionLog`, harvester retirement), the WPF chrome regressions (`AppDialog`, `StatePanel`),
`DjQueueService`, and — as of the last pass — every LLM boundary (`AnthropicApi`,
`PromptInterpreter`, `LlmSearchRanker`, and `AgenticSearchService`'s stationuuid validation gate)
are covered. `tests/RadioPlayer.Tests/Fakes/FakeHttpMessageHandler.cs` scripts Messages API
replies, so any further LLM-backed service can be driven end to end without a network.

**Deliberately not chasing whole-app coverage.** Every bug this project has actually hit lived in
parsing/derivation logic or a state machine — the `Append`-after-exhaustion stall, the AAC+ regex
leaving `"Liquid DnB - +"`, lead/tail trim double-counting, the implicit style not matching a
subclass, "12 songs vs 7 segments". Wiring and I/O have not been the problem. Order the remaining
work by that evidence, not by line count.

**Next, in priority order:**

1. **`LocalPlaybackEngine`'s queue state machine** — the strongest candidate. The silent-stall bug
   lived here (`Append` resumed only when the queue had been empty, never again once it had been
   played to the end) and the `_ranDry` fix that repaired it has no test. `Append` / `Next` /
   `PlayAt` / `Stop` / `QueueExhausted` is exactly the shape of logic that breaks twice. Needs a
   small seam so the transitions can run without BASS — the state machine and the stream handling
   are already nearly separable.
2. **`LibraryStore`'s v1→v2 migration** — the app's only schema migration, currently unverified,
   and getting it wrong costs the user their library. Test the upgrade-from-v1 path against a real
   v1 file, not just `CREATE TABLE IF NOT EXISTS` on a fresh DB.
3. **`StreamRecorder` segment cutting** — boundary offset, the GUID+tick filenames that keep N+1
   concurrent recorders from colliding, and cache eviction.
4. **`SettingsStore`** — cheap: the `Load()` clamps, and that `SetApiKey` is load-modify-save so it
   can't clobber `Volume`.

**Explicitly parked: `MainViewModel`.** 2,489 lines and 17 concrete dependencies including two
audio engines — it cannot be instantiated in a test at all. Testing it means refactoring it, which
is a separate decision from adding tests and shouldn't be smuggled in under one.

**Why deferred:** the highest-risk uncovered surface (the model boundaries) is now covered; the
rest is worth doing but nothing is currently failing there.

## DJ mode: a maximum segment length (long mixes / DJ sets)

**The concern:** electronic stations often play extended mixes or full DJ sets rather than
3-minute songs. A station announcing one ICY title for a 60-minute set produces a single
60-minute segment — useless as a "song": it dominates the mix, can't be crossfaded sensibly,
eats a large slice of the 500 MB harvest cache on its own, and makes "Up next" meaningless.
The QC gate has a *minimum* length (`DjMinSongSeconds`) but no maximum, so such a segment
sails straight through.

**What the measurements actually show (56 segments across four sessions):** median around 4:30,
and a tail of 7:12 / 7:24 / 7:37 / 8:02 / 8:25 / 8:36 / 10:04. Long, but nothing like an
hour-long set. So this is currently **anticipatory, not observed** — worth building only when a
station is seen doing it, and worth checking the session logs for a genuinely huge segment first.

**A related thing that IS observed:** metadata bounce cutting a mix into fragments — the opposite
problem. "UK Hardcore #13 Mix 2017" appeared twice in one session at 3:33 and 2:25, and a
9-second sliver of "YOU'RE THE ONE" was produced 2 seconds after a 5:20 segment of the same
title. Some stations re-announce the current title mid-track, and every re-announcement is
treated as a song boundary. The new minimum-length gate hides the worst of it, but the underlying
cut logic is wrong: a boundary whose title equals the current one isn't a boundary.

**Design tension if we do chunk.** Fixed-time chunking reintroduces exactly the abrupt
start/end problem the whole deferred-boundary-cut design exists to avoid — every chunk would
begin and end mid-phrase. Better options, roughly in order of appeal:
1. **Cap and reject** — treat anything over N minutes as "not a song", let the live bridge cover
   the gap. Simplest, no arbitrary cuts, no new failure modes.
2. **Silence-aligned cutting** — chop at a detected low-energy point near the target length
   rather than at a fixed offset. `MusicDetector` already computes a per-frame energy envelope,
   so the signal is there.
3. **Treat a long segment as a "set"** — play it as-is but exclude it from crossfade and mark it
   in the UI as a set rather than a track.

**Gotcha for whichever route:** `DjQueueService` dedupes on `artist|title`, so chunks of one mix
would all carry the same key and everything after the first would be silently discarded. Chunking
needs a distinct key per chunk (and probably a "part N" suffix in the displayed title).

## DJ mode: save songs out of the mix (and the list-header actions)

**What:** the DJ-panel mockup (`doc/design-review/DJ Panel Restructure.dc.html`) shows a save
glyph on every Mix row and a header action on each list — "save the whole mix to your library"
on Mix, "add a station to the pool" on Sources. Pass 5 built the restructure around them but not
the actions themselves, because all three need plumbing that doesn't exist yet.

**Why it's more than a button:** harvested songs live in the harvest folder tagged
`SongSource.Harvested`, on a size-capped rotation that deletes them. "Saving" one means copying
it into the user's library folder and re-inserting it as `UserSaved` — a path
`SongLibraryService` doesn't have (its only writer is `AddAndEnrich`, which indexes in place).
Adding a station to a running pool means `DjHarvestService` growing a public "start one more
harvester" entry point and deciding what that does to the reserve.

**Why it matters:** without it, a song you love in the mix is on a countdown to eviction and
there's nothing you can do about it. This is also half of the tier-C "one save model across all
three modes" item — worth doing them together.

## Save-ability expires silently as the rolling cache prunes

**What:** a history row can only be saved while its captured audio is still in the rolling cache.
`MainViewModel.PruneCacheToCap` drops the oldest segments whenever the cache exceeds
`CacheCapMb`, so a row that was saveable this morning quietly isn't by the evening — the row
stays in history, it just loses the affordance. Pass 4 made the *state* legible (the save button
now dims with "Can't save this one — it wasn't recorded while it played" instead of vanishing),
but there's still no way to see how much runway is left before something you might want gets
evicted.

**Possible shapes:** a "cache: 340 MB of 500 MB" line in Options; a subtle marker on rows whose
segment is next in line for eviction; or an explicit "keep" pin that exempts a row from pruning.
The last is probably the most useful and the most work — it needs a flag on `SongHistoryEntry`,
persistence, and a rule for what happens when pinned segments alone exceed the cap.

**Why deferred:** the confusing part (silent absence) is fixed; this is the "help me act before
it's gone" refinement on top.

## UX audit Tier C: DJ session as a first-class object

**What:** (from `doc/design-review/Crystal Radio UX Audit.dc.html`, "bigger ideas") a session
card at the top of the DJ panel — vibe, elapsed time, stations tuned, songs in the mix, refill
health — replacing the status line entirely, giving all five session states (idle → sourcing →
warm-up → steady → refilling/exhausted) somewhere structural to live. Harvest rows become small
live meters rather than counters. **Why deferred:** the audit's tier-b DJ-panel restructure
(pass 5 of the implementation sequence) should land first; this builds directly on it.

## UX audit Tier C: artwork that responds to what's playing

**What:** one fixed background image with a bright diagonal will fight every layout placed over
it. Either derive a heavily-darkened blur from station/album art, or ship 3–4 ambient variants
rotating slowly. **Why deferred:** the pass-1 scrims solve the legibility problem; this is the
nicer long-term answer, not the urgent one.

## UX audit Tier C: one save model across all three modes

**What:** saving currently exists only in Radio history. Every song surface — DJ history, curated
queue, Now Playing — should offer the same save affordance with the same three-state feedback
(unsaved / saving / saved). This is what makes the library feel like the centre of the app.
**Why deferred:** depends on the audit's tier-b save-glyph work (sparkle reclaim); DJ-history
saves also need a "copy from harvest cache into the user library" path that doesn't exist yet.

## UX audit Tier C: "Up next" line in Now Playing

**What:** Library and DJ both play from a queue but never show the next track. A single
"Up next · Title — Artist" line under the transport — the thing a listener most often wants to
know without switching panels. `LocalPlaybackEngine` already knows its queue; needs a small
"peek next" surface plus a Now Playing binding. Cheap and high-value once pass 4's transport
surface exists.

## UX audit Tier C: keyboard-first operation

**What:** for a hands-off, hours-long app: a defined tab order, Space for play/pause, Ctrl+K to
focus the current mode's prompt box, Ctrl+S to save the playing song. Builds on the pass-1 teal
FocusVisualStyle. **Why deferred:** most valuable after the tier-b component work settles the
layout.

## UX audit Tier C: "About this song" as a rail, not a swap

**What:** the reading view currently hides Now Playing, so you lose sight of what's playing while
reading about it. If the window can widen, a third column keeps both; if not, an inline expansion
under the title preserves context. **Why deferred:** a real layout change; revisit after the
audit's tier-a/b work has landed and been lived with.

---

## DJ Mode: apply SegmentQualityChecker/edge-trim to the live "Save Song" path too

**What:** `Services/SegmentQualityChecker.cs` (reject-clear-talk + byte-level edge-trim) currently
only runs on DJ-mode harvested segments (`Services/DjHarvestService.cs`). The existing live
"Save Song" flow (`MainViewModel.SaveSong`) still copies the raw captured segment unchanged —
same boundary-cut imprecision the harvest path had before edge-trim was added could affect
manually-saved songs too.

**Why deferred:** out of scope for the DJ-mode production pass; a plausible, cheap follow-up
since the checker is already a clean, reusable, instantiable service — just needs
`SaveSong` to route the captured segment through it before the final `File.Copy`.

## DJ Mode: dedicated Options UI for the new config knobs

**What:** `DjHarvesterCount`, `DjHarvestReserveCount`, `DjQueueLowWatermark`, `DjMaxHarvestCacheMb`
(`Services/SettingsStore.cs`) are settings-file-editable only — no UI in `OptionsDialog` yet.
`DjHarvesterCount` in particular is the direct lever to reduce the ongoing background-LLM
enrichment cost (every harvested song gets a full description, same as explicitly-saved ones —
an explicit user choice made during planning, not a bug) if it becomes noticeable.

**Why deferred:** sensible defaults ship day one; add sliders/fields once real usage shows which
knobs people actually want to tune.

## DJ Mode: smarter harvest-cache eviction

**What:** `DjHarvestService.EvictIfNeeded` is a simple folder-size-cap, oldest-file-first eviction.
The spec (`doc/DJ-MODE-SPEC-HARVEST.md` §6.5) allows for smarter "lowest-value" ranking (e.g.
never-played songs before ones the queue actually played), which would need a play-count/
last-played signal this pass didn't add.

**Why deferred:** simple oldest-first is enough to bound disk usage for v1; revisit if eviction
turns out to be discarding songs users would have wanted kept.

## Library folder rescan (reconcile disk ↔ library.db)

**Problem:** the song library list is read entirely from `LibraryStore`'s SQLite DB
(`Services/LibraryStore.cs`), never from scanning the actual library folder on disk. Two
consequences, found while manually testing local playback with harvested songs:

1. **Deleting a file on disk doesn't remove it from the list.** `SongLibraryService.BackfillInBackground()`
   already has a step that drops DB rows whose file no longer exists — but `MainViewModel` fires
   that reconciliation as a background `Task` and builds the UI's `LibrarySongs` collection
   synchronously right after, without waiting for or refreshing from the result. So the dead-file
   cleanup logic already exists but never reaches the screen.
2. **Copying a file directly into the library folder doesn't add it.** Nothing scans the folder for
   untracked files — only `SongLibraryService.AddAndEnrich(...)` (the normal in-app "save this
   song" flow) inserts new rows.

**Proposed fix:** a `RescanLibraryFolder()` addition to `SongLibraryService` that, in one pass:
   - Removes DB rows for missing files (fixes the existing dead code path — the reconciliation
     logic is already there, this just makes sure the UI list reflects it once done).
   - Finds files on disk not yet in the DB and inserts them via the *existing* `AddAndEnrich`
     pipeline (unchanged) — title/artist parsed from the filename where possible (the harvest
     naming convention `station · artist - title.ext` is directly parseable; anything else falls
     back to filename-as-title, blank artist, rather than blocking ingestion on perfect metadata).
   - Triggered both at startup and via an explicit "Refresh library" UI action — useful for exactly
     this kind of manual-copy workflow, not just crash/startup recovery.

**Why deferred:** not blocking DJ mode work; a real feature but low urgency until someone actually
needs to hand-manage the library folder again.
