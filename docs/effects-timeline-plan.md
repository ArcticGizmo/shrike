# Shrike — Unified Effects Timeline (implementation plan)

> Replace the separate zoom lane + mouse-click ticks with **one "effects" timeline** that holds every
> timed effect (zoom, mouse spotlight, click ripple, mouse visibility, and a new drawing **canvas**),
> auto-stacking overlapping effects into rows. Make the ruler a draggable playhead, and give the
> properties pane a permanent home that never reflows the window.

**Status:** Planned · **Date:** 2026-08-18 · **Owner:** Jon
**Supersedes:** the dedicated zoom lane from [`zoom-timeline-editing-plan.md`](zoom-timeline-editing-plan.md)
(zoom becomes one effect *kind* on the unified lane). Builds on the compositor-chain platform.

---

## Decisions locked (from scoping Q&A, 2026-08-18)

1. **One effects lane, auto-stacking rows.** A single "Effects" area under the scrubber; overlapping
   effects flow onto stacked sub-rows, colour-coded by kind. The recorded **mouse-click ticks stay
   pinned as a thin strip at the bottom** of the lane.
2. **All effects are ranged; today's "defaults" become full-length seed effects.** There is no separate
   clip-wide toggle concept any more. "Cursor shown" and "ripples on" are represented as **full-length
   effect blocks** seeded by default (and by migration), which the user can shorten, split, or delete.
3. **Canvas space is per-drawing** — each canvas effect chooses **content-space** (glued to the pixels,
   zooms/moves with the frame) or **screen-space** (fixed overlay on the output frame).
4. **Canvas motion: static now, animated later, with no rewrite.** We adopt the professional-editor
   *layer* model — a rasterized sprite + a per-frame transform (position/scale/rotation/opacity)
   evaluated from animation channels. Static is the one-keyframe degenerate case. This keeps the whole
   existing screenshot annotation renderer (text, arrows, badges, redaction) reusable **and** makes
   later transform-animation purely additive. Only *content* animation (text typing on, freehand
   draw-on reveal) is deferred.
5. **Right-click → Add effect ▸ …** places the effect starting at the clicked source-time.
6. **Ruler shows the playhead and is draggable** to scrub; the filmstrip drag stays too.
7. **Properties pane is always visible**, empty when nothing is selected, and its column is permanently
   reserved so selecting an effect never reflows the editor.

## Guiding rules (carried from the project)

- **WYSIWYG.** Every effect that bakes into the export must show live in the preview, using the same
  inputs (positions, timings, viewport map). ([`CLAUDE.md`](../CLAUDE.md))
- **Core stays UI-free and tested.** New model + resolvers land in `Shrike.Core` with headless tests.
- **Source-time authoring.** Effects are anchored in source time so they ride the cuts, like zoom today.
- **Off means off.** With no effects the export path is byte-for-byte the normal transcode.
- **Non-destructive.** Effects are metadata in `*.edit.json`; the source is never mutated (except
  redaction, which is a deliberate destructive scrub at export, as on screenshots).
- **Snappy-load budget stays green.**

---

## How it maps onto today's code (from the codebase survey)

- **Effect seam:** `IFrameCompositor` + `CompositorChain` (transforms before overlays), driven by
  `FrameCompositePipeline` (single decode→compose→encode). Every new effect is a new compositor
  appended to the chain. Zoom transform + cursor/ripple overlays already live here.
- **Shared framing:** everything maps through the per-frame `ZoomViewport[]` produced by
  `ZoomTrack.Resolve`. Content-space overlays reuse `CursorCompositor.Map(...)` to stay glued through
  zoom; screen-space overlays are appended after the zoom transform and skip the map.
- **Persistence growth point:** `ClipEdit` → `*.edit.json` (currently `SchemaVersion = 1`, carrying
  `Zoom` + `ShowCursor`). It is explicitly documented as the home for new lanes. We bump to **v2** with
  a forgiving v1→v2 migration.
- **Annotation reuse:** the annotation *model* + *geometry* are already UI-free in Core
  (`Annotation`, `AnnotationDocument`, `AnnotationGeometry`, `Redaction`) in image-pixel coords that
  drop 1:1 onto a video BGRA frame. Rasterization is Avalonia-only today (`AnnotationSurface.BuildControl`
  / `RenderFlattened`) — we reuse it to bake the canvas sprite **once**, then blit headlessly per frame.
- **Known duplication (accepted for now):** the preview re-derives compositor math in the Avalonia
  layer (`TimelineEditorWindow` + `PreviewSurface`) rather than running the real compositors, so each
  new effect is authored twice (once in a Core compositor, once in the preview). We keep this dual-path
  pattern per effect; a future "run the real chain into the preview" unification is noted as a stretch,
  not in scope.
