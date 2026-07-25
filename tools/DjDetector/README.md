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

Label inference: a file under a folder named `music` → *music*; under `talk` / `speech` /
`ads` / `news` / `jingle` → *nonmusic*; otherwise unlabelled (analyzed, not scored).

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
