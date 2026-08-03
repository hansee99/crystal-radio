# DjTranscribe — can speech recognition do what the acoustic detector can't?

A PoC, not a shipping component. It answers one question: **does an ASR model separate music from
radio talk on the files `MusicDetector` gets wrong?**

The premise is that the acoustic detector fails for a structural reason (see
[`doc/KNOWN-LIMITATIONS.md`](../../doc/KNOWN-LIMITATIONS.md)): electronic music and speech are not
separable in the spectral/modulation domain, so a news bulletin and an Ace of Base single both score
3% music. Speech recognition asks a different question — *is anyone speaking, and what are they
saying* — and Whisper's own confidence output may answer it without reading the transcript at all.

## Result: yes, decisively

Test set: **36 files the acoustic detector classified as TALK** — 21 confirmed songs and 15 confirmed
idents/ads/news, all drawn from `harvest/_rejected/`. The detector's own failures, in other words,
which is the only test that matters.

Whisper `base`, 3 × 10-second sampled windows per file, mean `NoSpeechProbability`:

| | min | median | max |
| --- | --- | --- | --- |
| **music** (n=21) | 0.137 | **0.688** | 0.923 |
| **talk** (n=15) | 0.033 | **0.128** | 0.339 |

A single threshold gets **91.7%**, and — the direction that matters — **admits no talk at all**:

```
strictest-safe cut (zero talk admitted): 0.339
  songs lost : 3 of 21 (14%)
  accuracy   : 91.7%
```

The cut is not knife-edge despite the ranges touching: the music values jump from 0.290 to 0.478, so
anywhere in **0.34–0.44** gives the identical result. Only past ~0.49 does it start costing extra
songs. Baseline for comparison: the acoustic music-fraction gate separates these two classes not at
all.

## Why the three lost songs are lost, and the second signal it reveals

All three sit in the overlap band, and their transcripts show textbook Whisper-on-music behaviour:

```
Aqua Bassino - Ola      [km] "I think it's a good idea to be a little bit more creative"  ·  "1/2 tsp of salt."  ·  (upbeat music)
Valerii M - Фанат       [ja] (♪ 恋を手に)  ·  [uk] [музика]  ·  garbled lyrics
MALCOLM TODD - Earrings [ko] [구독]  ·  ♪ The ones who say the same ♪
```

Three giveaways, all free: **explicit non-speech annotations** (`♪`, `(upbeat music)`, `[музика]`,
`*Musik*`), **hallucinated filler** ("1/2 tsp of salt", `[구독]` = "subscribe"), and **language
detection landing nowhere near** what these stations broadcast (Khmer, Japanese, Korean).

Counting those markers is a usable second signal on its own (86% alone). But **do not simply OR the
two rules** — that reaches 94% while letting 2 talk files through, which is the wrong trade: the
stated preference is to lose a song rather than play an ad. The right shape is `NoSpeechProbability`
as the gate, with markers (or an LLM on the transcript) used *only* to rescue files inside the
overlap band, where they cost nothing in precision.

## Cost

~5.4–6.1 s per file on CPU for 3 × 10 s of audio — about **5× realtime**, with the 148 MB `base`
model. DJ mode produces roughly one segment per minute across four harvesters, so this is a few
seconds of CPU per minute. Sampling windows rather than whole segments is what makes that true, and
it's why the tool samples by default: if it only worked on whole files it wouldn't be viable during a
live harvest.

Worth testing `tiny` (~75 MB) before shipping anything — the metric is the model's own speech/no-speech
token, and this task needs far less than transcription accuracy.

## Usage

```sh
dotnet run --project tools/DjTranscribe -- <model.bin> <files-or-folders...> [options]
```

| Arg | Meaning |
|---|---|
| *(first positional)* | path to a ggml Whisper model |
| *(rest)* | files and/or folders (recursed over `.mp3 .aac .m4a`) |
| `--label music\|talk` | force a label; otherwise inferred from the filename |
| `--windows N` | windows per file (default 3) |
| `--window-seconds N` | seconds per window (default 10) |
| `--language xx` | force a language; default is per-window auto-detect |
| `--csv <path>` | per-window rows, including the transcript |
| `--transcripts` | print what was actually heard |

Models come from the whisper.cpp releases, e.g.

```sh
curl -L -o ggml-base.bin \
  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin
```

Labels are inferred from the harvest naming convention `station · artist - title`: a station
announcing *itself* as the artist is an ident or a bulletin — the same signal `SongHistoryFilter`
uses. Anything else is assumed music, so point `--label` at a folder if that isn't true.

## Notes and gotchas

- **`SegmentData.Probability` is always 0** on this code path in Whisper.net 1.9.1. The CSV column is
  kept so the next person doesn't repeat the experiment; use `NoSpeechProbability`.
- **Zero segments returned is the strongest "not speech" signal**, so it's recorded as
  `noSpeech = 1.0` rather than skipped.
- Resampling to Whisper's 16 kHz is **linear interpolation** — crude for audio, fine here, since
  Whisper's front end is a log-mel spectrogram and the question is whether anyone is talking.
- Windows skip the first and last 10% of a segment: that's where a boundary cut leaves the
  neighbouring track's bleed, which would label the wrong thing.
- Language auto-detection runs per window, which is what makes the implausible-language signal
  available. Forcing `--language de` removes it.
