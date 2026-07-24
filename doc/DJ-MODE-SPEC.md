# DJ Mode — feature specification

A playback mode that turns a vibe prompt into a continuous, self-driving stream: the app
searches for matching stations (reusing the existing AI search), keeps several playing in
the background, detects when one drops out of music (ads, news, DJ talk), and crossfades
to another that is currently playing a song — so the listener gets an uninterrupted "radio
of radios" with no ad breaks.

This document is written for Claude Code. It is deliberately **PoC-first**: the goal of the
first phase is to cheaply answer *"does the core idea actually work?"* before building the
full engine. Read [doc/TECHNICAL.md](TECHNICAL.md) first — this spec assumes its
architecture (the `IPlaybackEngine` seam, `RadioEngine` as the sole-ish BASS owner, the
unified search pipeline, `SongHistoryFilter`, `StreamRecorder`).

---

## 1. Goals and non-goals

### Goals

- Given a vibe prompt (or an existing station list / genre tag), play a continuous audio
  experience that automatically avoids ad / news / talk segments.
- Keep several **hot standby** streams decoding in the background so a switch is gapless.
- **Crossfade** between streams rather than hard-cutting.
- Fit the existing engine seam: DJ mode is just another `IPlaybackEngine`, so SMTC, the
  Win11 flyout, media keys, and taskbar buttons keep working with zero special-casing.
- Degrade gracefully: no API key, no embeddings, a dead standby, or a poor detector
  verdict must never crash or dead-end the mode.

### Non-goals (v1)

- **Song recording / library capture during DJ mode.** `StreamRecorder` assumes one clean
  playback connection cut at ICY boundaries; crossfaded output spans stations mid-song and
  is not clean per-song material. DJ mode does not feed the library. (Revisit later — a
  standby could be recorded *before* it becomes the foreground, but out of scope now.)
- **Perfect ad detection.** The target is "detect when it stops being music," which covers
  ads, news, and talk uniformly. A few seconds of leaked ad audio on a bad call is
  acceptable and handled by hysteresis, not a failure.
- **Beat-matched / tempo-synced mixing.** v1 crossfades on a volume envelope only. No
  tempo or key matching.
- **HLS streams.** Already excluded upstream (`hls == false` filter); unchanged here.

---

## 2. Key concepts

| Term | Meaning |
| --- | --- |
| **Standby pool** | The set of streams DJ mode is actively decoding in the background. Each is connected, decoding, and continuously classified. |
| **Hot standby** | A pool member fully decoding + probed. Costs one live connection + CPU. Realistically 2–3 at once. |
| **Cold reserve** | Validated station *candidates* (URLs only, not connected) held in reserve. When a hot standby dies or goes to ads and needs replacing, one is promoted. |
| **Foreground** | The single standby currently audible (or fading in). Its ICY title + name is the now-playing identity exposed to SMTC. |
| **Delay buffer** | A per-stream ring buffer. Playback is pulled from the *tail* (N seconds behind live); the detector reads the *head* (live). This lookahead is what lets us fade out *before* an ad reaches the listener's ear. |
| **Detector** | The music / non-music classifier. Runs per standby, continuously, on the head of that stream's buffer. |
| **Decision loop** | Applies hysteresis to detector output + ICY-metadata signal and decides when to crossfade and to which target. |

---

## 3. Why PoC-first — the riskiest assumptions

The feature stands or falls on three independent risks. The PoC exists to retire them in
cheapest-first order, so we don't build a whole engine on top of a detector that turns out
not to work.

1. **Detection reliability (biggest risk, cheapest to test).** Can we tell music from
   ads/news/talk on *real, messy* internet radio, reliably enough to drive switching? If
   not, the feature is dead — so test this first, offline, with no engine at all.
2. **Gapless crossfade with multiple live streams.** Can BASS (via BASSmix) hold several
   live decode channels and crossfade between them without a gap or a device stall? Test in
   isolation, ignoring detection.
