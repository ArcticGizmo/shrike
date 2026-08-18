# Shrike — Timeline-based Zoom Editing (Scoping)

> A fuller editing experience for zoom/pan: instead of framing derived automatically from click
> clusters, the user authors **zoom events** on a dedicated timeline lane — each with a target
> shape (the region to frame) and easing (how fast, what curve) — and the framing lerps between
> them. This supersedes the automatic auto-zoom (SC5) as the primary model.

**Status:** MVP built (branch `feature/compositor-chain`) · **Date:** 2026-08-18 · **Owner:** Jon
**Relationship to existing work:** replaces the click-cluster **auto-zoom** (`AutoZoom`, SC5) as the main
path. Built on the compositor-chain platform (Phase 0). Larger than a §-item in
[`smooth-cursor-followups.md`](smooth-cursor-followups.md) — scoped and tracked here on its own.

> **Built (2026-08-18).** Authored zoom is working end-to-end: a **zoom lane** under the scrubber with
> add / select / drag / resize event blocks, **click-tick markers** that drags snap to, a selection
> **inspector** (zoom amount + ease), and **drag-a-box on the preview** to aim (focus = box centre, zoom =
> fit the box). Events persist to the per-clip **edit document** (`*.edit.json`) and resolve through the
> shared `ZoomViewport[]` into both the live preview and every export preset. Decisions taken during the
> build: events are anchored in **source time** (pinned to content across cuts); the old auto-zoom UX was
> **removed** (not kept as a generator) per review — `AutoZoom` stays as dormant, tested code should a
> "suggest from clicks" ever be wanted. **Remaining:** overlap/transition polish between adjacent events;
> keyboard nudge/delete; a possible "suggest from clicks" seed.

## Why

The current zoom (`AutoZoom.ZoomCurve`) is **automatic**: it eases toward click activity, holds, and
eases out, with only two knobs (on/off, max-zoom). It's a decent default but not *directable* — the
user can't say "zoom here, to this region, this fast, and hold until there." A screen-recording
editor needs authored zoom: place it, shape it, time it. That's a real editing surface, not a slider.

## What we're building (intent)

- **A zoom lane on the timeline.** A second track beneath the scrubber where **zoom events** live as
  draggable/resizable blocks, positioned in edited time (they must ride the cuts like everything else).
- **A zoom event** defines:
  - **Target shape** — the rectangle (region) the framing zooms *to*. Anchored in frame space; ideally
    settable by dragging a box on the preview. Carries a zoom factor + centre (or an explicit rect).
  - **Easing** — zoom **speed** and the **curve** (ease-in/out, linear, custom) for the ramp into and
    out of the hold. This is the "shape it zooms to" (lerp) the user asked for.
  - **Timing** — start, hold duration, end; derived from the block's position/length on the lane.
- **Lerp between states.** Between events (and from the un-zoomed base into an event and back), the
  viewport interpolates — position and scale — along the event's easing curve. Overlaps/transitions
  between adjacent events need a defined blend.
- **WYSIWYG preview.** Per the project rule ([`CLAUDE.md`](../CLAUDE.md)), the authored framing shows
  live in the preview as you scrub/play and as you edit an event — the crop path already exists
  (`PreviewSurface.SetViewport`); this feeds it from the authored track instead of `AutoZoom`.

## How it maps onto today's code

- **Keep the render primitive.** `ZoomViewport` + `CursorCompositor`'s crop/bilinear-resample already
  apply a per-frame viewport and keep the cursor glued through zoom. The change is *where the per-frame
  viewport comes from*: an authored **zoom track** → per-frame `ZoomViewport`, replacing
  `AutoZoom.ZoomCurve`/`Viewport` as the source.
- **New Core model (headless, tested):** a `ZoomTrack` = ordered `ZoomEvent`s (target rect/factor +
  centre, start/hold/end, easing), and a resolver `ZoomTrack → ZoomViewport[perFrame]` (the lerp +
  blend logic). Pure, unit-tested like `AutoZoom` is — keyframes → per-frame rect, deterministic.
- **Auto-zoom becomes a generator, not the model.** The existing click-cluster logic can survive as an
  optional **"suggest zoom events from clicks"** action that *emits* `ZoomEvent`s into the lane for the
  user to then tweak — best of both: a quick starting point that's still fully editable. Or it's retired.
- **Export** already runs a `zoomCurve` through the composite pass; swap the curve's source to the
  resolved `ZoomTrack`. Non-destructive re-export stays intact (events are just more sidecar data).
- **Persistence.** Zoom events belong with the clip — extend the `*.track.json` sidecar (or a sibling)
  so authored zooms survive restart and re-export, consistent with the non-destructive model.

## Rough phases (to be refined when we pick this up)

1. **Core model + resolver** — `ZoomEvent`/`ZoomTrack` + `Resolve → ZoomViewport[]` with easing/lerp;
   headless tests (single event ramp, hold, overlap/blend, ride-the-cuts).
2. **Preview-driven authoring** — the zoom lane UI, create/drag/resize events, set the target rect by
   dragging a box on the preview, live WYSIWYG framing while editing.
3. **Export wiring** — resolved track → composite pass; persistence in the sidecar; re-export parity.
4. **(Optional) Auto-suggest** — generate events from click clusters as an editable starting point.

## Open questions

1. **Target shape input** — drag a box on the preview (natural, needs a preview edit mode) vs. numeric
   factor+centre vs. both. Lean: draggable box, with the cursor-follow default as a fallback centre.
2. **Transitions between overlapping/adjacent events** — blend rule when one event's ramp-out meets the
   next's ramp-in.
3. **Cursor size through authored zoom** — today the cursor stays a constant on-screen size through
   zoom; confirm that still holds (it should — same viewport map).
4. **Fate of `AutoZoom`** — retire, or keep as the "suggest events" generator (leaning: keep as generator).
5. **Sidecar shape** — extend `*.track.json` vs. a separate `*.zoom.json`; versioning.

## Out of scope (for this effort)

Pan-only moves without zoom, keyframed cursor-size/opacity, multi-lane effects beyond zoom, motion
blur on the zoom move. Revisit after the core authored-zoom experience lands.

## Interim

Until this ships, the existing automatic `AutoZoom` (on/off + max-zoom in the tuning panel) stays as
the zoom path — it's previewed and exported and works; it's just not directable. No rush to rip it out.
