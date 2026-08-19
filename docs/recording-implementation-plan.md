# Shrike — Recording & Audio (implementation plan)

> Milestone- and phase-based plan for adding **audio** to Shrike: live mic + system-sound capture, an
> editor voiceover flow with ad-hoc punch-in re-record, and the export mux — held to the project's
> WYSIWYG / off-means-off / non-destructive rules. Scopes **M1a** and **M1b** in detail; **M2 captions**
> and beyond are referenced, not detailed here.

**Status:** Planned · **Date:** 2026-08-19 · **Owner:** Jon · **Branch:** `feature/recording`
**Companions:** [`recording-audio-roadmap.md`](recording-audio-roadmap.md) (the feature line & ordering) ·
[`recording-vision.html`](recording-vision.html) (the M1 UX vision).

---

## Decisions locked (from scoping Q&A, 2026-08-19)

1. **Capture/playback stack: NAudio `3.0.1`** (latest stable, confirmed on NuGet — v3 is released). It
   provides WASAPI capture (`WasapiCapture`), system-sound capture (`WasapiLoopbackCapture`), device
   enumeration (`MMDeviceEnumerator`), and playback (`WasapiOut`). **Core stays UI-free**: it defines the
   interfaces and models; the NAudio implementation lives in an **App-side adapter** (or a small
   `Shrike.Audio` project), so `Shrike.Core` gains **no** new dependency.
2. **Phasing: split M1a / M1b.** M1a ships live capture + mux + editor playback; M1b adds in-editor
   voiceover and punch-in re-record. Ship M1a standalone.
3. **Anchoring: output/timeline time for all audio**, with **explicit lip-sync safeguards** (see §Lip-sync).
   Uniform, predictable model; capture-origin audio carries a link back to its source span so it *moves
   with its video* under cuts/reorders, plus a manual A/V offset and a re-sync action.

**Working assumptions** (say the word to change):
- Preview audio playback is **purely additive** — it does not touch the visual compositor chain, so M1
  does **not** pay down the dual-path preview debt. (Captions/keycaps in M2/M3 are what force that.)
- Sidecars capture to **WAV** (PCM, lossless, seekable) during recording; audio is encoded to **AAC** only
  at export. One sidecar **per source** (mic, system) so gains stay independent.

## Guiding rules (carried from the project)

- **WYSIWYG.** Audible in export ⇒ audible in preview. The editor **plays the narration** as you scrub.
- **Off means off.** No audio track ⇒ export is byte-for-byte today's transcode, still forcing `-an`.
- **Non-destructive.** Audio is sidecar WAV referenced from `*.edit.json` (**v3**); source MP4 untouched;
  re-records version the sidecar, never overwrite in place.
- **Core stays UI-free and tested.** Models, mixing math, level metering, anchoring resolvers land in
  `Shrike.Core` with headless tests; device I/O is mocked behind interfaces.
- **Snappy-load stays green.** Nothing audio touches tray start — no device enumeration, no NAudio load
  until the recording/mic-check surface opens.

---

## Milestone M1a — Live capture → mux → editor playback

*Goal: record a region with mic and/or system sound, hear it back in the editor against the preview, and
export a Slack-sized clip with mixed audio. Voiceover-in-editor is M1b.*

### Phase A0 · Foundations *(Core, no devices)*
- [ ] Add **NAudio 3.0.1** to the App-side adapter project (or new `Shrike.Audio`); Core references neither.
- [ ] Core interfaces: `IAudioSource` (mirrors `IFrameSource`: `Start/Stop`, `Format`, frame/buffer event),
      `IAudioPlayer`, `IAudioDeviceCatalog` (enumerate + default), `ILevelMeter` (RMS/peak → dB).
- [ ] Core model: `AudioFormat`, `AudioClip` (sidecar ref + source-span link + `OutputOffset` + `Gain` +
      `Muted`), `AudioTrack` (ordered clips), and an `AudioMixPlan` resolver (output-time → active clips
      + gains at time *t*). Pure, deterministic, headless-tested.
