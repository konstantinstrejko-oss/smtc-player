# SMTC Player

[![Download](https://img.shields.io/badge/Download-SMTC--Player.rmskin-2ea44f?style=for-the-badge)](https://github.com/konstantinstrejko-oss/smtc-player/releases/latest/download/SMTC-Player.rmskin)
[![Release](https://img.shields.io/github/v/release/konstantinstrejko-oss/smtc-player)](https://github.com/konstantinstrejko-oss/smtc-player/releases/latest)
[![License](https://img.shields.io/github/license/konstantinstrejko-oss/smtc-player)](LICENSE)

A minimal now-playing widget for [Rainmeter](https://www.rainmeter.net/) that
reads **Windows SMTC** (Global System Media Transport Controls) — the same
system channel that draws the volume overlay with album art.

That means it works with players Rainmeter's bundled `NowPlaying` plugin does
not support: **Yandex Music**, Spotify, browsers, VLC — anything that reports to
the Windows media overlay.

![SMTC Player](docs/screenshot.png)

## Install

1. **[Download SMTC-Player.rmskin](https://github.com/konstantinstrejko-oss/smtc-player/releases/latest/download/SMTC-Player.rmskin)**
2. Double-click the file — Rainmeter's own installer opens.
3. Press **Install**. The skin loads itself, nothing else to set up.

![Installer](docs/installer.png)

Requires Rainmeter 4.5+ and Windows 10 1809 or newer (that is when the SMTC API
appeared). To move the widget, drag it; to resize it, scroll on it.

> **Windows may warn about the bundled `.exe`.** It is the background bridge —
> unsigned, because code-signing certificates cost money. The full C# source is
> in this repo (`@Resources/Bridge/src`) and builds with one command, so you can
> compile it yourself and replace the binary if you prefer.

## Features

- Title, artist and album art from any SMTC-aware player
- Progress bar that actually moves (see [notes](docs/notes-ru.md) — most players
  report position as a snapshot, not a stream)
- Previous / play-pause / next, and click-to-seek on the progress line
- No cover art? The card collapses instead of leaving a hole
- Scroll wheel to resize
- Picks the session that is really playing, not the one that merely holds the
  media keys

## How it works

Rainmeter cannot talk to WinRT, so a tiny background helper does it:

```
smtc_bridge.exe                        Rainmeter
 ├─ reads Windows SMTC (WinRT)
 ├─ !SetVariable ──WM_COPYDATA──▶      Player.ini  (title, artist, position, …)
 └─ cmd.inc      ◀──!WriteKeyValue──   button clicks
```

The bridge is a ~14 KB C# executable with no window. The skin starts it on load,
a mutex keeps a single copy, and it exits on its own about a minute after
Rainmeter is gone.

On first run it copies itself to `%LOCALAPPDATA%\RainmeterSMTC` and runs from
there — a running executable inside the skin folder cannot be moved, and the
Rainmeter installer moves that folder to `@Backup` on every update. Album art
and logs land in the same folder.

## Uninstall

Right-click the skin → **Unload skin**, then delete `SMTC Player` from your
Rainmeter skins folder. The bridge exits by itself; leftovers live in
`%LOCALAPPDATA%\RainmeterSMTC` and can be deleted too.

## Configuration

`@Resources\Variables.inc`:

| Variable | Meaning |
|----------|---------|
| `TextColor`, `ButtonColor`, `ButtonHover`, `DimAlpha` | colors |
| `PreferApp` | pin the widget to one app, e.g. `ru.yandex.desktop.music`. Empty = follow the system session |

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Builds the bridge and packs `dist\SMTC-Player.rmskin`. Needs only `csc.exe` from
.NET Framework 4 (ships with Windows) and `Windows.winmd` from the Windows SDK.
`build.ps1 -SkipBridge` repacks without recompiling. Pushing a `v*` tag builds
the package on CI and attaches it to the release.

`tools\deploy.ps1` copies the skin straight into your Rainmeter folder for
development.

## Credits

- Visual language follows **Mond** by [Connect-R](https://www.deviantart.com/connect-r);
  icons here are original, drawn by `tools\make_icons.ps1`.
- Fonts: [Quicksand](https://fonts.google.com/specimen/Quicksand) (SIL OFL),
  Aquatico by Andrew Herndon (free for personal and commercial use).

## License

MIT — see [LICENSE](LICENSE). Fonts keep their own licenses.
