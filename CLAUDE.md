# CLAUDE.md

Project context for Claude Code. Read this before working on audio playback or OS
media integration — it captures decisions and non-obvious gotchas that aren't
discoverable from the code alone.

## Project overview

A Windows desktop app that plays internet radio streams (Icecast/Shoutcast), with
playback surfaced in the OS in two places:

1. **Taskbar thumbnail buttons** — play/pause shown in the live preview when hovering
   the app's taskbar icon.
2. **System Media Transport Controls (SMTC)** — the now-playing widget in the Win11
   volume/quick-settings flyout, lock screen, and keyboard media-key handling.

Primary test stream: Radio Paradise AAC 128 — `http://stream.radioparadise.com/aac-128`.

## Tech stack

- **Language/runtime:** C# / .NET 8
- **UI:** WPF — chosen for first-class taskbar thumbnail button support via
  `TaskbarItemInfo.ThumbButtonInfos` (WinUI 3 has no built-in equivalent).
- **Audio engine:** BASS (un4seen) via **ManagedBass** + **ManagedBass.Aac**
- **OS media integration:** SMTC via WinRT interop (`ISystemMediaTransportControlsInterop`)
- **Target framework:** `net10.0-windows10.0.19041.0`
  (the Windows SDK version in the TFM is required to project the WinRT/SMTC types).
  This dev machine only has the .NET 10 SDK/runtime; net8.0 would build but not run
  without installing the .NET 8 runtime. The WPF/SMTC/taskbar code is identical either
  way — bump the major version in the TFM if you target a different runtime.
- **Architecture pattern:** MVVM
- **Platform target:** **x64** (must match the native BASS DLLs — see gotchas)

## Decisions & open questions

- **Engine = BASS, not libVLC.** Rationale: lightweight, simple streaming API,
  first-class ICY metadata (live song titles). The ManagedBass wrapper is MIT.
  **Tradeoff:** BASS itself is free for *non-commercial* use only; shipping
  commercially requires a paid un4seen license. If the project must be
  free-to-distribute commercially, switch to **LibVLCSharp** (LGPL, but a ~40MB+
  native payload and you'd wire ICY metadata via `Media.MetaChanged`). This is the
  one decision most likely to be revisited — keep the engine behind the
  `RadioEngine` abstraction so it can be swapped.

## Architecture

Keep a clean seam between the audio engine and everything else.

- **`RadioEngine`** — the only class that talks to BASS. Exposes
  play/pause/stop/volume, raises events for playback state, buffering, errors, and
  ICY metadata. Nothing else in the app calls BASS directly.
- **`SmtcController`** — owns the `SystemMediaTransportControls` instance, pushes
  now-playing info via `DisplayUpdater`, and handles `ButtonPressed` (mapping
  play/pause/stop to `RadioEngine`). Bootstrapped from the main window once the
  HWND exists.
- **`MainWindow` (XAML)** — taskbar thumbnail buttons via `TaskbarItemInfo`; minimal
  code-behind (only view wiring + SMTC/HWND bootstrap).
- **`MainViewModel`** — playback state, current station, station list, commands.
- **`Station` model** — `{ Name, Url, Format }` so the engine knows whether to use
  the AAC add-on path or the core path.

### Threading model (important)

- BASS sync callbacks (metadata changes, stalls, end-of-stream) fire on
  **BASS-owned threads**. Marshal to the UI thread via `Dispatcher` before touching
  the view model, UI, or SMTC.
- Treat `RadioEngine`'s public surface as UI-thread-affine; do the marshalling inside
  the engine so callers don't have to think about it.

## Build & run

```
dotnet build
dotnet run
```

**Native dependencies** must be present in the output directory and match the process
architecture (x64):

- `bass.dll` (core)
- `bass_aac.dll` (AAC/MP4 decoding — required for the Radio Paradise stream)

Prefer the ManagedBass NuGet native packages, or add the DLLs as `Content` with
`CopyToOutputDirectory`. Do **not** build AnyCPU unless you guarantee the right
native DLLs are resolved.

## Key gotchas (read before writing audio/SMTC code)

1. **AAC needs the add-on.** BASS *core* does not decode AAC. The `aac-128` test
   stream requires `bass_aac` (ManagedBass.Aac). Create AAC URL streams with
   `BassAac.CreateStream(url, ...)`, not `Bass.CreateStream(...)`. Use plain
   `Bass.CreateStream(url, ...)` only for MP3/OGG stations. Verify the exact overload
   signature against the installed ManagedBass.Aac version.

2. **SMTC in WPF has no CoreWindow.** `SystemMediaTransportControls.GetForCurrentView()`
   throws in a Win32/WPF app, and the old hand-rolled `[ComImport]`
   `ISystemMediaTransportControlsInterop` cast **no longer works** in modern .NET —
   built-in WinRT support was removed from the runtime and `IInspectable`-based interop
   interfaces can't be declared that way anymore. Instead use the **projected** static
   class: `Windows.Media.SystemMediaTransportControlsInterop.GetForWindow(hwnd)` (it
   returns a typed `SystemMediaTransportControls`; available via the Windows SDK TFM).
   Get the HWND from `new WindowInteropHelper(window).Handle` *after* the window is shown
   (in `OnSourceInitialized`/`SourceInitialized`). Then set `IsPlayEnabled`/`IsPauseEnabled`,
   subscribe to `ButtonPressed` (fires on a background thread — marshal to the UI thread),
   set `PlaybackStatus`, and push title/artist via `DisplayUpdater` + `DisplayUpdater.Update()`.

3. **ICY metadata setup.** `Configuration.NetMetadata` does **not** exist in ManagedBass
   4.0.2 — ICY/Shoutcast metadata is requested by default, so no `Bass.Configure` call is
   needed to enable it. Read the initial station info from
   `Bass.ChannelGetTags(handle, TagType.ICY)`; subscribe to a metadata sync
   (`SyncFlags.MetadataReceived`) for live track changes and read
   `TagType.META`, then parse the `StreamTitle='...';` field. Feed parsed title/artist
   into both the view model and `SmtcController`.

4. **It's an endless HTTP stream.** The URL is plain `http://` (not HTTPS) and has no
   duration. Don't treat the channel as seekable; don't show/seek a position bar.

5. **Reconnection.** Radio streams drop. Handle stalls and end-of-stream syncs by
   recreating the stream, and reflect a buffering/reconnecting state in both the UI
   and SMTC `PlaybackStatus`.

6. **TFM must include the Windows SDK version** (`net10.0-windows10.0.19041.0`) or the
   WinRT `Windows.Media.*` types won't be available.

## Conventions

- MVVM throughout; keep code-behind to view wiring and the HWND/SMTC bootstrap only.
- All BASS access funnels through `RadioEngine`.
- Engine-agnostic abstraction so BASS can be swapped for libVLC without touching the
  view model.

## Testing

- **Primary:** `http://stream.radioparadise.com/aac-128` (AAC — exercises the
  `bass_aac` path + ICY metadata).
- **Add one MP3 station** to validate the non-AAC `Bass.CreateStream` path.
- Verify: taskbar play/pause toggles state; hardware media keys toggle playback via
  SMTC; the Win11 media flyout shows the current track and reflects play/pause.
