<#
.SYNOPSIS
    Fetches the whisper.cpp CLI that Shrike bundles for local, offline transcription (captions).

.DESCRIPTION
    Downloads a pinned, prebuilt whisper.cpp Windows build, verifies its SHA-256, and drops whisper-cli.exe
    (and the DLLs it needs) into a 'whisper' subfolder of the output folder - next to shrike.exe, where
    Shrike.Core.Recording.Whisper.Locate() looks (it checks both <app>\whisper-cli.exe and
    <app>\whisper\whisper-cli.exe). The release workflow calls this before `vpk pack` so the engine ships
    inside the package.

    IMPORTANT: this fetches only the small ENGINE BINARY. Transcription MODELS are large and
    language-specific, so they are an opt-in, in-app download at runtime (not shipped in the installer) -
    do NOT fetch a model here.

    Why a pinned prebuilt rather than building from source: same reasoning as fetch-ffmpeg.ps1 - it is the
    reliable, fast path in GitHub Actions. To update, bump $Version below, confirm the asset name still
    matches, run this once with an empty $Sha256 to print the new hash, paste it in, and commit.

    KEEP THIS FILE PURE ASCII (no BOM) so Windows PowerShell 5.1 parses it correctly.

.PARAMETER OutDir
    Parent folder; the binary lands in <OutDir>\whisper. Defaults to ./publish.
#>
param(
    [string] $OutDir = 'publish'
)

$ErrorActionPreference = 'Stop'

# --- The pin (bump these to update) --------------------------------------------------------------------
# ggml-org/whisper.cpp publishes prebuilt Windows binaries as 'whisper-bin-x64.zip' on GitHub Releases
# (a plain CPU x64 build - portable, no GPU/driver assumptions; a GPU build is a later opt-in).
# Verify the tag + asset name at https://github.com/ggml-org/whisper.cpp/releases before bumping.
$Version = 'v1.9.2'
$Url     = "https://github.com/ggml-org/whisper.cpp/releases/download/$Version/whisper-bin-x64.zip"
# Run once with this empty to have the script print the downloaded zip's hash, then paste it here.
$Sha256  = ''

# -------------------------------------------------------------------------------------------------------

try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("shrike-whisper-" + [guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'whisper.zip'
    Write-Host "Downloading whisper.cpp $Version ..."
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    $got = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($Sha256)) {
        Write-Warning "No SHA-256 pinned. Downloaded zip hash is:`n  $got`nPaste that into `$Sha256 in this script and re-run to enforce it."
    }
    elseif ($got -ne $Sha256.ToLowerInvariant()) {
        throw "whisper zip SHA-256 mismatch.`n  expected $($Sha256.ToLowerInvariant())`n  actual   $got`nRefusing to use it."
    }
    else {
        Write-Host "  SHA-256 verified  $got"
    }

    Expand-Archive -LiteralPath $zip -DestinationPath $work -Force

    # Find the CLI (whisper-cli.exe preferred; older builds ship main.exe). Copy it plus every DLL that sits
    # beside it (ggml*.dll, whisper.dll, ...) so the engine is self-contained next to shrike.exe.
    $cli = Get-ChildItem -Path $work -Recurse -Include 'whisper-cli.exe', 'main.exe' | Select-Object -First 1
    if (-not $cli) { throw 'whisper-cli.exe (or main.exe) not found inside the downloaded archive.' }
    $srcDir = $cli.Directory.FullName

    $dest = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path (Get-Location) $OutDir }
    $dest = Join-Path $dest 'whisper'
    New-Item -ItemType Directory -Path $dest -Force | Out-Null

    Copy-Item -LiteralPath $cli.FullName -Destination (Join-Path $dest $cli.Name) -Force
    Get-ChildItem -Path $srcDir -Filter '*.dll' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dest $_.Name) -Force
    }

    $count = (Get-ChildItem -Path $dest -File | Measure-Object).Count
    Write-Host "whisper CLI + $($count - 1) DLL(s) -> $dest"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
