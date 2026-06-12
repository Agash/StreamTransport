#!/usr/bin/env pwsh
# Fetches the pinned FFmpeg 8.1 shared libraries for one or more RIDs into native/ffmpeg/<rid>/.
# These are LGPL builds that include the hardware encoders (nvenc / amf / qsv / videotoolbox / vaapi)
# and exclude the GPL-only software encoders (x264 / x265). The libraries are shipped with the package
# under runtimes/<rid>/native and are gitignored in this repo (fetched on demand).
#
# Usage:  ./eng/fetch-ffmpeg.ps1 [-Rids win-x64,linux-x64,...]
[CmdletBinding()]
param(
    [string[]]$Rids = @('win-x64')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'native/ffmpeg'

# Pinned FFmpeg 8.1 shared builds per RID. Desktop RIDs use BtbN's LGPL builds; linux-arm64 uses the
# jellyfin-ffmpeg Rockchip build (rkmpp) for RK3588-class IRL boards.
$sources = @{
    'win-x64'     = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip'
    'linux-x64'   = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linux64-lgpl-shared-8.1.tar.xz'
    'linux-arm64' = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-linuxarm64-lgpl-shared-8.1.tar.xz'
    # macOS shared dylibs are taken from a pinned Homebrew bottle at package time; see eng/README.md.
}

# Shared-library file patterns per RID family.
$patterns = @{
    'win-x64'     = '*.dll'
    'linux-x64'   = 'lib*.so*'
    'linux-arm64' = 'lib*.so*'
    'osx-x64'     = 'lib*.dylib'
    'osx-arm64'   = 'lib*.dylib'
}

foreach ($rid in $Rids) {
    if (-not $sources.ContainsKey($rid)) {
        Write-Warning "No pinned source configured for RID '$rid'; skipping."
        continue
    }

    $url = $sources[$rid]
    $ridDir = Join-Path $dest $rid
    New-Item -ItemType Directory -Force $ridDir | Out-Null
    $archive = Join-Path $ridDir ('download' + [IO.Path]::GetExtension($url))

    Write-Host "Fetching $rid from $url"
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    $extract = Join-Path $ridDir '_extract'
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $extract | Out-Null

    if ($archive.EndsWith('.zip')) {
        Expand-Archive -Path $archive -DestinationPath $extract -Force
    } else {
        tar -xf $archive -C $extract
    }

    # Flatten the shared libraries (found anywhere in the archive) into native/ffmpeg/<rid>/.
    $pattern = $patterns[$rid]
    Get-ChildItem $extract -Recurse -Filter $pattern -File |
        Where-Object { $_.FullName -match '[\\/](bin|lib)[\\/]' } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $ridDir $_.Name) -Force }

    Remove-Item $extract -Recurse -Force
    Remove-Item $archive -Force
    Write-Host "  -> $((Get-ChildItem $ridDir -Filter $pattern).Count) libraries in $ridDir"
}
