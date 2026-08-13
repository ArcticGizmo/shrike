<#
.SYNOPSIS
    Installs Shrike on Windows.

.DESCRIPTION
    Downloads the Velopack installer (Shrike-win-Setup.exe) for a GitHub release, verifies it against the
    SHA256SUMS.txt published alongside it, and runs it. Every update after that is in-app: open Shrike from
    the tray and choose About -> Check for updates.

    Designed to be run as a one-liner:

        irm https://raw.githubusercontent.com/ArcticGizmo/shrike/main/install.ps1 | iex

    Downloading via PowerShell rather than a browser skips the mark-of-the-web, so this route avoids the
    "Windows protected your PC" SmartScreen dialog (the build is not code-signed yet).

    KEEP THIS FILE PURE ASCII (no byte-order mark). Windows PowerShell 5.1 decodes it as the system
    codepage, so a stray non-ASCII dash or quote can silently terminate a string and mis-parse the script.

.PARAMETER Version
    Install a specific version (e.g. 0.1.0) instead of the latest. A leading "v" is fine. Also reads
    $env:SHRIKE_VERSION, which is how you pin through the piped one-liner:
        $env:SHRIKE_VERSION = '0.1.0'; irm .../install.ps1 | iex

.PARAMETER Repo
    owner/name of the GitHub repository to install from. Defaults to ArcticGizmo/shrike; override for a
    fork. Also reads $env:SHRIKE_REPO.
#>
#Requires -Version 5.1
param(
    [string] $Version = $env:SHRIKE_VERSION,
    [string] $Repo    = $(if ($env:SHRIKE_REPO) { $env:SHRIKE_REPO } else { 'ArcticGizmo/shrike' })
)

# Failures are raised with `throw`, never `exit`: this script is normally executed by `iex` inside the
# user's own shell, and `exit` there would close their session.
function Install-Shrike {
    [CmdletBinding()]
    param([string] $Version, [string] $Repo)

    $ErrorActionPreference = 'Stop'

    $SetupAsset = 'Shrike-win-Setup.exe'
    $SumsAsset  = 'SHA256SUMS.txt'

    # --- Preflight -------------------------------------------------------------------------------------
    if ([System.Environment]::OSVersion.Platform -ne 'Win32NT') {
        throw 'Shrike is a Windows desktop app and only installs on Windows.'
    }
    if (-not [System.Environment]::Is64BitOperatingSystem) {
        throw 'Shrike ships as 64-bit only; this looks like a 32-bit Windows.'
    }
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
        Write-Warning 'ARM64 Windows detected. Shrike is x64-only, so it will run under emulation.'
    }
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

    # --- Resolve the release ---------------------------------------------------------------------------
    $release = Get-ShrikeRelease -Repo $Repo -Version $Version
    $tag     = $release.tag_name
    Write-Host "Installing Shrike $tag" -ForegroundColor Cyan

    $setupUrl = Get-AssetUrl -Release $release -Name $SetupAsset
    $sumsUrl  = Get-AssetUrl -Release $release -Name $SumsAsset

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("shrike-install-" + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $work | Out-Null
    $keepWork = $false
    try {
        # --- Download + verify -------------------------------------------------------------------------
        # GitHub serves the manifest as octet-stream, so .Content is a byte[] on Windows PowerShell 5.1.
        $raw   = (Invoke-WebRequest -Uri $sumsUrl -UseBasicParsing).Content
        $sums  = if ($raw -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($raw) } else { [string]$raw }
        $want  = Get-ExpectedHash -Sums $sums -Name $SetupAsset -Tag $tag

        $setup = Join-Path $work $SetupAsset
        Save-File -Uri $setupUrl -OutFile $setup -Label $SetupAsset

        $got = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        if ($got -ne $want) {
            Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
            throw @"
Checksum mismatch for $SetupAsset - refusing to install.
  expected  $want
  actual    $got
The download has been deleted. Retry; if it keeps failing, report it at $(Get-RepoUrl $Repo)/issues.
"@
        }
        Write-Host "  SHA-256 verified  $($want.ToLowerInvariant())" -ForegroundColor DarkGray

        # --- Install -----------------------------------------------------------------------------------
        # Velopack's installer needs no admin rights: it installs to %LocalAppData%\Shrike, registers the
        # uninstaller + Start Menu shortcut, and launches shrike.exe before exiting.
        #
        # Do NOT use `Start-Process -Wait`: it waits for the started process AND its descendants, including
        # the Shrike window it launches - the wait would not return until the user closed Shrike. Wait on
        # the Setup process's own handle instead.
        Write-Host 'Running the installer...'
        $psi = [System.Diagnostics.ProcessStartInfo]::new($setup)
        $psi.UseShellExecute = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        if (-not $proc) { throw "Could not start $SetupAsset." }

        if (-not $proc.WaitForExit(10 * 60 * 1000)) {
            $keepWork = $true
            Write-Warning "The installer is still running after 10 minutes - leaving it to finish. Delete $work once Shrike has installed."
            return
        }
        if ($proc.ExitCode -ne 0) {
            throw "The Shrike installer exited with code $($proc.ExitCode)."
        }
    }
    finally {
        if (-not $keepWork) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }

    Write-Host ''
    Write-Host "Shrike $tag is installed and starting." -ForegroundColor Green
    Write-Host '  It lives in the tray (no main window). Find it in the Start Menu; installed under %LocalAppData%\Shrike (no admin needed).' -ForegroundColor DarkGray
    Write-Host '  Press Alt+Shift+Q to open the capture chooser, then pick a capture or Record region.' -ForegroundColor DarkGray
    Write-Host '  Updates from here are in-app: tray -> About Shrike -> Check for updates.' -ForegroundColor DarkGray
}

