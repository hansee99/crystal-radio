# DjHarvest — DJ Mode (harvest) PoC 1

The minimal proof of concept for the **harvest-and-replay** DJ mode
([doc/DJ-MODE-SPEC-HARVEST.md](../../doc/DJ-MODE-SPEC-HARVEST.md)). It answers the one new
engineering question and the fill-rate assumption together:

> Can we run several **headless** (download-only, no playback) recording connections in
> parallel, cut clean complete-song segments at ICY boundaries, and do songs arrive fast
> enough to keep a queue playing continuously?

It reuses the app's real `StreamRecorder` — the same boundary-cut, deferred-offset,
min-size, and `SongHistoryFilter` logic that the shipping "save this song" feature uses — so
the segments it produces are exactly what the eventual harvest engine would queue.

## How it works

- Initializes BASS on the **"no sound" device (0)** and *plays* each station there. Playback
  drives the download + ICY metadata syncs at real-time with no audible output and no
  contention for a real output device — the headless-harvest trick.
- Each station gets its own `StreamRecorder`; the download callback feeds it raw bytes and
  the ICY metadata sync feeds it title changes, exactly as `RadioEngine` does during playback.
- Every complete, song-like segment is copied to the scratch folder (named
  `station · artist - title.ext`) so you can **listen to a sample by ear**, and the original
  is removed from the app's real cache so this tool doesn't pollute it.
- Reconnects a dropped stream a bounded number of times, then marks it dead.

## Usage

```sh
# Defaults: 4 stations (1 AAC + 3 MP3), 15 minutes, segments to %TEMP%\DjHarvest
dotnet run --project tools/DjHarvest

# Longer soak to a chosen folder
dotnet run --project tools/DjHarvest -- --seconds 3600 --out C:\harvest

# Your own stations (codec inferred from the URL, or forced with |aac / |mp3)
dotnet run --project tools/DjHarvest -- "http://host/stream|aac" "http://host/other|mp3"
```

| Arg | Default | Meaning |
|---|---|---|
| *(positional)* | 4 built-in stations | Station URLs, each `url` or `url\|aac` / `url\|mp3` |
| `--seconds N` | 900 | How long to run before auto-stopping |
| `--out <dir>` | `%TEMP%\DjHarvest` | Where kept segments are copied |
| `--offset <sec>` | 6 | Boundary-cut offset (same meaning as `CaptureBoundaryOffsetSeconds`) |

Ctrl+C stops early and prints the final summary. No API key needed — PoC 1 takes explicit
station URLs; search-based seeding is a later phase.

## What to look at

- The periodic status shows per-station **kept songs**, **ICY titles seen**, and **songs/hr**,
  plus an aggregate vs. a rough starvation line (~17 songs/hr sustains one continuous
  playback at ~3.5-min average song length).
- The `--out` folder is the by-ear check: are the cut segments clean, complete songs, or do
  some stations leak ads/talk (poor ICY hygiene → would need the optional QC backstop)?

## Exit criterion (from the spec)

N harvesters run stably in parallel, produce clean complete-song segments, and the aggregate
fill rate comfortably exceeds one song per average-song-length (the queue would grow, not
starve) after warm-up. If that holds, the harvest design's core risk is retired and the next
step is PoC 2 (warm-start + self-refilling queue on `LocalPlaybackEngine`).

## Notes

- **Run with the main app closed** (like the SeedEnrichment tool) to avoid the app's cache
  sweep racing with the harvesters.
- Not part of `crystal-radio.sln`; an on-demand PoC/soak tool, kept per the spec's testing
  strategy as a fill-rate / stability harness.
