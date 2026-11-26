# Islet

A lightweight Dynamic Island for Windows. It stays out of sight until you push the mouse to the top of the screen, then drops down with what matters: what's playing, synced lyrics, and your favourite apps.

**~27–45 MB RAM, under 0.5% CPU.** Built with C# / WPF; no Electron, no browser engine.

## Features

- **Dark liquid glass.** A live, rounded blur of whatever is behind the island, with a smoky tint, a specular rim and a soft top sheen. Toggle it in the tray menu if you prefer solid black.
- **Hidden until you need it.** Push the pointer against the top-centre edge of the screen and the island drops down. Move away and it tucks itself back in.
- **Now playing.** Works with anything that uses Windows media controls (Spotify, YouTube in Chrome/Edge, Apple Music, Media Player…). Album art, waveform tinted to the cover's colour, progress, and play/pause/skip.
- **Synced lyrics.** The current line, live, from [LRCLIB](https://lrclib.net).
- **App shortcuts.** 3–5 of your most-used apps, picked automatically on first run from Windows usage and your taskbar pins.
  - Drag any app or shortcut onto the island to pin it (up to 5).
  - Right-click an icon to remove it.
- **Alerts.** Charging, low battery, and Bluetooth devices connecting.
- **Privacy dots.** Green while the camera is in use, orange for the microphone.
- **Gets out of the way.** Hides automatically in fullscreen games and videos.

## Install

1. Go to **Releases** and download one of:
   - `Islet-portable.exe`: runs on any Windows 10/11 x64 PC, no setup (larger file).
   - `Islet.exe`: much smaller, but needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed.
2. Put it somewhere permanent (e.g. `C:\Apps\Islet\`) and run it.
3. Optional: tray icon → **Start with Windows**.

> Windows SmartScreen may warn about an unsigned app the first time. Click **More info → Run anyway**.

## Controls

| Action | What it does |
|---|---|
| Push pointer to top-centre edge | Show the island |
| Click the island | Switch between music and apps |
| Scroll on the island | Volume up / down |
| Middle-click | Play / pause |
| Right-click the island or the tray icon | Settings menu |
| Drag an app onto the island | Pin it as a shortcut |
| Right-click an app icon | Remove it |

Settings (tray menu): liquid glass effect, auto-hide, synced lyrics, expand on hover, Bluetooth/battery alerts, start with Windows, add/reset shortcuts.

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
.\build.ps1            # dist\Islet.exe
.\build.ps1 -Portable  # dist\Islet-portable.exe
```

Pushing a tag like `v1.1.0` makes GitHub Actions build both files and attach them to a Release automatically.

## Privacy

Everything runs locally. The only network request is the song title and artist sent to lrclib.net to fetch lyrics, and you can turn that off in the tray menu. Settings and shortcuts are stored in `%APPDATA%\Islet`.
