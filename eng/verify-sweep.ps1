#!/usr/bin/env pwsh
# Streaming-use-case sweep: measure throughput and A/V-sync quality across resolution x fps x profile x flags x
# duration, modelling real workloads (webcam IRL, avatar/alpha sharing, desktop share). Unlike verify-matrix
# (PASS/FAIL grid), this tabulates the measured numbers - decoded fps, the publish-side fps (active-window
# rate), and the sync skew/jitter/trend - so the behaviour of each use case is visible, not just pass/fail.
#
# Relay + STUN on Windows (signaling only; media is direct P2P). Each row runs a sender on one machine and a
# verifying receiver on another, both synthetic with --verify, self-terminating after --seconds. The Windows
# LAN IP is auto-detected (DHCP moves it); override with -WinIp.
#
# Usage:
#   pwsh eng/verify-sweep.ps1 [-Durations 15,60] [-Rows webcam-irl-1080p30,desktop-2160p30] [-WinIp x.x.x.x]
[CmdletBinding()]
param(
    [int[]]$Durations = @(15, 60),
    [string[]]$Rows,
    [string]$WinIp,
    [string]$LinuxHost = 'agash@192.168.20.102',
    [string]$LinuxRepo = '~/stx',
    [string]$MacHost = 'agash@mac-mini.local',
    [string]$MacRepo = '~/repos/StreamTransport',
    [int]$Port = 8099
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $WinIp) {
    $WinIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' } |
        Select-Object -First 1).IPAddress
    if (-not $WinIp) { throw 'could not auto-detect a 192.168.* Windows IP; pass -WinIp.' }
}
$ws = "ws://${WinIp}:$Port/ws"
$winAgent = Join-Path $repo 'samples/StreamTransport.Agent/bin/Release/net11.0-windows10.0.19041.0/streamtransport-agent.exe'
$relayDll = Join-Path $repo 'samples/StreamTransport.Relay/bin/Release/net11.0/StreamTransport.Relay.dll'
$logDir = Join-Path $repo '.matrix-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
function Strip-Ansi([string]$s) { return ($s -replace "`e\[[0-9;]*m", '') }

# --- use-case rows ------------------------------------------------------------------------------------------
# Each row: a streaming use case mapped to source dimensions + profile + flags + the leg (sender->receiver,
# which fixes the publish sink). All are A/V (audio on) and --synced so the sync metrics are exercised; alpha
# rows model avatar/overlay sharing. Receiver publishes (GPU zero-copy verify) per platform.
$allRows = @(
    @{ Id = 'webcam-irl-720p30';      Leg = 'win2mac'; Res = '1280x720';  Fps = 30; Profile = 'irl';         Alpha = $false }
    @{ Id = 'webcam-irl-1080p30';     Leg = 'win2mac'; Res = '1920x1080'; Fps = 30; Profile = 'irl';         Alpha = $false }
    @{ Id = 'avatar-1080p30-alpha';   Leg = 'win2mac'; Res = '1920x1080'; Fps = 30; Profile = 'interactive'; Alpha = $true }
    @{ Id = 'avatar-1080p60-alpha';   Leg = 'win2mac'; Res = '1920x1080'; Fps = 60; Profile = 'interactive'; Alpha = $true }
    @{ Id = 'desktop-1080p30-share';  Leg = 'win2mac'; Res = '1920x1080'; Fps = 30; Profile = 'screenshare'; Alpha = $false }
    @{ Id = 'desktop-1440p30-share';  Leg = 'win2mac'; Res = '2560x1440'; Fps = 30; Profile = 'screenshare'; Alpha = $false }
    @{ Id = 'desktop-2160p30-share';  Leg = 'win2mac'; Res = '3840x2160'; Fps = 30; Profile = 'screenshare'; Alpha = $false }
    @{ Id = 'desktop-2160p60-share';  Leg = 'win2mac'; Res = '3840x2160'; Fps = 60; Profile = 'screenshare'; Alpha = $false }
    # Linux GPU-publish synced legs - the sync-FAIL cells from the matrix, swept over duration to see whether
    # lip-sync converges with more markers or the jitter is inherent.
    @{ Id = 'lin-sync-1080p30';       Leg = 'win2lin'; Res = '1920x1080'; Fps = 30; Profile = 'interactive'; Alpha = $false }
    @{ Id = 'lin-sync-1080p60';       Leg = 'win2lin'; Res = '1920x1080'; Fps = 60; Profile = 'interactive'; Alpha = $false }
)
if ($Rows) {
    $want = $Rows | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ }
    $allRows = $allRows | Where-Object { $want -contains $_.Id }
}

