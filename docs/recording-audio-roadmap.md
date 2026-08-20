# Shrike — Recording & Audio (feature roadmap)

> Where recording goes next. Today Shrike records **video only** — audio is stripped everywhere
> (`-an`, `a=0`) and was an explicit v1 non-goal. This doc lays out the audio/narration line of work,
> the features that ride on it, and the order to build them in. The headline is **narration you record
> *in the editor*, with ad-hoc punch-in re-recording**, and the sleeper is **auto-captions** for the
> muted-autoplay Slack case.

**Status:** Proposed · **Date:** 2026-08-19 · **Owner:** Jon · **Branch:** `feature/recording`
**Builds on:** the non-destructive, source-time-anchored edit model and the compositor chain
([`effects-timeline-plan.md`](effects-timeline-plan.md)).

---

## Why this seam

Normal screen recorders **weld audio to video at capture time**, so a fumbled sentence means
re-recording the whole take. Shrike doesn't have to: the edit model is already non-destructive and
**source-time anchored**, so an audio track can be an independent, editable layer that rides cuts and
trims for free — exactly like every effect does today. That turns "add a mic" into something more
valuable: **record narration against the preview, and re-record just the slice you fumbled.**

The second observation drives the ordering below: **Slack clips autoplay muted.** The moment there's an
audio track, local transcription gives **burned-in captions** — arguably higher value than the audio
itself for the stated use case, and cheap to render because it's just timed text over frames.

## Guiding rules (carried from the project)

- **WYSIWYG.** Anything audible in the export must be audible in the preview — the preview *plays the
  narration* during scrub/playback. New visual bits (waveform lane, captions) preview live too.
  ([`CLAUDE.md`](../CLAUDE.md))
- **Core stays UI-free and tested.** Capture, mixing, and caption models land in `Shrike.Core` headless.
- **Output-time authoring, with capture-link.** Audio anchors to output/timeline time (uniform,
  predictable); capture-origin clips link to their source span so they *move with their video* and don't
  drift. See the lip-sync safeguards in [`recording-implementation-plan.md`](recording-implementation-plan.md).
- **Off means off.** With no audio track and no captions, the export path stays byte-for-byte today's
  transcode. Audio is opt-in; silent clips get `-an` exactly as now.
- **Non-destructive.** Audio and captions are metadata + sidecar media referenced from `*.edit.json`;
  the source recording is never mutated.
- **Snappy-load budget stays green.** Transcription/caption models load lazily, never at tray start.

---

## The features, in build order

Priorities are P1 (do first) → P4 (later). The recommendation is to ship **M1 + M2 as one milestone** —
they share the transcription-adjacent plumbing conceptually and are each other's best friend, but M2 can
follow if scope needs trimming. Everything after reuses the same `IAudioSource` / compositor patterns.

### M1 · Narration & mic capture  *(P1)*  — **the foundation**

The core of Jon's ask. Two capture modes, one editable track.

- **Mic test *before* recording** *(non-negotiable — this is the recurring pain point)*: device picker,
  live input-level meter, "record 3s and play it back" check, and a remembered default device. Nothing
  is worse than finishing a take and finding the wrong input was armed or the mic was muted.
- **Live mic at record time** — narrate while you demo. New `IAudioSource` (WASAPI, via NAudio or thin
  P/Invoke to match the existing interop style), captured into the recording session on the same
  pause-excluded clock as `MouseTrackRecorder`, written as a sidecar audio file.
- **System-sound (loopback) capture — a first-class option** — record what the machine is playing (app
  audio, a video, a notification chime) via **WASAPI loopback** on the render endpoint. Selectable
  independently and *alongside* the mic (mic-only, system-only, or both), mixed on the same clock. Each
  source is a separate sidecar so gains stay independently adjustable in the editor. Ducking system audio
  under narration is a natural M3 follow-up.
- **Voiceover *in the editor*** — sit in the timeline, hit record, the preview plays, talk over it.
- **Ad-hoc punch-in re-record** — scrub back, re-record just a span; it overwrites that slice of the
  narration track and leaves the rest. This is the differentiating flow.
- **Waveform lane** in the effects timeline; **preview plays the narration** (WYSIWYG).
- **Export mux** — un-strip the encode: the `FrameCompositePipeline` second encode gains an audio input;
  the no-effects path grows an audio-mux branch. `ExportSize` starts accounting for an audio stream.

