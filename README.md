# Aurora Radio

A lightweight Windows desktop app for playing internet radio streams (Icecast/Shoutcast),
with playback surfaced natively in the OS:

- **System Media Transport Controls (SMTC)** — the now-playing widget in the Windows 11
  volume/quick-settings flyout and lock screen, plus hardware media-key handling.
- **Taskbar thumbnail buttons** — play/stop shown in the live preview when hovering the
  taskbar icon.

Optionally, describe a vibe in plain language ("something mellow for late-night coding")
and the app finds real, playable stations via **AI-assisted search** (requires an
Anthropic API key).

Built with WPF on .NET 10, using the [BASS](https://www.un4seen.com/) audio library.

![Aurora Radio — now playing, with the AI search panel](doc/screenshot.png)

## Features

- Stream AAC and MP3 internet radio.
- **Live track titles** via ICY metadata.
- **Station management** — add / edit / delete stations, persisted as JSON.
- **AI-assisted search** — describe a vibe, get real playable stations (optional; see
  below).
- **OS integration** — SMTC (Win11 flyout, lock screen, media keys) and taskbar thumbnail
  buttons.
- Automatic reconnection on dropped streams; single running instance.

## Quick start

Requires the **.NET 10 SDK** and Windows 11.

```sh
dotnet build
dotnet run
```

The core player works with no extra setup. AI search needs an API key — see
[Enabling AI search](#enabling-ai-search).

### Native dependencies

`bass.dll` and `bass_aac.dll` (x64) are tracked under `native/x64/` and copied to the
output directory at build time. Build **x64 only** — AnyCPU is not supported.

### Embedding model (Phase 2 semantic search)

Local semantic search requires `all-MiniLM-L6-v2.onnx` (~90 MB) under `MlAssets/`. The
file is **git-ignored** (too large to track) and must be downloaded once after cloning:

```sh
curl -L -o MlAssets/all-MiniLM-L6-v2.onnx \
  https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
```

Without it, fuzzy search falls back gracefully to Pattern B (web discovery). The core
player and all other AI search layers still work.

## Enabling AI search

AI search is disabled until an Anthropic API key is provided. Either:

- Click the **gear (Options)** button and paste your key — stored **encrypted** on your
  PC (Windows DPAPI, current user) and never committed; or
- Set the `ANTHROPIC_API_KEY` environment variable.

Get a key at [console.anthropic.com](https://console.anthropic.com). The in-app key takes
precedence; changes apply on restart.

> Pattern B uses Anthropic's server-side web search tool (must be enabled in the
> Anthropic Console) and incurs per-search cost. Local layers (enrichment + embeddings)
> are free.

## How AI search works

See [doc/TECHNICAL.md](doc/TECHNICAL.md) for the full architecture, AI search patterns
(structured output, agentic loop, local enrichment, semantic search), and design
decisions.

## License

Licensed under the **PolyForm Noncommercial License 1.0.0** — use, modify, and share for
any **non-commercial** purpose. See [LICENSE](LICENSE.md).

**Commercial use is not permitted**, which also aligns with the bundled BASS audio engine
(free for non-commercial use only). Switching the engine to
[LibVLCSharp](https://github.com/videolan/libvlcsharp) (LGPL) is feasible — the engine
is isolated behind the `RadioEngine` abstraction. See
[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md) for third-party component licenses.
