# DjQueue — warm-start + self-refilling queue (PoC 2)

The second PoC from [doc/DJ-MODE-SPEC-HARVEST.md](../../doc/DJ-MODE-SPEC-HARVEST.md) (§5, "PoC 2 —
Warm-start + self-refilling queue"). Proves continuous playback by reusing the Library side of the
app almost unchanged: `SongCurator` picks an opening queue from the local library for a prompt,
`LocalPlaybackEngine` plays it (real audio, not the "no sound" device `DjHarvest` uses), and this
tool watches a running `DjHarvest` (PoC 1) instance's output folder for newly-kept segments and
appends them to the queue as they land — so it never runs dry.

Two separate processes, run side by side:

```sh
# Terminal 1 — start harvesting (PoC 1)
dotnet run --project tools/DjHarvest -- --out C:\harvest

# Terminal 2 — start the queue against the same folder
dotnet run --project tools/DjQueue -- --prompt "mellow electronic for coding" --harvest-dir C:\harvest
```

| Arg | Meaning |
|---|---|
| `--prompt "<text>"` | Required. Fed to `SongCurator` for the warm-start seed. |
| `--harvest-dir <dir>` | Folder to watch (must match `DjHarvest`'s `--out`). Default `%TEMP%\DjHarvest`. |
| `--max-seed N` | Max songs pulled from the library for the opening queue (default 20). |
| `--seconds N` | Auto-stop after N seconds. Default 0 = run until Ctrl+C. |

## What it does

1. **Warm start.** `SongCurator.CurateAsync(prompt)` — semantic recall over the existing library
   (`%LocalAppData%\RadioPlayer\library.db`) + an LLM arc, same pipeline the app's offline curated
   playlist feature already uses. If the library is empty (or has no embeddings yet), it degrades
   gracefully per `SongCurator`'s own design (recency order, or nothing) — this tool then reports
   "warming up" and starts playing the moment the first harvested segment lands, per the spec's
   cold-start requirement.
2. **Playback.** `LocalPlaybackEngine.SetQueue(...)` — the *real* engine, unchanged. This is the
   one PoC in the harvest design that uses actual audio output (DjHarvest and DjDetector both use
   BASS's "no sound" device).
3. **Self-refill.** Every 2s, tails `manifest.csv` in `--harvest-dir` (the file `DjHarvest`'s QC
   step writes/appends to) for new rows with a non-empty `keptFile`, dedupes by artist+title
   (case-insensitive, checked against both the seed and everything appended so far), and calls the
   new `LocalPlaybackEngine.Append(...)` to add them to the tail of the queue without disturbing
   whatever's currently playing.

## `LocalPlaybackEngine` changes

Two small, real (non-PoC) additions in `src/RadioPlayer.Core/Services/LocalPlaybackEngine.cs`, needed because
`SetQueue` replaces the whole queue and restarts playback — wrong for a queue that's supposed to
grow quietly in the background:

- `QueueCount` — total tracks (played + upcoming), for status reporting.
- `Append(IReadOnlyList<LocalTrack>)` — adds to the tail; if the queue was empty (cold start with
  no seed), starts playing the first appended track automatically.

## A real gotcha hit while building this: Dispatcher vs. `await` in a console host

`LocalPlaybackEngine` and `SongCurator` are UI-thread-affine (WPF `Dispatcher`), same as
`StreamRecorder` in `DjHarvest`. But unlike `DjHarvest`, this tool's setup does one bit of async
work (`SongCurator.CurateAsync`, an HTTP round trip) *before* the timers/`Dispatcher.Run()` are set
up. A plain console app installs no `SynchronizationContext`, so `await`ing that call resumed the
rest of the script on a thread-pool thread — meaning `Dispatcher.Run()` (which always pumps
`Dispatcher.CurrentDispatcher` for *whatever thread calls it*) ended up pumping a different
dispatcher than the one the `DispatcherTimer`s further up were explicitly bound to. Symptom: no
crash, no error — the timers (manifest watcher, status, auto-stop) simply never fired, and the
process hung until externally killed.

Installing a `DispatcherSynchronizationContext` up front (what WPF's `Application` does for you
automatically) doesn't fix it either — it trades the problem for a deadlock, since a posted
continuation can only run once the dispatcher's message loop is *already pumping*, and at the
point of that first `await` it isn't yet (chicken-and-egg). The fix that actually works here: keep
the one async call fully synchronous (`.GetAwaiter().GetResult()`) so the whole script — seeding,
timer setup, and `Dispatcher.Run()` — runs start to finish on a single, unchanging thread, exactly
like `DjHarvest`'s fully-synchronous main body already does.

## How to read it against the PoC-2 exit criterion

Run both tools together for a while and listen: playback should go warm-start → (gap-free)
transition into freshly-harvested songs as `DjHarvest` drops them, with no repeats back-to-back.
The console output makes each half of that verifiable without needing to listen the whole time —
`▶ now playing` lines for transitions, `+ appended` for each new song entering the queue, and
`(skip dup)` if a title reappears (station replays, or an artist+title collision).

## Notes

- Not part of `crystal-radio.sln`; an on-demand PoC/spike tool, same convention as `DjHarvest`.
- Uses the app's *real* `%LocalAppData%\RadioPlayer\library.db` and local embedding model — no
  separate PoC-only data store.
- Doesn't write anything back into the library — harvested songs are queued in-memory only, not
  persisted as saved library entries. Whether they should be is a decision for the real
  `DjQueueService` (see the spec's §10 build order), not this spike.
