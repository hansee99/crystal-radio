# Detector corpus

Feature CSVs for re-fitting `MusicDetector`'s weights with `../fit_logreg.py`. **These files are
the corpus** — the audio behind them is not in the repo and much of it no longer exists, so treat
them as the durable record.

That is a lesson learned the hard way: the negatives for the original fit were never committed, so
for a while the shipping weights could not be reproduced and a re-fit had nothing to learn
"not music" from. Any new labelled audio should have its features generated and committed here.

## What's here

| file | files | label | what it is |
| --- | --- | --- | --- |
| `features-clips.csv` | 27 + 42 | music / nonmusic | The original hand-assembled corpus. Music: mainstream pop/rock/disco. Non-music: radio commercials, PSAs, jingle montages, and real UK DJ links — the last being **talk over a music bed**, the hardest negative there is. |
| `features-harvest-songs.csv` | 121 | music | Whole songs harvested from real sessions across ~15 stations, played without complaint. Deduped to one per song. |
| `features-electronic.csv` | 21 | music | Confirmed-good electronic tracks the previous fit called TALK. The genre gap, as data. |
| `features-o3-idents.csv` | 15 | nonmusic | ORF Ö3 station idents and news/weather/traffic bulletins from real sessions. |

`corrections.csv` / `corrections-template.csv` are ground-truthed **span** files for
`--corrections`, not features; they stay useful independently.

## Re-fitting

```sh
dotnet run --project tools/DjDetector -- <folder-or-files> --label music|nonmusic --csv new.csv
python tools/DjDetector/fit_logreg.py tools/DjDetector/corpus/features-*.csv new.csv
```

Paste the printed constants into `Services/MusicDetector.cs` and update the provenance comment
above them with the honest CV numbers.

## Gotchas

- **`--label` writes the string verbatim.** `fit_logreg.py` only accepts `music` and `nonmusic`, so
  `--label talk` produces rows it silently skips. Use `nonmusic`.
- **The CSV has 15 columns since `pulseStrength` was added.** Anything with 14 predates it and
  cannot be merged into a fit — regenerate from audio instead. The two pre-pulse files that used to
  live here were deleted for exactly that reason.
- **`fit_logreg.py` parses right-anchored** because filenames contain commas and the CSV is
  unquoted. Don't quote the writer without changing the parser, and don't use a naive `split(',')`
  in ad-hoc analysis.
- **More non-music diversity is still the highest-value addition.** The corpus is ~6:1
  music:non-music by window count, and the non-music side is thin on anything that isn't an
  ad, a jingle or a DJ link.
