# Known limitations and standing decisions

Things that are **understood but not fixed**, and decisions about what deliberately isn't being
built. This is not a task list — discrete work lives in
[GitHub issues](https://github.com/hansee99/crystal-radio/issues). What's here is the reasoning and
the evidence behind it, kept in the repo because it cites specific code and has to stay true to the
commit it ships with.

Add an entry when a problem is diagnosed but not solved, or when a plausible feature is
deliberately declined. Delete one when it stops being true.

---

## The music detector: fixed for the observed cases, still thin on negatives

**The original problem, for the record.** Five files from one real session (2026-08-03) showed the
detector could not tell speech from music at all: Ö3's five-minute `Nachrichten, Wetter und Verkehr`
scored **3% music**, and Ace of Base's `The Sign` scored **3%** too. Every real song was classified
TALK. No threshold on the old feature set separated those classes, because every feature described
how energy is *distributed* and none described whether it *repeats*.

**What fixed it** (issue #8): `PulseStrength` — the peak-to-mean of the envelope spectrum over
0.5–8 Hz (30–480 BPM), computed over an 8-second context because a 1-second window holds about two
beats and periodicity can't be established from two cycles. Plus a re-fit on a corpus enlarged from
106 to 226 files. Per-FILE verdicts, before → after:

| subset | before | after |
| --- | --- | --- |
| original clips, music (27) | 92.6% | 96.3% |
| original clips, non-music (42) | 92.9% | 97.6% |
| 121 real harvested songs | 59.5% | **95.0%** |
| 21 confirmed electronic tracks | 9.5% | **81.0%** |
| 15 Ö3 idents / news bulletins | 100% | 100% |

Nothing was traded away — talk rejection improved alongside music recognition. Music fraction is a
usable signal again: songs now sit at a median 0.895 where they used to sit near 0.03.

**Also fixed: a single window could defeat the whole edge trim.** The scan stopped at the first
window over the threshold, so one 0.528 window at the end of a segment left a 70-second talk outro
completely untrimmed — and the same thing at the front is why those Ö3 bulletins reported lead trims
of 0.5s and kept five minutes of speech. It now needs two consecutive music windows. Measured: talk
segments trimmed away entirely went from 5 to 10 of 15, median trim 23.5s → 35.5s, with the median
song trim still 0.0s.

### What is still not solved

**Generalisation to unseen genres is real but partial.** Refitting with the electronic set held out
*entirely* still moves it from 10% to 52% — so the gain is the feature, not memorisation — but a
genre absent from the corpus is recognised far worse than one present in it. Expect new genres to
need adding.

**The negative class is thin.** 226 files, but ~6:1 music:non-music by window count, and the
non-music side is almost all ads, jingles, DJ links and Ö3 idents. Real-world talk that isn't one of
those is unrepresented.

**`DjMusicFractionFloor` is still 0 (off).** A floor is now viable — at 0.20 it would catch 12 of 15
idents for 1.4% of songs — but those idents are *already* caught for free by `SongHistoryFilter`'s
metadata check, so the floor's benefit is unmeasured while its cost is measured. Turn it on when
there's a case it actually catches.

**An ad break under a plausible artist/title** remains the open hole. Nothing has demonstrated one
occurring: a sweep of 121 played songs found no ad or bulletin among them (see issue #27).

**Speech recognition was tried and rejected** for this job — issue #27 has the measurements. Short
version: Whisper's `NoSpeechProbability` costs 26% of real songs at the zero-talk-admitted operating
point, because radio segments are mixtures. Songs carry speech-like vocals (rap, dialect, shouted
punk) and idents carry music beds.

**Corpus gotcha:** `fit_logreg.py` parses the feature CSV **right-anchored**, because filenames
contain commas and the CSV is unquoted. Don't quote the writer without changing the parser, and
don't parse those CSVs with a naive `split(',')` in ad-hoc analysis — doing exactly that during the
investigation shifted every column and produced impossible values (a ratio reading 1.4e7) that
looked like a numeric blow-up in the detector itself. See `tools/DjDetector/corpus/README.md`.

---

## Long DJ sets and extended mixes — deliberately not handled yet

Electronic stations sometimes play extended mixes rather than 3-minute songs. A station announcing
one ICY title for a 60-minute set yields a single 60-minute "song": it dominates the mix, can't be
crossfaded sensibly, eats a large slice of the harvest cache on its own, and makes "Up next"
meaningless. The QC gate has a minimum length (`DjMinSongSeconds`) but no maximum.

**The measurements say this isn't happening.** 56 segments across four sessions: median around
4:30, with a tail of 7:12 / 7:24 / 7:37 / 8:02 / 8:25 / 8:36 / 10:04. Long, but nothing remotely
like an hour. So this stays unbuilt on purpose — check the session logs for a genuinely huge
segment before writing any of it.

**And if it does turn up, the obvious fix is the wrong one.** Fixed-time chunking reintroduces
exactly the abrupt start/end problem the whole deferred-boundary-cut design exists to avoid: every
chunk would begin and end mid-phrase. Better options, in order of appeal:

1. **Cap and reject** — treat anything over N minutes as "not a song" and let the live bridge cover
   the gap. Simplest; no arbitrary cuts, no new failure modes.
2. **Silence-aligned cutting** — chop at a detected low-energy point near the target length.
   `MusicDetector` already computes a per-frame energy envelope, so the signal is there.
3. **Treat it as a "set"** — play it as-is, exclude it from crossfade, mark it in the UI as a set
   rather than a track.

Whichever route: `DjQueueService` dedupes on `artist|title`, so chunks of one mix would all share a
key and everything after the first would be silently discarded. Chunking needs a distinct key per
chunk, and probably a "part N" suffix in the displayed title.

*Related, and now fixed:* metadata bounce used to cut one track into fragments — the opposite
problem. Some stations re-announce the track they're already playing, and every announcement was
treated as a boundary. `StreamRecorder.OnTrackChanged` now ignores an announcement whose title and
artist match the track in hand.

---

## Testing: what is deliberately not covered

**The policy.** Every bug this project has actually shipped lived in parsing/derivation logic or a
state machine — the `Append`-after-exhaustion stall, the AAC+ regex leaving `"Liquid DnB - +"`,
lead/tail trim double-counting, an implicit WPF style not matching a subclass, "12 songs vs 7
segments", `Enum.TryParse` accepting `"3"`. Wiring and I/O have not been the problem. Remaining
coverage work is therefore ordered by that evidence, not by line count or percentage.

**`MainViewModel` is explicitly parked.** ~2,500 lines and 17 concrete dependencies including two
audio engines — it cannot be instantiated in a test at all. Testing it means refactoring it, and
that is a separate decision that shouldn't be smuggled in under a testing task. Consequence worth
being honest about: bugs in mode-switching and DJ session lifecycle are currently caught by manual
use, not by tests. Two real ones already were.

**What is covered** (241 tests): the pure derivation logic (`StationNameFormatter`,
`TrackTitleCleaner`, `SongHistoryFilter`, `MusicDetector` trim, `SegmentQualityChecker`,
`StagedProgress`, `DjSessionLog`, harvester retirement, duplicate-stream detection), the WPF chrome
regressions (`AppDialog`, `StatePanel`), `DjQueueService`, `StreamRecorder`'s boundary rules, mirror
failover, and every LLM boundary — `AnthropicApi`, `PromptInterpreter`, `LlmSearchRanker`,
`DjIntroService`, and `AgenticSearchService`'s stationuuid validation gate.

`tests/RadioPlayer.Tests/Fakes/FakeHttpMessageHandler.cs` scripts Messages API replies, so any
further LLM-backed service can be driven end to end without a network. It has a `Latency` knob —
use it for anything concurrency-related, because without it the fake completes inline and
"parallel" callers actually run one after another, which silently makes a concurrency test prove
nothing.
