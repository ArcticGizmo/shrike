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
| Video encoder | **FFmpeg**, not Media Foundation (MF proved unavailable on stripped images — see M4). Project is **GPL** (accepted). **Bundle a lean ffmpeg** (~30–50 MB, only our codecs) with the app — not download, not user-install. |
| Recording model | **Live-encode a high-quality H.264 _source_** during capture (instant at stop, any length). **Downscale / trim / final codec (H.265) are _export_-time re-encodes** (M5), plus an optional capture-time downscale for the quick path. |

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
- ✅ **M4.1 — recording pipeline core.** `IFrameEncoder` + `FfmpegMp4Encoder` (pipes top-down BGRA to `ffmpeg` stdin → **live-encoded high-quality H.264 _source_**; stderr drained off-thread), an `Ffmpeg` locator (env override → managed `%LOCALAPPDATA%\Shrike\ffmpeg` → bundled → winget shim → PATH, graceful when absent), and a headless `RecordingSession` (constant-fps pacing, duplicate-on-slow-capture, pause excludes time, discard/stop state machine). Tests: encoder round-trip + 1080p throughput smoke (skip when no ffmpeg), `RecordingSession` pacing/pause/state. *(Follow-up: tune the capture preset to an explicit high-quality-source CRF, and add an optional capture-time downscale.)*
- ✅ **M4.2 — frame source + recorder.** `IFrameSource` seam with a `GdiFrameSource` (BitBlt the region each frame, rounded to even dims) and a threaded `Recorder` (background capture loop → paced `RecordingSession`, pause/resume/stop/discard serialised behind one lock, monotonic `Stopwatch` clock). Chose **GDI BitBlt over WGC** for v1: reuses the working/tested capture, ships an end-to-end recorder now, and keeps WGC as a drop-in `IFrameSource` upgrade later. Tests: recorder lifecycle (fakes), `GdiFrameSource` sizing, and an **end-to-end record-a-region → playable MP4** integration test (real screen + ffmpeg).
- ✅ **M4.3 — HUD + region wiring.** "Record region" in the capture chooser (key 4) and the tray reuse the M1 region overlay to pick the area; a floating `RecordingHudWindow` (live elapsed clock, pause/resume, stop, discard) drives the `Recorder`. Stop finalises off the UI thread and reveals the MP4 in Explorer (drag straight into Slack); discard deletes it. `ToastWindow` surfaces a clear notice when ffmpeg isn't found. HUD is born on the current desktop and positioned just outside the region.
>
> **HUD stays out of frame:** `WindowExclusion` sets `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` on the HUD (and any toast), so DWM keeps it out of the capture path — it shows on the physical display but never lands in the recording, even for full-screen captures. Verified against the real GDI BitBlt path on this machine (captured-before-exclude=true, captured-after=false).
>
> **v1 limitations (GDI capture):** GDI grabs only what's composited — no exclusive-fullscreen/DX-exclusive content; that goes away with the WGC `IFrameSource` upgrade. (ffmpeg delivery is settled — bundle a lean build at M6; see the encoder-decision box.)

> **M4 complete.** Region → HUD → paced capture → FFmpeg live-encode → high-quality **source** MP4, revealed for sharing. Next: **M5** turns that source into the small, trimmed, downscaled deliverable.

