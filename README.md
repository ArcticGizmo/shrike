# Shrike

A snappy, tray-resident replacement for the Windows Snipping Tool — screenshots with real
annotation, screen recording with lightweight timeline trimming, and a capture flow that
**never throws you across virtual desktops**.

Built on Avalonia (.NET 10), sibling to [`sprig`](../sprig).

## Status

Early development. Current milestone: **M0 — Foundation** (tray shell, single-instance,
global hotkeys, current-desktop overlay, and the startup-budget harness).

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