- **Timeline UI today:** code-behind + custom-drawn `Control`s (no MVVM). `TimeRuler` (no playhead/no
  interaction), `TimelineStrip` (scrub), `ZoomLane` (zoom blocks + click ticks). The pane is a
  zoom-only inspector, hidden until selection. All of this is what we refactor.

---

## The unified model (Core)

```
EffectEvent (abstract, source-time)
  Id, StartMs, EndMs, EaseInMs, EaseOutMs
  RampAt(sourceMs) -> 0..1   // reuse ZoomEvent's smoothstep envelope

  ZoomEffect       : CenterX, CenterY, Zoom            (wraps today's ZoomEvent fields)
  SpotlightEffect  : Color, Opacity, Radius
  RippleEffect     : (marker range — enables ripples for clicks inside it)
  VisibilityEffect : Visible (bool)  // default seed = full-length Visible=true
  CanvasEffect     : AnnotationDocument, Space{Content|Screen},
                     TransformChannels (pos/scale/rot/opacity; constant today)

EffectTrack = ordered IReadOnlyList<EffectEvent>
  ByKind<T>()               // typed views for resolvers/compositors
  ResolveZoom(...) -> ZoomViewport[]   // ZoomEffects only; same output as ZoomTrack today
```

`ClipEdit` v2 serializes the whole `EffectTrack`. Migration: a v1 doc's `Zoom[]` become `ZoomEffect`s;
its `ShowCursor` becomes a **full-length `VisibilityEffect`** (Visible = ShowCursor). Absent/again-empty
docs seed the defaults for a fresh clip. Load stays forgiving (drop malformed events, never fail open).

---

## Milestones

Each milestone is independently demoable and ends on hard exit criteria (project convention). Rebuild +
relaunch the dev exe on every change (kill running Shrike first).

### ✅ M0 · Unified effect model + persistence  *(Core, headless, tested)*
- ✅ `EffectEvent` hierarchy (`EffectEvent.cs`): abstract base (source-time span + shared eased `RampAt`
  envelope + `ActiveAt` + an `EffectKind` discriminator) with `ZoomEffect`, `SpotlightEffect`,
  `RippleEffect`, `VisibilityEffect`. (`CanvasEffect` deferred to M4 with its annotation persistence.)
- ✅ `EffectTrack` (`EffectTrack.cs`): ordered container, `OfKind<T>()` typed views, `VisibilityAt` /
  `RipplesEnabledAt` lookups, and **`ResolveZoom` that delegates to the existing `ZoomTrack.Resolve`** —
  so authored zoom framing is unchanged by construction.
- ✅ **Forward migration** `ClipEdit.ToEffectTrack(clipDurationMs)`: zoom events → `ZoomEffect`s; the
  clip-wide `ShowCursor` → a single **full-length** `VisibilityEffect` (the editable default seed). The
  on-disk `*.edit.json` format is **kept at v1** for now (no new ranged data to store until M3), so
  existing clips and persistence are untouched — the v1→effects mapping lives (and is tested) in
  `ToEffectTrack`. `ZoomTrack`/`AutoZoom` stay intact underneath; `AutoZoom` dormant.
- ✅ **Tests** (`EffectTrackTests` + extended `ClipEditTests`, 9 new): ordering, `OfKind` filtering,
  envelope-matches-`ZoomEvent`, **zoom-resolve parity (identical `ZoomViewport[]` vs the legacy track)**,
  visibility/ripple range lookups, and the v1-doc → effects migration + full-length visibility seeding.
- ✅ **Exit / demo:** full suite green (**262 passed**); zoom resolves byte-for-byte identically through
  the new model; no UI change; existing `*.edit.json` files load unchanged.

  *(Deferred to when consumed: `FromEffectTrack` / on-disk **v2** format — added in M3 when ranged
  visibility/ripple/spotlight first need persisting. M0 deliberately changes no file format.)*

### M1 · The unified effects lane  *(replaces `ZoomLane` + click ticks)*
- New `EffectsLane` control: auto-stacking rows, colour-coded blocks per kind, **click-tick mouse strip
  pinned at the bottom**, playhead line. Generalize `ZoomLane`'s add/select/drag/resize/snap/min-dur.
- **Right-click context menu → "Add effect ▸ [Zoom · Spotlight · Click ripple · Mouse visibility ·
  Canvas]"**, placing the new effect at the clicked source-time with a sensible default duration; drag
  edges to resize. Keyboard **Delete** and arrow-nudge on the selection.
- Retire `ZoomPanel`/`ZoomLane`; route selection into existing window state.
- **Exit / demo:** place/drag/resize/delete any effect kind on one lane; overlaps stack into rows;
  clicks show at the bottom. Zoom still fully works end-to-end via the new lane (others are inert blocks
  until their milestone).

