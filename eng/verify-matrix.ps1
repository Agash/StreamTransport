#!/usr/bin/env pwsh
# Cross-machine A/V verify matrix - Windows <-> Linux (handheld) <-> macOS, orchestrated from Windows.
#
# Relay + STUN run on Windows (signaling only; media is direct P2P UDP - WebRTC ICE punches the handheld's
# stateful WiFi path so unsolicited-inbound-UDP drops don't matter). Each cell runs a sender on one machine and
# a verifying receiver on another; both use the synthetic source with --verify (correlated A/V sync markers),
# so each side self-terminates after --seconds and the receiver prints a VERIFY-PASS/FAIL report we parse.
#
# Verify-capability asymmetry (drives which side is the verifier): the in-agent GPU-readback verify exists on
# the Linux receiver (--publish-pipewire reads each decoded GPU surface back to CPU, even with no consumer) and
# the macOS receiver (--publish-syphon, which reads back the published BGRA surface); Windows --verify is
# CPU-decode only. So GPU-path cells put Linux or macOS as the receiver. Windows GPU output (Spout) stays
# display-attached and is not in this in-agent matrix.
#
# Usage:
#   pwsh eng/verify-matrix.ps1 [-Build] [-Seconds 18] [-Cells C1,CM3] [-WinIp 192.168.20.51]
#   Cell IDs: C1-C9 = Win<->Linux; CM1-CM6 = macOS legs (Win<->Mac, Mac<->Linux).
[CmdletBinding()]
param(
    [switch]$Build,
    [int]$Seconds = 18,
    [string[]]$Cells,
    [ValidateSet('all', 'loopback', 'cross')][string]$Group = 'all',
    [string[]]$Profiles = @('interactive'),
    [string]$Resolution,
    [int]$Fps,
    [string]$WinIp,
    [string]$LinuxHost = 'agash@192.168.20.102',
    [string]$LinuxRepo = '~/stx',
    [string]$MacHost = 'agash@mac-mini.local',
    [string]$MacRepo = '~/repos/StreamTransport',
    [int]$Port = 8099
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
# Profiles arrives as one token under -File ("a,b"); split it. Auto-detect the Windows LAN IP (DHCP moves it).
$Profiles = $Profiles | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ }
if (-not $WinIp) {
    $WinIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' } | Select-Object -First 1).IPAddress
    if (-not $WinIp) { throw 'could not auto-detect a 192.168.* Windows IP; pass -WinIp.' }
}
$ws = "ws://${WinIp}:$Port/ws"
# Prefer the Windows NativeAOT publish (the deployment artifact); fall back to the framework-dependent build.
$winAgentAot = Join-Path $repo 'samples/StreamTransport.Agent/bin/Release/net11.0-windows10.0.19041.0/win-x64/publish/streamtransport-agent.exe'
$winAgentFdd = Join-Path $repo 'samples/StreamTransport.Agent/bin/Release/net11.0-windows10.0.19041.0/streamtransport-agent.exe'
$winAgent = if (Test-Path $winAgentAot) { $winAgentAot } else { $winAgentFdd }
$relayDll = Join-Path $repo 'samples/StreamTransport.Relay/bin/Release/net11.0/StreamTransport.Relay.dll'
$logDir = Join-Path $repo '.matrix-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Strip-Ansi([string]$s) { return ($s -replace "`e\[[0-9;]*m", '') }

