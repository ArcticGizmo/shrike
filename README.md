<h1 align="center">Shrike</h1>
<p align="center">
 <img src="./landing-icon.png" width="150"  />
</p>

<p align="center">
<strong>A snappy, tray-resident replacement for the Windows Snipping Tool.</strong>
</p>

<br>

Shrike lives in your system tray and pops open the instant you press its hotkey. Grab a screenshot,
mark it up, or record a slice of your screen and trim it down to something small enough to drop into
Slack — all without a main window getting in your way, and **without ever throwing you across virtual
desktops**.

## What it does

- **Fast screenshots** — region, window, or whole-monitor, from one hotkey.
- **Real annotation** — arrows, boxes, highlights, freehand, text, and numbered step badges you can
  move around after placing. Zoom and crop before you export.
- **True redaction** — black out the sensitive bits and the exported image carries *no* recoverable
  trace of what was underneath (not a blur you can reverse).
- **Screen recording** — record a region to a video, with a floating control bar and a clear frame
  around exactly what's being captured.
- **Trim and shrink** — cut the dead bits on a simple timeline, then export with a preset: *Slack-small*
  for a tiny clip, *Balanced*, *Most-compatible*, or GIF / animated WebP. A live size estimate shows
  the footprint before you commit.
- **Copy straight into Slack** — copy an image or the exported clip as a file and paste it right in, or
  save it to disk.
- **Recent captures** — the last few shots are one click away from the tray or the editor's filmstrip,
  so you never lose one.
- **Yours to configure** — rebindable hotkeys, a default save folder and format, and an opt-in
  launch-at-login toggle.

## Install

Windows 10 or 11 (64-bit). Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/shrike/main/install.ps1 | iex
```

It installs per-user (no admin needed) and starts Shrike in the tray. Installing this way verifies the
download and sidesteps the SmartScreen "unknown publisher" prompt. Updates from then on are in-app.

## Using Shrike

Shrike has no main window — it's the amber-and-navy bird in your system tray.

- **Capture** — press `Alt+Shift+Q` (or click the tray icon) to open the chooser, then pick a region,
  window, or monitor. Your shot opens in the editor.
- **Annotate & export** — use the toolbar to mark it up, redact, crop, or zoom; then **copy** it or
  **save** it (PNG / JPG / WebP).
- **Record** — press `Alt+Shift+R` (or *Record region* in the tray), drag out the area, and use the
  control bar to pause or stop. On stop it opens the timeline editor: scrub, set in/out, cut a section,
  then **Export** with a preset and **Save** or **Copy file** to paste into Slack.
- **Recent** — re-copy or re-open your last few captures from the tray's **Recent** menu or the
  editor's filmstrip.
- **Settings** — tray → **Settings…** to rebind hotkeys, set your save folder/format, or turn on
  launch-at-login.
- **About & updates** — tray → **About Shrike…** shows the version and changelog and checks for updates.

Everything obeys one rule: whatever Shrike shows appears on the desktop you're already looking at — it
never switches you to another virtual desktop.

## Building from source

Shrike is built on [Avalonia](https://avaloniaui.net/) (.NET 10) and is a sibling to
[`sprig`](../sprig).

```
src/Shrike.Core/   # capture, encoding, annotation model, storage — no UI deps
src/Shrike.App/    # Avalonia UI: tray, overlay, editors
tools/             # icon generator, ffmpeg fetch
tests/Shrike.Tests/
```

```
run.bat                    # or: dotnet run --project src\Shrike.App
dotnet test                # run the test suite
```

Packaging and release details are in [`docs/packaging.md`](docs/packaging.md); the full design and
phased plan live in [`docs/design.md`](docs/design.md) and
[`docs/implementation-plan.md`](docs/implementation-plan.md).
