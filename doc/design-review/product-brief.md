# Crystal Radio — product brief (for design review)

## What it is

A Windows 11 desktop internet-radio player (WPF, dark theme, single fixed-layout window).
It plays live Icecast/Shoutcast streams and layers AI features on top: natural-language
station discovery, a personal library of saved songs with AI-curated playlists, and a
"DJ mode" that continuously harvests songs matching a described vibe and plays them back
as an uninterrupted, crossfaded stream — no ads, no talk.

## Who it's for

A single music-loving power user (it's a personal project, not a mass-market product) who
listens for hours while working. They value: music that keeps flowing without interaction,
discovering stations/songs beyond what they'd find by browsing, and a calm UI that doesn't
demand attention. They are technical, but the app should not *feel* technical.

## The three modes (left panel switches; right side is always Now Playing + transport)

1. **Radio** — classic live-stream listening. A station list, a live playback view with
   track metadata, a song history with "save this song," and an AI search box: type a vibe
   or a literal request ("BBC Radio 1", "mellow jazz for late-night coding") and get real,
   playable stations back. Songs can be saved to the library as they play.
2. **Library** — the saved songs. A full song list, plus a "Curate" tab: type a prompt and
   an AI arranges a playlist from your own library, with a one-line reason per pick.
   Local playback with a seek bar and crossfade.
3. **DJ** — the newest and most ambitious mode. Type a vibe; the app finds matching
   stations, silently records clean songs from several of them at once, and plays a
   continuously self-refilling queue. While warming up it plays the best station live.
   Shows which stations are being harvested, a session play history, and a short
   DJ-style "why this song" intro line for the current track in Now Playing.

There is also an on-demand "About this track" panel (AI briefing about the playing song)
that swaps into the Now Playing area, and an Options dialog (API key, cache sizes,
library folder).

## OS integration

Playback is surfaced in the Windows taskbar thumbnail (play/pause) and the Win11 media
flyout / lock screen / hardware media keys. Not part of this review, but it explains why
the in-window transport can stay minimal.

## Tone & design intent

- **Voice:** professional but slightly quirky — status lines like "Warming up the decks —
  scouting stations for your vibe…" or "Making sure they actually play…". Friendly, never
  corny, never technical jargon in user-facing copy.
- **Look:** calm dark UI, one teal accent, generous whitespace, few borders. The right
  side (Now Playing) is deliberately spacious and typographic; the left panel is denser
  and functional. Long-session ambience over dashboard density.
- **AI is a helper, not a theme:** the AI features should feel like natural parts of the
  player (a search box, a curate button, a DJ prompt), not like a chatbot bolted on.
  Loading/thinking states matter — several AI operations take seconds to a minute.

## Known rough edges (honest starting points for the review)

- The three modes were built at different times; spacing, sub-tab patterns, and empty
  states have drifted apart.
- DJ mode has many states (idle → sourcing stations → live warm-up → steady playback →
  exhausted/error) expressed mostly through one status line — it may carry too much load.
- Several AI operations (web-escalated search, DJ session start) can take 30+ seconds;
  the current feedback is a spinner plus rotating status text.
- Empty states (fresh install: no stations, empty library, cold DJ start) got less design
  attention than the happy paths.

## Constraints for recommendations

- WPF/XAML — no web components; effects like blur/shadows are possible but used sparingly.
- Keep the overall skeleton: left mode panel + right Now Playing, single window.
- Incremental, implementable changes preferred over a redesign; a shared XAML theme file
  holds all colors/styles, so token-level changes propagate cheaply.
