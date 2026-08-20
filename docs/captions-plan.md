# Shrike — Auto-captions / burned-in subtitles (implementation plan)

> Add **local, offline transcription** of recorded narration and **editable, styled, burned-in
> captions** to the timeline editor. This is **M2** of the recording/audio line
> ([`recording-audio-roadmap.md`](recording-audio-roadmap.md)) — the "sleeper": because Slack autoplays
> muted, burned-in captions may reach more people than the audio itself. Builds directly on the audio
> track from M1 and the unified effects/compositor platform from
> [`effects-timeline-plan.md`](effects-timeline-plan.md).

**Status:** Planned · **Date:** 2026-08-20 · **Owner:** Jon
**Depends on:** M1 audio (mic / voiceover sidecars, `AudioTrack`) — shipped in v0.3.1.
**Companions:** [`recording-audio-roadmap.md`](recording-audio-roadmap.md) (feature line & ordering) ·
[`recording-implementation-plan.md`](recording-implementation-plan.md) (audio detail, referenced M2 here).

---

## Decisions locked (from scoping Q&A, 2026-08-20)

1. **Engine: whisper.cpp**, run offline, no upload — matches the "bundle it, works on stripped images"
   philosophy that drove the FFmpeg-over-Media-Foundation choice. Shells out to a `whisper-cli.exe` the
   same way Core already shells out to `ffmpeg`.
2. **Model delivery: opt-in, in-app download — NOT bundled in the installer.** Models are large and
   language-specific. The small whisper **binary** is bundled/fetched at release (like ffmpeg); the
   **model** is downloaded on first use through an in-app **model manager** that lets the user pick
   language + size, shows the download size + progress, and remembers the choice. `base.en` is the
   suggested default for English; a multilingual model is one click away for non-English users.
3. **Cue anchoring: linked to source.** Cues are stored in **source time**, exactly like every other
   effect, so they ride cuts/trims for free via the existing `Timeline.EditedToSourceMs` resolvers. The
   mic sidecar shares the source (pause-excluded capture) time axis with the video, so source-time cues
   *are* the "linked to source audio" behaviour — no new anchoring machinery.
4. **Scope for this milestone: transcribe → edit → burn-in (the full loop).** Auto-generate cues from
   narration, let the user fix the text/timing and style them, preview them live, and bake them into the
   export.

## Guiding rules (carried from the project)

- **WYSIWYG.** Captions visible in the export must show live in the preview, from the same cue list +
  style + viewport map. ([`CLAUDE.md`](../CLAUDE.md))
- **Off means off.** No caption effect ⇒ export path is byte-for-byte today's transcode.
- **Non-destructive.** Cues + style are metadata in `*.edit.json`; the source MP4 and audio sidecars are
  never mutated.
- **Core stays UI-free and tested.** Cue model, resolver, SRT/JSON parser, mix/anchoring math live in
  `Shrike.Core` headless; text rasterisation reuses the Avalonia annotation renderer at the App layer.
- **Snappy-load stays green.** No model load, no whisper process, no device work at tray start — all lazy
  on first "Generate captions".

---

## How it maps onto today's code (from the code-seam survey)

- **Compositor seam:** `IFrameCompositor.Compose(bgra, w, h, frameIndex)`
  (`src/Shrike.Core/Recording/IFrameCompositor.cs:11`); appended to `CompositorChain`
  (`CompositorChain.cs:11`), run by `FrameCompositePipeline` (`FrameCompositePipeline.cs:15`). The chain
  is assembled per-export in `ExportDialog.CompositeExport` (`src/Shrike.App/Views/ExportDialog.axaml.cs:259`),
  ordered content-canvas → **zoom transform** → spotlight → cursor → screen-canvas. **Captions are a
  screen-space overlay → appended last** (after `screenCanvas`), so they land on final on-screen pixels.
