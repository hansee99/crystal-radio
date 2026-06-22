# Assets

## Background graphic (`player-bg.jpg` / `.png`)

The player window is fixed at **720 × 460 DIP** (`ResizeMode=CanMinimize`). After the
title bar the drawable content area is ~**720 × 425 DIP**, aspect ratio ≈ **16:9.5**
(close to 16:9). The background is drawn with `Stretch="UniformToFill"`, so it fills the
window and crops the overflow — keep important detail toward the centre.

**Recommended export size:** **1920 × 1080** (16:9) — covers up to ~2.6× display scaling
crisply and crops only a sliver. If you want an exact-fit, edge-to-edge image instead,
use **2160 × 1275** (3× of the content area).

- Format: JPG for photographic art, PNG if you need transparency.
- The app overlays a top→bottom dark scrim (transparent → ~70% black) for text
  legibility, so a mid/dark image or one with a darker lower-third works best.

### To enable it
1. Save your file here as `player-bg.jpg` (or `.png`).
2. In the build, set its **Build Action** to `Resource`.
3. In `MainWindow.xaml`, uncomment:
   `<!-- <Image Source="Assets/player-bg.jpg" Stretch="UniformToFill"/> -->`