function Start-Agent([ValidateSet('win', 'linux', 'mac')]$On, [string[]]$AgentArgs, [string]$OutFile) {
    if ($On -eq 'win') { $file = $winAgent; $argList = $AgentArgs }
    elseif ($On -eq 'mac') { $file = 'ssh'; $argList = @($MacHost, 'bash', "$MacRepo/eng/matrix-mac-agent.sh") + $AgentArgs }
    else { $file = 'ssh'; $argList = @($LinuxHost, 'bash', "$LinuxRepo/eng/matrix-linux-agent.sh") + $AgentArgs }
    return Start-Process -FilePath $file -ArgumentList $argList -RedirectStandardOutput $OutFile `
        -RedirectStandardError "$OutFile.err" -NoNewWindow -PassThru
}
function Wait-Receiver([System.Diagnostics.Process]$Proc, [int]$Seconds) {
    if (-not $Proc.WaitForExit(($Seconds + 35) * 1000)) { $Proc | Stop-Process -Force -ErrorAction SilentlyContinue }
}

if (-not (Test-Path $winAgent)) { throw "Windows agent missing: $winAgent (build it first)" }
& scp (Join-Path $repo 'eng/matrix-linux-agent.sh') "${LinuxHost}:$LinuxRepo/eng/matrix-linux-agent.sh" 2>$null | Out-Null
& scp (Join-Path $repo 'eng/matrix-mac-agent.sh') "${MacHost}:$MacRepo/eng/matrix-mac-agent.sh" 2>$null | Out-Null

Write-Host "== relay on http://0.0.0.0:$Port  (win=$WinIp) ==" -ForegroundColor Cyan
$env:STREAMTRANSPORT_RELAY_URLS = "http://0.0.0.0:$Port"
$relay = Start-Process dotnet -ArgumentList @($relayDll) -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $logDir 'relay-sweep.out') -RedirectStandardError (Join-Path $logDir 'relay-sweep.err')
$ready = $false
foreach ($i in 1..40) { try { Invoke-WebRequest "http://localhost:$Port/health" -TimeoutSec 2 -UseBasicParsing | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 } }
if (-not $ready) { $relay | Stop-Process -Force -EA SilentlyContinue; throw 'relay not healthy' }

$results = @()
"{0,-24} {1,-5} {2,-4} {3,-12} {4,-6} {5,-9} {6}" -f 'ROW', 'secs', 'fps', 'recv-fps', 'pub', 'sync', 'verdict' | Write-Host
try {
    foreach ($r in $allRows) {
        $recvOn = if ($r.Leg -eq 'win2mac') { 'mac' } else { 'linux' }
        $pub = if ($recvOn -eq 'mac') { @('--publish-syphon', 'MxV') } else { @('--publish-pipewire', 'MxV') }
        foreach ($secs in $Durations) {
            $room = ("sw{0}{1}" -f $r.Id, $secs) -replace '[^a-z0-9]', ''
            $base = @('--relay', $ws, '--room', $room, '--source', 'synthetic', '--verify', '--synced',
                '--resolution', $r.Res, '--fps', $r.Fps, '--profile', $r.Profile)
            if ($r.Alpha) { $base += '--alpha' }
            $sendArgs = @('send') + $base + @('--seconds', ($secs + 8))
            $recvArgs = @('receive') + $base + @('--seconds', $secs) + $pub
            $recvLog = Join-Path $logDir "$room-recv.log"

            $sender = Start-Agent 'win' $sendArgs (Join-Path $logDir "$room-send.log")
            Start-Sleep -Seconds 2
            $receiver = Start-Agent $recvOn $recvArgs $recvLog
            Wait-Receiver $receiver $secs
            if ($sender -and -not $sender.HasExited) { Start-Sleep 1; $sender | Stop-Process -Force -EA SilentlyContinue }

            $out = if (Test-Path $recvLog) { Strip-Ansi (Get-Content $recvLog -Raw) } else { '' }
            $recvFps = if ($out -match 'video : \d+ frames \(([\d.]+) fps\)') { $Matches[1] } else { '-' }
            $pubFps = if ($out -match 'publish: \d+ frames in [\d.]+s \(([\d.]+) fps\)') { $Matches[1] } else { '-' }
            $sync = if ($out -match 'skew ([-+]?\d+) ms.*?jitter \+/-(\d+) ms') { "skew$($Matches[1])/jit$($Matches[2])" }
                elseif ($out -match 'INCONCLUSIVE') { 'inconcl' } else { '-' }
            $syncState = if ($out -match 'OUT OF SYNC') { 'OUT' } elseif ($out -match 'IN SYNC') { 'IN' } elseif ($out -match 'INCONCLUSIVE') { 'incon' } else { '?' }
            $verdict = if ($out -match 'VERIFY-PASS') { 'PASS' } elseif ($out -match 'VERIFY-FAIL') { 'FAIL' } else { 'NO-REPORT' }
            $color = if ($verdict -eq 'PASS') { 'Green' } elseif ($verdict -eq 'FAIL') { 'Yellow' } else { 'Red' }
            Write-Host ("{0,-24} {1,-5} {2,-4} {3,-12} {4,-6} {5,-9} {6} ({7})" -f $r.Id, $secs, $r.Fps, $recvFps, $pubFps, $sync, $verdict, $syncState) -ForegroundColor $color
            $results += [pscustomobject]@{ Row = $r.Id; Secs = $secs; Fps = $r.Fps; RecvFps = $recvFps; PubFps = $pubFps; Sync = $sync; State = $syncState; Verdict = $verdict }
        }
    }
}
finally { $relay | Stop-Process -Force -EA SilentlyContinue }

$csv = Join-Path $logDir 'sweep-results.csv'
$results | Export-Csv -NoTypeInformation -Path $csv
Write-Host "`nwrote $csv" -ForegroundColor Cyan
