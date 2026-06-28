# Bundled UI font — Hanken Grotesk

The app's UI font is **Hanken Grotesk** (the Direction A design font), SIL Open Font
License 1.1 — free to bundle and distribute.

## Add the font files here

Download Hanken Grotesk from Google Fonts (https://fonts.google.com/specimen/Hanken+Grotesk)
and copy the **static** TTFs for the weights the UI uses into this folder:

- `HankenGrotesk-Light.ttf`     (300 — the big now-playing title)
- `HankenGrotesk-Regular.ttf`   (400 — body)
- `HankenGrotesk-Medium.ttf`    (500 — list titles, tabs)
- `HankenGrotesk-SemiBold.ttf`  (600 — labels, active tab)

The `.csproj` already includes `Fonts\*.ttf` as a `Resource`, so they're embedded in the
assembly on build. Once the files are present, `UiFont` in `Theme.xaml` is set to
`/RadioPlayer;component/Fonts/#Hanken Grotesk` so the app uses the bundled font on any
machine (no install required).

> Note: a single `HankenGrotesk-VariableFont_wght.ttf` can work too, but WPF's variable-font
> support is limited — the static per-weight files above are the reliable choice.
