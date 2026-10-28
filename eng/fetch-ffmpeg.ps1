#!/usr/bin/env pwsh
# Puts an FFmpeg build other than the packaged one into native/ffmpeg/<rid>/, which the tests and samples
# then load instead of FFmpeg.Interop.Native's: a CI build of an FFmpeg.Interop commit not yet released
# (the one pinned below), or a local build of it with -Build. Builds and tests need nothing of it otherwise:
# Agash.StreamTransport.Codecs.FFmpeg takes FFmpeg from the FFmpeg.Interop.Native packages.
# Each RID's directory carries that build's licence: LGPL 2.1, or LGPL 3 on linux-arm64 (Rockchip's MPP).
#
# Usage:  ./eng/fetch-ffmpeg.ps1 [-Rids win-x64,linux-x64,...] [-Build <FFmpeg.Interop>/artifacts/native]
# Downloading needs the GitHub CLI, signed in or with GH_TOKEN set.
[CmdletBinding()]
param(
    [string[]]$Rids = @([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier),
    [string]$Build
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'native/ffmpeg'

$repository = 'Agash/FFmpeg.Interop'
$commit = '7a460e0b28deb683392be4f8f7b497be116e3632'

foreach ($rid in $Rids) {
    $ridDir = Join-Path $dest $rid
    $source = $null
    $download = $null

    if ($Build) {
        $source = Join-Path $Build $rid
        if (-not (Test-Path $source)) { throw "No FFmpeg build for $rid in $Build." }
    } else {
        $download = Join-Path ([IO.Path]::GetTempPath()) "ffmpeg-$rid-$([Guid]::NewGuid().ToString('N'))"
        $run = & gh run list --repo $repository --workflow build.yml --commit $commit --status completed `
            --limit 1 --json databaseId --jq '.[0].databaseId'
        if ($LASTEXITCODE -ne 0 -or -not $run) { throw "No finished FFmpeg.Interop build of $commit." }
        & gh run download $run --repo $repository --name "ffmpeg-$rid" --dir $download
        if ($LASTEXITCODE -ne 0) { throw "The FFmpeg.Interop build of $commit has no ffmpeg-$rid." }

        $archive = Join-Path $download "ffmpeg-$rid.tar.gz"
        # On Windows a Git for Windows GNU tar earlier on PATH reads "C:" as a remote host; the bsdtar
        # that ships with Windows handles drive paths.
        $tar = if ($IsWindows) { Join-Path $env:SystemRoot 'System32/tar.exe' } else { 'tar' }
        & $tar -xzf $archive -C $download
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
        $source = Join-Path $download $rid
    }

    Remove-Item $ridDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ridDir | Out-Null

    # The shared libraries only, flat, each under the name the loader opens (its SONAME on Linux, its
    # install name on macOS), symlinks copied as the files they name; the tools stay out.
    $libraries = switch -Wildcard ($rid) {
        'win-*' { Get-ChildItem (Join-Path $source 'bin') -Filter '*.dll' -File }
        'linux-*' { Get-ChildItem (Join-Path $source 'lib') -File | Where-Object Name -Match '^lib.+\.so\.\d+$' }
        default { Get-ChildItem (Join-Path $source 'lib') -File | Where-Object Name -Match '^lib[a-z0-9_]+\.\d+\.dylib$' }
    }
    $libraries | ForEach-Object { Copy-Item $_.FullName (Join-Path $ridDir $_.Name) -Force }
    Copy-Item (Join-Path $source 'LICENSE.ffmpeg.txt') (Join-Path $ridDir 'LICENSE.ffmpeg.txt') -Force

    if ($download) { Remove-Item $download -Recurse -Force }
    Write-Host "FFmpeg for ${rid}: $(@($libraries).Count) libraries in $ridDir"
}
