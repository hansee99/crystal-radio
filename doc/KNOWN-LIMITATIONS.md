# Known limitations and standing decisions

Things that are **understood but not fixed**, and decisions about what deliberately isn't being
built. This is not a task list — discrete work lives in
[GitHub issues](https://github.com/hansee99/crystal-radio/issues). What's here is the reasoning and
the evidence behind it, kept in the repo because it cites specific code and has to stay true to the
commit it ships with.

Add an entry when a problem is diagnosed but not solved, or when a plausible feature is
deliberately declined. Delete one when it stops being true.

---

## The music detector cannot tell speech from music

**Measured, not suspected.** Five files from one real session (2026-08-03, Austrian pop stations)
through `tools/DjDetector`:

| file | content | verdict | music % | longest music run |
| --- | --- | --- | --- | --- |
| `HITRADIO Ö3 - Nachrichten, Wetter und Verkehr` | 5 min of pure speech | TALK | **3%** | 0.0s |
| `Ace of Base - The Sign` | a real song | TALK | **3%** | 1.5s |
| `HITRADIO Ö3 - Livestream` | talk + ads | TALK | 7% | 2.0s |
| `Simon Lewis - Break Your Wall` | a real song | TALK | 18% | 8.5s |
| `Katy Perry - I Kissed a Girl` | a real song | TALK | 33% | 5.0s |

A news bulletin and an Ace of Base single score **identically**, and every real song is classified
TALK. This is not a threshold that needs nudging: no cut on music fraction, on longest contiguous
run, or on any monotone function of the current confidences separates these classes — and
re-fitting the existing weights cannot either, because they are not linearly separable in the
present feature space. Consistent with the earlier deep-house finding (confirmed-good tracks at
0.00, identical to a confirmed ad break).

**Why it hasn't hurt more.** The QC gate keys on post-trim duration, not music fraction, and
`DjMusicFractionFloor` is 0 (off). The trim is a *local* run-length measure and works acceptably;
the *global* fraction is the useless part. The two failures on 2026-08-03 were caught at the
metadata layer instead — `SongHistoryFilter` spotted that the station had labelled them as itself.
That only works when the station is honest about the label; **an ad break under a plausible
artist/title still gets through**, and nothing downstream will catch it.

**The missing feature is pulse/beat strength.** Music has a strong periodic beat, speech doesn't,
and it is nearly free to compute: `MusicDetector.Modulation4Hz` already runs an FFT over the
amplitude envelope, so the peak-bin-to-mean ratio over roughly 0.5–8 Hz comes from data already in
hand. That gives the classifier the axis it currently lacks. Tracked as an issue; the plan lives
there.

**Gotcha for anyone touching the corpus:** `fit_logreg.py` parses the feature CSV **right-anchored**
on purpose, because filenames contain commas and the CSV is unquoted. Don't "fix" that by quoting
the writer without changing the parser — and don't parse those CSVs with a naive `split(',')` in
ad-hoc analysis either. Doing exactly that during the 2026-08-03 investigation shifted every column
and produced impossible values (a ratio reading 1.4e7) that looked like a numeric blow-up in the
detector itself.

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