# Launch one agent invocation, returning the started Process. Local Windows runs the exe; Linux and macOS run
# over SSH through their eng/matrix-*-agent.sh launcher (which sets the runtime env and locates the agent;
# quote-free so the SSH command line stays simple). Callers bound the wait themselves (see Wait-Receiver) - a
# remote agent can exit cleanly yet leave its SSH pipe held open (a lingering PipeWire/Syphon connection), which
# would hang an unbounded -Wait, so we never block indefinitely on a remote process.
function Start-Agent {
    param([ValidateSet('win', 'linux', 'mac')]$On, [string[]]$AgentArgs, [string]$OutFile)
    if ($On -eq 'win') {
        $file = $winAgent; $argList = $AgentArgs
    }
    elseif ($On -eq 'mac') {
        $file = 'ssh'
        $argList = @($MacHost, 'bash', "$MacRepo/eng/matrix-mac-agent.sh") + $AgentArgs
    }
    else {
        $file = 'ssh'
        $argList = @($LinuxHost, 'bash', "$LinuxRepo/eng/matrix-linux-agent.sh") + $AgentArgs
    }
    $p = @{ FilePath = $file; ArgumentList = $argList; RedirectStandardOutput = $OutFile;
        RedirectStandardError = "$OutFile.err"; NoNewWindow = $true; PassThru = $true }
    return Start-Process @p
}

