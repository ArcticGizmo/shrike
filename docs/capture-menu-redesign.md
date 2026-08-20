# Capture menu redesign + delayed capture

Status: implemented (0.3.x). Supersedes the flat six-row chooser.

## Why

The capture chooser (`CaptureMenuWindow`) was a flat list — screenshots, video, the
colour tool, and the recent library all at one level, split only by a single divider
before "Recent". As modes accumulated it stopped reading as groups. We also want a
**self-timer** so you can capture transient UI (a dropdown, a hover tooltip) that
vanishes the moment you touch anything else.

## New layout

Actions are grouped under monospace section headers; the delay is a **modifier**, not a
mode, sitting below a divider so it reads as "affects the next shot" rather than "a thing
to pick".

```
┌──────────────────────────────────┐
│ CAPTURE                            │
│                                    │
│ SCREENSHOT                         │
│   1  Region or window        +5s   │  ← badge shows when a delay is armed
│   2  This monitor            +5s   │
│   3  All monitors            +5s   │
│                                    │
│ RECORD                             │
│   4  Record region                 │
│                                    │
│ TOOLS                              │
│   5  Pick colour                   │
│   6  Recent captures (3)           │
│                                    │
│ ──────────────────────────────    │
│   D  Delay        Off              │  ← D (or click) cycles Off → 3s → 5s → 10s
│                                    │
│ 1–6 pick · D delay · Esc cancel    │
└──────────────────────────────────┘
```

Keys 1–6 keep their existing meaning, so muscle memory is preserved. `D` cycles the
delay; clicking the Delay row does the same.

## Delay semantics (decisions)

- **UI:** modifier toggle (chosen over a submenu / separate timed rows) — one control,
  no row explosion.
- **Scope:** **screenshots only** (Region, This monitor, All monitors). Recording keeps
  its own 3-2-1 countdown; Pick colour and Recent ignore the delay. Only the screenshot
  rows show the `+Ns` badge.
- **Values:** Off / 3s / 5s / 10s.
- **Persistence:** remembered in `AppSettings.CaptureDelaySeconds`, so the last delay
  survives across captures and sessions.

## How a delayed shot works (and why it must re-grab live)

Instant captures crop a **frozen** snapshot taken when the menu opened. A self-timer that
cropped that stale frozen image would be pointless — the whole reason for a delay is to
change the screen *during* the wait. So a delayed shot **ignores the frozen snapshot and
does a fresh live grab after the countdown**:

1. Chooser tears down (dim + menu gone) — the screen is left **clean**, not dimmed, so
   you can open the menu/tooltip you want to capture.
2. A countdown pill (`DelayCountdownWindow`) appears at the top-centre of the target
   monitor and counts N → 1.
3. At zero the pill closes and Shrike does a live `ScreenCapture.Capture(bounds)` of the
   originally chosen bounds, straight into the editor.

For **Region + delay**, you pick the region first (frozen only feeds the loupe), *then*
the countdown runs, *then* the chosen rectangle is grabbed live.

### The countdown pill is visible but never in the shot (WYSIWYG)

Per the project's WYSIWYG rule, the countdown is shown honestly on screen — but it must
not end up *in* the screenshot. The pill reuses `WindowExclusion`:

- `Hide` → `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` keeps it out of the capture
  path while still rendering on the display.
- `MakeClickThrough` → clicks fall through, so you can drive the app you're capturing.
- `MakeNonActivating` → it never steals focus from the window you're arranging.

As a belt-and-suspenders for pre-2004 Windows (where exclusion degrades to a black box),
the pill is closed and given an ~80 ms settle before the grab.

## Touched files

- `src/Shrike.Core/Settings/AppSettings.cs` — new `CaptureDelaySeconds` (+ sanitise to
  {0,3,5,10}).
- `src/Shrike.App/Views/CaptureMenuWindow.axaml(.cs)` — grouped layout, delay row,
  `+Ns` badges, `D` cycles, `DelaySeconds` + `DelayChanged`.
- `src/Shrike.App/Views/DelayCountdownWindow.cs` — new capture-excluded countdown pill.
- `src/Shrike.App/Services/CaptureController.cs` — read/persist the delay, delayed
  screenshot flow (`StartDelayedGrab`).
