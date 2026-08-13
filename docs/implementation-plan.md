# Shrike — Implementation Plan

> Milestone-based, phased delivery of the design in [`design.md`](design.md).
> Each milestone is independently demoable and ends on hard exit criteria.

**Status:** Approved 2026-08-12 · **Owner:** Jon · **Stack:** Avalonia 12 · .NET 10 (sibling to `sprig`)

## Locked decisions (from review)

| # | Decision |
| --- | --- |
| Autostart | **Opt-in** via a settings toggle; off by default. |
| Recent ring | **Memory-only**, cleared on quit. Disk persistence deferred. |
| Hotkeys | **Fully rebindable**; ship `Alt+Shift+…` defaults (e.g. `Alt+Shift+Q` region). |
| CLI | **Deferred** — no `Shrike.Cli` in v1. |
| Redaction | **True, pixel-destroying** redaction on export. |
| Audio | **Deferred** — v1 records video only. |

## Guiding rules for every milestone

- **Snappy-load budget is a gate, not a goal.** The startup-budget test (overlay <100 ms, editor <250 ms) lands in M0 and must stay green in CI through every later milestone. A milestone that regresses it isn't done.
- **Never switch virtual desktops.** Any window shown obeys the current-desktop rule (§5 of the design).
- **Core stays UI-free.** Logic that can be tested headless lives in `Shrike.Core` with tests; `Shrike.App` is the thin Avalonia shell.
- **Every milestone ships a demo.** If it can't be shown working end-to-end, it's not a milestone boundary.

---

## Phasing at a glance

| Phase | Milestones | Outcome |
| --- | --- | --- |
| **A · Foundation** | M0 | Tray app that proves snappy-load + no-desktop-switch on a stub. |
| **B · Screenshots (MVP)** | M1 · M2 · M3 | A genuinely useful screenshot tool: capture → annotate → redact → copy/save, with the recent ring. **First release candidate.** |
| **C · Recording** | M4 · M5 | Region recording with timeline trimming and footprint-tuned export. |
| **D · Ship** | M6 | Settings, packaging, autostart, updates, perf gate — installable v1. |

> **Ship gate at end of Phase B.** Phase B is a complete product on its own. It can go out as **v0.x (screenshots only)** while Phase C is built — de-risking the release and getting the tool into daily use early.

---

## Phase A — Foundation

### M0 · Skeleton & the snappy-load harness
*The whole premise (tray-resident, sub-100ms, no desktop teleport) is de-risked here, before any feature work.*

