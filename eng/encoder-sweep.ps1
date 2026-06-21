# Encoder-option sweep: empirically find the best low-latency encoder settings per platform/profile, rather than
# theoretically. For each platform's auto-selected HW encoder (Windows nvenc, Linux vaapi, macOS videotoolbox)
# it varies ONE knob at a time around the profile defaults via the STX_ENC_VBV / STX_ENC_OPT env overrides (read
# by LowLatencyEncoderOptions), runs a same-machine loopback --verify, and records the glass-to-glass
# capture->present video latency and decoded fps. Output: .matrix-logs/encoder-sweep.csv + a per-knob summary.
#
#   pwsh eng/encoder-sweep.ps1 [-Build] [-Profile interactive] [-Seconds 14] [-Platforms win,linux,mac]
[CmdletBinding()]
param(
    [switch]$Build,
    [string]$Profile = 'interactive',
    [int]$Seconds = 14,
    [string[]]$Platforms = @('win', 'linux', 'mac'),
    [string]$WinIp,
    [string]$LinuxHost = 'agash@192.168.20.102',
    [string]$LinuxRepo = '~/stx',
    [string]$MacHost = 'agash@mac-mini.local',
    [string]$MacRepo = '~/repos/StreamTransport',
    [int]$Port = 8099
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$Platforms = $Platforms | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ }
if (-not $WinIp) { $WinIp = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' } | Select-Object -First 1).IPAddress }
$ws = "ws://${WinIp}:$Port/ws"
$winAgent = Join-Path $repo 'samples/StreamTransport.Agent/bin/Release/net11.0-windows10.0.19041.0/streamtransport-agent.exe'
$relayDll = Join-Path $repo 'samples/StreamTransport.Relay/bin/Release/net11.0/StreamTransport.Relay.dll'
$logDir = Join-Path $repo '.matrix-logs'; New-Item -ItemType Directory -Force $logDir | Out-Null
function Strip-Ansi([string]$s) { return ($s -replace "`e\[[0-9;]*m", '') }

# Per-platform, per-knob configs. Each is a label + the STX_ENC_* env to set on the sender. 'default' = the
# profile baseline (no override). One knob varies per row so its isolated effect is readable.
$grid = @{
    win   = @(  # hevc_nvenc
        @{ K = 'default' }, @{ K = 'vbv0.25'; V = '0.25' }, @{ K = 'vbv1.0'; V = '1.0' }, @{ K = 'vbv1.5'; V = '1.5' },
        @{ K = 'tune-ll'; O = 'tune=ll' }, @{ K = 'preset-p1'; O = 'preset=p1' }, @{ K = 'preset-p7'; O = 'preset=p7' },
        @{ K = 'lookahead-8'; O = 'rc-lookahead=8' }
    )
    linux = @(  # hevc_vaapi
        @{ K = 'default' }, @{ K = 'vbv0.25'; V = '0.25' }, @{ K = 'vbv1.0'; V = '1.0' }, @{ K = 'vbv1.5'; V = '1.5' },
        @{ K = 'async2'; O = 'async_depth=2' }
    )
    mac   = @(  # hevc_videotoolbox
        @{ K = 'default' }, @{ K = 'vbv0.25'; V = '0.25' }, @{ K = 'vbv1.0'; V = '1.0' }, @{ K = 'vbv1.5'; V = '1.5' }
    )
}
$encByPlat = @{ win = 'hevc_nvenc'; linux = 'hevc_vaapi'; mac = 'hevc_videotoolbox' }

if ($Build) {
    Write-Host '== build win + relay + remotes ==' -ForegroundColor Cyan
    dotnet build (Join-Path $repo 'samples/StreamTransport.Agent/StreamTransport.Agent.csproj') -c Release -f net11.0-windows10.0.19041.0 | Out-Null
    dotnet build (Join-Path $repo 'samples/StreamTransport.Relay/StreamTransport.Relay.csproj') -c Release | Out-Null
    if ($Platforms -contains 'linux') { & ssh $LinuxHost "bash -lc 'cd $LinuxRepo && git fetch origin -q && git reset --hard origin/main -q && export DOTNET_ROOT=`$HOME/.dotnet PATH=`$HOME/.dotnet:`$PATH && dotnet build samples/StreamTransport.Agent/StreamTransport.Agent.csproj -c Release' " | Out-Null; if ($LASTEXITCODE) { throw 'linux build failed' } }
    if ($Platforms -contains 'mac') { & ssh $MacHost "bash -lc 'cd $MacRepo && git fetch origin -q && git reset --hard origin/main -q && export DOTNET_ROOT=`$HOME/.dotnet PATH=`$HOME/.dotnet:`$PATH && dotnet build samples/StreamTransport.Agent/StreamTransport.Agent.csproj -c Release -r osx-arm64 -p:ValidateXcodeVersion=false' " | Out-Null; if ($LASTEXITCODE) { throw 'mac build failed' } }
    & scp (Join-Path $repo 'eng/matrix-linux-agent.sh') "${LinuxHost}:$LinuxRepo/eng/matrix-linux-agent.sh" | Out-Null
    & scp (Join-Path $repo 'eng/matrix-mac-agent.sh') "${MacHost}:$MacRepo/eng/matrix-mac-agent.sh" | Out-Null
}

$env:STREAMTRANSPORT_RELAY_URLS = "http://0.0.0.0:$Port"
$relay = Start-Process dotnet -ArgumentList @($relayDll) -PassThru -NoNewWindow -RedirectStandardOutput "$logDir/relay-sweep.out" -RedirectStandardError "$logDir/relay-sweep.err"
$ready = $false; foreach ($i in 1..40) { try { Invoke-WebRequest "http://localhost:$Port/health" -TimeoutSec 2 -UseBasicParsing | Out-Null; $ready = $true; break } catch { Start-Sleep -Milliseconds 500 } }
if (-not $ready) { $relay | Stop-Process -Force; throw 'relay not healthy' }

# Launch one agent (sender or receiver). For the sender, inject the STX_ENC_* env so the encoder picks it up.
function Start-Sweep-Agent([string]$On, [string[]]$AgentArgs, [string]$OutFile, [hashtable]$EncEnv) {
    $envPrefix = ($EncEnv.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
    if ($On -eq 'win') {
        foreach ($k in @('STX_ENC_VBV', 'STX_ENC_OPT')) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
        foreach ($e in $EncEnv.GetEnumerator()) { Set-Item "env:$($e.Key)" $e.Value }
        $p = Start-Process $winAgent -ArgumentList $AgentArgs -PassThru -NoNewWindow -RedirectStandardOutput $OutFile -RedirectStandardError "$OutFile.err"
        foreach ($k in @('STX_ENC_VBV', 'STX_ENC_OPT')) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
        return $p
    }
    $h = if ($On -eq 'mac') { $MacHost } else { $LinuxHost }
    $launcher = if ($On -eq 'mac') { "$MacRepo/eng/matrix-mac-agent.sh" } else { "$LinuxRepo/eng/matrix-linux-agent.sh" }
    # env set on the bash that execs the launcher -> inherited by the agent.
    $argList = @($h, "$envPrefix bash $launcher") + $AgentArgs
    return Start-Process 'ssh' -ArgumentList $argList -PassThru -NoNewWindow -RedirectStandardOutput $OutFile -RedirectStandardError "$OutFile.err"
}

$rows = @()
foreach ($plat in $Platforms) {
    if (-not $grid.ContainsKey($plat)) { continue }
    foreach ($cfg in $grid[$plat]) {
        $encEnv = @{}
        if ($cfg.V) { $encEnv['STX_ENC_VBV'] = $cfg.V }
        if ($cfg.O) { $encEnv['STX_ENC_OPT'] = $cfg.O }
        $room = ("sw$plat$($cfg.K)" -replace '[^a-z0-9]', '').ToLower()
        $base = @('--relay', $ws, '--room', $room, '--source', 'synthetic', '--verify', '--verbose', '--profile', $Profile)
        $sendLog = "$logDir/SW-$plat-$($cfg.K)-send.log"; $recvLog = "$logDir/SW-$plat-$($cfg.K)-recv.log"
        Write-Host ("== {0} {1} ({2}) ==" -f $plat, $cfg.K, ($encEnv.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Join-String -Separator ' ')) -ForegroundColor Yellow
        $snd = Start-Sweep-Agent $plat (@('send') + $base + @('--seconds', ($Seconds + 6))) $sendLog $encEnv
        Start-Sleep -Seconds 3
        $rcv = Start-Sweep-Agent $plat (@('receive') + $base + @('--seconds', $Seconds)) $recvLog @{}
        if (-not $rcv.WaitForExit(($Seconds + 30) * 1000)) { $rcv | Stop-Process -Force -ErrorAction SilentlyContinue }
        if ($snd -and -not $snd.HasExited) { Start-Sleep 1; $snd | Stop-Process -Force -ErrorAction SilentlyContinue }
        $out = if (Test-Path $recvLog) { Strip-Ansi (Get-Content $recvLog -Raw) } else { '' }
        $lat = if ($out -match 'capture->present video ~(-?\d+) ms') { [int]$Matches[1] } else { $null }
        $fps = if ($out -match 'video : \d+ frames \(([\d.]+) fps\)') { [double]$Matches[1] } else { $null }
        $pass = $out -match 'VERIFY-PASS'
        Write-Host ("   lat={0}ms fps={1} {2}" -f $lat, $fps, ($(if ($pass) { 'PASS' } else { 'NO/FAIL' }))) -ForegroundColor ($(if ($lat) { 'Green' } else { 'Red' }))
        $rows += [pscustomobject]@{ Platform = $plat; Encoder = $encByPlat[$plat]; Profile = $Profile; Knob = $cfg.K; LatencyMs = $lat; Fps = $fps; Pass = $pass }
    }
}
$relay | Stop-Process -Force -ErrorAction SilentlyContinue

$csv = "$logDir/encoder-sweep.csv"
$rows | Export-Csv -NoTypeInformation -Path $csv
Write-Host "`n================ ENCODER SWEEP ($Profile) ================" -ForegroundColor Cyan
$rows | Sort-Object Platform, LatencyMs | Format-Table Platform, Encoder, Knob, LatencyMs, Fps, Pass -AutoSize | Out-String | Write-Host
foreach ($plat in ($rows.Platform | Select-Object -Unique)) {
    $best = $rows | Where-Object { $_.Platform -eq $plat -and $_.LatencyMs -ne $null -and $_.Pass } | Sort-Object LatencyMs | Select-Object -First 1
    if ($best) { Write-Host ("best {0}: {1} @ {2} ms" -f $plat, $best.Knob, $best.LatencyMs) -ForegroundColor Green }
}
Write-Host "csv: $csv"