- [ ] `WavSidecarWriter` / reader in Core (PCM WAV; no NAudio needed to write a WAV header).
- [ ] **Tests:** mix resolver (overlaps, gains, mute, offset), WAV round-trip, anchoring math.

### Phase A1 · Capture at record time
- [ ] `NAudioMicSource : IAudioSource` (`WasapiCapture`) and `NAudioLoopbackSource : IAudioSource`
      (`WasapiLoopbackCapture`), in the adapter.
- [ ] Wire into the recording session: capture on the **same pause-excluded clock** as `MouseTrackRecorder`
      so audio, video, and the pointer track share one timeline. (`Recorder`, `RecordingSession`.)
- [ ] Write one sidecar WAV per active source next to the source MP4; record the format + start offset.
- [ ] **Design the session so audio can run with no `IFrameSource`** (forward-compat for the future
      audio-only pathway) — don't hard-require video.
- [ ] `RecordingsRetention`: include sidecars in the 20-file / 2 GB / 14-day accounting.
- [ ] **Tests:** clock alignment (audio start vs pause/resume), sidecar written & referenced.

### Phase A2 · The mic-check gate *(the pain-point solve — see vision §01)*
- [ ] Device catalog + **remembered default** input; detect unplug and surface it (no silent fallback).
- [ ] Live **level meter** (RMS + peak, dB) driven by `ILevelMeter` off a monitoring tap.
- [ ] **Test & play back**: record ~3 s to a temp WAV, play it back via `IAudioPlayer`.
- [ ] **Capture-source toggles**: Microphone / System sound (loopback), independently armable.
- [ ] **Gate**: the record action stays disabled until the armed input shows signal.
- [ ] New pre-record view (Avalonia) — the mock in the vision doc; wired through `CaptureController`.
- [ ] **Tests:** catalog/selection logic, meter dB mapping, arm-state gate (device I/O mocked).

### Phase A3 · Export mux *(off still means off)*
- [ ] `ExportCommand`: when the edit has an audio track, stop forcing `-an`; add mic/system WAV inputs and
      an `amix`/`amerge` + per-clip `adelay`/`volume` filter graph derived from `AudioMixPlan`.
- [ ] Feed both export paths: the no-effects trim/concat path **and** `FrameCompositePipeline`'s second
      encode gain the audio input in the same single pass.
- [ ] Encode to **AAC**; keep `-an` verbatim when there is no audio track.
- [ ] `ExportSize`: account for the audio stream in the live estimate; presets in `ExportProfile` note it.
- [ ] **Tests:** arg-builder snapshots (audio vs none), off-means-off byte-identity guard stays green.

### Phase A4 · Editor: waveform, playback & lip-sync controls
- [ ] **Waveform lane** in the effects timeline (`EffectsLane`/new track): render peaks from the sidecar
      (decimated, cached), themed like the vision.
- [ ] **Preview plays audio** synced to the playhead/scrubber (`IAudioPlayer` + `FramePlayer`); play/pause
      already on spacebar. This satisfies WYSIWYG for audio.
- [ ] Per-clip **Gain** and **Mute** in the properties pane.
- [ ] **Lip-sync controls** (see §Lip-sync): capture-link on by default, **A/V offset nudge (± ms)**, and
      **Re-sync to source** action; visual source-span guide on the waveform.
- [ ] **Persistence:** `ClipEdit` → **v3** with a forgiving v2→v3 migration (absent audio track = today).

**M1a exit:** record a region with mic + system sound → open the editor → see two waveforms, hear them in
the preview while scrubbing → export a Slack-small clip with mixed AAC audio. With no audio armed, capture
and export are byte-for-byte unchanged. Debug + Release clean; new Core tests green.

---

## Milestone M1b — In-editor voiceover & punch-in re-record

*Goal: narrate against the playing preview, and re-record just a fumbled span without disturbing the rest.*

### Phase B0 · Record in the editor
- [ ] Arm + record from the timeline editor: the preview plays, mic captures, a **new `AudioClip` is
      inserted at the playhead, anchored to output time**.