### M2 · Draggable ruler playhead + permanent properties pane
- `TimeRuler` gains a playhead marker and becomes draggable to scrub (raises `Scrubbing`/`Seeked` like
  the strip); filmstrip drag retained.
- Properties pane: **always visible**, fixed reserved column (no window reflow), **empty state** when
  nothing is selected, content region **swaps per selected effect kind**. Move the existing zoom
  inspector (amount / ease-in / ease-out + delete) into this framework as the first per-kind editor.
- **Exit / demo:** scrub from the ruler; select/deselect a zoom — the pane stays put and swaps content;
  layout never shifts.

### M3 · Mouse effects as ranged effects  *(spotlight · ripple · visibility)*
- **Spotlight:** net-new `SpotlightCompositor : IFrameCompositor` (eased glow under the smoothed cursor
  within range) + matching preview draw in `PreviewSurface`. Pane editor: colour, opacity, radius
  (seed from the leftover `AppSettings.Spotlight*` fields).
- **Ripple:** convert the clip-wide ripple to a ranged `RippleEffect` — `CursorCompositor` enables
  ripples only for clicks inside an active range; preview mirrors. Default full-length seed keeps
  today's behaviour.
- **Visibility:** convert `ShowCursor` to ranged `VisibilityEffect`; within a non-visible range the
  cursor (and its ripples/spotlight) are suppressed. Default full-length "shown" seed.
- **Exit / demo:** hide the cursor for one span, spotlight another, ripple a third — each visible in the
  **preview and the exported file**, tuned from the pane.

### M4 · Canvas effect — static, full tool parity
- **Model:** `CanvasEffect` = `AnnotationDocument` + content/screen `Space` + ease (+ constant transform
  channels reserved for M5).
- **Authoring:** selecting a canvas effect turns the preview into an annotation editing surface — reuse
  `AnnotationSurface` + the full screenshot toolset (rect, ellipse, line/arrow, freehand, highlight,
  text, step badge, redaction), scoped to the frame at the effect's mid-time; toolbox hosted in the pane.
- **Rendering (layer-sprite):** bake the canvas layer to a transparent RGBA sprite via the existing
  Avalonia renderer (`RenderFlattened` onto transparent, no base image), **cached per effect**,
  invalidated on edit. A headless `CanvasCompositor : IFrameCompositor` alpha-blits the sprite per
  active frame with eased alpha; **content-space** maps through `ZoomViewport[]` (composited before/with
  zoom), **screen-space** blits after zoom. **Redaction** is applied as the existing headless
  destructive scrub over the active range (content- or screen-space rect) — irreversible, as on
  screenshots.
- **Exit / demo:** draw box + text + redaction over a range; shows live in the preview and bakes into
  the export; the content/screen toggle behaves through a zoom; redaction leaves no recoverable trace.

### M5 · Transform animation channels  *(deferred, architected-for)*
- Add keyframeable transform channels (position/scale/rotation/opacity) evaluated per frame; the cached
  canvas sprite is blit with the animated transform — **no re-raster**, static stays the single-keyframe
  case. Optionally extend animated params to spotlight/zoom.
- *Content* animation (text typing on, freehand reveal) remains a later, narrower add.
- **Exit / demo:** a canvas layer slides / scales / fades over its range.

---

## Cross-cutting

| Concern | Handling |
|---|---|
| **Migration / versioning** | `ClipEdit` v1→v2 with forgiving load; old clips open unchanged. Tested in M0. |
| **WYSIWYG parity** | Each effect wired into the preview in the same milestone it lands (dual-path, per project rule). |
| **Snappy-load budget** | Guarded each milestone; the lane/pane refactor must not regress editor open time. |
| **Testing discipline** | Model + resolvers + migration + redaction stay headless-tested in Core; Avalonia wiring boot-verified. |
| **AutoZoom** | Stays dormant/tested; not surfaced. Optional future "suggest zoom effects from clicks" generator. |

## Open questions (non-blocking; will resolve in-flight)

1. **Default durations** per effect kind when right-click-adding (zoom ~1.5s today; spotlight/canvas?).
2. **Effect z-order within a row-stack** — draw order for overlapping same-space canvas effects
   (proposed: lane order = composite order, top row last).
3. **Ripple range semantics** — does a ripple range also *suppress* ripples outside it, or is
   "no ripple effect anywhere" the off state? (Proposed: presence enables; the default seed = on.)
4. **Canvas editing at which frame** — edit against the effect's start, mid, or the current playhead if
   inside the range (proposed: current playhead when inside, else start).
5. **Stretch:** unify the preview onto the real compositor chain to kill the dual-path cost.
