# Packaging & updates

Shrike is packaged for Windows with [Velopack](https://velopack.io). A build produces an installer
(`Setup.exe`), a portable zip, and a release feed. Updates are **notification-only** (the app tells
you a newer version exists and the About window can apply it, but launch never auto-updates), and
builds are **not code-signed** yet. This mirrors sprig's setup.

## Prerequisites

- .NET 10 SDK
- The `vpk` CLI, matching the `Velopack` NuGet version referenced by `Shrike.App` (currently 1.2.0):
  ```sh
  dotnet tool install -g vpk
  ```

## Build a release

From the repo root, publish the app self-contained and pack it:

```sh
dotnet publish src/Shrike.App/Shrike.App.csproj -c Release -r win-x64 --self-contained true -o ./publish

vpk pack --packId Shrike --packVersion 0.1.0 --packDir ./publish \
  --mainExe shrike.exe --packTitle Shrike --icon src/Shrike.App/Assets/shrike.ico -o ./feed
```

`./feed` then contains `Shrike-win-Setup.exe`, `Shrike-<version>-full.nupkg`, a portable zip, and a
`RELEASES` index. Packing a later `--packVersion` into the same `-o` directory appends to the feed
and generates a delta.

> `vpk` verifies that `VelopackApp.Build().Run()` is the first meaningful call in `Program.Main` — the
> hook handles the install/update lifecycle and must stay first. Shrike's hook also re-asserts the
> autostart entry on update (`OnAfterUpdateFastCallback`), so the login `Run` key follows the new
> version rather than pointing at the old one.

**Publish config.** `Shrike.App.csproj` sets `PublishReadyToRun` so the cold path is pre-JITted.
Trimming is deliberately **off** — Avalonia leans on reflection, and the startup-budget win from R2R
alone is enough without risking a trimmed-away type at runtime.

### ffmpeg (M6.3)

Recording/export need ffmpeg. The shipping plan is to bundle a **lean GPL ffmpeg** (~30–50 MB, only
our codecs) next to `shrike.exe`, where `Shrike.Core.Recording.Ffmpeg.Locate()` finds it first. Until
that M6.3 packaging step lands, a dev machine uses the copy in `%LOCALAPPDATA%\Shrike\ffmpeg` (or a
`SHRIKE_FFMPEG` override). The lean build carries the GPL licence + written-offer notices required by
redistribution.

## Install

`Setup.exe` installs per-user to `%LocalAppData%\Shrike` (no admin needed) with Start Menu + Desktop
shortcuts. `--silent` installs without UI. Uninstall via `%LocalAppData%\Shrike\Update.exe --uninstall`
(or Add/Remove Programs). Because the download is unsigned, a browser download trips SmartScreen's
"unknown publisher" prompt on first run; a PowerShell-based install one-liner (M6.3) avoids the
mark-of-the-web and so the prompt.

## Updates

On launch the app runs a **notify-only** check (`UpdateChecker`, background, swallows all failures) and
shows a dismissible toast if a newer release exists. The **About** window (tray → *About Shrike…*)
shows the installed version, the embedded changelog, and a **Check for updates** → **Install & restart**
path that applies an available release.

- The feed defaults to Shrike's GitHub Releases (`UpdateChecker.DefaultFeedUrl`). **TODO(release):**
  point it at the real repo once it exists.
- `SHRIKE_UPDATE_FEED` (a directory path or URL) overrides the feed — handy for testing an update flow
  against a local `./feed` folder without publishing:
  ```sh
  SHRIKE_UPDATE_FEED=./feed "%LocalAppData%\Shrike\current\shrike.exe"
  ```
- If the app wasn't installed via Velopack (e.g. run from the build output), the check is a no-op —
  no network call.

## Changelog

`CHANGELOG.md` (repo root, [Keep a Changelog](https://keepachangelog.com/) format) is **embedded** into
the app (resource `CHANGELOG.md`) at build time and shown by the About window.

## Not yet done (future / M6.3)

- **Lean ffmpeg bundle** + the CI step that produces it.
- **Code signing** — `vpk pack` warns that files are unsigned; add `--signParams` once a cert exists.
- **CI release workflow** (tag-triggered `dotnet publish` + `vpk pack` + GitHub Release) and a verified
  install one-liner.
- **App icon** (`shrike.ico` from the amber shrike mark) — the `--icon` path above assumes it exists.
