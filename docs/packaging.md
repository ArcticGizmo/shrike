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

### ffmpeg

Recording/export need ffmpeg, bundled next to `shrike.exe` (where `Shrike.Core.Recording.Ffmpeg.Locate()`
finds it first). [`tools/fetch-ffmpeg.ps1`](../tools/fetch-ffmpeg.ps1) downloads a **pinned, hash-verified**
prebuilt GPL ffmpeg and drops `ffmpeg.exe` into the publish folder; the release workflow runs it before
`vpk pack`. A pinned prebuilt (not a source build) is the reliable path in GitHub Actions — to update,
bump `$Version` in the script, run once to print the new hash, paste it into `$Sha256`, and commit.

The pin is a **full static** build (~150 MB), larger than the ~30–50 MB "lean" target. Because the locator
only needs *an* `ffmpeg.exe` at that path, a hand-built lean exe can later be dropped there with no code
change — the fetch script just automates the reproducible CI download in the meantime. The GPL licence +
written-offer notices required by redistribution ship alongside. A dev machine can instead use a copy in
`%LOCALAPPDATA%\Shrike\ffmpeg` (or a `SHRIKE_FFMPEG` override) with no bundling step.

### Icon

All raster icon assets are generated from the single source-of-truth [`shrike.svg`](../shrike.svg) by
[`tools/gen-icons.ps1`](../tools/gen-icons.ps1) (a small SVG→PNG/ICO tool under `tools/IconGen`):
`src/Shrike.App/Assets/shrike.ico` (the `.exe`/`vpk --icon` icon), `Assets/icon.png` (256px), and
`shrike-icon.png` (512px, the README header). Re-run it after editing the SVG and commit the assets.

## Install

The primary install path is the PowerShell one-liner ([`install.ps1`](../install.ps1) at the repo root):

```powershell
irm https://raw.githubusercontent.com/quartexsoftware/shrike/main/install.ps1 | iex
```

It resolves the latest GitHub release, fetches `SHA256SUMS.txt` + `Shrike-win-Setup.exe`, verifies the
installer against the manifest (deleting it rather than running it on any mismatch), then hands off to
Velopack's setup. Downloading via PowerShell rather than a browser skips the mark-of-the-web, so it avoids
the SmartScreen "unknown publisher" dialog an unsigned browser download hits. Pin a version with
`$env:SHRIKE_VERSION = '0.1.0'` before the pipe, or a fork with `$env:SHRIKE_REPO`.

> **`install.ps1` must stay pure ASCII** (no BOM) — Windows PowerShell 5.1 decodes it as the system
> codepage, and a stray non-ASCII dash/quote silently terminates a string.

`Setup.exe` itself installs per-user to `%LocalAppData%\Shrike` (no admin needed) with Start Menu + Desktop
shortcuts. `--silent` installs without UI. Uninstall via `%LocalAppData%\Shrike\Update.exe --uninstall`
(or Add/Remove Programs).

## Cutting a release (CI)

[`.github/workflows/release.yml`](../.github/workflows/release.yml) is triggered by **pushing a `v*` tag**.
On a `windows-latest` runner it derives the version from the tag, publishes, fetches the bundled ffmpeg,
`vpk pack`s, generates a `SHA256SUMS.txt` (and fails the build if it doesn't match `Shrike-win-Setup.exe`),
and creates the GitHub Release with the Velopack feed attached. Keep the pushed tag and the csproj
`<Version>` in step.

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

## Not yet done (future)

- **Create the GitHub repo** — no git remote is configured yet. Once it exists, replace the
  `TODO(release)` placeholder in `UpdateChecker.DefaultFeedUrl` and confirm the `quartexsoftware/shrike`
  slug in `install.ps1` / `release.yml`.
- **Pin the ffmpeg hash** — `tools/fetch-ffmpeg.ps1` currently downloads without an enforced `$Sha256`
  (it warns and prints the hash). Paste the printed hash in to lock it.
- **Code signing** — `vpk pack` warns that files are unsigned; add `--signParams` once a cert exists.
- **A truly lean ffmpeg** — swap the ~150 MB pinned prebuilt for a ~30–50 MB custom build dropped at the
  same path, if bundle size becomes a concern.
