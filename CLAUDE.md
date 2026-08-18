# Shrike — project conventions

## Editor: every effect must be visible in the preview (WYSIWYG)

**Any effect that gets baked into the export MUST also be shown in the editor preview** — the
smoothed cursor, click ripples, zoom/pan framing, and anything added later. The preview render
**may be lower quality** than the final encode (simpler raster, no supersampling, approximate
colours) — that trade-off is fine. What is **not** acceptable is an effect that only appears in
the exported file: if the user can't see it while editing, they can't tune it, and they have no
idea what's happening. "It shows up in the export" is not done.

- When you add or change an export-time effect, wire it into the preview in the same change.
- Preview and export should share the same *inputs* (positions, timings, viewport mapping) so what
  you see matches what you get, even if the pixels are cheaper.
- If a faithful preview is genuinely impractical, show an honest approximation (a marker, an
  outline) rather than nothing — and call out the gap.

## Dev build

Always stop the running Shrike before rebuilding, so the user never tests a stale binary
(`Get-Process shrike | Stop-Process -Force`, then build, then relaunch the exe).