- [ ] Live input monitoring + the same mic-check affordance (reuse A2) before the first editor take.
- [ ] Countdown / pre-roll so you're not clipped at the top of the take.

### Phase B1 · Punch-in re-record *(the differentiator)*
- [ ] Select a span on the narration lane → **punch in** → re-record overwrites **only** that slice.
- [ ] Non-destructive **take versioning**: the prior audio is retained as a superseded take; **undo**
      restores it. New WAV segment stitched into the clip's mix plan, not written over the old bytes.
- [ ] Crossfade at the punch boundaries to avoid clicks.
- [ ] **Tests:** span-replace mix plan, undo restores prior take, boundary crossfade math.

### Phase B2 · Take management & polish
- [ ] Multiple takes per clip with a picker; trim handles; fade-in/out handles; snap to playhead/segments.
- [ ] Right-click on the lane: add voiceover, split, delete, re-record span.

**M1b exit:** narrate over the preview; re-record a single sentence and hear only that slice change; undo
brings the previous take back; export reflects the final mix.

---

## Lip-sync handling *(reconciles output-time anchoring with A/V sync)*

Output-time anchoring is simple and predictable, but a **live-captured** mic/system clip could drift from
its own video if segments are cut or reordered. Safeguards, cheapest first:

1. **Capture-link (default on for capture-origin clips).** A clip recorded *with* the video stores its
   source span and is **linked** to that video region; trimming/reordering that region **ripples the audio
   with it**, so it never drifts. Editor-voiceover clips are unlinked (they're authored against output time
   deliberately). Link is toggleable per clip.
2. **A/V offset nudge (± ms).** A per-clip manual offset for fine sync (`adelay`/negative trim at export).
   Standard editor control; also fixes device-latency skew.
3. **Re-sync to source.** One action snaps a linked clip back to its source-span position if it was
   detached.
4. **Drift indicator.** When a linked clip's output position no longer matches its source span, flag it on
   the lane with a one-click realign — desync is *visible*, not silent.

*Net:* the base model is output-time (as chosen), but you can't accidentally lip-sync-drift captured audio,
and you have a manual trim for the residual device latency.

---

## Cross-cutting

- **Schema:** `ClipEdit` v2 → **v3** adds `AudioTrack`; migration treats missing audio as none. Sidecar
  paths are relative to the recording, like the source MP4.
- **Files to touch:** capture `Recording/{Recorder,RecordingSession}.cs` + new `IAudioSource` & adapters;
  model `{ClipEdit,Timeline,Segment}.cs` + new `Audio*`; export `{ExportCommand,VideoExporter,
  FrameCompositePipeline,FfmpegMp4Encoder,ExportSize}.cs`; app `Services/CaptureController.cs`,
  `Views/RecordingHudWindow`, new mic-check view, `Controls/{EffectsLane,TimeRuler}`,
  `Controls/PreviewSurface.cs`, `Views/TimelineEditorWindow`.
- **Packaging:** NAudio is managed (no native fetch like ffmpeg). Confirm Velopack picks up the new
  assemblies; check per-user install size delta.
- **Snappy-load guard:** device enumeration + NAudio init happen lazily on first mic-check/editor-audio
  use, never at tray start — add a startup-cost check.

## Risks & watch-items

- **WASAPI format negotiation / resampling** (shared-mode mix format vs 48 kHz) — normalise early in the
  adapter; test on the dev hardware (mirror the smooth-cursor §C hardware-QA discipline).
- **Loopback + mic clock skew** — two endpoints, two clocks; resample/timestamp against the session clock.
- **A/V playback sync in Avalonia** — the editor must keep audio and the frame scrubber locked; prototype
  in A4 before committing the lane UX.
- **Retention** must not orphan or prematurely delete sidecars referenced by an open edit.

## To verify at kickoff

- [ ] **NAudio 3.0.1** API shape for `WasapiLoopbackCapture` playback-tap + metering on .NET 10 (spike).
- [ ] Whether audio lives in the App project or a dedicated `Shrike.Audio` (keeps Core clean either way).
- [ ] The dual-path preview decision — confirmed **deferred** for M1 unless you want it folded in.