> ### Encoder decision (2026-08-13) — FFmpeg, bundled lean; live-source + export re-encode
>
> The locked "no-dependency MP4 via built-in Media Foundation" plan was **abandoned**. What we found and decided:
>
> - **MF is not viable.** A hand-rolled `IMFSinkWriter` was proven correct (every attribute read back matched), yet failed. A thorough spike (`spikes/Shrike.MfSpike`) then found the root cause: **`MFTEnumEx` returns _zero_ video encoders on this machine** — the Media Foundation `VideoEncoder` MFT-category registry key doesn't even exist. This is a stripped enterprise/VM image (media features removed), not normal Windows. So MF works for _most_ end users but is absent on stripped images/N-editions/some VMs, and can't be relied on alone. (A runtime `MFTEnumEx` probe cleanly detects it: 0 → no MF.)
> - **Decision: FFmpeg, and the project is GPL** (accepted by the owner). ffmpeg encodes on every machine regardless of MF state.
> - **Delivery: bundle a _lean_ ffmpeg** (~30–50 MB — only `libx264`/`libx265`, the hardware encoders `qsv`/`nvenc`/`amf`, the `mp4`/`gif`/`webp` muxers, and the `scale` filter), **not** the 212 MB full build, **not** a first-run download, **not** user-install. Rationale: recording must work instantly, offline, first time; bundling avoids fetch failures, AV false-positives, and version drift. Producing the lean build is an M6 packaging task; until then dev uses the copy in `%LOCALAPPDATA%\Shrike\ffmpeg`.
> - **Recording model: encode _live_ during capture to a high-quality H.264 _source_.** Because ffmpeg encodes as frames stream in, the source is ready **~1–2 s after stop, at any length** — a 10-min recording has no post-stop compression wait (verified: `libx264 veryfast` ≈ 12× real-time, `libx265 ultrafast` ≈ 9×; this box's Intel Arc also exposes `hevc_qsv`). The catch: only **real-time-capable presets** can encode live, so the source is high-quality H.264 (a fast preset), _not_ the final small file.
> - **Downscale / trim / final small codec (H.265) are _export_ operations (M5).** You can't resize or re-cut a compressed stream in place — editing is a decode→re-encode from the source. So the small/downsampled/trimmed final is produced at export (with progress + a size estimate). Two places to downscale: **at capture** (baked in, instant, no going back) or **at export** (flexible re-encode). Rough keyframe-boundary trims can stream-copy (no re-encode); resolution/codec changes always re-encode.
> - **GIF/WebP stay on the table as self-contained export formats** via **ImageSharp** (already a dependency, MIT, no size/GPL cost) — verified encoding animated GIF/WebP; WebP is tiny and inline-shareable. Secondary to the MP4/H.265 path but a nice, dependency-free option for short clips.
>
> The MF encoder + NV12 converter were removed. The spike lives under `spikes/` as the record of this investigation.

### M5 · Timeline trimming + export presets
*Quick edits and the footprint dial — the "Slack-small" lever.* **This is the _export_ half of recording:** M4 captured a high-quality source; M5 re-encodes it into the small, trimmed, downscaled deliverable.

**Build**
- **Timeline model (`Shrike.Core`)**: segment list over the M4 source; non-destructive **cut / keep / restore**; playback maps to joined kept ranges; **source untouched until export** (all edits are just a range list — no re-encode happens until you export).
- **Timeline editor (`Shrike.App`)**: preview player, thumbnail filmstrip scrubber, segment lanes, set in/out, cut a middle section (split), delete/restore a segment.
- **Export (`Shrike.Core`)** — ffmpeg **re-encodes from the source** applying the kept ranges + `scale` (downscale) + target codec/bitrate:
  - Named **presets** — *Slack-small* (**H.265** ~720p, capped fps, quality-targeted), *Balanced* (H.265/H.264 1080p), *Most-compatible* (H.264), *Source* (stream-copy trim only, no re-encode), plus **GIF / animated WebP** (self-contained via **ImageSharp** — no ffmpeg needed for these).
  - Controls for **resolution scale / fps cap / codec / quality**. Prefer **hardware encoders** (`hevc_qsv`/`nvenc`/`amf`) when present; software `libx265` fast preset otherwise.
  - **Rough trims that land on keyframes stream-copy** (instant, no re-encode); resolution/codec changes always re-encode.
  - Runs off the UI thread with **progress + a live size estimate**; an optional **"maximum compression"** slow-preset pass for users who want the smallest file and will wait.
- **Codec-compatibility note surfaced in the UI:** H.265 = smallest but not universally previewable (Slack inline / older browsers need the HEVC extension); H.264 = universal, larger.
- Output: **copy-as-file** to clipboard (paste into Slack/Explorer) or save to disk.

**Exit criteria**
- Trim a recording (cut ≥1 section), export at a chosen preset; result plays and matches the kept segments. *(Verified headlessly by `ExportIntegrationTests`: synthesise → cut the middle → export → the output's duration matches the kept length. The interactive editor drives the same `Timeline`/`ExportCommand` path.)*
- **Slack-small** produces a genuinely small file; the pre-export size estimate is within a sensible tolerance of the actual output; export shows progress and never blocks the UI.
- GIF/WebP export works via the bundled ffmpeg (see the plan correction below — ImageSharp can't decode an MP4 source); H.265/H.264 export uses the bundled ffmpeg and prefers a hardware encoder when available, falling back to software if it fails.

> **M5 complete** (pending a live end-to-end pass of the editor UI). Region → record → **trim on a timeline** → **export a footprint-tuned deliverable** (Save or Copy-file into Slack). Next: **M6** — settings, packaging (incl. bundling the lean ffmpeg), autostart, the perf gate, and the v1.0 tag.

**Chunks**
- ✅ **M5.1 — timeline model.** Headless, correctness-sensitive core: a `RecordingSource` record (path + dims + fps + duration the recorder hands over at stop) and a `Timeline` — a non-destructive segment list over the source. The minimal merge model: adjacent same-state spans coalesce, so cutting a middle yields *keep · cut · keep*, `KeepOnly` sets in/out, delete/restore act on the span at a point, and edited↔source time maps through the joined kept ranges (`EditedToSourceMs` / `SourceToEditedMs`, `KeptRanges`, `KeptDurationMs`). Fully tested (`TimelineTests`, 15 cases). Source is never mutated — edits are pure metadata until export.
- ✅ **M5.2 — export pipeline.** Headless Core, all tested (`ExportCommandTests`, `ExportSupportTests`, `ExportIntegrationTests` — 18 cases incl. a real synthesise→cut→export→probe round-trip). `ExportProfile` + six presets (Slack-small H.265 720p · Balanced H.265 1080p · Most-compatible H.264 · Source stream-copy · GIF · WebP); a **pure** `ExportCommand.Build` that assembles the ffmpeg arg list (kept ranges → `trim`+`concat`, `scale` downscale-only, `fps` cap, codec/CRF, HEVC `hvc1` tag), so the whole encode plan is unit-testable; `HardwareEncoders` (parses `-encoders`, prefers QSV→NVENC→AMF over software); `MediaProbe` (duration/size/fps from the `ffmpeg -i` banner — no ffprobe in the lean bundle); `ExportSize` (bpp size estimate that moves right with res/fps/codec/CRF); and `VideoExporter` (async run, `-progress pipe:1` → 0..1 fraction, cancel kills + cleans up). **Source stream-copies a single range** (instant, no re-encode); multi-range Source re-encodes near-lossless.
>
>   **Plan correction (GIF/WebP):** the earlier note had GIF/WebP as *ImageSharp, no ffmpeg*. That can't hold for a **video** source — decoding the H.264 source needs ffmpeg, and ImageSharp can't decode MP4. Since ffmpeg is always bundled, GIF/WebP go through ffmpeg too (GIF via `palettegen`/`paletteuse`, WebP via `libwebp`). ImageSharp stays the dependency-free path only where we already hold raw frames (image export), not here.
- ✅ **M5.3 — timeline editor UI.** **Preview decision: ffmpeg-frame** (Avalonia has no native video widget; chose reusing the bundled ffmpeg over a heavy native player — keeps the bundle lean and sidesteps the "missing on stripped image" risk that killed MF). A `FrameExtractor` pulls the still at a source time as PNG (fast keyframe seek) — the one primitive behind scrubbing, the filmstrip, and timer-driven Play (integration-tested). `TimelineStrip` (custom-drawn control) renders the filmstrip with cut spans dimmed + a red rule, in/out marks, and the playhead; drag scrubs (cheap live playhead, coalesced preview extraction). `TimelineEditorWindow` wires preview + Mark In/Out → Cut / Keep-only / Restore / Reset over the `Timeline`. `ExportDialog` picks a preset, shows the target spec + estimated size + kept length, and runs `VideoExporter` off the UI thread with a live progress bar; **Save** (file picker) or **Copy file** (new `ClipboardImage.SetFileDrop` CF_HDROP → paste into Slack). Hardware encoder is preferred but **falls back to software automatically** if it fails. The recorder now surfaces `Width`/`Height`/`Fps`/`Duration`, so `CaptureController` builds a `RecordingSource` at stop and opens the editor (too-short clips / no-ffmpeg still just reveal the file — the M4 path). Core primitives (`FrameExtractor`, `RecordingSource`, `Recorder.Duration`) are tested; the Avalonia wiring is build- and boot-verified — the interactive record → trim → export round-trip is ready to exercise live.

---

## Phase D — Ship

### M6 · Settings, packaging, polish, release
*Make it installable, updatable and configurable.*

**Build**
- **Settings window**: rebind all hotkeys; **desktop behaviour** (follow-me / new-window-here); ring size; default save dir + format; **autostart-at-login toggle (opt-in, off by default)**; cursor-in-recording toggle. Finalise the `Alt+Shift+…` default set.
- **Packaging**: Velopack install/update (mirror sprig's lifecycle hooks); autostart registration driven by the opt-in toggle only; `ReadyToRun`/trimming on the cold path.
- **Bundle a lean ffmpeg**: a build/CI step produces a minimal GPL ffmpeg (only `libx264`/`libx265`, hardware encoders, `mp4`/`gif`/`webp`, `scale`; target ~30–50 MB) and ships it next to the app (found first by the `Ffmpeg` locator). Carry the GPL licence + source-offer notices required by redistribution.
- **About / changelog** viewer (embed `CHANGELOG.md`, sprig-style).
- **Perf & QA pass**: CI startup-budget gate enforced; memory-ceiling test for the ring; encoder throughput test; clipboard matrix (Slack/Teams/Office/browser); multi-monitor + mixed-DPI sweep; **verify recording end-to-end on a machine _with_ MF stripped (bundled ffmpeg) and confirm hardware-encoder selection.**
- Icon/branding (amber shrike mark), first-run experience.

**Exit criteria**
- Clean install → tray → capture → annotate → record → trim → export, all within budget.
- Update flow works via Velopack; autostart only when the user opts in.
- All performance gates green in CI. **v1.0 tag.**

**Chunks**
- ✅ **M6.1 — settings.** Headless `AppSettings` (record with per-field defaults, so an older/partial settings file still loads sensibly) + `SettingsStore` (JSON at `%APPDATA%\Shrike\settings.json`, corruption-tolerant → defaults, values clamped) — tested (`SettingsStoreTests`, 6 cases). A `SettingsWindow` edits: **rebindable** capture + record hotkeys (validated via `Hotkey.Parse`, blank = unbound), desktop behaviour (follow-me / new-window-here), recent-ring size, default image format + save folder, and the **opt-in autostart** toggle. Wiring: `SettingsService` holds the live value and, on save, persists + applies autostart (HKCU `Run`, via `Autostart`) + re-registers hotkeys live; `HotkeyService` gained a second (record-region) hotkey and an `Apply(...)` re-register; ring size is built from settings at startup; save folder / default format seed the editor + export save pickers; desktop behaviour drives editor reuse-vs-new. *(Cursor-in-recording is in the model for later; its GDI compositing isn't wired yet, so it's kept out of the UI rather than shipped as a dead toggle.)*
- ✅ **M6.2 — packaging + updates + About.** Mirrors `sprig`. `VelopackApp.Build().Run()` is the first call in `Program.Main` (install/update lifecycle hook), re-asserting the autostart entry on update so the login `Run` key follows the new version. `UpdateChecker` (notify-only, GitHub Releases feed with a `SHRIKE_UPDATE_FEED` override, all failures swallowed, no-op on dev builds) drives a quiet launch toast and the **About window** (tray → *About Shrike…*: version + embedded `CHANGELOG.md` + Check-for-updates → Install & restart). `ReadyToRun` was already on; trimming stays **off** (Avalonia reflection risk). Release runbook in [`packaging.md`](packaging.md). **Decisions (agreed):** GitHub Releases feed (repo URL is a `TODO(release)` placeholder until the repo exists) and **unsigned** for v1. *(Deferred to M6.3/release: lean-ffmpeg bundle, CI release workflow + install one-liner, app icon, code signing.)*
- ⏭ **M6.3 — lean ffmpeg bundle + perf/QA gates + branding, v1.0.** Lean GPL ffmpeg bundle step; CI startup-budget gate + memory/throughput/clipboard/DPI matrices; icon/branding (`shrike.svg`) + first-run. *(Decision: ffmpeg build-from-source vs. ship a known-good minimal build.)*

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
| **Win32 interop** | Hotkeys + desktop COM (M0); GDI BitBlt capture (M1, M4); capture-exclusion for the HUD (M4); WGC frame-source is a later `IFrameSource` upgrade. |
| **Export/flatten correctness** | Image flatten + destructive redaction (M2); video **live-encode → source** (M4) and **export re-encode / presets** (M5). |
| **Testing discipline** | Budget + interop harness (M0); headless core tests throughout; clipboard/DPI/throughput matrices (M6). |

## Explicitly out of v1 (backlog)

Timeline **keyframes** for timed text/drawings · **audio** capture · scrolling capture · OCR · GIF captions · cloud share links · cross-platform · **CLI** (`Shrike.Cli`). Revisit after v1 ships and sees daily use.

## Pre-M0 confirmations (resolved 2026-08-12)

1. **Repo/solution bootstrap** — ✅ **Bootstrap from sprig's** csproj/manifest/theme setup, to keep the two projects consistent.
2. **Interop approach** — ✅ **CsWin32** source generator for the WGC / virtual-desktop / clipboard / hotkey surface.
3. **Budget numbers** — ✅ **M0 measures a real baseline first.** The <100 ms overlay / <250 ms editor figures are provisional targets; the CI gate is wired in M0 but the exact thresholds are **tuned after real-world use**, not treated as firm from day one.
