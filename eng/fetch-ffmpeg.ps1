#!/usr/bin/env pwsh
# Fetches the pinned FFmpeg 9.0 shared libraries for one or more RIDs into native/ffmpeg/<rid>/.
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

# Pinned FFmpeg 9.0 shared builds per RID, all BtbN LGPL builds. The linux-arm64 build is generic and
# has no Rockchip rkmpp encoders; the RK3588 field-agent lane needs a rkmpp-enabled build (#16).
$sources = @{
    'win-x64'     = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip'
    'linux-x64'   = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0.tar.xz'
    'linux-arm64' = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-linuxarm64-lgpl-shared-9.0.tar.xz'
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
        # On Windows, a Git for Windows GNU tar earlier on PATH reads "C:" as a remote host;
        # the bsdtar that ships with Windows handles drive paths and .tar.xz.
        $tar = if ($IsWindows) { Join-Path $env:SystemRoot 'System32/tar.exe' } else { 'tar' }
        & $tar -xf $archive -C $extract
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
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
