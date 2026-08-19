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

### ✅ M1 · The unified effects lane  *(replaces `ZoomLane` + click ticks)*
- ✅ New `EffectsLane` control (`EffectsLane.cs`): **auto-stacking rows** (greedy first-free-row per block,
  lane height grows with the stack), colour-coded blocks per kind, **click-tick mouse strip pinned at the
  bottom**, playhead line. Generalised the old lane's add/select/drag/resize/snap/min-dur; timing edits use
  polymorphic `record with { StartMs, EndMs }` so one path moves any kind.
- ✅ **Right-click menu → Zoom · Spotlight · Click ripple · Mouse visibility · Canvas**, placing at the
  clicked source-time with per-kind default durations; drag to move / resize; double-click still quick-adds
  a zoom; right-click on a block also offers **Delete**. **Keyboard Delete** and **←/→ nudge** (Shift =
  larger step) on the selection. The **+ Add effect** button opens the same menu at the playhead.
- ✅ Retired `ZoomLane`; window rewired from `List<ZoomEvent>` to the unified `List<EffectEvent>` (zoom
  resolves via `OfKind<ZoomEffect>()`), preview/inspector/aim-box are zoom-only (other kinds have no editor
  until their milestone). Added a minimal `CanvasEffect`/`CanvasSpace` so Canvas is placeable now.