3. **Timing.** Does the delay-buffer + preemptive-fade approach actually hide ad onsets in
   practice, given real detector latency + hysteresis? Test by combining 1 and 2 in a
   dry-run that only *logs* the switches it would make.

Only after all three look good do we build `DjEngine` and wire it into the UI.

---

## 4. Proof of concept

Each milestone is a self-contained, throwaway-friendly deliverable with an explicit exit
criterion. **Milestone 1 is "the minimal PoC"** the request asks for — it alone answers
"could this work?". The later milestones de-risk the rest before committing to the engine.

### PoC 1 — Offline detector harness (the minimal PoC)

**A standalone console tool, no UI, no crossfade, no mixer.** Follows the existing
`tools/SeedEnrichment/` convention.

**Location:** `tools/DjDetector/`

**Input:** local audio files — ideally
- known-music segments already produced by `StreamRecorder`, and
- a handful of manually captured ad / news / talk clips (record a few live streams during
  break time; even 10–20 clips is enough to start).

**What it does:**
1. Decode each file with a BASS `BASS_STREAM_DECODE` channel.
2. Slide a ~1 s analysis window (≈50% overlap) over the audio. Per window, compute
   speech/music discrimination features (Scheirer–Slaney lineage), each aggregated from
   ~20 ms frames:
   - **4 Hz modulation energy** — energy of the amplitude envelope around the ~4 Hz
     syllabic rate; high for speech, low for music. *The single most discriminating
     feature — prioritise getting this one right.*
   - **Zero-crossing-rate mean + variance** — speech has high ZCR variance.
   - **Low-energy-frame ratio** — fraction of frames well below the window's mean energy;
     speech has inter-word pauses, music tends not to.
   - **Spectral flux** and its variance.
   - **Spectral centroid / rolloff** mean + variance.
   - **Spectral flatness** — music is more tonal, speech/noise flatter (optional, cheap).
   For this offline harness it's fine to get the magnitude spectrum straight from BASS
   (`Bass.ChannelGetData` with an `FFT*` flag) on the dedicated decode channel — there is
   no mixer contention here. (The live engine will differ — see §6.4.)
3. Emit a per-window **music-confidence** value (0–1) and a whole-file verdict, plus a CSV
   of the raw features so thresholds/weights can be tuned.
4. Start with a hand-tuned threshold rule or a tiny logistic regression; the CSV is the
   training data for the latter. Keep the classifier small and explainable — no heavyweight
   dependency.

**Optional stretch:** since the app already ships ONNX Runtime for MiniLM, spike a
pretrained audio tagger (e.g. YAMNet: 16 kHz mono, ~1 s frames, AudioSet Music/Speech
classes) as a second opinion and compare accuracy vs. the DSP path on the same clips.
Decide DSP-only vs. DSP-gate-then-ONNX from the numbers.

**Exit criterion:** on held-out real clips, the detector separates music from
ads/news/talk with a stable margin (target ≳90% window accuracy, and — more importantly —
no *sustained* misclassification longer than the planned hysteresis window). If this fails,
stop and rethink before building anything else.

### PoC 2 — Two-stream crossfade spike

**A minimal spike proving the mixing half**, independent of detection. Console tool or a
hidden debug command — not production UI.

**What it does:**
1. Connect two live stations (one MP3 via `Bass.CreateStream`, one AAC via
   `BassAac.CreateStream` — exercise both paths).
2. Create each as a decode channel; add both to a BASSmix mixer feeding one output device.
3. On a keypress, crossfade between them over a few seconds using per-channel volume
   envelopes (BASSmix channel volume / slide).

**Exit criterion:** repeated crossfades are audibly gapless and click-free; the output
device never stalls; CPU with 3 simultaneous decode channels is acceptable on target
hardware. Confirms BASSmix can carry the standby pool.

### PoC 3 — Delay buffer + live detection dry-run

