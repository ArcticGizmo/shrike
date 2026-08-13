# Shrike — Design Document

> A snappy, tray-resident replacement for the Windows Snipping Tool: screenshots with
> annotation, screen recording with lightweight timeline editing, and — above all — a
> capture flow that never yanks you across virtual desktops.

**Status:** Draft for review · **Date:** 2026-08-12 · **Owner:** Jon
**Sibling project:** `../sprig` (shares tech stack and conventions)

---

## 1. Why build this

The built-in Windows Snipping Tool is serviceable but has real friction:

| Pain today | Shrike's answer |
| --- | --- |
| Invoking Snip **teleports you to another virtual desktop** because it activates its existing window. | The capture overlay always appears on the **current** desktop; the editor either follows you or opens a fresh instance. Never a desktop switch. |
| No quick "show me the last few shots I took" — you have to re-capture. | A **recent-captures ring** kept in memory (tray-resident) so the last *N* shots are one click away to re-copy. |
| Annotation is basic and slow to reach. | Immediate annotate-on-capture: text, shapes, scribble, arrows, highlight, redact. |
| Screen recordings are huge and awkward to trim/share. | **Timeline trim** (cut dead sections) plus **encode-for-target** presets so a Slack clip is a few hundred KB, not tens of MB. |
| Cold, slow launch. | **Snappy loading is the #1 priority** — see §4. |

## 2. Naming & identity

**Shrike** — a small predatory bird known for precision strikes and for *impaling* its
catch on thorns to store it (the "butcher bird"). The metaphor fits: capture precisely,
pin it for later, come back to it. Keeps the short, single-syllable, bird-adjacent naming
family with `sprig`.