function Get-RepoUrl { param([string] $Repo) "https://github.com/$Repo" }

# The GitHub release metadata for $Version, or the latest release when it's blank.
function Get-ShrikeRelease {
    param([string] $Repo, [string] $Version)

    $headers = @{ 'Accept' = 'application/vnd.github+json'; 'User-Agent' = 'shrike-install.ps1' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $uri  = "https://api.github.com/repos/$Repo/releases/latest"
        $what = 'the latest release'
    }
    else {
        $tag  = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
        $uri  = "https://api.github.com/repos/$Repo/releases/tags/$tag"
        $what = "release $tag"
    }

    try {
        return Invoke-RestMethod -Uri $uri -Headers $headers -UseBasicParsing
    }
    catch {
        throw "Could not look up $what of $Repo. $($_.Exception.Message)"
    }
}

function Get-AssetUrl {
    param($Release, [string] $Name)

    $asset = $Release.assets | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $asset) {
        throw "Release $($Release.tag_name) has no $Name asset, so this installer can't verify or install it. Install by hand from $($Release.html_url)."
    }
    return $asset.browser_download_url
}

# Pulls the one line for $Name out of a sha256sum-format manifest ("<64 hex>  <filename>").
function Get-ExpectedHash {
    param([string] $Sums, [string] $Name, [string] $Tag)

    foreach ($line in $Sums -split "`r?`n") {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $Name) {
            return $Matches[1].ToUpperInvariant()
        }
    }
    throw "SHA256SUMS.txt for $Tag has no entry for $Name, so the download can't be verified."
}

# Streams a URL to disk with a progress bar. No Authorization header: asset URLs redirect to a pre-signed
# objects.githubusercontent.com URL that rejects requests carrying one.
function Save-File {
    param([string] $Uri, [string] $OutFile, [string] $Label)

    $req = [System.Net.WebRequest]::CreateHttp($Uri)
    $req.UserAgent = 'shrike-install.ps1'
    $req.Timeout = 60000
    $req.ReadWriteTimeout = 60000

    $resp = $req.GetResponse()
    try {
        $total = $resp.ContentLength
        $shown = -1
        $in  = $resp.GetResponseStream()
        $out = [System.IO.File]::Create($OutFile)
        try {
            $buffer = [byte[]]::new(131072)
            $read = 0
            while (($n = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $out.Write($buffer, 0, $n)
                $read += $n
                if ($total -gt 0) {
                    $pct = [int](100 * $read / $total)
                    if ($pct -ne $shown) {
                        $shown = $pct
                        Write-Progress -Activity "Downloading $Label" `
                            -Status ("{0:N1} of {1:N1} MB" -f ($read / 1MB), ($total / 1MB)) -PercentComplete $pct
                    }
                }
            }
        }
        finally {
            $out.Dispose()
            $in.Dispose()
            Write-Progress -Activity "Downloading $Label" -Completed
        }
        if ($total -gt 0 -and (Get-Item -LiteralPath $OutFile).Length -ne $total) {
            throw "$Label downloaded incompletely ($((Get-Item -LiteralPath $OutFile).Length) of $total bytes)."
        }
    }
    finally {
        $resp.Dispose()
    }
    Write-Host ("  downloaded {0} ({1:N1} MB)" -f $Label, ((Get-Item -LiteralPath $OutFile).Length / 1MB))
}

Install-Shrike -Version $Version -Repo $Repo
