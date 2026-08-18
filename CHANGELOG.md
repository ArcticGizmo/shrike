# Changelog

All notable changes to Shrike are recorded here, newest first. Dates are ISO-8601.

---

## [Unreleased]

---

## [v0.3.0] - 2026-08-18

- Timeline zoom — place, drag and resize zoom events on a clip
- Aim a zoom by dragging an aspect-locked box on the preview
- Zoom snaps to where you clicked and eases smoothly in and out
- Edit a selected zoom's amount and ease-in / ease-out in a side panel
- Zoom events show their scale and duration on the lane
- Time ruler above the timeline with round-number increments
- Hand-placed zoom events replace the old automatic auto-zoom
- Spacebar plays and pauses in the editor
- The cursor is always recorded and drawn in the editor — a single **Show cursor** toggle, per clip
- Cursor smoothing is one **Smoothness** slider, with **size** and click-**ripple** controls
- Click ripples now preview live in the editor
- Cursor look and zoom bake into every export preset
- Spotlight cursor removed from recording (returning later as an editor effect)

---

## [v0.2.0] - 2026-08-18

- Experimental smooth cursor for recordings (opt-in) — smoothed synthetic pointer with click ripples
- Auto-zoom toward clicks, tunable live in the timeline editor
- Recordings kept in a managed folder and auto-pruned (no longer lost to temp cleanup)
- Timeline: Play restarts from the start after reaching the end

---

## [v0.1.3] - 2026-08-14

- Colour pipette — sample any on-screen pixel and copy its HEX, RGB or HSL
- Show or hide the mouse cursor in recordings

---

## [v0.1.2] - 2026-08-14

- Resize and rotate shapes with handles, like the crop tool
- Resize text labels by dragging their corners
- Copy, paste and duplicate annotations (Ctrl+C / Ctrl+V / Ctrl+D)
- New-capture button in the editor to quickly grab another shot
- Single-key shortcuts for every drawing tool, shown on the icons
- Region capture: clicking bare desktop grabs just that monitor, not all of them
- Adjust the record region with handles before recording starts
- 3-2-1 countdown before recording begins
- Single recording bar from setup through to stop
- Drag the recording bar anywhere on screen
- Spotlight cursor — a glow under the mouse, on screen and in the recording
- Adjustable spotlight colour, opacity and size

---

## [v0.1.1] - 2026-08-13

- Drag handles to move or resize the editor's crop rectangle
- Start recording from the capture chooser instead of a dedicated hotkey
- Dev builds run side-by-side with an installed release, keeping their own settings

---

## [v0.1.0] - 2026-08-13

- Screen recording — capture a region to MP4 with a floating control bar and a visible frame
- Timeline editor — trim recordings and export small, Slack-sized clips (H.265, H.264, GIF, WebP)
- Copy an image or an exported clip straight into Slack, or save to disk
- Screenshot annotation — arrows, boxes, highlights, freehand, text, and movable step badges
- True redaction that destroys the covered pixels on export (not a reversible blur)
- Zoom and crop before exporting
- Recent captures — re-copy or re-open your last few shots from the tray
- Settings — rebindable hotkeys, default save folder and format, opt-in launch-at-login
- What's-new changelog after an update, and an About window with an update check
- Never switches you across virtual desktops

---

## [v0.0.1] - 2026-08-12

- Initial skeleton: tray-resident app, global hotkey, and the snappy-load budget harness

---
