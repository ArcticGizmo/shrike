<#
.SYNOPSIS
    Fetches the ffmpeg.exe that Shrike bundles for recording/export.

.DESCRIPTION
    Downloads a pinned, prebuilt GPL ffmpeg, verifies its SHA-256, and drops ffmpeg.exe into the output
    folder (default: the publish folder, next to shrike.exe, where Shrike.Core.Recording.Ffmpeg.Locate()
    finds it first). The release workflow calls this before `vpk pack` so ffmpeg ships inside the package.

    Why a pinned prebuilt rather than building from source: it is the reliable, fast path in GitHub Actions
    (a source build of ffmpeg on a runner is slow and brittle). To update, bump $Version below, run this
    once with an empty $Sha256 to print the new hash, paste it in, and commit.

    Leanness note: the pinned build is a full static ffmpeg (~150 MB) - larger than the ~30-50 MB "lean"
    target. Because the locator only needs *an* ffmpeg.exe at this path, you can later drop a hand-built
    lean exe here instead with no code change; this script just automates the reproducible CI fetch.

    KEEP THIS FILE PURE ASCII (no BOM) so Windows PowerShell 5.1 parses it correctly.

.PARAMETER OutDir
    Where ffmpeg.exe is written. Defaults to ./publish.
#>
param(
    [string] $OutDir = 'publish'
)

$ErrorActionPreference = 'Stop'

# --- The pin (bump these to update) --------------------------------------------------------------------
# GyanD/codexffmpeg publishes versioned, GPL full builds on GitHub Releases (static single exe, includes
# libx264/libx265, the qsv/nvenc/amf hardware encoders, and the gif/webp/mp4 muxers Shrike uses).
$Version = '7.1'
$Url     = "https://github.com/GyanD/codexffmpeg/releases/download/$Version/ffmpeg-$Version-full_build.zip"
# Run once with this empty to have the script print the downloaded zip's hash, then paste it here.
$Sha256  = ''

# -------------------------------------------------------------------------------------------------------

try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("shrike-ffmpeg-" + [guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'ffmpeg.zip'
    Write-Host "Downloading ffmpeg $Version ..."
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    $got = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($Sha256)) {
        Write-Warning "No SHA-256 pinned. Downloaded zip hash is:`n  $got`nPaste that into `$Sha256 in this script and re-run to enforce it."
    }
    elseif ($got -ne $Sha256.ToLowerInvariant()) {
        throw "ffmpeg zip SHA-256 mismatch.`n  expected $($Sha256.ToLowerInvariant())`n  actual   $got`nRefusing to use it."
    }
    else {
        Write-Host "  SHA-256 verified  $got"
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $work -Force

    $exe = Get-ChildItem -Path $work -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $exe) { throw 'ffmpeg.exe not found inside the downloaded archive.' }

    $dest = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path (Get-Location) $OutDir }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    $target = Join-Path $dest 'ffmpeg.exe'
    Copy-Item -LiteralPath $exe.FullName -Destination $target -Force

    $mb = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
    Write-Host "ffmpeg.exe ($mb MB) -> $target"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
