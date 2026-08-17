# Shrike — Smooth Cursor (Implementation Plan)

> Milestone-based, phased delivery of the **smooth cursor** feature: record the real pointer,
> smooth it in post, and composite a synthetic cursor (with click feedback) back over the frames —
> on rails that also carry **auto-zoom** later. Concept and rationale live in
> [`smooth-cursor-concept.html`](smooth-cursor-concept.html); this is the build plan.

**Status:** Proposed · **Date:** 2026-08-14 · **Owner:** Jon · **Stack:** Avalonia 12 · .NET 10
**Builds on:** recording (M4) + timeline export (M5), both shipped. **Track:** experimental, post-v1.

## Locked decisions (from review)

| # | Decision |
| --- | --- |
| Compositing | **Option B — managed per-frame pass** (decode → draw → re-encode). Chosen over the ffmpeg-overlay route **because auto-zoom is on the roadmap**: adaptive, eased zoom/pan is natural to compute per frame in code and brittle to express as ffmpeg filter expressions. One foundation, no mid-feature rewrite. |
| Smoothing | **One-Euro filter** — adaptive low-pass (heavy smoothing when slow, low lag when fast), resampled to frame times. |
| When | **Export-time and non-destructive.** The source MP4 + the raw input track are the truth; smoothing/effects/zoom are re-runnable with different settings and never mutate the source. |
| Capture | Log the input track with a **low-level mouse hook** (`WH_MOUSE_LL`); when the mode is on, **don't draw the real cursor** (reuse the hide-cursor path) so the frames stay a clean plate. |
| Default | **Experimental, off by default.** When off, the export path is exactly today's — no decode/re-encode overhead, no behaviour change. |
| MVP boundary | **SC4** = track + One-Euro + one synthetic cursor + click ripple. Auto-zoom (**SC5**) and any tuning UI are explicitly later, on the same pipeline. |

## Guiding rules for every milestone

- **Clean plate is the enabler.** When smooth-cursor is on, the recording must contain **no baked cursor** — we log the path and draw it back in post. This is the hide-cursor toggle turned inside out; reuse it, don't reinvent it.
- **Non-destructive, always.** Every pixel effect is an export-time re-encode from the untouched source + track. Nothing is baked at capture beyond the (cursor-free) video and the sidecar track.
- **Core stays UI-free and tested.** The correctness-sensitive parts — track model, One-Euro, coordinate mapping, zoom curves — live in `Shrike.Core` with headless tests. `Shrike.App` is the thin shell (the hook + wiring).
- **Snappy-load budget stays green.** The mouse hook is lightweight and only armed during a recording; the feature adds nothing to the cold path. The M0 startup-budget gate must stay green.
- **Off means off.** With the experimental flag disabled, `VideoExporter`'s existing path is untouched — no managed frame pass, no generation loss, no slowdown.
- **Every milestone ships a demo.** If it can't be shown end-to-end (or, for headless core, proven by a test), it isn't a milestone boundary.

---

## Phasing at a glance

