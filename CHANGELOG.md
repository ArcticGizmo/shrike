# Changelog

All notable changes to Shrike are recorded here. Dates are ISO-8601.

## [Unreleased]

### Added
- **Screen recording** — record a region to a playable MP4, with a floating HUD (elapsed clock,
  pause/resume, stop, discard) and a visible boundary frame around the captured area.
- **Timeline editor** — trim a recording non-destructively (cut / keep-only / restore), smooth
  preview playback, and export to footprint-tuned presets (Slack-small H.265, Balanced,
  Most-compatible H.264, Source stream-copy, GIF, WebP) with a live size estimate. Save to disk or
  copy the file straight into Slack.
- **Settings** — rebindable capture + record hotkeys, desktop behaviour, recent-ring size, default
  save folder and image format, and an opt-in launch-at-login toggle.
- Screenshot capture, annotation toolbox, true destructive redaction, zoom, crop, and the
  recent-captures ring (memory-only).

## [0.0.1]

- Initial skeleton: tray-resident app, global hotkey, snappy-load budget harness.
