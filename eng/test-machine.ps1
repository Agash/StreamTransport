#!/usr/bin/env pwsh
# Runs every test on this machine with coverage. No CI runner has a GPU, so the hardware paths are covered
# only here; tests skip what the machine cannot do. Coverage goes to
# artifacts/coverage/<machine>.cobertura.xml: merge it with CI's with ./eng/merge-coverage.ps1.
#
# Usage:  ./eng/test-machine.ps1 [-Configuration Release]
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet build StreamTransport.slnx -c $Configuration -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$coverageDir = Join-Path $root 'artifacts/coverage'
New-Item -ItemType Directory -Force $coverageDir | Out-Null
dotnet test --solution StreamTransport.slnx --no-build -c $Configuration --filter 'TestCategory!=Integration' `
    -- --coverage --coverage-settings coverage.settings.xml --coverage-output-format cobertura `
    --coverage-output machine.cobertura.xml
$result = $LASTEXITCODE

$reports = @(Get-ChildItem $root -Recurse -Filter machine.cobertura.xml | ForEach-Object FullName)
if ($reports) {
    $target = Join-Path $coverageDir "$([Environment]::MachineName.ToLowerInvariant()).cobertura.xml"
    $totals = ./eng/merge-coverage.ps1 -Reports $reports -Output $target
    Write-Host ("Coverage: {0:P1} lines, {1:P1} branches -> {2}" -f $totals.Lines, $totals.Branches, $target)
}

exit $result