# Wait for the verifying receiver to finish, bounded by its --verify window plus margin for ICE setup and
# teardown. If it overruns (a remote agent that exited but left its SSH pipe open), force-kill it so the matrix
# moves on instead of hanging - the verdict is still parsed from whatever the receiver logged.
function Wait-Receiver([System.Diagnostics.Process]$Proc, [int]$Seconds) {
    if (-not $Proc.WaitForExit(($Seconds + 30) * 1000)) {
        Write-Host '  (receiver overran its window; killing - likely a lingering SSH pipe)' -ForegroundColor DarkYellow
        $Proc | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

# --- matrix cells -------------------------------------------------------------------------------------------
# Each cell names its Sender and Receiver (the verifier) explicitly. SendExtra/RecvExtra carry the mode flags;
# the GPU receivers add their zero-copy publish (--publish-pipewire on Linux, --publish-syphon on macOS) so the
# in-agent GPU-readback verify taps the real decoded surface.
$matrix = @(
    # Win <-> Linux (handheld)
    @{ Id = 'C1'; Name = 'win2lin-cpu-video';       Sender = 'win';   Receiver = 'linux'; SendExtra = @('--video-only'); RecvExtra = @('--video-only') }
    @{ Id = 'C2'; Name = 'win2lin-cpu-av';          Sender = 'win';   Receiver = 'linux'; SendExtra = @();               RecvExtra = @() }
    @{ Id = 'C3'; Name = 'win2lin-cpu-av-synced';   Sender = 'win';   Receiver = 'linux'; SendExtra = @();               RecvExtra = @('--synced') }
    @{ Id = 'C4'; Name = 'win2lin-gpu-video';       Sender = 'win';   Receiver = 'linux'; SendExtra = @('--video-only'); RecvExtra = @('--video-only', '--publish-pipewire', 'MxV') }
    @{ Id = 'C5'; Name = 'win2lin-gpu-av-synced';   Sender = 'win';   Receiver = 'linux'; SendExtra = @();               RecvExtra = @('--synced', '--publish-pipewire', 'MxAV') }
    @{ Id = 'C6'; Name = 'win2lin-gpu-alpha-synced';Sender = 'win';   Receiver = 'linux'; SendExtra = @('--alpha');      RecvExtra = @('--synced', '--alpha', '--publish-pipewire', 'MxAL') }
    @{ Id = 'C7'; Name = 'lin2win-cpu-video';       Sender = 'linux'; Receiver = 'win';   SendExtra = @('--video-only'); RecvExtra = @('--video-only') }
    @{ Id = 'C8'; Name = 'lin2win-cpu-av-synced';   Sender = 'linux'; Receiver = 'win';   SendExtra = @();               RecvExtra = @('--synced') }
    @{ Id = 'C9'; Name = 'lin2win-cpu-alpha-synced';Sender = 'linux'; Receiver = 'win';   SendExtra = @('--alpha');      RecvExtra = @('--synced') }
    # macOS legs - Win <-> Mac and Mac <-> Linux. The Mac verifies GPU cells via --publish-syphon (zero-copy
    # publish + BGRA readback); Mac-as-sender uses the synthetic source so no display/capture is needed.
    @{ Id = 'CM1'; Name = 'win2mac-cpu-av-synced';  Sender = 'win';   Receiver = 'mac';   SendExtra = @();               RecvExtra = @('--synced') }
    @{ Id = 'CM2'; Name = 'win2mac-gpu-av-synced';  Sender = 'win';   Receiver = 'mac';   SendExtra = @();               RecvExtra = @('--synced', '--publish-syphon', 'MxAV') }
    @{ Id = 'CM3'; Name = 'win2mac-gpu-alpha-synced';Sender = 'win';  Receiver = 'mac';   SendExtra = @('--alpha');      RecvExtra = @('--synced', '--alpha', '--publish-syphon', 'MxAL') }
    @{ Id = 'CM4'; Name = 'mac2win-cpu-av-synced';  Sender = 'mac';   Receiver = 'win';   SendExtra = @();               RecvExtra = @('--synced') }
    @{ Id = 'CM5'; Name = 'mac2lin-gpu-av-synced';  Sender = 'mac';   Receiver = 'linux'; SendExtra = @();               RecvExtra = @('--synced', '--publish-pipewire', 'MxAV') }
    @{ Id = 'CM6'; Name = 'lin2mac-gpu-av-synced';  Sender = 'linux'; Receiver = 'mac';   SendExtra = @();               RecvExtra = @('--synced', '--publish-syphon', 'MxAV') }
)
foreach ($c in $matrix) { $c.Group = 'cross' }

# --- loopback cells -----------------------------------------------------------------------------------------
# Sender == Receiver on one machine: signaling still goes through the Windows relay, but media is P2P-local on
# that host, so this isolates the platform's full pipeline (encode -> WebRTC -> decode -> GPU publish) from the
# cross-machine network. Generated across -Profiles x {cpu video-only, cpu A/V synced, GPU A/V synced}. Windows
# has no in-agent GPU-output verify (its --verify is CPU-decode), so it gets CPU rows only; mac/linux add the
# GPU-publish row (Syphon/PipeWire readback verify). These answer "what can each box actually do, network aside".
$loopPlatforms = @(
    @{ Plat = 'win';   Pub = $null }
    @{ Plat = 'mac';   Pub = @('--publish-syphon', 'LB') }
    @{ Plat = 'linux'; Pub = @('--publish-pipewire', 'LB') }
)
$loopKinds = @(
    @{ K = 'cpu-video';     Gpu = $false; Send = @('--video-only'); Recv = @('--video-only') }
    @{ K = 'cpu-av-synced'; Gpu = $false; Send = @();               Recv = @('--synced') }
    @{ K = 'gpu-av-synced'; Gpu = $true;  Send = @();               Recv = @('--synced') }
)
foreach ($p in $loopPlatforms) {
    foreach ($prof in $Profiles) {
        foreach ($k in $loopKinds) {
            if ($k.Gpu -and -not $p.Pub) { continue }
            $recv = @($k.Recv); if ($k.Gpu) { $recv += $p.Pub }
            $matrix += @{
                Id = "LB-$($p.Plat)-$($k.K)-$prof"; Name = "loopback $($p.Plat) $($k.K) $prof";
                Sender = $p.Plat; Receiver = $p.Plat; Group = 'loopback'; Profile = $prof;
                SendExtra = $k.Send; RecvExtra = $recv
            }
        }
    }
}

if ($Group -ne 'all') { $matrix = $matrix | Where-Object { $_.Group -eq $Group } }
if ($Cells) {
    # -File passes "-Cells C1,C7" as a single token, so split on commas/space to get the real list.
    $want = $Cells | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ }
    $matrix = $matrix | Where-Object { $want -contains $_.Id -or $want -contains $_.Name }
}

# --- build (optional) ---------------------------------------------------------------------------------------
if ($Build) {
    Write-Host '== building Windows agent + relay (Release) ==' -ForegroundColor Cyan
    dotnet build (Join-Path $repo 'samples/StreamTransport.Agent/StreamTransport.Agent.csproj') -c Release -f net11.0-windows10.0.19041.0 | Out-Null
    dotnet build (Join-Path $repo 'samples/StreamTransport.Relay/StreamTransport.Relay.csproj') -c Release | Out-Null
    Write-Host '== updating + building handheld (git ff + Release) ==' -ForegroundColor Cyan
    & ssh $LinuxHost "bash -lc 'cd $LinuxRepo && git fetch origin && git pull --ff-only && export DOTNET_ROOT=`$HOME/.dotnet PATH=`$HOME/.dotnet:`$PATH && dotnet build samples/StreamTransport.Agent/StreamTransport.Agent.csproj -c Release'"
    if ($LASTEXITCODE -ne 0) { throw 'handheld build failed' }
    Write-Host '== updating + building Mac (git ff + Release, net11.0-macos) ==' -ForegroundColor Cyan
    # Xcode (26.5) is ahead of the .NET macOS SDK's pinned version, so skip the version gate; app/test
    # projects also need an explicit RID. The .refs/Syphon.NET clone is synced separately (gitignored).
    & ssh $MacHost "bash -lc 'cd $MacRepo && git fetch origin && git pull --ff-only && export DOTNET_ROOT=`$HOME/.dotnet PATH=`$HOME/.dotnet:`$PATH && dotnet build samples/StreamTransport.Agent/StreamTransport.Agent.csproj -c Release -r osx-arm64 -p:ValidateXcodeVersion=false'"
    if ($LASTEXITCODE -ne 0) { throw 'mac build failed' }
}
# Ensure the per-OS launchers are present/fresh on the remote machines.
& scp (Join-Path $repo 'eng/matrix-linux-agent.sh') "${LinuxHost}:$LinuxRepo/eng/matrix-linux-agent.sh" | Out-Null
& scp (Join-Path $repo 'eng/matrix-mac-agent.sh') "${MacHost}:$MacRepo/eng/matrix-mac-agent.sh" | Out-Null

if (-not (Test-Path $winAgent)) { throw "Windows agent missing: $winAgent (run with -Build)" }

# --- relay --------------------------------------------------------------------------------------------------
Write-Host "== starting relay on http://0.0.0.0:$Port ==" -ForegroundColor Cyan
$env:STREAMTRANSPORT_RELAY_URLS = "http://0.0.0.0:$Port"
$relay = Start-Process dotnet -ArgumentList @($relayDll) -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $logDir 'relay.out') -RedirectStandardError (Join-Path $logDir 'relay.err')
$ready = $false
foreach ($i in 1..40) {
    try { Invoke-WebRequest "http://localhost:$Port/health" -TimeoutSec 2 -UseBasicParsing | Out-Null; $ready = $true; break }
    catch { Start-Sleep -Milliseconds 500 }
}
if (-not $ready) { $relay | Stop-Process -Force -ErrorAction SilentlyContinue; throw 'relay did not become healthy' }
Write-Host 'relay ready' -ForegroundColor Green

