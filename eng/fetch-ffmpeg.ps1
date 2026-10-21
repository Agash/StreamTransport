#!/usr/bin/env pwsh
# Fetches the pinned FFmpeg 9 shared libraries for one or more RIDs into native/ffmpeg/<rid>/, where
# Agash.StreamTransport.Codecs.FFmpeg packs them as runtimes/<rid>/native.
#
# The pin is FFmpeg.Interop's (eng/fetch-ffmpeg.ps1 there): the bindings are generated against that
# build, so both repositories take the same archive. Windows and Linux use BtbN's month-end LGPL
# builds, which BtbN keeps for about two years (the daily builds are gone after two weeks), each
# verified by SHA-256 before anything is extracted. They carry the hardware encoders (NVENC, AMF, QSV,
# VA-API, Vulkan, D3D12VA, Media Foundation) and the LGPL software codecs (OpenH264, kvazaar, SVT-AV1,
# dav1d, libopus); the GPL x264 and x265 are not in them. macOS links Homebrew's FFmpeg 9 at package
# time, which has no pinned archive.
#
# Usage:  ./eng/fetch-ffmpeg.ps1 [-Rids win-x64,linux-x64,...]
[CmdletBinding()]
param(
    [string[]]$Rids = @([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'native/ffmpeg'

$release = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08'
$build = 'ffmpeg-n9.0.2-17-g2a571b6068'
$sources = @{
    'win-x64'     = @("$build-win64-lgpl-shared-9.0.zip", '7157177b8a6cb2174c1650ba8c71b363f2c78cba5330f88c4c02cf5b2b880646')
    'win-arm64'   = @("$build-winarm64-lgpl-shared-9.0.zip", 'f3133abf5c2b6ae45bd428d480cf830a9fbda3a3866dc249110e85f99f13b8ce')
    'linux-x64'   = @("$build-linux64-lgpl-shared-9.0.tar.xz", '7a1ba2af36e5beeb98151e766a903b1d115f2206a86571f17f873cd7292c424f')
    'linux-arm64' = @("$build-linuxarm64-lgpl-shared-9.0.tar.xz", '1aa02c59afe06dea2602b40517a5cb75c0ff6d524f217b5aea1f0ca07977cd51')
}

$patterns = @{
    'win-x64'     = '*.dll'
    'win-arm64'   = '*.dll'
    'linux-x64'   = 'lib*.so*'
    'linux-arm64' = 'lib*.so*'
}

foreach ($rid in $Rids) {
    $ridDir = Join-Path $dest $rid
    Remove-Item $ridDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ridDir | Out-Null

    if ($rid.StartsWith('osx-')) {
        $prefix = (& brew --prefix ffmpeg).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Homebrew ffmpeg is not installed: brew install ffmpeg' }
        if (-not (Test-Path "$prefix/lib/libavcodec.63.dylib")) {
            throw "Homebrew's ffmpeg at $prefix is not FFmpeg 9 (no libavcodec.63.dylib)."
        }

        Get-ChildItem "$prefix/lib" -Filter 'lib*.dylib' | ForEach-Object { Copy-Item $_.FullName $ridDir }
        Write-Host "Copied Homebrew ffmpeg at $prefix into $ridDir"
        continue
    }

    if (-not $sources.ContainsKey($rid)) { throw "No pinned FFmpeg build for RID '$rid'." }

    ($file, $sha256) = $sources[$rid]
    $archive = Join-Path ([IO.Path]::GetTempPath()) $file
    Write-Host "Fetching $file"
    Invoke-WebRequest -Uri "$release/$file" -OutFile $archive -UseBasicParsing

    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        Remove-Item $archive -Force
        throw "SHA-256 mismatch for ${file}: expected $sha256, got $actual."
    }

    $extract = Join-Path $root "native/.extract-$rid"
    Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $extract | Out-Null
    if ($file.EndsWith('.zip')) {
        Expand-Archive -Path $archive -DestinationPath $extract -Force
    } else {
        # On Windows a Git for Windows GNU tar earlier on PATH reads "C:" as a remote host; the bsdtar
        # that ships with Windows handles drive paths and .tar.xz.
        $tar = if ($IsWindows) { Join-Path $env:SystemRoot 'System32/tar.exe' } else { 'tar' }
        & $tar -xf $archive -C $extract
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
    }

    # FFmpeg's licence (LGPL), which ships with the libraries.
    Get-ChildItem $extract -Recurse -Filter 'LICENSE.txt' -File | Select-Object -First 1 |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $dest 'FFMPEG-LICENSE.txt') -Force }

    # The shared libraries only, flat; symlinks are copied as the files they name.
    Get-ChildItem $extract -Recurse -Filter $patterns[$rid] -File |
        Where-Object { $_.FullName -match '[\\/](bin|lib)[\\/]' } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $ridDir $_.Name) -Force }

    Remove-Item $extract -Recurse -Force
    Remove-Item $archive -Force
    Write-Host "FFmpeg $build for ${rid}: $((Get-ChildItem $ridDir).Count) libraries in $ridDir"
}
