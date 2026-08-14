# SMTC Player

A minimal now-playing widget for [Rainmeter](https://www.rainmeter.net/) that
reads **Windows SMTC** (Global System Media Transport Controls) — the same
system channel that draws the volume overlay with album art.

That means it works with players Rainmeter's bundled `NowPlaying` plugin does
not support: **Yandex Music**, Spotify, browsers, VLC — anything that reports to
the Windows media overlay.

![SMTC Player](docs/screenshot.png)

## Features

- Title, artist and album art from any SMTC-aware player
- Progress bar that actually moves (see [notes](docs/notes-ru.md) — most players
  report position as a snapshot, not a stream)
- Previous / play-pause / next, and click-to-seek on the progress line
- No cover art? The card collapses instead of leaving a hole
- Scroll wheel to resize
- Picks the session that is really playing, not the one that merely holds the
  media keys

## Install

1. Download the latest `.rmskin` from
   [Releases](../../releases) and open it — the Rainmeter installer does the rest.
2. Load **SMTC Player** from the Rainmeter manage window if it does not appear
   right away.

Requires Rainmeter 4.5+ and Windows 10 1809 or newer (that is when the SMTC API appeared).

## How it works

Rainmeter cannot talk to WinRT, so a tiny background helper does it:

```
smtc_bridge.exe                        Rainmeter
 ├─ reads Windows SMTC (WinRT)
 ├─ !SetVariable ──WM_COPYDATA──▶      Player.ini  (title, artist, position, …)
 └─ cmd.ini      ◀──!WriteKeyValue──   button clicks
```

The bridge is a ~14 KB C# executable with no window. The skin starts it on load,
a mutex keeps a single copy, and it exits on its own about a minute after
Rainmeter is gone.

On first run it copies itself to `%LOCALAPPDATA%\RainmeterSMTC` and runs from
there — a running executable inside the skin folder cannot be moved, and the
Rainmeter installer moves that folder to `@Backup` on every update. Album art
and logs land in the same folder.

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

Builds the bridge and packs `dist\SMTC-Player-<version>.rmskin`. Needs only
`csc.exe` from .NET Framework 4 (ships with Windows) and `Windows.winmd` from
the Windows SDK. `build.ps1 -SkipBridge` repacks without recompiling.

`tools\deploy.ps1` copies the skin straight into your Rainmeter folder for
development.

## Credits

- Visual language follows **Mond** by [Connect-R](https://www.deviantart.com/connect-r);
  icons here are original, drawn by `tools\make_icons.ps1`.
- Fonts: [Quicksand](https://fonts.google.com/specimen/Quicksand) (SIL OFL),
  Aquatico by Andrew Herndon (free for personal and commercial use).

## License

MIT — see [LICENSE](LICENSE). Fonts keep their own licenses.
