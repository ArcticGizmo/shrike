# Shrike — Smooth Cursor (Follow-ups Plan)

> The next pass on the **smooth cursor** feature. SC1–SC5 are built, tested, and un-gated (opt-in in
> Release); see [`smooth-cursor-plan.md`](smooth-cursor-plan.md) for that work. This plan is everything
> deferred along the way — ship tasks, release-facing UX, QA, performance, and backlog.

**Status:** In progress · **Date:** 2026-08-18 · **Owner:** Jon · **Branch:** `feature/smooth-cursor-followups`
**Feature state:** merged + released at **v0.2.0**, opt-in via the **Smooth cursor** HUD toggle (off by default).

## Where it stands

Working end-to-end today: record with the pointer logged → smoothed synthetic cursor + click ripples →
auto-zoom toward clicks → one managed decode→compose→encode pass → every export preset (via a
high-quality intermediate). Tunable and previewed live in the editor. 225 tests green; Debug + Release clean.

**Shipped:** §A — `feature/smooth-cursor` merged to `main` (PR #4, `23cd471`); **v0.2.0** bumped, tagged, and
released (the tag contains the merge).

**Not done:** a release-grade tuning UI (§B, next), a real-hardware QA pass (§C), and the performance and
fidelity items below.

## Guiding rules (carried from the main plan)

- **Opt-in stays opt-in.** The HUD toggle is off by default; nothing changes for users who don't turn it on.
- **WYSIWYG.** The editor preview and the exported file must keep matching (same projection + zoom).
- **Core stays UI-free and tested.** New logic lands in `Shrike.Core` with headless tests.
- **Off means off.** With no track, the export path is byte-for-byte the normal transcode.

---

## A · Ship it  *(P1)* ✅

*Get the finished feature into users' hands.*

- ✅ **Merged**: `feature/smooth-cursor` landed on `main` via PR #4 (`23cd471`).
- ✅ **Version**: **v0.2.0** bumped (csproj + changelog), tagged, and released; the `v0.2.0` tag contains the merge.
- **Manual QA pass** (see §C for the matrix) — still outstanding, folded into §C.

**Exit:** ✅ `main` carries the feature; `v0.2.0` released. *(Real fresh-install record→export verification
rolls into the §C hardware pass.)*

## B · Release-facing tuning UX  *(P2)*

*The tuning panel works but reads like a dev tool (`Min cutoff` / `Beta`). Make it presentable.*

- **Relabel** the smoothing control to a single **Smoothness** slider (0–100, higher = smoother), mapping
  internally to `MinCutoff`/`Beta` — hide the raw 1€ params. Keep **Zoom on/off + amount**.
- **Cursor size** control (wire `CursorStyle.Height`), and a **click ripple on/off** toggle; optionally the
  press **"punch"** (already in `CursorStyle`, off by default).
- **Persist** the chosen smoothing/zoom/size to `AppSettings` so they carry across sessions (today the
  editor opens at `CursorSmoothing.Default` / `ZoomConfig.Default` each time).
- Make the cursor **size scale with export resolution** (fixed 24px looks small on 1080p, large on 480p) —
  or expose it and pick a sensible default per height.

**Exit:** a non-developer can dial in the look from the editor, and it sticks.

## C · Fidelity & correctness QA  *(P2)*

*Unit-tested, but not exercised on real hardware across the awkward cases.*

- **Multi-monitor / mixed-DPI**: record a region on a secondary, non-100%-scale monitor; confirm the cursor
  lands exactly on the real pointer path in the export (the mapping is unit-tested but not hardware-verified).
- **Look review**: cursor contrast on light/dark/busy backgrounds; ripple timing/size; zoom ease feel;
  cursor-stays-constant-size-through-zoom is the intended look (confirm).
- **Pause/resume** during a smooth-cursor recording: track timestamps stay aligned across the pause.
- **Hook degradation**: behaviour when the `WH_MOUSE_LL` hook can't run (secure desktop / anti-cheat) —
  should silently produce a normal recording with no track.
- **Very short / no-movement / no-click** clips: no crashes, sensible output.

**Exit:** a short QA checklist run on real hardware with no surprises.

## D · Performance  *(P3)*

*Fine offline, but heavy on big/long clips.*

- **Zoom resample** is managed per-frame bilinear (CPU). Parallelise across rows (`Parallel.For`) and/or
  skip the resample entirely on frames where zoom ≈ 1. Measure 1080p throughput.
- **Two-encode cost**: parity uses an intermediate + final encode. If quality/speed matters, reconstruct the
  preset's encode args to read the composited rawvideo directly (single encode) — bigger change; only if the
  double transcode proves a real problem. The generous-bitrate intermediate keeps loss ~transparent for now.
- **Export progress**: the two stages map to 0–50% / 50–100%; confirm it reads smoothly and cancel is prompt.

**Exit:** a multi-minute 1080p smooth-cursor export completes in reasonable time with a smooth progress bar.

## E · Backlog  *(P3)*

- **Real cursor capture** — bundled vector arrow only today; optionally capture the user's actual pointer
  (including shape changes: I-beam, hand, resize) for authenticity. Needs logging cursor-shape changes +
  storing bitmaps in the track.
- **Recordings retention settings** — the working-folder caps (20 files / 2 GB / 14 days) are hardcoded;
  expose in settings if users want control.
- **Keystroke / click-sound overlays**, motion blur on fast moves, cursor themes — the usual "produced-look"
  extras. Revisit only if the core sees real use.

## Open questions / decisions

1. **Ship at v0.2.0 or hold for the UX polish (§B)?** It's usable now but the tuning UI is rough.
2. **Cursor size**: fixed px, resolution-scaled, or user-controlled (§B)?
3. **Single-encode parity (§D)** — worth the complexity, or is the intermediate fine indefinitely?

## Done (for reference)

SC1 track capture · SC2 One-Euro smoothing + coordinate mapping · SC3 decode→compose→encode pipeline ·
SC4 synthetic cursor + click ripple · SC5 auto-zoom · un-gate + full export-preset parity. All in
[`smooth-cursor-plan.md`](smooth-cursor-plan.md).
