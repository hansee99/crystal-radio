# DjDetector — music / speech detector harness

The offline detector proof of concept from
[doc/DJ-MODE-SPEC.md](../../doc/DJ-MODE-SPEC.md) (PoC 1), reused by the harvest design
([doc/DJ-MODE-SPEC-HARVEST.md](../../doc/DJ-MODE-SPEC-HARVEST.md), §6.4) as the segment
**quality check + edge-trim** detector.

It answers: *can we tell music from ads / news / talk on real, messy internet-radio audio,
reliably enough to drive the harvest QC (and later the live switching)?* — offline, with no
engine, on files you already have (the segments `DjHarvest` produced).

## What it computes

Per file: decode to mono PCM → slide a 1 s window (50% overlap) → per window, speech/music
features (Scheirer–Slaney lineage) built from 25 ms frames:

- **4 Hz modulation energy** — energy of the amplitude envelope near the ~4 Hz syllabic rate;
  high for speech, low for sustained music. *The primary discriminator.*
- Zero-crossing-rate mean + variance, low-energy-frame ratio, spectral flux mean + variance,
  spectral centroid / rolloff, spectral flatness.

From those it derives a per-window **music confidence** (0 = speech/ad, 1 = music), a whole-file
**verdict** (MUSIC / MIXED / TALK), and — the part your PoC-1 finding needs — an **edge-trim**
suggestion: how many seconds of non-music sit at the head and tail of an otherwise-music
segment. It also writes every window's raw features to CSV so the weights can be tuned/fitted.

The detector (`MusicDetector`) and FFT (`Fft`) are dependency-free and operate on `float[]` PCM,
so they lift straight into the app as `Services/MusicDetector.cs` later (live engine §6.4 and
the harvest QC both reuse them).

## Usage

```sh
# Analyze a folder of clips (recurses); writes djdetector-features.csv
dotnet run --project tools/DjDetector -- C:\harvest

# Explicit files, custom CSV
dotnet run --project tools/DjDetector -- clip1.mp3 clip2.aac --csv out.csv

# Measure accuracy: label the clips (or use music/ and talk/ subfolders)
dotnet run --project tools/DjDetector -- C:\clips\music --label music
dotnet run --project tools/DjDetector -- C:\clips           # music/…, talk/…, ads/… inferred
```

| Arg | Meaning |
|---|---|
| *(positional)* | Files and/or folders (folders recurse over `.mp3 .aac .m4a .wav .ogg`) |
| `--label X` | Force a label for accuracy (`music` or anything else → `nonmusic`) |
| `--csv <path>` | Feature CSV output (default `djdetector-features.csv`) |
| `--suspects-csv <path>` | Write the suspect-span report (see below) to CSV as well as console |
| `--corrections <path>` | Confirmed ground-truth spans that override labels for real harvested files (see below) |

Label inference: a file under a folder named `music` → *music*; under `talk` / `speech` /
`ads` / `news` / `jingle` → *nonmusic*; otherwise unlabelled (analyzed, not scored).

## Ground-truthing real harvested songs (no whole-file relabelling, no audio editing)

A whole-file MUSIC/TALK pass over harvested songs is coarse: it tells you a file has *some*
leaked talk, not where, and can't localize a mid-song DJ drop-in. Instead, the harness reports
**suspect spans** — contiguous below-threshold runs — so you spot-check specific timestamps by
ear instead of relistening to whole songs.

```sh
# 1) Run over the harvested folder — suspect spans print to console and to the CSV.
dotnet run --project tools/DjDetector -- C:\harvest --suspects-csv suspects.csv

#      suspect   12.0s – 18.5s  (6.5s, interior, conf 0.31)
#      suspect  238.0s – 244.0s (6.0s, tail, conf 0.22)

# 2) Seek to those timestamps in any player. For each you can actually confirm, add a line to
#    corrections.csv:  file,startSec,endSec,label   (label is music or nonmusic)
#    - Genuinely talk?              → nonmusic  (turns it into new hard-negative training data)
#    - False alarm (quiet passage)? → music     (a hard-positive — just as valuable)

# 3) Re-run with the corrections applied — only windows inside a confirmed span get that
#    label in the feature CSV; the rest of the file stays unlabelled until you confirm it too.
dotnet run --project tools/DjDetector -- C:\harvest --corrections corrections.csv --csv corrections-features.csv
```

`kind` in the suspect report: **lead**/**tail** spans touch the very start/end (same thing the
edge-trim readout already shows); **interior** spans are mid-file dips edge-trim can't see —
the new information worth checking first (a talk-over, a DJ drop-in mid-song).

### When suspects avalanche (hundreds of tiny spans): it's a corpus gap, not a screening task

If a harvested folder produces a suspect count wildly out of proportion to the song count
(hundreds of spans across a few dozen songs), don't try to review them span-by-span — that's
usually not real talk-in-music. Check whether the spans for one file are actually contiguous
(a `--suspects-csv` sorted by file will show this): dozens of "interior" spans that run back-to
-back for most of a song's length mean the detector's confidence is hovering near the threshold
for a *sustained* stretch, which is a **corpus gap** — the detector under-represents whatever
genre that song is (quiet/sparse indie and downtempo/chill electronic are the classic case,
since the original training clips skewed toward energetic mainstream pop/rock/disco) and
mistakes "quiet and sparse" for "speech-like."

The fix is cheap and doesn't require listening to hundreds of spans:

```sh
# Generate one whole-file "music" row per analyzed file (0..duration).
dotnet run --project tools/DjDetector -- C:\harvest --template-corrections corrections-template.csv
```

Open the template, and **delete (or edit) only the rows for songs you know had real audible
talk** — everything else defaults to "clean, whole song." This inverts the effort from
"confirm hundreds of spans" to "veto a handful," and it directly injects the missing genre
diversity as clean training examples — which is the actual fix for the avalanche, not the
symptom. Rename the thinned file to `corrections.csv` and use it as in the workflow above.

Merge `corrections-features.csv` with the original labelled-corpus CSV before re-fitting (or
pass both to a small script) — each confirmed span is a few seconds of real, precisely-located
production data, which is worth more per-window than another whole clip.

## How to read it against the PoC-1 finding

- Run it over the `DjHarvest` output folder. Segments that "had talk at the start/end" should
  show a nonzero **trim: Xs lead / Ys tail** — that's the detector locating the talk. Confirm
  by ear that the trim spans line up with what you heard.
- To get the **exit-criterion number**, sort a few dozen clips into `music/` and `talk/`
  (or `ads/`) folders and run against the parent. The tool prints per-window accuracy vs. the
  ≳ 90% target. If it clears that with no *sustained* misclassification, the detection risk is
  retired and the detector can move into the app for segment QC + edge-trim.
- The classifier is a **logistic regression** whose coefficients (`LrBias` / `Lr_*` in
  `MusicDetector`) were fitted to a labelled corpus. To re-fit as your corpus grows, run
  `python tools/DjDetector/fit_logreg.py djdetector-features.csv` and paste the printed
  constants back into `MusicDetector.cs`. The fitter reports honest, file-grouped hold-out
  accuracy (windows from one clip never split across train/test).

## Notes

- DSP-only, per the spec's default assumption. The ONNX/YAMNet "second opinion" is an optional
  later spike if ads-with-music-beds prove hard — not built here.
- Decode uses the BASS "no sound" device; no audio is played.
- Not part of `crystal-radio.sln`; an on-demand PoC / tuning / regression tool.
