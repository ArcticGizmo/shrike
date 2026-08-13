#!/usr/bin/env pwsh
# Regenerates every raster icon asset from the source-of-truth SVG (shrike.svg).
#
#   src/Shrike.App/Assets/icon.png    256x256 PNG   (window icons + in-app logo)
#   src/Shrike.App/Assets/shrike.ico  multi-res ICO (tray icon + .exe ApplicationIcon + vpk --icon)
#   landing-icon.png                   512x512 PNG   (README header)
#
# Run this after editing shrike.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'IconGen'
dotnet run --project $proj -c Release