- **Effect model:** `EffectKind` (`EffectEvent.cs:8`) gains `Caption`; a `CaptionEffect : EffectEvent`
  carries the whole cue list + style (mirroring how `CanvasEffect` carries an annotation list —
  **one lane block, not one-per-cue**, which matches Whisper's segment-list output and keeps the lane
  uncluttered). `EffectTrack` (`EffectTrack.cs:12`) gains `ResolveCaptions(...)` → a per-frame
  `CaptionFrame[]` (active cue index + alpha), following the `ResolveSpotlight`/`SpotlightFrame` pattern
  (`EffectTrack.cs:90,144`).
- **Sprite bake:** reuse `AnnotationSurface.RenderAnnotationLayer(w,h)`
  (`src/Shrike.App/Controls/AnnotationSurface.cs:1350`) → premultiplied top-down BGRA, exactly as
  `CanvasCompositor` (`CanvasCompositor.cs:12`) consumes; export-side bake mirrors
  `ExportDialog.RasterizeCanvas` (`ExportDialog.axaml.cs:233`).
- **Narration source:** the mic sidecar `name.mic.wav` (and voiceover `name.vo.wav`) beside the source MP4,
  paths from `AppStorage.MicWavFor`/`VoiceoverWavFor` (`AppStorage.cs:42,52`); seeded into `AudioClip`s in
  `TimelineEditorWindow.SeedAudioFromSidecars` (`TimelineEditorWindow.axaml.cs:321`). `CaptureAudio.ForOutput`
  (`CaptureAudio.cs:50`) is the cut→output projection the audio already uses.
- **Persistence:** `ClipEdit` `SchemaVersion = 3` (`ClipEdit.cs:19`); `CanvasDto` (`ClipEdit.cs:286`) +
  its write/read blocks are the template for a new `CaptionDto`. Bump to **v4**, forgiving load unchanged.
- **Export decision:** `ExportDialog.Encode` chooses composite when `wantsCursor || wantsZoom ||
  wantsCanvas` (`ExportDialog.axaml.cs:220`) — **add `wantsCaption`**. Audio mux
  (`ExportCommand.Build(..., audio)`, `ExportCommand.cs:26`) is untouched.
- **Process-shelling precedent:** `Ffmpeg` locator (`Ffmpeg.cs:11`, env → bundled → `%LOCALAPPDATA%` →
  winget → PATH) and `VideoExporter` running ffmpeg with progress — a `Whisper` locator + `WhisperTranscriber`
  follow the same shape, so transcription stays in Core (UI-free, testable) with the parser mocked.
- **Editor wiring:** add-effect menu `EffectsLane.ShowAddMenu` (`EffectsLane.cs:236`), per-kind construct
  `TimelineEditorWindow.OnAddEffect` (`:1144`), pane dispatch `OnEffectSelectionChanged` (`:518`), live
  preview `PreviewSurface.Set*`/`Render` (`PreviewSurface.cs:61,210`).

---

## The model (Core)

```
CaptionCue (record)
  StartMs, EndMs        // SOURCE time (rides cuts)
  Text

CaptionStyle (record)
  FontScale             // relative to frame height (like CursorStyle.ForExport)
  TextColor, BoxColor, BoxOpacity
  Position { Bottom | Top }
  MaxWidthFraction      // wrap width as a fraction of frame width

CaptionEffect : EffectEvent            // Kind = Caption; Start/End span all cues
  IReadOnlyList<CaptionCue> Cues
  CaptionStyle Style
  // short per-cue fade handled by the resolver envelope

EffectTrack.ResolveCaptions(timeline, frameCount, fps) -> CaptionFrame[]
  CaptionFrame { int CueIndex (-1 = none), double Alpha }
```

`ClipEdit` v4 serialises a `CaptionDto` (cue list + style) alongside the existing effect DTOs. A v3 doc
loads unchanged (no caption array). Load stays forgiving — malformed cues dropped, never fail open.

**Anchoring detail:** transcription runs per audio clip. A **capture-origin** clip's sidecar shares the
source time axis, so its cue timestamps map to source time by the clip's `SidecarOffsetMs` (near-identity)
— cues then ride cuts like all effects. An **editor-voiceover** clip is output-anchored, so its cues are
projected output→source via `Timeline.EditedToSourceMs` at import. Either way cues land in source time and
the resolver machinery is shared with the rest of the effects.