| Phase | Milestones | Outcome |
| --- | --- | --- |
| **1 · Record the motion** | SC1 | Recording also emits a timestamped mouse/click track aligned to frame time. *(The one part that can't be retrofitted — it must be captured live.)* |
| **2 · Smooth cursor MVP** | SC2 · SC3 · SC4 | Record → export → a smoothed synthetic cursor with click ripples, correctly placed. **First demoable feature; experimental toggle.** |
| **3 · Auto-zoom** | SC5 | Framing eases toward click clusters and back, on the same compositing pipeline. |

> **Ship gate at end of SC4.** The smooth-cursor MVP is a complete, shippable experimental feature on its own. SC5 (zoom) is purely additive on the same rails — the whole reason Option B was chosen.

---

## Phase 1 — Record the motion

### SC1 · Input-track capture ✅
*The only irreversible half: if the track isn't logged during the take, it can't be reconstructed later.*

**Build**
- **Track model (`Shrike.Core`)**: `MouseTrack` — an ordered list of samples `(tMs, x, y)` plus button events `(tMs, button, down/up)`, in **virtual-screen physical pixels**. Serialisable to a compact JSON sidecar. Headless-testable; no UI or Win32 deps in the model itself.
- **Recorder-clock alignment**: timestamps are stamped on the **recorder's own monotonic clock** (the same `Stopwatch` the `Recorder` paces from) and **exclude paused spans**, so a sample at `tMs` maps to the frame at `tMs`. Pause/resume must gate track logging exactly as it gates frames.
- **Low-level hook (`Shrike.App/Native`)**: a `WH_MOUSE_LL` hook capturing `WM_MOUSEMOVE` + button down/up, timestamped and forwarded to the track. Armed on record-start, torn down on stop/discard. Cheap, non-blocking callback (enqueue + return).
- **Wiring**: a per-recording **Smooth cursor** toggle in the recording HUD (beside Hide cursor / Spotlight, marked experimental) gates it. When on, `CaptureController` (a) forces the real cursor **off** in the recording (reuse `CursorInRecording=false`), (b) starts the track recorder alongside the `Recorder`, and (c) writes the finished track as a **`*.track.json` sidecar next to the MP4** and hands it to the `RecordingSource`.

**Exit criteria**
- Recording a region produces the MP4 **and** a track sidecar; the video contains no baked cursor.
- Track timestamps line up with frame times **including across a pause/resume** (verified: a synthesised record-with-pause leaves no time drift between track and frames).
- `MouseTrack` model + (de)serialisation are covered by headless tests; hook lifecycle is boot-verified.

**Risks/notes:** low-level hooks can be throttled if the callback is slow (keep it light) and may interact with anti-cheat/secure-desktop contexts — degrade gracefully (no track → feature simply unavailable for that clip, normal export still works).

> **SC1 complete (2026-08-14).** `MouseTrack` + `MouseTrackRecorder` (Core; each event stamped via `Recorder.CaptureTimeMs`, which returns null while paused so paused spans drop out), a `WH_MOUSE_LL` `MouseHook` (App), and the experimental **Smooth cursor** HUD toggle wired through `CaptureController` — it forces a clean plate (real cursor off), turns the live spotlight off, logs the track, and writes a `*.track.json` sidecar next to the MP4. Tests: `MouseTrackTests` (JSON round-trip incl. empty + malformed, capture-clock stamping, pause-exclusion, region carry). The native hook path was verified with a message-pump probe (`moves`+`clicks` delivered); the interactive record→sidecar round-trip is ready to exercise live. Next: **SC2** (One-Euro smoothing + coordinate mapping), which consumes this sidecar.
>
> **Storage (2026-08-17).** Recordings + their sidecars moved off `%TEMP%` (which the OS can purge) to a stable per-profile working folder, `%LOCALAPPDATA%\Shrike\recordings` (dev = `Shrike (Dev)`), via the new `AppStorage` helper — so a clip survives to be edited / re-exported. A **Debug-only** tray item, *Open working folder (debug)*, opens it for inspection. *(Follow-up: these accumulate; a size/age cap is worth adding later.)*

---

## Phase 2 — Smooth cursor MVP

### SC2 · Smoothing + coordinate mapping (headless core)
*The correctness-sensitive heart — pure, tested, no pixels yet.*

**Build**
- **One-Euro filter (`Shrike.Core`)**: `OneEuroFilter` (min-cutoff, beta, derivative-cutoff) applied to the x/y series; **resample** the track to the export frame grid (fps + kept ranges from the `Timeline`), so there is exactly one smoothed position per output frame.
- **Coordinate mapping (`Shrike.Core`)**: map each sample from virtual-screen physical px → **region-local** (account for the recorder's even-dimension trim) → **export space** (any downscale), for arbitrary region offset, multi-monitor and mixed-DPI origins. This is the fiddliest correctness area and gets dedicated tests.
- **Timeline-aware**: positions follow the kept ranges (cuts remove the corresponding track spans), so smoothing lines up with the edited output, not the raw source.

**Exit criteria**
- Given a synthetic jittery track, the filter yields a smoothed, frame-aligned position series; smoothing strength behaves monotonically.
- Mapping is verified against representative cases: region offset, odd→even trim, 2× downscale, and a non-zero-origin / scaled monitor — each asserts the smoothed point lands on the expected export pixel.
- All headless; no dependency on the compositing pipeline.

### SC3 · Compositing pipeline (the Option-B rails)
*The foundational render pass, proven with a no-op compositor before any cursor is drawn.*

**Build**
- **Frame pipeline (`Shrike.Core`)**: decode the source to raw BGRA via ffmpeg (`rawvideo` out), feed frames through an `IFrameCompositor`, and pipe back into ffmpeg to encode — reusing the existing raw-BGRA-into-ffmpeg pattern from `FfmpegMp4Encoder`, now with the decode side added. Start with an **identity compositor** to prove the round-trip stays in sync and lossless-enough.
- **Export integration**: when the feature is on, this managed pass slots into the export flow **ahead of** the normal encode (or replaces the straight transcode), driven from the same `ExportCommand`/`VideoExporter` surface with progress + cancel. When off, the existing path runs unchanged.
- **Quality**: operate from the high-quality source in a single decode→composite→encode so we don't stack generation loss; keep the intermediate at the export's target quality.

**Exit criteria**
- A recording round-trips decode → identity → encode with matching duration/frame count and no visible degradation; progress + cancel work.
- With the flag **off**, export output is byte-for-byte the current path (no regression).
- Throughput is acceptable on a representative clip (measured, not asserted-tight yet).

### SC4 · Draw the cursor + click feedback  ·  **MVP**
*The payoff: the smoothed synthetic cursor and a click you can see.*

**Build**
- **Cursor compositor (`Shrike.Core`)**: an `IFrameCompositor` that, per frame, samples the SC2 position and draws a **synthetic cursor** (bundled vector asset, correct hotspot) plus active **click ripples** (an expanding ring for ~350 ms at each logged click); optional press **punch** (brief scale-down) behind a constant, off by default for the MVP.
- **Cursor asset**: a crisp bundled cursor (not the OS arrow), rendered at the export resolution.
- **App wiring**: the **Smooth cursor** HUD toggle from SC1 gates capture; at export, SC3+SC4 run automatically whenever a track sidecar is present. No separate setting for the MVP.

**Exit criteria**
- Record with smooth-cursor mode → export → the output shows a **smoothed synthetic cursor** tracking the real path, with a **ripple on each click**, correctly positioned across the region/trim/downscale cases from SC2.
- Feature is fully behind the experimental flag; with it off, nothing changes.
- Cursor rendering + ripple timing covered where headless-testable (position → pixel; ripple lifetime); the end-to-end record→export is boot/demo-verified.

> **SC4 complete → smooth-cursor MVP is shippable (experimental).** Everything below is additive on the same pipeline.

---

## Phase 3 — Auto-zoom

### SC5 · Adaptive auto-zoom (+ tuning surfaces)
*The reason Option B was chosen — eased zoom/pan toward click activity, computed per frame.*

**Build**
- **Zoom track (`Shrike.Core`)**: derive zoom/pan keyframes from **click clusters** (and optionally dwell), producing an **eased crop rectangle per frame** (ease-in toward activity, hold, ease-out). Pure and tested.
- **Apply in the SC3 pass**: crop + scale each frame by the per-frame rectangle; draw the cursor in the correct space so it **scales with the zoom** and stays glued to the pointer.
- **Tuning (`Shrike.App`)**: surface controls in the timeline editor — smoothing strength, zoom on/off + intensity, cursor size, ripple toggle — all non-destructive re-exports.

**Exit criteria**
- Framing eases toward click clusters and back out; motion is smooth and the cursor stays correctly placed **and scaled** throughout.
- Zoom curves are tunable and reproducible; a given track + settings always yields the same framing.
- Zoom parameters covered by headless tests (cluster → keyframes → per-frame rect); the visual result is demo-verified.

---

## Dependencies & sequencing

```
SC1 ──▶ SC2 ──▶ SC3 ──▶ SC4  (MVP)  ──▶ SC5 (zoom)
 │               ▲
 └── track ──────┘  (SC3 can be built against a recorded track from SC1)
```

- **SC1 gates everything** — no track, no feature; and it's the only part that must happen at capture time, so it lands first.
- **SC2 and SC3 are independent of each other** (smoothing math vs. the render pipeline) and can proceed in parallel once SC1 provides a real track; **SC4 needs both**.
- **SC5 depends only on SC3+SC4** — it adds a per-frame transform to the same compositor and reuses the click data from SC1.

## Risks & open questions

| Risk / question | Mitigation / lean |
| --- | --- |
| **Coordinate mapping** across region trim, downscale, multi-monitor, mixed-DPI. | Isolate as a pure, heavily-tested Core function (SC2); enumerate the awkward cases as tests before wiring pixels. |
| **Timing / pause alignment** between track and frames. | Stamp on the recorder's own paused-excluding clock (SC1); assert zero drift with a synthesised paused recording. |
| **Generation loss / perf** of decode → re-encode. | Single decode→composite→encode from the HQ source (SC3); measure throughput; prefer a hardware encoder as export already does. |
| **Low-level hook** overhead / secure-desktop / anti-cheat interference. | Enqueue-only callback; arm only during recording; degrade gracefully to "no track → normal export" if the hook can't run. |
| **Cursor fidelity** — bundled vector vs. capturing the real cursor bitmap. | **Resolved:** bundled vector cursor for the MVP (consistent, polished, sharp at any scale); capturing the live cursor incl. shape changes is backlog. |
| **Where the tuning UI lives** and when. | Timeline editor, at SC5. MVP (SC4) ships with fixed sensible defaults and just an on/off. |
| **Live preview** of smoothing during recording. | Out of scope — post-only. A latency-buffered live preview is a possible future, not MVP. |

## Explicitly out of scope (backlog)

Live/real-time smoothed preview · cursor themes & per-user cursors · click **sound**/keystroke overlays · motion-blur on fast moves · smoothing applied to **screenshots** · exporting the track for external editors. Revisit after the MVP sees real demo use.

## Pre-SC1 confirmations (resolved 2026-08-14)

1. **Experimental surfacing** — ✅ **Per-recording HUD toggle.** A **Smooth cursor** toggle in the recording bar, alongside Hide cursor / Spotlight (marked experimental). It must be set at capture (the track is logged live), so the HUD is its natural home; smoothing then applies automatically at export. No separate settings flag for the MVP.
2. **Sidecar vs. in-memory track** — ✅ **Sidecar.** A tiny `*.track.json` next to the MP4, so a clip survives an app restart and can be re-exported later with different smoothing/zoom settings — consistent with the non-destructive export model.
3. **Cursor asset** — ✅ **Bundled vector cursor** for the MVP: one crisp, hotspot-correct pointer, sharp at any scale. Capturing the user's real cursor (incl. shape changes) stays on the backlog as a follow-up.