**Combines 1 and 2 into a timing test that makes no audible switches** — it only logs the
switches it *would* make, so we can eyeball whether the fade would have fired before the ad
was audible.

**What it does:**
1. One live stream, decoded into a **delay ring buffer** of N seconds (start N = 10 s).
2. Play from the buffer tail (listener hears audio N seconds old).
3. Run the PoC-1 detector on the buffer head (live edge).
4. When the detector reports sustained non-music at the head, log a timestamped
   "WOULD CROSSFADE NOW" line. Compare, against the recorded stream, whether that decision
   lands *before* the ad reaches the tail (the ear).

**Exit criterion:** across several real ad breaks, the "would switch" decision consistently
precedes the ad becoming audible at the tail, given realistic detector latency +
hysteresis. This validates the core timing invariant in §6.5. If it doesn't, the fix is
usually a longer delay N or shorter hysteresis — tune here, cheaply, before the engine.

---

## 5. How DJ mode fits the existing architecture

DJ mode is a **third `IPlaybackEngine`**, alongside `RadioEngine` and
`LocalPlaybackEngine`:

- It exposes the same transport surface (`State`, `Volume`, play/pause/stop,
  `StateChanged` / `ErrorOccurred`), so `MainViewModel` and `SmtcController` drive it
  without knowing it's special.
- Switching *into* DJ mode stops the previously active engine and repoints
  `SmtcController.SetActiveEngine`, exactly as Radio ↔ Library does today. The "one BASS
  device, one now-playing identity" invariant is preserved: the mixer feeds one device, and
  the *foreground* standby is the single now-playing identity.
- **Precedent for BASS access:** the doc says `RadioEngine` is "the only class that talks
  to BASS," but `LocalPlaybackEngine` already talks to BASS too. `DjEngine` doing so (it
  additionally needs BASSmix) is consistent with that existing sibling. Keep *all* of DJ
  mode's multi-stream + mixer complexity inside `DjEngine`; nothing leaks out.

Reuse, don't reinvent:
- **Search** — DJ mode calls a headless variant of `RunUnifiedSearchAsync` to get its
  candidate stations (§6.2). Same pipeline the visible search uses; results just go to the
  pool instead of the UI.
- **Metadata signal** — `SongHistoryFilter` already classifies ICY titles as
  song / ad / jingle / ident. DJ mode's decision loop **fuses that as a corroborating
  signal** (a blanked or ad-classified title is strong evidence for "not music"), rather
  than duplicating the logic.
- **AAC vs MP3** — same gotcha: `BassAac.CreateStream` for AAC/AAC+, `Bass.CreateStream`
  otherwise. Per standby.
- **Threading** — BASS callbacks fire on BASS threads; marshal to the UI thread via
  `Dispatcher` before touching the VM / SMTC, as everywhere else.

---

## 6. Full implementation — big picture

### 6.1 New components

| Path | Responsibility |
| --- | --- |
| `Services/DjEngine.cs` | `IPlaybackEngine` implementation. Owns the standby pool, the BASSmix mixer + output device, per-stream delay buffers, the crossfade, and the now-playing identity. The only place multi-stream + mixer state lives. |
| `Services/StandbyStream.cs` | One pool member: connection, decode channel, delay ring buffer, current music-confidence, ICY title, lifecycle state (Connecting / Confirming / Ready / Foreground / Ads / Dead). |
| `Services/MusicDetector.cs` | The classifier from PoC 1, promoted to a reusable service. `Classify(window) → confidence`. Pure/stateless per window; the harness and the engine share it. |
| `Services/DjDecisionLoop.cs` | Consumes per-standby confidence + `SongHistoryFilter` metadata, applies hysteresis, and decides when/where to crossfade. No BASS knowledge — testable in isolation. |
| `Services/DjSessionService.cs` | Seeds and replenishes the pool from search: runs the headless search, holds the cold reserve, promotes reserves when standbys die/go-to-ads. |
| `ViewModels/` (extend `MainViewModel`) | DJ mode entry command, prompt input, "now playing (auto)" display, stop. |
| `Views/` (extend `MainWindow`) | Minimal DJ mode entry point + status (see §6.6). |
| `tools/DjDetector/` | The PoC-1 harness, kept as an offline tuning/regression tool. |