# --- run cells ----------------------------------------------------------------------------------------------
$results = @()
try {
    foreach ($c in $matrix) {
        $recvOn = $c.Receiver
        $room = ($c.Id -replace '[^a-zA-Z0-9]', '').ToLower()
        # --verbose always on: every cell's per-cell log then carries the SCReAM congestion estimate
        # (target/pacing/rtt/loss) and other debug, which is what makes a failed/odd cell investigable after
        # the fact without re-running. The logs are per-cell files, so the volume is contained.
        $base = @('--relay', $ws, '--room', $room, '--source', 'synthetic', '--verify', '--verbose')
        if ($c.Profile) { $base += @('--profile', $c.Profile) }
        if ($Resolution) { $base += @('--resolution', $Resolution) }
        if ($Fps) { $base += @('--fps', $Fps) }
        $sendArgs = @('send') + $base + @('--seconds', ($Seconds + 6)) + $c.SendExtra
        $recvArgs = @('receive') + $base + @('--seconds', $Seconds) + $c.RecvExtra
        $sendLog = Join-Path $logDir "$($c.Id)-send.log"
        $recvLog = Join-Path $logDir "$($c.Id)-recv.log"

        Write-Host ("`n=== {0} {1}  ({2} send -> {3} verify) ===" -f $c.Id, $c.Name, $c.Sender, $recvOn) -ForegroundColor Yellow
        $sender = Start-Agent -On $c.Sender -AgentArgs $sendArgs -OutFile $sendLog
        Start-Sleep -Seconds 2
        $receiver = Start-Agent -On $recvOn -AgentArgs $recvArgs -OutFile $recvLog
        Wait-Receiver $receiver $Seconds

        if ($sender -and -not $sender.HasExited) { Start-Sleep -Seconds 1; $sender | Stop-Process -Force -ErrorAction SilentlyContinue }

        $out = if (Test-Path $recvLog) { Strip-Ansi ((Get-Content $recvLog -Raw)) } else { '' }
        $detail = ($out -split "`n" | Where-Object { $_ -match '^\s*(video|audio|alpha|sync)\s*:' } | ForEach-Object { $_.Trim() }) -join ' | '

        # Per-cell verdict (the agent bundles sync into its own VERIFY-PASS, but a non-synced cell shouldn't be
        # failed for an uncorrected offset, and an alpha cell's markers don't survive the BGRA path). So we grade
        # against this cell's actual expectations: flow + content always; audio when expected; alpha gradient when
        # alpha; and lip-sync ONLY for --synced cells (and only when markers matched - alpha => informational).
        $synced = $c.RecvExtra -contains '--synced'
        $wantAudio = -not ($c.RecvExtra -contains '--video-only')
        $wantAlpha = $c.SendExtra -contains '--alpha'
        $videoOk = $out -match 'video : flow OK, content live'
        $audioOk = (-not $wantAudio) -or ($out -match 'audio : flow OK, signal present')
        $alphaOk = (-not $wantAlpha) -or ($out -match 'alpha : gradient preserved')
        $syncOk = (-not $synced) -or (-not ($out -match 'OUT OF SYNC'))   # IN SYNC or INCONCLUSIVE both pass
        $pass = $videoOk -and $audioOk -and $alphaOk -and $syncOk -and ($out -match 'VERIFY-(PASS|FAIL)')
        $verdict = if ($pass) { 'PASS' } elseif (-not ($out -match 'VERIFY-(PASS|FAIL)')) { 'NO-REPORT' } else { 'FAIL' }
        $color = if ($pass) { 'Green' } else { 'Red' }
        Write-Host ("  -> {0}" -f $verdict) -ForegroundColor $color
        if ($detail) { Write-Host "     $detail" -ForegroundColor DarkGray }
        $results += [pscustomobject]@{ Cell = $c.Id; Name = $c.Name; Verdict = $verdict; Detail = $detail }
    }
}
finally {
    $relay | Stop-Process -Force -ErrorAction SilentlyContinue
}

# --- summary ------------------------------------------------------------------------------------------------
Write-Host "`n================ MATRIX SUMMARY ================" -ForegroundColor Cyan
$results | Format-Table Cell, Name, Verdict -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Verdict -ne 'PASS' })
if ($failed.Count -eq 0) { Write-Host "ALL $($results.Count) CELLS PASS" -ForegroundColor Green; exit 0 }
Write-Host "$($failed.Count)/$($results.Count) cells did not pass: $($failed.Cell -join ', ')" -ForegroundColor Red
exit 1