- Accent colour: **amber / rust** (`#F59E0B`-ish) to visually distinguish from sprig's blue.
- App is dark-theme-native (matching sprig's `RequestedThemeVariant="Dark"`).

## 3. Goals & non-goals

### Goals
- **G1** Screenshot capture (region, window, full-screen, monitor) that is instant and never switches desktops.
- **G2** In-memory recent-captures ring — re-copy or re-open the last *N* without re-capturing.
- **G3** Annotation: text, basic shapes, freehand scribble, arrows, highlighter, redaction/blur.
- **G4** Output: copy to clipboard (multi-format) or save to file (format + quality choice).
- **G5** Screen recording of a chosen region.
- **G6** Non-destructive **timeline trimming** — clip/remove sections for quick edits.
- **G7** Export recordings with **encoding presets** tuned for footprint (Slack-small) vs quality, incl. downsampling.
- **G8** Tray-resident to keep warm state and give sub-second capture.

### Non-goals (v1)
- Full non-linear video editing (multi-track, transitions, audio mixing).
- Cloud upload / hosted share links.
- OCR / text extraction (candidate for later).
- Cross-platform (Windows-first; Avalonia keeps the door open, but capture stack is Win32/WGC).

### Future state (explicitly deferred)
- **Keyframes** on the recording timeline that pin overlaid text or drawings to time ranges.
- Scrolling capture, OCR, GIF-caption keyframes.

## 4. The overriding principle: snappy loading

Everything below is subordinate to *time-to-capture*. Targets:

- **Overlay appears < 100 ms** after the global hotkey.
- **Editor window appears < 250 ms** after a capture is committed.
- **Zero perceptible cold start** in normal use because the process is already resident.

Strategy:

1. **Tray-resident single instance.** Shrike launches at login into the tray. The heavy
   costs (CLR JIT, Avalonia init, WGC device, clipboard hooks) are paid **once**, at
   login, not per-capture.
2. **Pre-warmed capture pipeline.** The Windows.Graphics.Capture device / GDI surfaces and
   the shared graphics context are created and kept alive so the first frame is immediate.
3. **Overlay is cheap.** The region-select overlay is a lightweight transparent, borderless,
   topmost window (or per-monitor windows) — not the full editor. It carries almost no VM state.
4. **Editor is lazy but pre-JITted.** The editor window is constructed on first capture, then
   **hidden and reused** rather than destroyed, so subsequent opens are instant.
5. **Single-instance guard.** A second launch (e.g. from hotkey wrapper or CLI) signals the
   resident instance over a named pipe instead of spinning up a new process.
6. **ReadyToRun / trimming.** Publish with R2R to cut JIT on the cold path; measure with a
   startup budget test (mirroring sprig's headless-render verification habit).

## 5. The virtual-desktop fix (a headline feature)

The Snip annoyance is that it `SetForegroundWindow`s an existing window that lives on
another virtual desktop, so Windows switches you there. Shrike's rules:

- The **capture overlay is created fresh on the current desktop each time** — a new
  top-level window is born on whichever desktop is active, so there is never a switch.
- When showing the **editor**, Shrike checks the target window's desktop via
  `IVirtualDesktopManager::IsWindowOnCurrentVirtualDesktop`. If the reusable editor window
  is parked on another desktop, Shrike **moves it to the current desktop**
  (`MoveWindowToDesktop`) *before* showing it — or, per user preference, **opens a new
  editor instance on the current desktop** and leaves the old one where it was.
- Setting: **"When editing a capture → Follow me to this desktop / Open a new window here."**

> Note: `IVirtualDesktopManager` is a documented COM interface but the richer per-build
> internal API is not. We deliberately restrict ourselves to the documented surface
> (detect current-desktop + move) to avoid per-Windows-build breakage. Risk noted in §12.

## 6. Architecture

Mirrors sprig's layout — a thin Avalonia app over a testable core, plus an optional CLI.

```
shrike.slnx
├── src/
│   ├── Shrike.Core/     # capture, encoding, annotation model, storage — no UI deps
│   └── Shrike.App/      # Avalonia UI: tray, overlay, screenshot editor, timeline editor
└── tests/
    └── Shrike.Tests/    # core logic, encoding math, timeline model, headless view render

# Shrike.Cli — DEFERRED (dropped from v1; revisit if scripting demand appears)
```

### Shrike.Core responsibilities
- **Capture**: screen/region/window/monitor grab (Windows.Graphics.Capture primary; GDI `BitBlt` fallback for older paths).
- **Recording**: frame source → encoder. **Superseded (2026-08-13): FFmpeg, not Media Foundation** — MF ships no video encoders on stripped images, so Shrike bundles a lean ffmpeg and live-encodes a high-quality H.264 *source*; downscale/trim/H.265 happen at export. See the encoder-decision box in `implementation-plan.md`.
- **Annotation model**: vector document (ordered layers of shapes/text/strokes) rendered over the bitmap; non-destructive.
- **Timeline model**: ordered list of kept segments over a source recording; trims are metadata until export.
- **Encoding presets**: named target profiles (codec, container, resolution scale, bitrate/CRF, fps).
- **Storage**: the recent-captures ring (bounded, memory-first, optional spill to disk temp), and save-to-file.

### Shrike.App responsibilities
- **Tray icon** (`Avalonia.Controls.TrayIcon` + `NativeMenu`): capture actions, recent flyout, settings, quit.
- **Overlay windows** per monitor for region selection + a crosshair/magnifier.
- **Screenshot editor**: canvas + annotation toolbar + export bar + recent strip.
- **Recorder HUD**: small floating controls (pause/stop/region) while recording.
- **Timeline editor**: scrubber, segment lanes, cut/keep, export dialog with live size estimate.
- **Global hotkeys**: register system-wide via Win32 `RegisterHotKey`.

### Tech stack (locked to sprig's versions)
- .NET 10, Avalonia 12.0.5, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, compiled bindings.
- `CommunityToolkit.Mvvm` for VMs.
- **Velopack** for install/update + login autostart registration (sprig already does the PATH pattern).
- Windows interop: `CsWin32`/P-Invoke for WGC, virtual desktop COM, hotkeys, clipboard.

## 7. Feature detail — Screenshots

### Capture modes
- **Region** (drag rectangle; snap-to-window highlight while hovering).
- **Window** (click a window; captured via WGC to include correct DWM composition).
- **Monitor** / **Full desktop**.
- **Delay** (3s/5s/custom) for menus and hover states.

### Recent-captures ring
- Bounded in-memory list of the last **N** (default 10, configurable) captures with thumbnails.
- Surfaced in the **tray flyout** and as a **strip** along the editor.
- Actions per item: **copy again**, **open in editor**, **save**, **delete**.
- Memory-first; large images may spill to a temp cache with a total-size cap. Cleared on quit (setting to persist across sessions).

### Annotation toolbox
- **Select/move**, **Arrow**, **Line**, **Rectangle**, **Ellipse**, **Freehand (scribble)**,
  **Text**, **Highlighter**, **Redact (true, pixel-destroying)**, **Crop**, **Step counter** (1,2,3 badges).
- **Redaction is destructive by design**: on export the covered pixels are irreversibly overwritten
  (not blurred, not a moveable overlay) so a redacted shot can't leak the original underneath.
- Colour, stroke width, fill, font size in a context bar. **Undo/redo** stack. Everything non-destructive until export.

### Output
- **Copy to clipboard** in multiple formats simultaneously (PNG for fidelity, DIB/bitmap for legacy apps).
- **Save to file**: PNG / JPG / WebP; quality slider; remembered folder + filename template
  (`shrike-{yyyyMMdd-HHmmss}`), matching the user's capture-naming habit from CLAUDE.md.
- **Copy file path** / **open folder** conveniences.

## 8. Feature detail — Screen capture (recording)

### Recording
- Choose a **region** (or window/monitor); a HUD shows elapsed time, pause, stop, and a discard.
- Source frames via WGC; encode on a background thread. Optional cursor capture toggle.
- **Audio is out of scope for v1** (keeps the encoder simple and footprint small) — flagged as a fast-follow.

### Timeline editing (v1 = trimming)
- Non-destructive **segment model**: the recording is a list of kept ranges over the source.
- **Scrubber** with thumbnail filmstrip; set in/out; **cut** a middle section (splits into two kept segments); **delete** a segment; **restore**.
- Playback previews the *edited* result (segments played back-to-back).
- Trims never touch the source until export → instant, reversible edits.

### Export & encoding-for-footprint
- **Presets** (the Slack-small lever):
  - **Slack-small** — H.264 MP4, scaled to ≤720p, tuned CRF/bitrate, capped fps → small footprint.
  - **Balanced** — 1080p, higher bitrate.
  - **High** — source resolution, high bitrate.
  - **GIF** / **WebP (animated)** — for inline embeds (via FFmpeg backend).
- Controls: **resolution scale / downsample**, **fps cap**, **codec/container**, **quality (CRF/bitrate)**.
- **Live estimated file size** updates as you change preset/trim — so you can dial footprint before exporting.
- **Copy to clipboard** (as file for paste into Slack/Explorer) or **Save to disk**.

### Future — keyframes (deferred)
- Timeline gains a **keyframe/annotation track**: pin text or a drawing to a time range so it
  appears as an overlay during that window. Reuses the screenshot annotation model, bound to time.

## 9. Data & state model (sketch)

```
Capture            { Id, Kind, Bitmap, CapturedAt, SourceBounds, Thumbnail }
AnnotationDoc      { BaseCapture, Layers[] (ordered: Shape|Text|Stroke|Redaction), Undo/Redo }
Recording          { Id, SourcePath/Frames, Fps, Bounds, Duration }
Timeline           { Recording, Segments[] { Start, End, Kept } }
ExportProfile      { Name, Codec, Container, ScalePct, FpsCap, Quality(CRF|Bitrate) }
RecentRing         { Items[] bounded=N, TotalBytesCap }
Settings           { Hotkeys, DesktopBehaviour(FollowMe|NewWindow), RingSize, DefaultSaveDir,
                     DefaultFormat, PersistRingAcrossSessions, StartAtLogin }
```

## 10. Global hotkeys (defaults, all rebindable)

| Action | Default (candidate) |
| --- | --- |
| Region screenshot | `Alt+Shift+Q` |
| Window screenshot | `Alt+Shift+W` |
| Full-screen screenshot | `Alt+Shift+F` |
| Open recent flyout | `Alt+Shift+V` |

Recording is started from the capture chooser (*Record region*) rather than its own hotkey.

All hotkeys are **fully rebindable** in settings; the `Alt+Shift+…` family avoids the OS `Win+Shift+S`.
Defaults are provisional and finalised in M6.

## 11. Performance verification

- A **startup-budget test** asserting overlay-ready and editor-ready timings (headless where possible), echoing sprig's `render` headless-verification discipline.
- Memory ceiling test for the recent-ring spill logic.
- Encoder throughput smoke test (can we sustain capture fps at target resolution without dropping frames?).

## 12. Risks & open questions

| Risk | Mitigation |
| --- | --- |
| Virtual-desktop COM API is partly undocumented / build-fragile. | Use only the **documented** `IVirtualDesktopManager` surface (detect + move). Feature-flag; degrade gracefully to "new window" behaviour if the interop fails on a given build. |
| WGC availability / capture of protected content (DRM) shows black. | Detect and message; GDI fallback where legal/possible. |
| Recording performance at high resolution / multi-monitor. | **Live-encode** during capture (source ready ~instantly at stop); fps cap + downscale; prefer **hardware ffmpeg encoders** (`hevc_qsv`/`nvenc`/`amf`) when present; measure early. |
| FFmpeg dependency bloats install / licensing. | Project is **GPL** (accepted); **bundle a lean ffmpeg** (~30–50 MB, only our codecs). GIF/WebP export needs no ffmpeg at all (ImageSharp). *(Prior "MP4 via built-in Media Foundation, no dependency" plan dropped — MF is absent on stripped images.)* |
| Clipboard multi-format quirks across target apps. | Set PNG + DIB; test against Slack, Teams, Office, browsers. |
| Snappy-load regressions creep in over time. | Enforce the startup-budget test in CI. |

### Resolved decisions (review, 2026-08-12)
1. **Autostart at login** — **opt-in via a settings toggle**, off by default. No autostart until the user chooses it.
2. **Recent ring** — **memory-only** to start (cleared on quit). Disk persistence is a later, opt-in consideration.
3. **Hotkeys** — **fully configurable / rebindable**; ship sensible `Alt+Shift+…` defaults (candidate: `Alt+Shift+Q` for region capture) that avoid the OS `Win+Shift+S`.
4. **CLI** — **deferred**. `Shrike.Cli` is dropped from v1; a graphical tool gains little from it. Revisit later if scripting demand appears.
5. **Redaction** — **true, pixel-destroying redaction** (covered pixels irreversibly overwritten on export), not blur.
6. **Audio in recordings** — **deferred to later** (not a frequent need); v1 records video only.

---

*Approved 2026-08-12. Implementation is broken into milestones in [`implementation-plan.md`](implementation-plan.md).*