### 6.2 Sourcing the pool from search

1. On start, DJ mode runs the **headless search** for the prompt, requesting *more*
   candidates than a normal page (e.g. top ~15–20 validated stations), because standbys
   churn — they die, go to ads, or get demoted, and must be replaced without another
   round-trip.
2. The top few become **hot standbys** (connected, decoding, confirming music). The rest
   are the **cold reserve** (URLs held, not connected).
3. **Seeding modes / graceful degrade** (mirrors the app's existing degrade philosophy):
   - Vibe prompt → full pipeline (needs API key, like the visible search).
   - Plain genre / tag, or an existing user station list → direct Radio Browser tag search,
     **no key required**. So DJ mode is usable without AI, just less "smart" about the vibe.
   - Empty reserve + all standbys dead → surface a clear "no more stations" state, don't
     spin.

### 6.3 Standby lifecycle

```
                promote reserve
Cold reserve ─────────────────▶ Connecting ──▶ Confirming ──▶ Ready
                                    │ fail          │ (music at      │
                                    ▼               │  head for      │ selected by
                                   Dead             │  ≥ confirm)    │ decision loop
                                    ▲               ▼               ▼
                                    │            (still talk/ads)  Foreground
      demote (dead / long ads) ─────┴───────────────┴───────────────┘
```

- A stream is only eligible as a **crossfade target** once it is `Ready` — i.e. confirmed
  on music at listener-time (head *and* the buffered span to the tail are music), so the
  fade lands on an actual song, not a target that's itself mid-ad.
- A `Foreground` stream that goes to sustained ads → decision loop crossfades to a `Ready`
  standby, and the old foreground is demoted (kept briefly in case nothing else is Ready,
  then dropped if the break is long).
- Dead/errored standbys are dropped and a reserve is promoted to keep the hot count at
  target.

### 6.4 Detector in the live engine (differs from the harness)

In PoC 1 the detector reads FFT straight from a dedicated BASS decode channel. In the live
engine that would fight the mixer: on a decode channel, `ChannelGetData` **consumes**
samples, so the detector and the mixer can't both pull from the same channel.

**Decode once, read twice:** each standby decodes into its delay ring buffer, and *both*
consumers read from the buffer, not from BASS:
- the **mixer** is fed from the buffer **tail** (playback), and
- the **detector** computes features over windows at the buffer **head** using a **managed
  FFT** (small dependency or a tiny in-tree Cooley–Tukey) rather than `ChannelGetData`.

This keeps the single BASS decode as the only consumer, gives a controllable delay, and
puts head (future) and tail (now) on the same timeline.

### 6.5 Timing invariants

Let `D` = delay-buffer length, `H` = hysteresis (consecutive non-music required before
switching), `F` = crossfade duration, plus detector latency `L` and a safety margin `M`.

> **`D ≥ H + F + L + M`.**  The delay must be long enough that, after we've waited out
> hysteresis, detected the change, and run the fade, the ad *still* hasn't reached the ear.

Suggested starting values (tune in PoC 3): `D = 10 s`, `H = 8–12 s`, `F = 3–5 s`. Hysteresis
is what prevents flapping on a 3-second DJ voiceover or a spoken song intro — the classic
false positives. Long talk-over intros/outros, jingles, and news read over a music bed are
handled by the same duration gating.

### 6.6 SMTC / UI integration

- **Now playing:** the foreground standby's name + ICY title, flipped to the new station
  when a crossfade completes (or at its midpoint — pick one; midpoint feels more "live").
- **Media-key / flyout "next track":** map to **force-skip** — immediately crossfade to
  another `Ready` standby. This is a free, natural UX win that reuses the next-track wiring
  `SmtcController` already has.
- **UI (keep minimal for v1):** a DJ mode toggle/entry with a prompt box, a compact "auto —
  now playing X, N stations in rotation" status line, and stop. No pool management UI in v1;
  the point is that it runs itself.

### 6.7 Failure handling & degrade

- No `Ready` target when the foreground hits ads → hold the foreground (better a few
  seconds of ad than silence) and keep trying to promote reserves; switch the instant one
  becomes Ready.
- Standby stream drops → reconnect a bounded number of times (reuse `RadioEngine`'s
  reconnection approach), else demote to Dead and promote a reserve.
- Detector unsure (confidence hovering mid-range) → treat as "not confidently music"; don't
  select as a target, but don't switch *away* from a currently-fine foreground either.
- All degrade paths favour *continuing to play something* over stalling.

---

## 7. Configuration knobs

| Setting | Default | Notes |
| --- | --- | --- |
| `PlaybackDelaySeconds` (`D`) | 10 | Must satisfy the §6.5 invariant. |
| `NonMusicHysteresisSeconds` (`H`) | 10 | Consecutive non-music before switching. |
| `CrossfadeSeconds` (`F`) | 4 | Volume-envelope fade length. |
| `HotStandbyCount` | 2 | Simultaneous decoding streams. CPU/bandwidth scale linearly. |
| `ReserveCandidateCount` | 15 | Validated URLs held for replenishment. |
| `MusicConfidenceThreshold` | tune in PoC 1 | Above → music; band below → uncertain. |
| `ConfirmMusicSeconds` | ≈ `D` | A promoted standby must be music this long before it's a valid target. |

---

## 8. Open decisions (resolve during PoC)

- **DSP-only vs. DSP-gate + ONNX tagger** for the detector — decide from PoC-1 accuracy vs.
  cost. Default assumption: DSP-only is enough for "is this still music"; only add ONNX if
  ads-with-music-beds prove hard.
- **Exact BASS delay mechanism** — ring buffer + push-to-mixer vs. a BASSmix-native
  position/pause trick. Pin down in PoC 2/3; the spec only requires the *behaviour* (per-
  stream delay, head/tail split, no double-consume).
- **Crossfade curve** — linear vs. equal-power. Equal-power usually sounds better across two
  unrelated songs; confirm by ear in PoC 2.
- **Fingerprinting to avoid dupes** (Chromaprint/AcoustID on the buffer) so DJ mode never
  crossfades into the *same* song already playing on another standby. Nice-to-have; separate
  from music/speech detection; defer unless duplicates are common in testing.

---

## 9. Testing strategy

- **Detector:** `tools/DjDetector/` doubles as a regression harness — keep a small labelled
  clip set in-tree and assert accuracy stays above threshold.
- **Decision loop:** `DjDecisionLoop` takes confidence + metadata as inputs and emits switch
  decisions with no BASS dependency — unit-test hysteresis, target selection, and the
  no-target hold behaviour with synthetic confidence timelines.
- **Engine:** manual / soak testing against real streams (long-run stability, reconnection,
  CPU over hours). Automate what's feasible behind the `IPlaybackEngine` seam with a fake
  standby source.

---

## 10. Suggested build order

1. **PoC 1** — `tools/DjDetector/`. Retire the detection risk. *(This is the minimal PoC.)*
2. **PoC 2** — two-stream crossfade spike. Retire the mixing risk.
3. **PoC 3** — delay buffer + dry-run. Retire the timing risk.
4. `MusicDetector` + `DjDecisionLoop` (pure, unit-tested) from the PoC learnings.
5. `StandbyStream` + `DjEngine` (mixer, pool, delay, crossfade) behind `IPlaybackEngine`.
6. `DjSessionService` (headless search seeding + reserve replenishment).
7. `MainViewModel` / `MainWindow` entry + status; `SmtcController` next-track → force-skip.
8. Config, degrade paths, soak testing.