---

## Milestones / chunks

One milestone (M2), delivered in chunks like M2/M5 elsewhere. Each is independently demoable; rebuild +
relaunch the dev exe on every change (kill running Shrike first — [`CLAUDE.md`](../CLAUDE.md)).

### ✅ C0 · Caption model + resolver + persistence  *(Core, headless, tested)*
- ✅ `CaptionCue` / `CaptionStyle` / `CaptionPosition` / `CaptionEffect` (`CaptionEffect.cs`) — cues in
  **source time**, one lane block carrying the whole cue list + a shared style (like `CanvasEffect`), a
  legible-by-default style (white on a translucent box, lower third, 80% wrap, short crossfade), and
  `CaptionEffect.FromCues` spanning the cues. `EffectKind.Caption` added.
- ✅ `EffectTrack.ResolveCaptions` / `CaptionAt` / `HasCaptions` + the `CaptionFrame` struct — per output
  frame, edited→source via the timeline (mirrors `ResolveCanvasTransforms`), active cue (last-wins on
  overlap) with a smoothstep crossfade folded with the effect envelope.
- ✅ `ClipEdit` → **v4** (`CaptionDto`/`CueDto`, write + forgiving read; malformed/empty cues dropped);
  v1/v2/v3 documents load unchanged; unknown fields ignored (forward-compat).
- ✅ **Tests** (`CaptionEffectTests` 9, `ClipEditTests` +3): ordering/spanning, half-open cues, active-cue
  pick + last-wins, edge crossfade values, `HasCaptions`, frame→source mapping, **source-time cues ride a
  mid-clip cut** (and a cue buried in a cut never shows), round-trip with style, malformed-cue drop, v3
  doc loads with no captions. Suite **384 passed**; Core build clean, 0 warnings.
- **Exit:** ✅ a hand-authored cue list resolves per frame and round-trips; no UI yet.

### ✅ C1 · Transcription engine  *(Core + release tooling)*
- ✅ `Whisper` locator (`Whisper.cs`) — mirrors `Ffmpeg`: `SHRIKE_WHISPER` override → bundled next to app
  (`whisper-cli.exe`, also `whisper/…` and the legacy `main.exe`) → `%LOCALAPPDATA%\Shrike\whisper` → PATH;
  cached, `ResetCache()` for tests, null when absent so callers prompt to install.
- ✅ `ITranscriber` + `WhisperTranscriber` (`WhisperTranscriber.cs`): ffmpeg resamples the sidecar to 16 kHz
  mono PCM → `whisper-cli -oj` → `ParseJson` → `CaptionCue[]` (in the audio file's own time; the caller maps
  to source time). `TryCreate()` returns null when either binary is missing. Pure, testable arg builders
  (`ResampleArgs`/`WhisperArgs`) + a forgiving JSON parser (offsets, timestamp-string fallback, junk → none);
  process runner shells out like `VideoExporter`, draining stderr for `progress = NN%` → a real 0..1 bar,
  cancellable, clear errors on non-zero exit.
- ✅ `tools/fetch-whisper.ps1` — mirrors `fetch-ffmpeg.ps1` (pin/download/verify), places the **binary + its
  DLLs** in `publish/whisper` for the release bundle. **Models are NOT fetched here** (opt-in in-app, C2).
  Parse-clean on PowerShell 5.1, pure ASCII.
- ✅ **Tests** (`WhisperTranscriberTests`, 8): parser reads offset segments + trims + drops empty/zero-length,
  timestamp-string fallback, junk tolerance; `ResampleArgs`/`WhisperArgs` (translate + word-level variants,
  blank-language → auto); locator honours an existing override and ignores a missing one. Suite **392 passed**.
