# Backlog

Small, deferred, non-urgent items — things worth doing but not right now. Not a full spec each;
just enough context to pick back up later. Add new items at the top.

---

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