- ✅ **Exit / demo:** any kind can be placed/dragged/resized/deleted on one lane; overlaps stack into rows;
  clicks show at the bottom; **zoom works end-to-end** (authoring, preview, export, persistence) exactly as
  before. Build- and boot-verified; full suite **262 passed**.

  *(Zoom still persists via the v1 edit doc; other kinds are **session-only** until the v2 format lands in
  M3. Interactive multi-kind pass pending, like the codebase's existing Avalonia-wiring convention.)*

### ✅ M2 · Draggable ruler playhead + permanent properties pane
- ✅ `TimeRuler` now carries the **playhead** (amber line + a downward tab) and is **draggable to scrub**
  (raises `Scrubbing`/`Seeked` like the filmstrip; hand cursor); the strip drag is retained. Window feeds
  the ruler's playhead from both scrub and playback.
- ✅ Properties pane is **always visible** for a track-carrying clip (fixed 248px column), so selecting an
  effect never widens the window / reflows the editor. Content **swaps by selection**: an **empty-state**
  hint when nothing's selected, the **zoom inspector** (amount / ease-in / ease-out) for a zoom, and a
  "timing/delete only" note + **Delete** for kinds without an editor yet. Header names the selected kind.
- ✅ **Exit / demo:** scrub from the ruler; select/deselect any effect — the pane stays put and only its
  content changes; layout never shifts. Build clean, **262 passed**.

### ✅ M3 · Mouse effects as ranged effects  *(spotlight · ripple · visibility)*
- ✅ **Spotlight:** net-new `SpotlightCompositor : IFrameCompositor` (soft radial glow under the smoothed
  cursor, eased alpha, mapped through the shared viewports) + matching `PreviewSurface.SetSpotlight`
  radial-gradient draw. Pane editor: colour (hex), opacity, radius; new-block defaults seed from
  `AppSettings.Spotlight*`.
- ✅ **Ripple:** `CursorCompositor` gained an optional per-frame `ripplesEnabled` mask; a `RippleEffect`
  span enables ripples for clicks inside it. Preview mirrors via `RipplesEnabledAt`. Default full-length
  ripple block seeded (from `CursorRippleEnabled`) so today's behaviour is preserved.
- ✅ **Visibility:** `CursorCompositor` gained an optional per-frame `cursorVisible` mask; a
  `VisibilityEffect(Visible=false)` span hides the cursor (ripples belong to their own range). Default
  full-length "shown" block seeded (migrated from v1 `ShowCursor`). Pane editor: a shown/hidden toggle.
- ✅ **Resolvers** on `EffectTrack` (`ResolveCursorVisible` / `ResolveRipplesEnabled` / `ResolveSpotlight`
  + `SpotlightAt`, `VisibilityAt`, `RipplesEnabledAt`, hex parse) — pure, headless-tested; the preview and
  export share them so WYSIWYG holds.
- ✅ **Persistence bumped to v2**: `ClipEdit` now stores the whole `EffectTrack` (zoom + visibility + ripple
  + spotlight); v1 docs still read and migrate; the capture-time default writer stays v1. Export takes the
  effect track (`ConfigureEffects`) and builds the chain **zoom → spotlight → cursor(+masks)**.
- ✅ The global **Show / Ripple** checkboxes were removed from the cursor panel — they're timeline effects
  now (Smoothness + Size remain global tuning).
- ✅ **Exit / demo:** hide the cursor for one span, spotlight another, ripple a third — each resolves in
  the preview and the export from the same track, tuned from the pane. Build clean; **271 passed**.

  *(Canvas blocks are still not serialised — that lands with M4's annotation payload; a placed canvas is
  session-only until then.)*

### ✅ M4 · Canvas effect — static, full tool parity
- ✅ **Model:** `CanvasEffect` carries an immutable `IReadOnlyList<Annotation>` (source-frame pixels) +
  content/screen `Space` + ease. `AnnotationJson` (every type, forgiving) persists it in the v2 edit doc.
- ✅ **Authoring (inline, chosen UX):** selecting a canvas effect shows a canvas editor in the pane (space
  toggle + **Edit drawing**). Editing overlays a real `AnnotationSurface` on the preview, backed by the
  frame at the **playhead-if-inside-else-start**, with the full screenshot toolset (box, ellipse, arrow,
  line, pen, highlight, text, badge, redaction) + colour swatches; Delete removes a selection, Esc
  finishes. Edits commit live to the effect.
- ✅ **Rendering (layer-sprite):** `AnnotationSurface.RenderAnnotationLayer(w,h)` bakes the drawing to a
  transparent **premultiplied** BGRA sprite via the existing renderer (text/arrows/redaction all free). A
  headless `CanvasCompositor : IFrameCompositor` alpha-blits it per active frame; **content-space** is
  composited **before** the zoom transform (magnifies with the content), **screen-space** **after** every
  overlay (fixed). Redaction bakes as opaque black; canvas defaults to a **hard cut** so redaction stays
  fully opaque for its whole span.
- ✅ **Preview WYSIWYG:** `PreviewSurface.SetCanvasLayers` composites cached layer bitmaps over the frame —
  content rides the zoom crop, screen is fixed — so scrubbing shows the drawing; while editing, the
  annotation surface is the live view.
- ✅ **Tests:** annotation-JSON round-trip (every type), canvas compositor blit + envelope, v2 canvas
  persistence. Build clean; **276 passed**.
- **Exit / demo:** draw a box + text + redaction over a range; shows in the preview and bakes into the
  export; content/screen behaves through a zoom; redaction is opaque in the output. *(Interactive draw
  pass pending, per the Avalonia-wiring convention.)*

### ✅ M5 · Transform animation channels
- ✅ **Model** (`CanvasAnimation.cs`): five keyframe channels (translate x/y, scale, rotation, opacity) in
  the layer's **local** time, sampled with hold-at-ends + smoothstep between keys. `LayerTransform` is the
  resolved per-frame transform; `CanvasAnimation.Identity` (all empty) reproduces a static layer exactly, so
  animation is **purely additive**. `CanvasEffect.Animation` carries it; `EffectTrack.ResolveCanvasTransforms`
  folds the eased envelope into per-frame opacity.
- ✅ **Compositor**: `CanvasCompositor` blits under the per-frame `LayerTransform` — a **static layer**
  (identity geometry) keeps the cheap straight blit; an **animated** one takes a bilinear **affine** resample
  (inverse-mapped, scale/rotate about the frame centre). The cached sprite is **never re-rasterised**.
- ✅ **Authoring**: a preset dropdown in the canvas pane (**Fade · Slide from left/right/bottom · Pop**)
  populates the keyframe channels; "None" clears to static. Presets seed the full model — a per-key editor
  is the natural next refinement.
- ✅ **Preview WYSIWYG**: `PreviewSurface` applies the same per-frame transform (scale/rotate about the drawn
  frame centre + mapped translate + opacity) to the canvas layer, so scrubbing shows the animation.
- ✅ **Persistence**: the animation channels round-trip in the v2 edit doc.
- ✅ **Tests**: keyframe sampling, resolve identity-vs-animated, affine translate + opacity blit, preset
  generation, animation persistence. Build clean; **280 passed**.
- *Deferred (narrower, later):* a per-key timeline editor; *content* animation (text typing on, freehand
  reveal); animated params on spotlight/zoom (the same channel model extends to them).
- **Exit / demo:** a canvas layer slides / scales / fades over its range, in the preview and the export.

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
