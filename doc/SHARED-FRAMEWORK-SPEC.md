# Crystal Radio — Shared Framework Spec (Radio + Library)

**Purpose:** Unify the Radio and Library modes under one structural framework so they read as one app, without changing the existing visual language (teal accent, dark ground, generated waveform artwork). Companion visual: `Shared Framework Redlines.dc.html` (numbered pins map to the Change Log here). This spec is written for the Claude Code / WPF developer.

Design tokens are unchanged from `XAML-IMPLEMENTATION-SPEC.md`; only structure/behavior changes below.

---

## 1. The framework in one sentence

Every screen = **Left panel** (mode toggle → 3-tab sub-nav → scrollable list → footer toolbar) + **Right panel** (a fixed set of slots A–H where only one region, F, swaps per mode).

Switching Radio ⇄ Library must never rearrange the skeleton — only the contents of the list and slot F change.

---

## 2. Left panel — identical skeleton in both modes

| Slot | Radio | Library |
|---|---|---|
| Mode toggle | Radio · Library | Radio · Library |
| Sub-nav (3 tabs) | **Stations · Search · History** | **Playlists · Curate · Songs** |
| List | station / search-result / history rows | playlist tracks / curate results / all songs |
| Footer toolbar | `+ Add station` · count | `+ Save as playlist` · count |

**Sub-nav pattern (pin 1):** replace the current Library "single search box, no tabs" with the same 3-tab control Radio uses. The parallel is intentional:
- Tab 1 = your saved collection (Stations / Playlists)
- Tab 2 = a query surface (Search stations / Curate a playlist)
- Tab 3 = the flat backing list (History / all Songs)

Use the underlined-tab style shown in the redline (not a second segmented pill), so it's visually distinct from the mode toggle above it.

**List rows (pin 5):** description text clamps to **two lines** (`TextTrimming` won't do multi-line — use a `TextBlock` with `MaxLines`-equivalent behavior: `TextWrapping="Wrap"` + height cap + `TextTrimming="CharacterEllipsis"`), instead of the current one-line hard cut. Row height grows to accommodate; keep the leading icon top-aligned.

---

## 3. Library "Curate" tab (pin 4)

The current Library search box uses a `+` prefix and produces "Playing 3 songs" — the query reads as "add" and the query/result relationship is unclear. Replace with:

- **Curate tab** holds a search-styled field (magnifier icon, placeholder: *"Describe a mood or vibe — e.g. 'cumbia from Mexico'"*).
- On submit, the app curates a playlist from the **local recordings** and shows a **context card** at the top of the list: sparkle icon, `CURATED PLAYLIST` eyebrow, the query as a title (title-cased), and `N songs · from your recordings`.
- The resulting tracks list below the card. `+ Save as playlist` in the footer persists it into the **Playlists** tab.

This makes "the thing I asked for" (query) visually distinct from "the songs it produced" (queue).

---

## 4. Right panel — shared anatomy (A–H)

Same order and position in both modes:

- **A. Context eyebrow** — `NOW PLAYING` (+ `LIVE` badge in Radio).
- **B. Title** — track title (or station name when no track metadata).
- **C. Secondary** — artist (or station genre).
- **D. Source chip** — station name + bitrate (Radio) / playlist name (Library).
- **E. Actions** — `About this track` pill (always, when a track is known) + `Save to Library` pill (Radio only).
- **F. Progress region — THE ONLY PER-MODE DIFFERENCE:**
  - **Library:** scrubber with elapsed / total (`0:04 … 4:10`), draggable.
  - **Radio:** `LIVE` state — no scrubber (streams aren't seekable). Space is used for a **"Recently on this station"** list (pin 2) instead.
- **G. Transport** — prev / play-pause / next / stop. Identical cluster, identical position, both modes.
- **H. Volume** — identical.

### 4a. Radio right panel earns its space (pin 2)

Today Radio shows a large empty "Not playing." Instead:

- **Playing, track metadata available:** slots A–E as above, then **"Recently on this station"** (last ~3–6 tracks with artist, relative time, and a per-row Save control).
- **Playing, no track metadata (stream sends none):** B = station name, C = genre/description, D = bitrate; still show Recently-on-station if history exists.
- **Selected but not started:** show the selected station's name/genre/description as a preview + its recent tracks — never a bare "Not playing." Only the truly-empty first-run state (no station ever selected) shows a minimal empty message.

---

## 5. Actions & icon clarity (pin 3)

Every icon-only control gets a tooltip. Specific controls:

| Control | Icon | States / behavior |
|---|---|---|
| **Save to Library** (now-playing pill + per-row in History/Recent) | download arrow | Records the currently-streaming song to the local library. Once saved, the control becomes a **check** (non-interactive, tooltip "Saved to Library"). This is the download→check pair from the current History screen, now labeled. |
| **About this track** (pill + sparkle in lists) | sparkle | Opens the About replace-view (§6). Available anywhere a track is known — Radio now-playing, Library now-playing, History rows, Recent rows. |
| Add station / Save as playlist | plus / list | Footer toolbar, labeled. |
| Edit / Delete station | pencil / trash | Keep, but move off each row into a row hover-menu or the footer to reduce per-row icon clutter (current Stations rows carry pencil+trash on every row). Tooltip both. |

---

## 6. "About this track" — replace-view (unchanged, now in both modes)

The existing About design is reused verbatim: it replaces slots A–F of the right panel with a back button, the track context, and the three AI sections (The Song / The Artist / Notable), with idle → loading (spinner + shimmer) → result states, a Regenerate link, and the "AI-generated · may be imperfect" disclaimer. Transport (G) + volume (H) stay visible beneath it.

Now reachable from **both** modes and from History/Recent rows (the sparkle), not Radio-only. See `XAML-IMPLEMENTATION-SPEC.md` for the full state/loading spec and copy.

Two refinements to the currently-shipped About view (redline frame 3, pins 7–8):

- **Consistent icons (pin 7):** the header and the **Regenerate** control currently use a generic `+` glyph — switch both to the **sparkle** icon, the same mark that triggers About elsewhere, so the feature reads as one thing.
- **Refined scroll (pin 8):** replace the chunky native scrollbar with a **slim custom thumb** (≈4px, rounded, low-opacity track) and add a **bottom fade** (linear gradient to the ground color over the last ~38px of the scroll region) so the text dissolves at the fold instead of clipping mid-line. In WPF, restyle `ScrollViewer`/`ScrollBar` templates and overlay the fade as a non-hit-testable element pinned to the bottom of the scroll region.

Transport (G) + volume (H) remain visible beneath the About view in both modes (Radio keeps LIVE, no scrubber).

---

## 7. Artwork

Confirmed: no album art. Continue using the generated waveform background in the right panel for every track/station. No per-track art fetch needed.

---

## 8. Build order (suggested)

1. Extract the left-panel skeleton into one shared control; drive tab set + list source by mode.
2. Add the Library 3-tab sub-nav and the Curate context card.
3. Extract the right-panel into the A–H slot layout; make F a mode-switched region.
4. Wire Radio "Recently on this station" + per-row Save (download→check).
5. Apply two-line description clamp to all list rows.
6. Add tooltips to every icon-only control; move Stations row edit/delete into a hover menu.
7. Verify About replace-view triggers from all four entry points.
