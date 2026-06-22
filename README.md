# Radio Player

A lightweight Windows desktop app for playing internet radio streams
(Icecast/Shoutcast), with playback surfaced natively in the OS:

- **System Media Transport Controls (SMTC)** — the now-playing widget in the Windows 11
  volume/quick-settings flyout and lock screen, plus hardware media-key handling.
- **Taskbar thumbnail buttons** — play/stop shown in the live preview when hovering the
  app's taskbar icon.

Built with WPF on .NET, using the [BASS](https://www.un4seen.com/) audio library.

## Features

- Stream AAC and MP3 internet radio (Icecast/Shoutcast).
- **Live track titles** via ICY metadata.
- **Station management** — add / edit / delete stations (friendly name + stream URL +
  format), persisted as JSON under `%AppData%\RadioPlayer\stations.json`.
- **Next-station** button that cycles through the list (also mapped to the media-key /
  SMTC "next track" button).
- Volume control, automatic **reconnection** on stalls / dropped streams.
- Custom borderless, drag-anywhere UI with a teal theme and a generated app icon.

## Tech stack

- **C# / .NET 10**, **WPF** (`net10.0-windows10.0.19041.0`; the Windows SDK version in
  the TFM is required to project the WinRT/SMTC types).
- **Audio engine:** [BASS](https://www.un4seen.com/) via
  [ManagedBass](https://github.com/ManagedBass/ManagedBass) + `ManagedBass.Aac`.
- **OS media integration:** SMTC via the projected
  `Windows.Media.SystemMediaTransportControlsInterop`.
- **Architecture:** MVVM, with all BASS access funnelled through a single `RadioEngine`
  so the engine can be swapped without touching the rest of the app.
- **Platform:** **x64** (must match the native BASS DLLs).

## Building & running

Requires the **.NET 10 SDK** and Windows 11 (for Segoe Fluent Icons and SMTC).

```sh
dotnet build
dotnet run
```

### Native dependencies

The native BASS DLLs are tracked in the repo under `native/x64/` and copied to the
output directory at build time:

- `bass.dll` — BASS core (MP3/OGG)
- `bass_aac.dll` — AAC add-on (required for AAC streams, e.g. the default
  Radio Paradise station)

> Build **x64** only — AnyCPU is not supported, because the native DLLs are 64-bit.

### Background artwork

The window background is `Assets/player-bg.png` (compiled in as a WPF resource).
Swap in your own ~16:9 image (≈1920×1080) to retheme the player.

## Project structure

| Path | Responsibility |
| --- | --- |
| `Services/RadioEngine.cs` | The only class that talks to BASS: play/pause/stop/volume, ICY metadata, reconnection. Marshals BASS callbacks to the UI thread. |
| `Services/SmtcController.cs` | Owns the SMTC instance; pushes now-playing info and maps the flyout/media-key buttons (incl. next-track) onto the app. |
| `Services/StationStore.cs` | Loads/saves the station list as JSON. |
| `ViewModels/MainViewModel.cs` | Playback state, commands, station list, now-playing. |
| `StationDialog.xaml(.cs)` | Add/edit station editor. |
| `MainWindow.xaml(.cs)` | UI, taskbar thumb buttons, custom window chrome, HWND/SMTC bootstrap. |
| `Models/Station.cs` | `{ Name, Url, Format }` station record. |

## Licensing note

BASS (un4seen) is **free for non-commercial use only**. Shipping this app commercially
requires a paid BASS license, or switching the engine to an alternative such as
[LibVLCSharp](https://github.com/videolan/libvlcsharp) (LGPL). The engine is kept behind
the `RadioEngine` abstraction to make that swap feasible.