- **Exit:** ✅ given a WAV + a model on disk, produce cues headlessly; parser + arg builders fully unit-tested.
  *(Live transcription against a real model is exercised in C6's hardware pass.)*

### ✅ C2 · In-app model manager  *(the opt-in download — decision #2)*
- ✅ `WhisperModel` + `WhisperModelCatalog` (`WhisperModel.cs`) — tiny/base/small English + base/small
  multilingual GGML weights on Hugging Face; `base.en` default; friendly `ApproxSize`; SHAs blank to pin at
  release. `WhisperModelStore` (`WhisperModelStore.cs`) — `PathFor`/`IsInstalled`/`Installed`/`InstalledPath`
  and `DownloadAsync` (streamed 0..1 progress, SHA-256 verify when pinned, atomic `.part`→final, cleanup on
  error/cancel) over an **injectable `HttpClient`**. `AppStorage.Whisper*Directory`; `AppSettings.CaptionModelId`.
- ✅ `WhisperModelWindow` (Avalonia) — picker with per-model installed/size state, **Download** with a
  progress bar (cancellable), **Use for captions** (sets the default), **Delete**, and an engine-missing
  note; the chosen default persists via an injected callback (decoupled from the settings service). A static
  `EnsureModelAsync(...)` returns the model path C5's Generate action needs (opening the picker only when
  nothing is installed). Entry point: **Settings → Captions → "Manage transcription models…"**.
- ✅ **Tests** (`WhisperModelStoreTests`, 6): catalog lookup/default, size formatting, installed-state,
  download+verify+place, checksum-mismatch cleanup, HTTP-error. Suite **398 passed**; App build clean, boot OK.
- **Exit:** ✅ pick a model, watch it download + verify; it becomes the caption model and is reused silently;
  a second (e.g. multilingual) model installs alongside. *(Interactive download exercised live in C6.)*

### ✅ C3 · Burn-in rendering  *(export)*
- ✅ `CaptionCompositor : IFrameCompositor` + `CaptionSprite` (`CaptionCompositor.cs`) — pre-resolved
  `CaptionFrame[]` + one pre-baked premultiplied-BGRA sprite per cue; each frame blits the active cue's
  sprite at its baked position, scaled by the eased alpha (premultiplied source-over, bounds-checked).
- ✅ `CaptionRasterizer` (`Controls/CaptionRasterizer.cs`) — renders the styled, word-wrapped text over a
  translucent rounded box to a sprite via `RenderTargetBitmap` (height-derived sizing → resolution-consistent;
  centred, lower/upper third with a margin), mirroring `RasterizeCanvas`.
- ✅ Wired into `CompositeExport` — captions rasterise on the UI thread and append **last** (topmost
  screen-space overlay, after the cursor + screen canvas); `wantsCaption = _effects.HasCaptions` added to the
  `Encode` composite decision. Audio mux and the plain/off path are untouched (off-means-off holds: no cue →
  no compositor).
- ✅ **Tests** (`CaptionCompositorTests`, 6): blit position, alpha scaling, inactive frame, cue-index→sprite
  selection, off-frame clipping, empty/null-sprite safety. Suite **404 passed**; App build clean, boots OK.
- **Exit:** ✅ a clip with captions bakes legible, correctly-timed text into the export; audio mux unaffected.

### ✅ C4 · Preview WYSIWYG
- ✅ `PreviewSurface.PreviewCaption` + `SetCaption(...)` + a `DrawCaption` block in `Render` (topmost
  screen-space overlay, after the screen canvas, before the aim overlay) — Avalonia `FormattedText` box +
  wrapped, centred text at the **same proportions as `CaptionRasterizer`** scaled to the drawn frame, faded
  together via the eased alpha (the accepted dual-path preview).
- ✅ `TimelineEditorWindow.CaptionPreviewAt(srcMs)` resolves the active cue via the shared
  `EffectTrack.CaptionAt`, wired into both `UpdateCursorOverlay` branches alongside `SetCanvasLayers`, so
  scrubbing/playback shows the live caption.
- **Exit:** ✅ scrubbing shows the active caption live, matching what the export bakes. App build clean, boots OK.

### ✅ C5 · Editor authoring — the full loop  *(decision #4)*
- ✅ Captions are a first-class effect kind on the lane — added to `EffectsLane` (menu, green palette,
  "Captions ×N" / "CC" labels), `OnAddEffect` (empty `CaptionEffect` spanning the whole clip), and `KindName`.