**Build**
- Solution scaffold: `shrike.slnx`, `Shrike.Core`, `Shrike.App`, `Shrike.Tests`. Versions pinned to sprig's (Avalonia 12.0.5, .NET 10, CommunityToolkit.Mvvm, Fluent + Inter, compiled bindings).
- App boots **straight to a tray icon** (`TrayIcon` + `NativeMenu`) — no main window at startup.
- **Single-instance guard**: mutex + named-pipe; a second launch signals the resident instance instead of cold-starting.
- **Global hotkey** registration (Win32 `RegisterHotKey`) wired to a stub action.
- **Stub overlay**: a transparent, borderless, topmost window **born on the current desktop** each invocation (proves the no-switch rule with `IVirtualDesktopManager`).
- **Startup-budget test harness**: measures time-to-tray-ready and time-to-overlay; asserts against the budget. Wired into CI. (Mirrors sprig's headless-render discipline.)
- Win32 interop foundation (P/Invoke or CsWin32) established for hotkeys + virtual-desktop COM.

**Exit criteria**
- Hotkey pops the stub overlay on the **current** desktop in **<100 ms**; no desktop switch, ever.
- Only one instance runs; second launch re-signals the first.
- Startup-budget test is green in CI.

**Risks/notes:** `IVirtualDesktopManager` fragility — restrict to the documented surface; feature-flag with graceful degradation. This is the milestone where that risk is proven out.

---

## Phase B — Screenshots (the MVP)

### M1 · Capture → editor → output (the round-trip)
*The core value: a fast, correct screenshot that lands where you want it.*

**Build**
- **Capture (`Shrike.Core`)**: region, window, monitor, full-desktop via **Windows.Graphics.Capture** (GDI `BitBlt` fallback). Correct DWM composition for window grabs. Multi-monitor / DPI-aware.
- **Selection overlay (`Shrike.App`)**: crosshair, drag-rectangle, **window snap-highlight** on hover, magnifier/loupe, per-monitor overlay windows. Escape to cancel.
- **Delay timer** (3s / 5s / custom) for menus and hover states.
- **Editor window**: constructed once, then **hidden and reused** (pre-JITted) so re-opens are instant; shows the captured bitmap. Obeys the desktop rule (follow-me / new-window).
- **Output**: copy to clipboard as **PNG + DIB** simultaneously; save to file (PNG / JPG / WebP) with quality and a filename template (`shrike-{yyyyMMdd-HHmmss}`), remembered folder.
- Conveniences: copy file path, open folder.

**Exit criteria**
- Full round-trip: hotkey → region select → editor (**<250 ms**) → clipboard **and** file — with **zero desktop switch**.
- Clipboard image pastes correctly into Slack, Teams, Office and a browser.
- Window-mode capture is pixel-correct incl. rounded corners / shadows.

### M2 · Annotation model + toolbox
*The differentiator over a raw grab.*

**Build**
- **Annotation document (`Shrike.Core`)**: ordered, non-destructive vector layers; `Undo`/`Redo` stack. Headless-testable.
- **Tools (`Shrike.App`)**: select/move, arrow, line, rectangle, ellipse, freehand scribble, text, highlighter, **step counter** badges, crop. Context bar for colour / stroke width / fill / font size.
- **True redaction**: an authoring region that, **on export, irreversibly overwrites the underlying pixels** (solid fill) — no recoverable original. In-editor it shows as an opaque block; the destructive flatten happens at export only.
- **Composite/flatten pipeline**: render annotations over the base bitmap on copy/save; the exported artefact carries no recoverable redacted content.

**Exit criteria**
- Annotate → undo/redo → export flattened image (clipboard + file).
- A redacted-then-exported PNG contains **no trace** of the covered pixels (verified by test: sample the region, assert it's the fill colour).
- Snappy-load budget still green.

**Remaining M2 chunks (after the draw/undo/redo/redact first cut):**
- ✅ **Editor zoom / unzoom** — fit / +/− (1.25× steps) / 100%, Ctrl+wheel zoom-to-cursor, Ctrl+ +/-/0/1 shortcuts; canvas scrolls when zoomed past the viewport. Annotations stay in image-pixel coords so they're zoom-independent.
- ✅ **Text tool** (click to drop an in-place editor; Enter/click-away commits, Esc cancels) and **step-number badges** (click to place; numbers derive from the document so undo/redo renumber automatically).
- ✅ **Select/move** existing annotations — Select tool hit-tests topmost, drag to move (single undo per gesture via `BeginInteractive`/`ReplaceLive`), dashed selection box, move-cursor on hover, Delete/Backspace to remove. Pure geometry (bounds/hit-test/translate) lives in `Shrike.Core.AnnotationGeometry` with tests.
- ✅ **Crop** — non-destructive export rectangle: drag with the Crop tool to set it (tiny drag clears), editor masks the discarded area with a bright keep-border, size readout shows "(cropped)". Applied last in export (flatten → redact → crop), so redaction coordinates stay correct.

> **M2 toolbox complete.** All annotation tools, undo/redo, destructive redaction, zoom, and crop are in. ✅ M3 (recent-captures ring) is now done too — see below.

### M3 · Recent-captures ring ✅
*"Show me the last few and let me re-copy" — memory-only.*

**Build**
- ✅ **Ring (`Shrike.Core`)**: `RecentRing` — bounded in-memory list of the last **N** (configurable, default 10) captures + thumbnails; total-bytes cap (default 512 MB) with newest-first eviction that always keeps the most recent shot; `Changed` event; **cleared on quit** (no disk spill in v1). Thumbnails via a headless box-average `Thumbnail.Downscale`.
- ✅ **Surfaces (`Shrike.App`)**: tray **Recent** flyout (thumbnail-iconed submenu per capture → copy / open in editor / delete, plus **Clear recent**) + a thumbnail **filmstrip** along the bottom of the editor (click to re-open; right-click context menu → copy / save / delete). Strip hides when the ring is empty.

**Exit criteria**
- ✅ After several captures, the last N are re-copyable from the tray **without re-capturing**.
- ✅ Ring respects both the count cap and the byte cap; memory does not grow unbounded; state is gone after quit. *(Covered by `RecentRingTests` / `ThumbnailTests`.)*

> **M3 complete.** Phase B (the screenshot MVP) is feature-complete.
>
> **→ Release candidate: Shrike v0.x (screenshots).** Phase B is shippable here. Next: **M4** (screen recording + HUD) begins Phase C.

---

## Phase C — Recording

### M4 · Screen recording + HUD
*Reliable region recording to a playable file.* Delivered in three chunks (like M2).

**Build**
- **Recorder (`Shrike.Core`)**: ~~WGC frame source → Media Foundation H.264 encoder~~ **FFmpeg-backed H.264 encoder** (see decision below) on a background thread, writing an MP4 to a temp source file. Frame pacing / drop handling. Optional cursor capture. **No audio** (deferred).
- **Region selection**: reuse the M1 overlay to pick a region / window / monitor.
- **HUD (`Shrike.App`)**: small floating bar — elapsed time, pause/resume, stop, discard. On stop, hands the source to the timeline editor (M5).

**Exit criteria**
- Record a region and produce a **playable MP4**.
- Sustains the target fps at 1080p without runaway frame drops (throughput smoke test).
- Recording, HUD and overlay all obey the desktop rule.

**Chunks**
- ✅ **M4.1 — recording pipeline core.** `IFrameEncoder` + `FfmpegMp4Encoder` (pipes top-down BGRA to `ffmpeg` stdin → libx264 MP4; stderr drained off-thread), an `Ffmpeg` locator (env override → bundled → winget shim → PATH, graceful when absent), and a headless `RecordingSession` (constant-fps pacing, duplicate-on-slow-capture, pause excludes time, discard/stop state machine). Tests: encoder round-trip + 1080p throughput smoke (skip when no ffmpeg), `RecordingSession` pacing/pause/state.
- ⬜ **M4.2 — WGC frame source.** `Windows.Graphics.Capture` feeding real frames to the encoder (needs the Windows-SDK projection / target-framework bump).
- ⬜ **M4.3 — HUD + region wiring.** Reuse the M1 overlay to pick region/window/monitor; floating HUD (elapsed, pause/resume, stop, discard); desktop-rule compliance; hand the MP4 to the timeline (M5) on stop.

> **Encoder decision (2026-08-13):** the locked "no-dependency MP4 via built-in Media Foundation" plan was **abandoned** for the encoder. A hand-rolled MF `IMFSinkWriter` was implemented and proven correct (all attribute readbacks matched), but this dev machine's H.264 encoder MFT rejects a textbook-valid output type (`MF_E_ATTRIBUTENOTFOUND`), and the sink writer fails downstream (`MF_E_INVALIDMEDIATYPE`) — reproduced across every input format, profile, resolution, and an exact replica of a known-working sample. Rather than ship an encoder that silently fails on some machines, Shrike now encodes MP4 via **FFmpeg** (the design already contemplated an optional FFmpeg backend for GIF/WebP in M5 — this widens it to the MP4 path). Cost: a bundled `ffmpeg.exe` dependency (packaged in M6); benefit: robust, portable encoding. The MF encoder + NV12 converter were removed.

### M5 · Timeline trimming + export presets
*Quick edits and the footprint dial — the "Slack-small" lever.*

**Build**
- **Timeline model (`Shrike.Core`)**: segment list over the source; non-destructive **cut / keep / restore**; playback maps to joined kept ranges; source untouched until export.
- **Timeline editor (`Shrike.App`)**: preview player, thumbnail filmstrip scrubber, segment lanes, set in/out, cut a middle section (split), delete/restore a segment.
- **Export (`Shrike.Core`)**: named **presets** — Slack-small (H.264 720p, capped fps/CRF), Balanced (1080p), High (source/HEVC), GIF/WebP (via **optional FFmpeg backend**; MP4 path uses built-in MF, no extra dependency). Controls for resolution scale / fps cap / codec / quality.
- **Live size estimate** that updates as preset and trims change.
- Output: **copy-as-file** to clipboard (paste into Slack/Explorer) or save to disk.

**Exit criteria**
- Trim a recording (cut ≥1 section), export at a chosen preset; result plays and matches the kept segments.
- **Slack-small** preset produces a genuinely small file (target: a short clip in the low hundreds of KB); the pre-export size estimate is within a sensible tolerance of the actual output.
- GIF/WebP export works when the FFmpeg backend is present, and degrades with a clear message when it isn't.

---

## Phase D — Ship

### M6 · Settings, packaging, polish, release
*Make it installable, updatable and configurable.*

**Build**
- **Settings window**: rebind all hotkeys; **desktop behaviour** (follow-me / new-window-here); ring size; default save dir + format; **autostart-at-login toggle (opt-in, off by default)**; cursor-in-recording toggle. Finalise the `Alt+Shift+…` default set.
- **Packaging**: Velopack install/update (mirror sprig's lifecycle hooks); autostart registration driven by the opt-in toggle only; `ReadyToRun`/trimming on the cold path.
- **About / changelog** viewer (embed `CHANGELOG.md`, sprig-style).
- **Perf & QA pass**: CI startup-budget gate enforced; memory-ceiling test for the ring; encoder throughput test; clipboard matrix (Slack/Teams/Office/browser); multi-monitor + mixed-DPI sweep.
- Icon/branding (amber shrike mark), first-run experience.

**Exit criteria**
- Clean install → tray → capture → annotate → record → trim → export, all within budget.
- Update flow works via Velopack; autostart only when the user opts in.
- All performance gates green in CI. **v1.0 tag.**

---

## Dependencies & sequencing

```
M0 ──▶ M1 ──▶ M2 ──▶ M3 ─┐
              │          ├──▶ M6 ──▶ v1.0
              └▶ M4 ──▶ M5 ┘
```

- **M0 gates everything** — it establishes the tray host, interop, and the budget test.
- **M2 and M3 both depend only on M1** (annotation and the ring are independent of each other) and can be built in parallel if desired.
- **M4 depends on M1** (reuses the selection overlay) but is independent of M2/M3 — recording can start as soon as capture exists.
- **M6 depends on all** — it's the packaging/settings convergence point.
- Redaction's destructive-flatten (M2) and the export pipelines (M2 images, M5 video) are the two correctness-sensitive areas that carry dedicated tests.

## Cross-cutting workstreams (span multiple milestones)

| Workstream | Where it shows up |
| --- | --- |
| **Snappy-load budget** | Established M0; guarded in every milestone; enforced in CI at M6. |
| **Virtual-desktop rule** | Overlay birth (M0); editor move/new-window (M1–M2); user setting (M6). |
| **Win32 interop** | Hotkeys + desktop COM (M0); WGC capture (M1); WGC + MF recording (M4). |
| **Export/flatten correctness** | Image flatten + destructive redaction (M2); video encode + presets (M5). |
| **Testing discipline** | Budget + interop harness (M0); headless core tests throughout; clipboard/DPI/throughput matrices (M6). |

## Explicitly out of v1 (backlog)

Timeline **keyframes** for timed text/drawings · **audio** capture · scrolling capture · OCR · GIF captions · cloud share links · cross-platform · **CLI** (`Shrike.Cli`). Revisit after v1 ships and sees daily use.

## Pre-M0 confirmations (resolved 2026-08-12)

1. **Repo/solution bootstrap** — ✅ **Bootstrap from sprig's** csproj/manifest/theme setup, to keep the two projects consistent.
2. **Interop approach** — ✅ **CsWin32** source generator for the WGC / virtual-desktop / clipboard / hotkey surface.
3. **Budget numbers** — ✅ **M0 measures a real baseline first.** The <100 ms overlay / <250 ms editor figures are provisional targets; the CI gate is wired in M0 but the exact thresholds are **tuned after real-world use**, not treated as firm from day one.
