# Shrike

<img src="shrike-icon.png" alt="Shrike" width="120" align="right" />

A snappy, tray-resident replacement for the Windows Snipping Tool — screenshots with real
annotation, screen recording with lightweight timeline trimming, and a capture flow that
**never throws you across virtual desktops**.

Built on Avalonia (.NET 10), sibling to [`sprig`](../sprig).

## Status

In development — **Phase D (M6): Ship**. Screenshots, annotation, redaction, the recent ring,
screen recording, timeline trimming + footprint-tuned export, and settings are all in; remaining
M6 work is the lean ffmpeg bundle, release automation, and the v1.0 tag.

See [`docs/design.md`](docs/design.md) for the full design and
[`docs/implementation-plan.md`](docs/implementation-plan.md) for the phased plan.
An interactive UI mockup lives at [`docs/mockup.html`](docs/mockup.html).

## Layout

```
src/Shrike.Core/   # capture, encoding, annotation model, storage — no UI deps
src/Shrike.App/    # Avalonia UI: tray, overlay, editors
tests/Shrike.Tests/
```

## Run

```
run.bat            # or: dotnet run --project src\Shrike.App
```

Shrike launches into the system tray. Press the capture hotkey (default `Alt+Shift+Q`)
to pop the region overlay. Right-click the tray icon for the menu.

### Diagnostics

```
shrike measure-startup   # boot headless, print startup timings as JSON, exit
```