- ✅ **"Generate from narration"** (`OnGenerateCaptions`) — picks a recorded narration sidecar (live mic/
  system preferred, else voiceover), ensures a model via `WhisperModelWindow.EnsureModelAsync` (prompts to
  download only when none is installed), runs `WhisperTranscriber` off the UI thread with a progress bar,
  maps cues into **source time** (`MapCuesToSource`: live-capture by sidecar offset, voiceover output→source
  via the timeline — decision #1), fills + retimes the effect, persists. English models → `en`, multilingual
  → auto.
- ✅ **Cue editor pane** — an editable text box per line (patched in place on keystroke so focus is kept) with
  a per-line delete; plus **style controls**: text size, text colour, box colour, box opacity, position
  (bottom/top), max width. Every edit rebuilds the effect and refreshes the live preview.
- ✅ **Exit:** transcribe narration → fix a mis-heard word → restyle → see it live → export (captions flow
  through `CurrentEffects` into `ExportDialog`). App build clean (0 warnings), boots OK; suite **404 passed**.
- *Deferred (documented gap):* per-cue **timing nudge / split / merge** — v1 ships text-edit + delete +
  restyle + regenerate (the transcription-error fix loop). Timing edits ride the effect's shared Start/End and
  the underlying cut model; per-cue retiming is a natural follow-up.

### C6 · Polish & QA
- Multi-source narration (mic + voiceover) ordering; empty/near-silent audio produces no cues gracefully;
  very long clip transcription runs off-thread with progress + cancel; snappy-load unaffected (lazy model).
- **Real-hardware pass:** transcribe a genuine recording, confirm timing lands on the words, legibility on
  light/dark/busy frames, and the model-download UX on a clean machine.
- **Exit:** a short QA checklist run with no surprises.

---

## Cross-cutting

| Concern | Handling |
|---|---|
| **Migration / versioning** | `ClipEdit` v3→v4, forgiving load; old clips open unchanged. Tested in C0. |
| **WYSIWYG parity** | Preview caption wired in the same chunk as burn-in (C3/C4), dual-path per project rule. |
| **Off means off** | No `CaptionEffect` ⇒ `wantsCaption` false ⇒ unchanged transcode. Byte-identity guard in C3. |
| **Snappy-load** | Model + whisper process load only on first Generate; nothing at tray start. Guarded. |
| **Packaging** | `fetch-whisper.ps1` adds the binary to the bundle; models download post-install (decision #2). GPL/licence notices for whisper.cpp carried like ffmpeg's. |
| **Testing discipline** | Cue model, resolver, JSON parser, model-store checksum stay headless-tested; Avalonia wiring boot-verified. |

## Risks & watch-items

- **whisper.cpp binary variants** (CPU vs BLAS/CUDA, DLL set) — pin a plain CPU x64 build for portability;
  a GPU build is a later opt-in, not v1.
- **Timing accuracy** — Whisper segment timestamps can be coarse; word-level (`-ml`/`--max-len`) gives
  tighter cues at some cost. Evaluate in C1; the cue editor (C5) is the safety net.
- **Transcription latency** on a long clip — always off the UI thread with progress + cancel; consider
  transcribing only the narration span, not silence.
- **Legibility** — need a background box / outline for busy frames; make it the default style.
- **Licensing** — whisper.cpp is MIT; models carry their own terms. Carry the notices with the app.

## Open questions (non-blocking; resolve in-flight)

1. **Cue granularity default** — segment-level (fewer, longer) vs word-level (karaoke-tight). Propose
   segment-level default, word-level as an option.
2. **Style scope** — one style per clip (simplest) vs per-cue overrides. Propose per-clip for v1.
3. **Voiceover transcription** — include editor-voiceover clips in v1, or mic-only first? Propose mic +
   voiceover both, projected as in the anchoring note.
4. **Bundle the binary vs download it too** — proposed: bundle the (small) binary at release, download only
   models. Revisit if the CPU build's DLL set is heavier than expected.
