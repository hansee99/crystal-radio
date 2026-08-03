# Design brief — Options dialog

**Ask:** redesign Crystal Radio's Options dialog as a left-rail sectioned dialog at a fixed size.

Companion docs: [`design-tokens.md`](design-tokens.md) (colour/type/component tokens),
[`product-brief.md`](product-brief.md) (what the app is). Source of truth for everything visual is
`Views/Theme.xaml`.

---

## The problem

The dialog is 480px wide with every field in one vertical stack: 4 section headers, 6 fields, and
8 explanatory hint paragraphs, at `SizeToContent="Height"`. It comes out around 800px tall — a
long thin column that reads as unfinished next to the rest of the app, and on a laptop it can
approach the height of the screen.

The hint copy is good and should stay. It's just all stacked in one column.

The dialog is also about to grow: issue #22 ("Options UI for the DJ config knobs") will add more
DJ settings. The new structure needs to absorb those **without getting taller**.

## Direction (already decided)

A **left rail** listing the four sections; the selected section's fields fill the right pane. One
section visible at a time. Fixed width and height — no scrolling in either pane at the default
content. Roughly 620 × 420 is the starting guess, not a requirement; propose what the content
actually wants.

Rationale: the app's three modes already use a pill-tab row, so sectioned navigation is existing
vocabulary rather than a new idiom, and a rail absorbs new fields by filling a pane instead of
extending a column.

---

## Content inventory

Four sections, six fields. Copy below is the current shipping text — treat it as editable if the
layout wants shorter, but it was written deliberately (fields are labelled by their **effect**,
not their setting name: "Stop … seconds before the end", never "OutroGuardSeconds").

### 1. AI-assisted search

Section hint: *"Leave blank to turn it off — the player works fine without it."*

| Field | Control | Hint |
|---|---|---|
| Anthropic API key | single-line text | *"Your key is stored securely on this PC. Get one at console.anthropic.com."* |

Conditional state: when no key is saved in-app **but** one is present in the environment, an extra
line appears under the box — *"A key from your system is currently in use."* Needs a resting place
in the design; it is informational, not a warning.

### 2. Saved songs

| Field | Control | Hint |
|---|---|---|
| Library folder | text + **Browse…** button (opens a folder picker) | *"Songs you save land here, named \"Artist - Title\". For personal use only — respect the stations' terms."* |

Paths are long and will overflow — the text box needs to handle a truncated `C:\Users\…\Music\Crystal Radio` gracefully.

### 3. DJ mode

| Field | Control | Hint |
|---|---|---|
| DJ voice | dropdown, 5 values: Warm · Upbeat · LateNight · Wry · Professional | *"How the DJ introduces tracks and fills the gaps between them."* |
| Stations to listen to at once | number | *"More stations means more variety, but more bandwidth and more AI cost — every song collected gets a description written for it."* **+ "Takes effect the next time you start a DJ session."** |
| Disk space for DJ mode (MB) | number | *"Collected songs are kept only while the mix might use them, then the oldest are deleted. Songs you save to your library are never touched."* |

This is the section that grows with #22 — assume it eventually holds 5–6 fields, not 3.

Note "LateNight" is the raw enum name and reads badly in a dropdown. Renaming the display labels is
in scope if you want ("Late night").

### 4. Track edges

Section hint: *"Songs recorded off live radio don't always start and end exactly on the beat. These
hide an imperfect cut by giving up a moment of the track. 0 turns them off."*

| Field | Control |
|---|---|
| Skip the first … seconds of each track | number (accepts decimals, and both `1.5` and `1,5`) |
| Stop … seconds before the end | number (accepts decimals) |

These two read as a pair and share one explanation.

### Footer

**Cancel** (ghost) and **Save** (primary), bottom right. Save is the default button; Cancel is bound
to Escape.

---

## Behaviour the design has to express

Settings now apply the moment you press Save — the old blanket *"Changes apply next time you start
Crystal Radio"* banner has been deleted. **One exception:** "Stations to listen to at once" is read
when a DJ session starts, so changing it can't re-deal the harvesters of a mix already playing.

That exception currently lives as a sentence appended to that field's hint. If a per-field marker
(a small "next session" chip, a muted suffix, something else) reads better, propose it — it needs
to be quiet enough that one field carrying it doesn't imply the others are broken.

Nothing else needs a state: there's no dirty indicator, no per-field validation, and unparseable
input silently keeps the field's previous value rather than erroring.

---

## Constraints

**Implementation is WPF**, styled from `Views/Theme.xaml`. Practical consequences:

- No CSS-only tricks — everything has to map to WPF controls and templates. Straightforward
  flex/grid layout, borders, radii, gradients and drop shadows are all fine.
- The **window chrome already exists and should not be redesigned**: all dialogs derive from
  `AppDialog`, which supplies a borderless rounded surface (`#06110F`, 12px radius), a hairline
  border, a drop shadow, a drag strip with the app's own close button, and a scrim over the main
  window while open. Design the *contents*; assume the frame and title row.
- The dialog is modal and centred on the app window.

## Components to reuse

All in `Views/Theme.xaml`. Prefer these over new ones; say so explicitly if the design needs a new
component, so it can be added to the theme rather than one-offed.

| Style | Current spec |
|---|---|
| `DialogSectionHeader` | 14px SemiBold, `TextPrimary` `#F3F9F7` |
| `DialogFieldLabel` | 12px, `TextSecondary` `#BCCCC8` |
| `DialogHint` | 11.5px wrapping, `TextMuted` `#7D938E` |
| `DialogTextBox`, `DialogComboBox` | fully templated dark inputs |
| `DialogPrimaryButton`, `DialogGhostButton` | footer buttons |
| `PillTab` | 12.5px SemiBold — the main window's mode tabs, the nearest existing precedent for the rail |
| `AboutPillButton` | teal-tinted pill treatment (`TealTint10` `#1A56C7BA` / `TealTint16` `#2956C7BA`) |

**Palette:** near-black with a green cast, not neutral grey. Surfaces `#050D0C` / `#06110F`;
hairlines white at 8–12% alpha; accent teal `#56C7BA`. Font is Hanken Grotesk throughout.

## Out of scope

- The window chrome, scrim and close button (already built).
- Any setting not in the six above — the rest stay file-editable in `settings.json` on purpose.
  Exposing a tuning parameter invites fiddling with something whose effect can't be heard.
- Search/filter over settings, presets, import/export, a reset-to-defaults action.

## Deliverable

A mockup of all four sections (so the rail's selected/unselected states and the tallest section are
both visible), plus the "a key from your system is currently in use" state. Redlines or a written
spec of spacing, sizes and any new component are more useful than pixels alone — implementation
reads the spec, not the image.
