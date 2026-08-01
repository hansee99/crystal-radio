# Crystal Radio — design-token & component summary (for design review)

Source of truth: `Views/Theme.xaml` (all colors/styles live there; token-level changes
propagate app-wide). Colors are WPF hex: `#AARRGGBB` (first byte = alpha).

## Typography

- **Font:** Hanken Grotesk (bundled), used everywhere.
- **Observed type scale** (from the main window):
  | Role | Size | Weight |
  |---|---|---|
  | Now Playing title | 44 | Normal (line-height 48) |
  | Now Playing artist | 20 | Light |
  | Section eyebrow ("NOW PLAYING", "RECENTLY ON THIS STATION") | 11 | SemiBold, letter-spaced feel via caps |
  | List row title | 13.5 | Medium |
  | List row subtitle / status lines | 11.5–12 | Regular |
  | Tabs (pill + underline) | 12.5 | SemiBold |
  | Buttons/pills ("About this track") | 13 | SemiBold |
  | DJ intro line | 12.5 | Italic |

## Color palette

**Surfaces** (near-black with a green cast, not neutral gray):
- Window background `#050D0C`; panel background `#06110F` (used at ~50% alpha `#7F040B0A`)
- Hairlines: white at 8–12% alpha (`#14FFFFFF`, `#0FFFFFFF`)
- Subtle fills: white at 4–13% alpha (`#0BFFFFFF` fill, `#21FFFFFF` slider track)

**Text tiers** (5 steps, all slightly green-tinted):
- Primary `#F3F9F7` · Body `#E7F0EE` · Secondary `#BCCCC8` · Muted `#7D938E` · Faint `#8A9F9A`
- `NonText` `#5F736F` — the old Faint value, retained for hairlines and decorative glyphs only.
  Faint was lifted to `#8A9F9A` (~7.1:1) because it carries 11.5–12px text, which failed AA at
  the old value.

**Teal accent ramp** (the only accent color):
- Base `#56C7BA` · Bright `#62D2C4` · TealText `#CDEEE7` (labels on teal-tinted fills)
- Tints for fills: teal at 10% (`#1A…`) and 16% (`#29…`) alpha; borders teal at ~28–50% alpha

**Gradients:**
- Play button: `#6FDCCD → #34988D` diagonal, with a teal glow (DropShadow blur 28)
- Volume fill: `#34988D → #62D2C4` horizontal
- Brand badge: `#62D2C4 → #2F8F86` diagonal

**One deliberate exception:** window-close button hovers Windows-red `#E81123`.

## Shape & spacing conventions

- **Corner radii:** 7–8 (small buttons/tabs), 10 (list rows, pills, chips, cards), full-round
  (transport buttons, thumbs). No sharp corners anywhere.
- **Borders:** 1 px hairlines at low-alpha white; selected list rows get a 2 px teal left bar
  instead of a full border.
- **Iconography:** all icons are 24×24 vector path geometries, stroke style (1.6–1.7 px,
  round caps), rendered at 13–20 px. Fill style only for transport glyphs (play/pause/stop/
  next/prev) and the sparkle. No emoji, no bitmap icons.
- **Layout:** left panel (mode content, denser) + right Now Playing (spacious, typographic).
  Left panel swaps whole-panel per mode; right side is persistent (transport + volume never
  move).

## Component inventory (recurring patterns)

| Component | Style key | Used for |
|---|---|---|
| Segmented pill tabs | `PillTab` | Top-level mode switch (Radio/Library/DJ) — teal-tinted fill + border when selected |
| Underline tabs | `UnderlineTab` | Second-level nav inside a mode (Stations/Search/History, Songs/Curate, Sources/History) — thin teal underline when selected; 32 px click band |
| List row | `StationRowItem` | Every list: radius-10 row, icon tile (34×34, radius 9, hairline border) + title/subtitle stack; hover = faint fill; selected = teal tint + 2 px teal left bar |
| Row typography | `RowTitleText`, `RowSubtitleCompact`, `RowSubtitleDetailed` | The only two row shapes. Compact = single trimmed subtitle line (history, library songs, DJ sources/history). Detailed = subtitle wrapped, capped at two lines (stations, search results, curated queue). Lists must use these rather than hand-rolled sizes |
| Panel states | `StatePanel` + `PanelMessage` / `PanelErrorMessage` / `PanelProgress` | Every list panel renders exactly one of Content / Empty / Loading / Error, driven by a `PanelState` from the view model |
| Staged progress | `PanelProgress`, `ProgressStageRow`, `SkeletonRows` | The 30 s+ AI waits: named steps that tick off (ring → spinner → teal check), three shimmering skeleton rows, and a ghost Cancel |
| Ghost square button | `GhostSquareButton` | Per-row actions (save, about, edit, delete) — 32×32, hairline border, teal on hover |
| Round ghost button | `RoundGhostButton` | Prev/next/stop — 46×46 circle, hairline border |
| Primary play button | `PlayButton` | 62×62 teal-gradient circle with glow — the single strongest visual element |
| Teal pill button | `AboutPillButton` | AI actions ("About this song", Start DJ session) — sparkle icon + label on teal tint |
| Ghost pill button | `GhostPillButton` | Non-AI secondary actions (DJ "End session") — no sparkle, quieter than the teal pill |
| Link button | `AboutLinkButton` | Low-emphasis inline actions (Regenerate, Find six more, Cancel) — sparkle + text, no border |
| Title-bar icon button | `TitleIconButton` | Window chrome and inline icon actions — 32×32, radius 7 |
| Dialog chrome | `AppDialog` + `DialogPrimaryButton` / `DialogGhostButton` / `DialogTextBox` / `DialogSectionHeader` / `DialogFieldLabel` / `DialogHint` | About, Options and Add/Edit station. Borderless radius-12 surface, own title strip + close, teal primary / ghost secondary, and a `#99050D0C` scrim over the main window while open |
| Volume slider | `VolumeSlider` | Gradient fill + glowing white thumb; the seek bar reuses the same visual language |
| Equalizer | `EqualizerTemplate` | 4 animated teal bars — "audio is playing" indicator (Now Playing + current list row) |
| Spinner | `SpinnerTemplate` | The one "you are waiting on something" mark; never shown in a steady state |
| Scrollbars | implicit `ScrollBar` | Thin (≈4 px visual) rounded thumb, no arrows; teal while dragging |
| Focus ring | implicit (system focus-visual key) | 2 px teal rounded ring, replacing WPF's dotted rectangle |

## Motion vocabulary

- Spinner: 14 px arc, 0.9 s rotation — used for every async/AI wait, paired with a status line.
- Equalizer bars: staggered 0.45 s ScaleY loop.
- LIVE badge: 6 px teal dot pulsing opacity (0.8 s auto-reverse).
- No page/panel transitions — mode and tab switches are instant swaps.

## Glyph vocabulary (one meaning each)

- **Sparkle** = AI does something here. Search field, Curate, Start DJ session, About this song,
  Regenerate — and nothing else. It is deliberately *not* on save, and not on End session.
- **Plus-in-circle → teal check-in-circle** = save to your library, and its state. On the
  now-playing control there's a middle state: teal plus = marked, saves when the song ends.
- **Spinner** = you are waiting on something. Never present in a steady state.
- **Pulsing teal dot** = live and healthy (LIVE badge, DJ steady session).

## Signature moments (protect these)

- The glowing teal play button as the focal point of the right side.
- The LIVE badge + equalizer pairing during radio playback.
- Quirky-professional status copy paired with the small spinner during AI waits.