*Model growth:* `ClipEdit` → v3, adding an `AudioTrack` of output-time-anchored `AudioClip`s (with a
source-span link on capture-origin clips) referencing sidecar media. New `IAudioSource` mirroring
`IFrameSource`. Detailed phasing in [`recording-implementation-plan.md`](recording-implementation-plan.md).

### M2 · Auto-captions / burned-in subtitles  *(P1–P2)*  — **the sleeper, investigate later**

Flagged by Jon as "definitely interesting to investigate later." Because Slack autoplays muted, this may
matter more than the audio for reach.

- **Local transcription** — bundle a whisper-family model (whisper.cpp), fetched like ffmpeg is via
  `tools/fetch-*.ps1`; runs offline, no upload. Transcribe the narration you just recorded.
- **Editable, styled captions** as a first-class effect — a `CaptionCompositor` in the chain; it's timed
  text over frames and reuses the existing text-rendering path (`AnnotationSurface`).
- **Preview** shows captions live, same as any overlay.

*Note:* this is deliberately staged **after** M1 lands, because it needs an audio track to transcribe and
should not block shipping narration.

### M3 · Fast-follows that reuse M1 plumbing  *(P2–P3)*

- **Silence / dead-air auto-trim** — detect quiet gaps, offer to cut them. On-brand for "small, snappy
  clips"; cheap once audio exists.
- **Keystroke & click-sound / keycap overlays** — already in
  [`smooth-cursor-followups.md`](smooth-cursor-followups.md) §E. Visual keycaps are valuable even without
  audio; a `KeycapCompositor` overlay.
- **Webcam / talking-head PIP** — a circular camera inset (Loom-style). Just another `IAudioSource`'s
  video sibling + a `CameraCompositor` overlay; fits the chain exactly.
- **Background music track** — trivial once the mux path exists; a second audio input, ducked under
  narration.

### M4 · Trim-by-transcript  *(P4)*  — **the payoff, big lift**

Descript-style: once there's a transcript, deleting a sentence of text deletes that span of video+audio.
The "wow" feature, and the largest piece. Treat it as the long-term reward for doing M2's transcription
well, not near-term scope.

### Future / speculative — audio-only capture pathway

Once the audio source, level metering, and mic-check gate exist for M1, an **audio-only recording
pathway** (capture narration or system sound with *no* video track at all) becomes a small increment —
the video source simply isn't started, and the export is an audio-only mux. Possible uses: a quick voice
memo, capturing a call or system audio, or laying down narration *first* and building the capture around
it. **Not scoped here** — noted so M1's capture plumbing is designed without assuming a video track is
always present (i.e. the recording session shouldn't hard-require an `IFrameSource`).

---

## Recommended ordering (one line)

**M1 (narration + mic test) → M2 (captions) → M3 (silence-trim, keycaps, webcam, music) → M4
(trim-by-transcript).** Ship M1 first and standalone; it's the biggest jump in what Shrike can produce
and the hard part (making audio editable) is mostly already solved by the non-destructive model.

## How it maps onto today's code

- **Capture seam:** new `IAudioSource` alongside `IFrameSource`; capture into `RecordingSession` on the
  pause-excluded clock, sidecar audio file next to the source MP4.
  (`src/Shrike.Core/Recording/{Recorder,RecordingSession}.cs`)
- **Effect seam:** `CaptionCompositor` / `KeycapCompositor` / `CameraCompositor` append to
  `CompositorChain`, driven by `FrameCompositePipeline`, previewed via matching `PreviewSurface` methods.
- **Export mux:** `ExportCommand` / `FfmpegMp4Encoder` stop forcing `-an` when an audio track exists; add
  the mic/loopback/music inputs and a mix filter; `ExportSize` accounts for the audio stream.
- **Persistence:** `ClipEdit` → **v3** with a forgiving v2→v3 migration; audio + captions as metadata +
  sidecar references.

## Open risk to decide up front

Audio + preview playback stresses the **dual-path preview** already flagged as tech debt (the preview
re-derives compositor math in the Avalonia layer instead of running the real Core compositors — see
[`effects-timeline-plan.md`](effects-timeline-plan.md) stretch goal). Adding audio sync is a good forcing
function to unify onto the real chain. **Decide at M1 kickoff:** does this milestone pay that debt down,
or defer it once more? Deferring is viable but each new previewed effect (captions, keycaps, PIP) doubles
the authoring cost until it's paid.

---

*Companion:* [`recording-vision.html`](recording-vision.html) — the UX vision for M1 (mic test →
record → punch-in re-record → waveform editing), to review before solidifying the plan.
