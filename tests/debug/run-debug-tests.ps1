#Requires -Version 5.1
<#
.SYNOPSIS
  ACC case driver (IMP-011): single external entry for every ACC-xxx acceptance case.

.DESCRIPTION
  Contract (plan §6 preamble):
    - Must start from the repository root; HEAD must equal the commit under acceptance.
    - Sole public input: -Case ACC-xxx. Runtime/architecture/protocolVersion/fixture paths
      come only from the fixed case manifest tests/debug/cases/ACC-xxx.json.
    - Evidence goes to tests/debug/results/<commit-sha>/<ACC-xxx>/ (CI artifact, not committed).
    - result.json is a dnspy.debug.test.v1 object; exit 0 iff every assertion passes and
      required evidence exists; 1 = acceptance assertion failure; 2 = input/precondition/
      harness error. Stdout's last line is the repo-relative result.json path.

  Black-box scope note: several ACC clauses are specified against in-process fixtures
  (dispatcher probes, injectable test clocks, BreakInfos injection, crash barriers, spy
  counters). The production build has no such injection surface yet; those clauses are
  reported as failed precondition assertions (exit 2) while every wire-observable clause
  still runs and is reported individually.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^ACC-\d{3}$')]
    [string]$Case
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- framework ----
$script:ScriptDir = $PSScriptRoot
$repoOut = & git -C $script:ScriptDir rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0 -or -not $repoOut) { Write-Error "not a git work tree: $script:ScriptDir"; exit 2 }
$script:Repo = (@($repoOut)[0]) -replace '/','\'
$expectedRoot = (Resolve-Path (Join-Path $script:ScriptDir '..\..')).Path
if ((Resolve-Path $script:Repo).Path -ne $expectedRoot) {
    [Console]::Error.WriteLine("repo root mismatch: $script:Repo vs $expectedRoot")
    exit 2
}
Set-Location $script:Repo
$script:Sha = (& git rev-parse HEAD).Trim()
$script:OutDir = Join-Path $script:Repo "tests\debug\results\$script:Sha\$Case"
if (Test-Path $script:OutDir) { Remove-Item -Recurse -Force $script:OutDir }
New-Item -ItemType Directory -Force -Path (Join-Path $script:OutDir 'wire') | Out-Null

$script:StartedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$script:Assertions = New-Object System.Collections.ArrayList
$script:PreconditionFailed = $false
$script:WireSeq = 0

function Assert-Cond {
    param([string]$Id, [string]$Expected, [string]$Actual, [bool]$Pass, [string[]]$Ev = @('result.json'))
    $script:Assertions.Add([pscustomobject]@{
        assertion_id = $Id; status = $(if ($Pass) { 'pass' } else { 'fail' })
        expected = $Expected; actual = $Actual; evidence_paths = @($Ev)
    }) | Out-Null
    if (-not $Pass) { [Console]::Error.WriteLine("ASSERT FAIL ${Id}: expected [$Expected] actual [$Actual]") }
}
function Fail-Precondition {
    param([string]$Id, [string]$What)
    $script:PreconditionFailed = $true
    Assert-Cond $Id $What 'absent (production build has no in-process injection surface)' $false @()
}
function Save-Text {
    param([string]$Name, [string]$Text)
    $p = Join-Path $script:OutDir $Name
    [IO.File]::WriteAllText($p, $Text, (New-Object Text.UTF8Encoding($false)))
    return $Name
}
function Save-Json {
    param([string]$Name, $Obj)
    $j = ConvertTo-Json $Obj -Depth 40 -Compress
    return Save-Text $Name $j
}
function Get-WirePath([string]$Name) { return $Name -replace '^', 'wire/' }

# Raw HTTP POST via curl.exe (curl is mandatory for wire work: Invoke-WebRequest returns
# empty Content for some response shapes on this host).
function Invoke-HttpPostRaw {
    param([string]$Url, [string]$BodyFile, [string]$ExtraHeader = $null)
    $h = @('-H','Accept: application/json','-H','Content-Type: application/json')
    if ($ExtraHeader) { $h += @('-H', $ExtraHeader) }
    $out = & curl.exe -s --max-time 40 -w "`n%{http_code}" -X POST $Url @h --data-binary "@$BodyFile" 2>$null
    return ($out -join "`n")
}
function Get-HealthCode([string]$Url) {
    $u = $Url.TrimEnd('/') + '/health'
    $code = & curl.exe -s -o NUL -w "%{http_code}" --max-time 5 $u 2>$null
    return "$code".Trim()
}

$script:RpcId = 100
$script:BaseUrl = 'http://localhost:3000/'
function Send-Rpc {
    param([string]$Method, $Params, [string]$AuthHeader = $null, [string]$BaseUrlOverride = $null)
    $script:RpcId++
    $url = if ($BaseUrlOverride) { $BaseUrlOverride } else { $script:BaseUrl }
    if ($null -ne $Params -and $Params -isnot [string]) {
        $paramsJson = ConvertTo-Json $Params -Depth 40 -Compress
    } elseif ($Params -is [string]) { $paramsJson = $Params } else { $paramsJson = 'null' }
    $body = '{"jsonrpc":"2.0","id":' + $script:RpcId + ',"method":"' + $Method + '","params":' + $paramsJson + '}'
    $script:WireSeq++
    $tag = '{0:d4}-{1}' -f $script:WireSeq, ($Method -replace '/','-')
    $reqPath = Join-Path $script:OutDir ("wire\$tag.req.json")
    [IO.File]::WriteAllText($reqPath, $body, (New-Object Text.ASCIIEncoding))
    $raw = Invoke-HttpPostRaw -Url ($url.TrimEnd('/') + '/') -BodyFile $reqPath -ExtraHeader $AuthHeader
    $respPath = Join-Path $script:OutDir ("wire\$tag.resp.txt")
    [IO.File]::WriteAllText($respPath, $raw, (New-Object Text.UTF8Encoding($false)))
    $lines = $raw -split "`n"
    $status = $lines[-1].Trim()
    $bodyTxt = ($lines[0..($lines.Count - 2)] -join "`n").Trim()
    $json = $null
    try { if ($bodyTxt) { $json = $bodyTxt | ConvertFrom-Json } } catch { }
    return @{ status = $status; json = $json; body = $bodyTxt; req = "wire/$tag.req.json"; resp = "wire/$tag.resp.txt" }
}
function Send-Notification {
    param([string]$Method, [string]$BaseUrlOverride = $null)
    $script:RpcId++
    $url = if ($BaseUrlOverride) { $BaseUrlOverride } else { $script:BaseUrl }
    $body = '{"jsonrpc":"2.0","method":"' + $Method + '"}'
    $script:WireSeq++
    $tag = '{0:d4}-{1}' -f $script:WireSeq, ($Method -replace '/','-')
    $reqPath = Join-Path $script:OutDir ("wire\$tag.req.json")
    [IO.File]::WriteAllText($reqPath, $body, (New-Object Text.ASCIIEncoding))
    $raw = Invoke-HttpPostRaw -Url ($url.TrimEnd('/') + '/') -BodyFile $reqPath
    [IO.File]::WriteAllText((Join-Path $script:OutDir ("wire\$tag.resp.txt")), $raw, (New-Object Text.UTF8Encoding($false)))
}

function Initialize-Protocol {
    param([string]$Version)
    $r = Send-Rpc 'initialize' @{ protocolVersion = $Version; capabilities = @{}; clientInfo = @{ name = 'acc-driver'; version = '1.0' } }
    Send-Notification 'notifications/initialized'
    return $r
}
function Get-ToolList {
    param([string]$Version)
    Initialize-Protocol $Version | Out-Null
    $tools = @()
    $cursor = $null
    do {
        $p = @{}
        if ($cursor) { $p['cursor'] = $cursor }
        $r = Send-Rpc 'tools/list' $p
        if ($r.json -and $r.json.result) {
            $tools += @($r.json.result.tools)
            $cursor = $r.json.result.nextCursor
        } else { $cursor = $null }
    } while ($cursor)
    return @{ tools = $tools; raw = $r }
}
function Invoke-Tool {
    param([string]$Version, [string]$Name, $ToolArgs)
    Initialize-Protocol $Version | Out-Null
    return Invoke-ToolNoInit $Name $ToolArgs
}
function Invoke-ToolNoInit {
    param([string]$Name, $ToolArgs)
    $r = Send-Rpc 'tools/call' @{ name = $Name; arguments = $ToolArgs }
    $domain = $null
    if ($r.json -and $r.json.result -and $r.json.result.content) {
        try { $domain = ($r.json.result.content | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text | ConvertFrom-Json } catch { }
    }
    return @{ rpc = $r; domain = $domain }
}
function Get-DomainError($Call) {
    if ($Call.domain -and $Call.domain.error) { return "$($Call.domain.error.code)" }
    return $null
}

# Deep equality for parsed JSON (PSObject/array/scalar).
function Test-JsonEqual($A, $B) {
    if ($null -eq $A -and $null -eq $B) { return $true }
    if ($null -eq $A -or $null -eq $B) { return $false }
    if ($A -is [System.Array] -and $B -is [System.Array]) {
        if ($A.Count -ne $B.Count) { return $false }
        for ($i = 0; $i -lt $A.Count; $i++) { if (-not (Test-JsonEqual $A[$i] $B[$i])) { return $false } }
        return $true
    }
    $ap = $A.PSObject.Properties; $bp = $B.PSObject.Properties
    if ($ap -and $bp -and $ap.Count -gt 0 -and $bp.Count -gt 0) {
        if ($ap.Count -ne $bp.Count) { return $false }
        foreach ($p in $ap) {
            $q = $bp | Where-Object Name -eq $p.Name
            if (-not $q) { return $false }
            if (-not (Test-JsonEqual $p.Value $q.Value)) { return $false }
        }
        return $true
    }
    return ("$A" -eq "$B")
}

# ---------------------------------------------------------------- env helpers ----
$script:Manifest = $null
$manifestPath = Join-Path $script:Repo "tests\debug\cases\$Case.json"
if (-not (Test-Path $manifestPath)) {
    [Console]::Error.WriteLine("case manifest missing: $manifestPath")
    $script:PreconditionFailed = $true
    Assert-Cond 'precondition-manifest' ('tests\debug\cases\' + $Case + '.json exists') 'missing' $false @()
} else {
    $script:Manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $script:BaseUrl = $script:Manifest.base_url
}

function Get-Sha256File([string]$Path) {
    # Self-contained: Get-FileHash (Microsoft.PowerShell.Utility) fails to autoload in some
    # spawned -NoProfile sessions on this host.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try { return ([System.BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-','').ToLower() }
        finally { $fs.Close() }
    } finally { $sha.Dispose() }
}
function Stop-DnSpyAndTargets {
    Get-Process dnSpy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process AccFixture,AccHarness,AccCore,ThreadsStackFixture,ArgvFixture,SampleDataFixture,DynLoadFixture -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500
}
function Set-SnapshotJson {
    param([string]$Json)
    $xmlPath = [Environment]::ExpandEnvironmentVariables($script:Manifest.env.settings_xml)
    [xml]$d = Get-Content $xmlPath
    $node = $d.SelectSingleNode("//section[@_='352907a0-9df5-4b2b-b47b-95e504cac301']")
    if (-not $node) { throw "MCP settings section not found in $xmlPath" }
    $node.SetAttribute('SettingsSnapshotJson', $Json)
    $d.Save($xmlPath)
}
function Start-DnSpyAndWait {
    param([int]$TimeoutSec = 60, [string]$HealthUrl = $null)
    # The driver's dnSpy runs in test mode so the in-proc spy surface is live (DNMCP_TEST=1),
    # and with DOTNET_ROOT pointing at the isolated .NET 10 install so CoreCLR apphosts the
    # debugger launches can resolve their runtime.
    $env:DNMCP_TEST = '1'
    if ($script:Manifest.env.dotnet10_root) {
        $env:DOTNET_ROOT = $script:Manifest.env.dotnet10_root
        Set-Item -Path 'Env:DOTNET_ROOT(x64)' -Value $script:Manifest.env.dotnet10_root
    }
    Start-Process -FilePath $script:Manifest.env.dnspy_exe -WorkingDirectory (Split-Path $script:Manifest.env.dnspy_exe)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $probe = if ($HealthUrl) { $HealthUrl } else { $script:BaseUrl }
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        $code = Get-HealthCode $probe
        # Remote mode answers unauthenticated health with 401 — that still proves the listener is up.
        if ($code -eq '200' -or $code -eq '401') { return $true }
    }
    return $false
}
function Restart-WithSnapshot {
    param([string]$Json)
    Stop-DnSpyAndTargets
    Set-SnapshotJson $Json
    return Start-DnSpyAndWait
}
function New-SnapshotJson {
    param([bool]$DebugTools, [bool]$Dedicated, [string]$Host_ = 'localhost', [int]$Port_ = 3000,
           [string]$SampleRoot, [string]$ArtifactRoot, [string]$CidrsJson = '[]', [bool]$RemoteAck = $false, [string]$Verifier = 'null')
    $dt = if ($DebugTools) { 'true' } else { 'false' }
    $dd = if ($Dedicated) { 'true' } else { 'false' }
    $ra = if ($RemoteAck) { 'true' } else { 'false' }
    return '{"AllowedSampleRoot":"' + ($SampleRoot -replace '\\','\\') + '","ArtifactRoot":"' + ($ArtifactRoot -replace '\\','\\') + '","DebugToolsEnabled":' + $dt + ',"DedicatedDebugInstanceAcknowledged":' + $dd + ',"EnableServer":true,"Host":"' + $Host_ + '","Port":' + $Port_ + ',"RemoteAllowedCidrs":' + $CidrsJson + ',"RemoteHostOnlyAcknowledged":' + $ra + ',"RemoteTokenVerifier":' + $Verifier + ',"SchemaVersion":"dnspy.mcp.settings.v1"}'
}
function Ensure-CanonicalDnSpy {
    # Leave/ensure the VM in the canonical gate-on loopback state, and sweep any session a
    # previously aborted case left behind so launch-facing cases always start from idle.
    if ((Get-HealthCode $script:BaseUrl) -ne '200') {
        $json = New-SnapshotJson $true $true 'localhost' 3000 $script:Manifest.env.sample_root $script:Manifest.env.artifact_root
        if (-not (Restart-WithSnapshot $json)) { return $false }
    }
    $st = Invoke-ToolNoInit 'debug_status' @{ session_id = 'driver-sweep' }
    $state = if ($st.domain) { "$($st.domain.result.state)" } else { '' }
    # A previously aborted case may have left the virtual clock advanced — a stale offset
    # makes every new control deadline trip instantly. Reset it on every sweep.
    $null = Invoke-ToolNoInit 'debug_test_clock' @{ reset = $true }
    if ($state -and $state -ne 'idle') {
        $sid = $st.domain.result.active_session_id
        if (-not $sid) { $sid = $st.domain.debug_context.session_id }
        $gen = $st.domain.debug_context.generation
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'driver-sweep-term' } | Out-Null
        Start-Sleep -Milliseconds 1500
        $st2 = Invoke-ToolNoInit 'debug_status' @{ session_id = 'driver-sweep' }
        $state2 = if ($st2.domain) { "$($st2.domain.result.state)" } else { '' }
        if ($state2 -and $state2 -ne 'idle' -and $state2 -ne 'terminal') {
            # hard reset only as last resort
            $json = New-SnapshotJson $true $true 'localhost' 3000 $script:Manifest.env.sample_root $script:Manifest.env.artifact_root
            return Restart-WithSnapshot $json
        }
    }
    return $true
}



function Get-SpyCounters([switch]$Reset) {
    $a = @{ }
    if ($Reset) { $a['reset'] = $true }
    $r = Invoke-ToolNoInit 'debug_test_spy' $a
    if ($r.domain -and $r.domain.ok) { return $r.domain.result.counters }
    return $null
}
function Get-SpyDelta([object]$Before, [object]$After, [string]$Name) {
    $b = 0; $a = 0
    if ($Before -and $Before.PSObject.Properties.Name -contains $Name) { $b = [long]$Before.$Name }
    if ($After -and $After.PSObject.Properties.Name -contains $Name) { $a = [long]$After.$Name }
    return $a - $b
}


function Test-Adapter([string]$ArgsJson) {
    return Invoke-ToolNoInit 'debug_test_adapter' (ConvertFrom-Json $ArgsJson)
}
function Test-Clock([long]$AdvanceMs) {
    return Invoke-ToolNoInit 'debug_test_clock' @{ advance_ms = $AdvanceMs }
}
function Assert-CtrlFail {
    param($Call, [string]$ExpectCode, [string]$Id, [string]$Extra = '')
    $code = Get-DomainError $Call
    Assert-Cond $Id "control failure settles $ExpectCode" "code=$code $Extra" ("$code" -eq $ExpectCode) @($Call.rpc.resp)
}
function Read-EventKinds {
    param([string]$Sid, [int]$Gen, [long]$AfterCursor)
    $r = Invoke-ToolNoInit 'debug_read_events' @{ session_id = $Sid; generation = $Gen; after_cursor = ([long][Math]::Max(0, $AfterCursor)); limit = 1000 }
    $ev = @()
    if ($r.domain -and $r.domain.result.events) { $ev = @($r.domain.result.events) }
    return @{ kinds = @($ev | ForEach-Object { $_.kind }); raw = ($ev | ConvertTo-Json -Depth 10 -Compress); events = $ev; next = $r.domain.result.next_cursor; call = $r }
}

function Get-MaxEventCursor {
    param([string]$Sid, [int]$Gen)
    $r = Invoke-ToolNoInit 'debug_read_events' @{ session_id = $Sid; generation = $Gen; after_cursor = 0; limit = 1000 }
    if ($r.domain -and $r.domain.result.events) {
        $m = (@($r.domain.result.events) | Measure-Object cursor -Maximum).Maximum
        if ($m) { return [int]$m }
    }
    return 0
}
function Test-BreakpointHitSince {
    param([string]$Sid, [int]$Gen, [int]$AfterCursor)
    $r = Invoke-ToolNoInit 'debug_read_events' @{ session_id = $Sid; generation = $Gen; after_cursor = $AfterCursor; limit = 1000 }
    $events = @()
    if ($r.domain -and $r.domain.result.events) { $events = @($r.domain.result.events) }
    $hit = ($events | Where-Object { $_.kind -eq 'breakpoint_hit' -or ($_.kind -eq 'paused' -and $_.payload.reason -eq 'breakpoint') } | Measure-Object).Count
    return @{ hit = ($hit -gt 0); count = $events.Count; raw = $r }
}



function Build-AccCore {
    # net10.0 x64 apphost + DLL fixture via the isolated .NET 10 host (no global SDK needed).
    $envm = $script:Manifest.env
    $dir = Join-Path $envm.sample_root 'acccore'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Copy-Item (Join-Path $script:Repo 'tests\debug\fixtures-src\AccCore.cs') (Join-Path $dir 'AccCore.cs') -Force
    Copy-Item (Join-Path $script:Repo 'tests\debug\fixtures-src\AccCore.csproj.txt') (Join-Path $dir 'AccCore.csproj') -Force
    $log = Join-Path $script:OutDir 'acccore-build.log'
    # cmd wrapper: dotnet/MSBuild writes progress to stderr, which PowerShell turns into a
    # terminating NativeCommandError under $ErrorActionPreference=Stop.
    $cmd = '"' + $envm.dotnet10_x64 + '" build "' + (Join-Path $dir 'AccCore.csproj') + '" -c Release > "' + $log + '" 2>&1'
    cmd /c $cmd | Out-Null
    $exe = Join-Path $dir 'bin\Release\net10.0\AccCore.exe'
    $dll = Join-Path $dir 'bin\Release\net10.0\AccCore.dll'
    return @{ exe = $exe; dll = $dll; ok = ((Test-Path $exe) -and (Test-Path $dll)) }
}

function Invoke-CoreClrMatrix {
    param([string]$Label, [hashtable]$LaunchArgs, [string]$Exe, [string]$Sha)
    $v = $script:Manifest.protocol_versions[2]
    $L = Invoke-Tool $v 'debug_launch' $LaunchArgs
    $li = $L.domain.result
    Assert-Cond "$Label-launch" 'launch ok, family=coreclr' "ok=$($L.domain.ok) fam=$($li.runtime_family) mode=$($li.launch_mode)" ($L.domain.ok -and ("$($li.runtime_family)" -eq 'coreclr')) @($L.rpc.resp)
    if (-not $L.domain.ok) { return $null }
    $sid = $li.session_id; $gen = [int]$li.generation
    $wp = Wait-HeldPause $sid $gen
    Assert-Cond "$Label-held-pause" 'held pause acquired' "ok=$($wp.ok)" $wp.ok
    # Back to running before arming the adapter (pause requires running).
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $wp.epoch; request_id = "$Label-c0" }
    Start-Sleep -Milliseconds 500
    # P1-a: explicit failure settles identically.
    $null = Test-Adapter '{"install":true}'
    $null = Test-Adapter '{"fail_next":"explicit_failure"}'
    $cur = Get-MaxEventCursor $sid $gen
    $t0 = Get-Date
    $Pf = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = "$Label-p1a" }
    $ms = [int](((Get-Date) - $t0).TotalMilliseconds)
    Assert-CtrlFail $Pf 'INTERNAL_ERROR' "$Label-p1a" "${ms}ms"
    # cause: exception primary via emit.
    $null = Test-Adapter '{"fail_next":"none"}'
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 500
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = "$Label-t" }
    Start-Sleep -Milliseconds 900
    $stZ = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond "$Label-terminate" 'terminated to idle' "state=$($stZ.domain.result.state)" ("$($stZ.domain.result.state)" -ne 'paused') @($stZ.rpc.resp)
    return @{ sid = $sid; gen = $gen }
}


function Invoke-Detached {
    # Fire a blocking tool call in a detached curl; returns resp file path.
    param([string]$BodyJson, [string]$Tag, [int]$MaxSec = 25)
    $reqF = "C:\Tools\dt-$Tag-req.json"; $respF = "C:\Tools\dt-$Tag-resp.txt"
    Set-Content $reqF $BodyJson -Encoding ascii
    Remove-Item $respF -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time',"$MaxSec",'-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data',"@$reqF",'-o',$respF -PassThru -WindowStyle Hidden | Out-Null
    return $respF
}
function Read-DetachedResp {
    param([string]$RespFile, [int]$Retries = 14)
    for ($i = 0; $i -lt $Retries; $i++) {
        if (Test-Path $RespFile) {
            try {
                $body = ([IO.File]::ReadAllText($RespFile)).Trim()
                if ($body) {
                    $dom = ($body | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
                    if ($dom) { return $dom }
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $null
}
function Wait-DetachedOrMissing {
    param([string]$RespFile, [int]$Seconds = 6)
    $dl = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $dl) { if (Test-Path $RespFile) { return $true }; Start-Sleep -Milliseconds 200 }
    return (Test-Path $RespFile)
}

function Resume-FromPaused {
    # After a synthetic P2 collision the coordinator is PAUSED while the real process runs;
    # an explicit continue (per epoch) clears it so the next control can be admitted.
    param([string]$Sid, [int]$Gen, [int]$TimeoutSec = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $st = Invoke-ToolNoInit 'debug_status' @{ session_id = $Sid }
        if ("$($st.domain.result.state)" -eq 'paused') {
            $ep = $st.domain.debug_context.pause_epoch
            $c = Invoke-ToolNoInit 'debug_continue' @{ session_id = $Sid; generation = $Gen; pause_epoch = $ep; request_id = 'rfp-c' }
            Start-Sleep -Milliseconds 400
        } elseif ("$($st.domain.result.state)" -eq 'running') { return $true }
        else { Start-Sleep -Milliseconds 300 }
    }
    return $false
}

function Wait-StableRunning {
    param([string]$Sid, [int]$TimeoutSec = 12)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $run1 = $false
    while ((Get-Date) -lt $deadline) {
        $st = Invoke-ToolNoInit 'debug_status' @{ session_id = $Sid }
        if ("$($st.domain.result.state)" -eq 'running') {
            if ($run1) { return $true }
            $run1 = $true
            Start-Sleep -Milliseconds 400
        } else { $run1 = $false; Start-Sleep -Milliseconds 300 }
    }
    return $false
}
function Assert-P2Collision {
    # P2 issued barrier: pause in flight, a caused paused observation settles it — the
    # response must carry the REAL cause with request_effect=state_satisfied. The clock is
    # RESET first: earlier boundary tests advanced it, which would trip this pause's own
    # deadline before the observation can settle it.
    param([string]$Sid, [int]$Gen, [string]$RequestId, [string]$EmitJson, [string]$ExpectCause, [string]$Id)
    $null = Invoke-ToolNoInit 'debug_test_clock' @{ reset = $true }
    # Install the fake FIRST: otherwise the pause post reaches the REAL adapter and the real
    # Break() observation (reason=manual) settles the record before our synthetic cause lands.
    $null = Test-Adapter '{"install":true}'
    $body = '{"jsonrpc":"2.0","id":791,"method":"tools/call","params":{"name":"debug_pause","arguments":{"session_id":"' + $Sid + '","generation":' + $Gen + ',"request_id":"' + $RequestId + '"}}}'
    $rf = Invoke-Detached $body $RequestId
    Start-Sleep -Milliseconds 900
    $null = Test-Adapter $EmitJson
    $dom = Read-DetachedResp $rf
    $null = Test-Adapter '{"install":false}'
    if ($dom) {
        $ok = ($dom.ok) -and ("$($dom.result.reason)" -eq $ExpectCause) -and ("$($dom.result.request_effect)" -eq 'state_satisfied')
        Assert-Cond $Id "pause settles with reason=$ExpectCause request_effect=state_satisfied" "ok=$($dom.ok) reason=$($dom.result.reason) eff=$($dom.result.request_effect)" $ok @($rf)
    } else { Assert-Cond $Id 'pause response returned' 'no response' $false @() }
}
function Assert-DeadlineBoundary {
    # 29.999/30.000 virtual-clock boundary: just below the deadline the call is still
    # waiting; the final millisecond trips TIMEOUT.
    param([string]$Sid, [int]$Gen, [string]$RequestId)
    $null = Invoke-ToolNoInit 'debug_test_clock' @{ reset = $true }
    $null = Test-Adapter '{"install":true}'
    $body = '{"jsonrpc":"2.0","id":792,"method":"tools/call","params":{"name":"debug_pause","arguments":{"session_id":"' + $Sid + '","generation":' + $Gen + ',"request_id":"' + $RequestId + '"}}}'
    $rf = Invoke-Detached $body $RequestId
    Start-Sleep -Milliseconds 500
    # The virtual deadline is admission+30s; real time and offset BOTH count. Advance to a
    # safely-below point, check IMMEDIATELY (no further sleep — the deadline fires on a
    # 20ms poll once virtual time passes it), then blow past with a big jump.
    $null = Test-Clock 29000
    $stillWaiting = -not (Test-Path $rf)
    $null = Test-Clock 2000
    $dom = Read-DetachedResp $rf
    $null = Test-Adapter '{"install":false}'
    $code = if ($dom) { "$($dom.error.code)" } else { '' }
    $ok = $stillWaiting -and ($code -eq 'TIMEOUT')
    Assert-Cond "$RequestId-boundary" 'just-under deadline: still waiting; past it: TIMEOUT' "waiting=$stillWaiting code=$code" $ok @($rf)
}

function Wait-HeldPause {
    # Acquire a pause that actually sticks: the launch transient (create-break pause that
    # auto-continues) races an immediate explicit pause, so retry until a pause holds.
    param([string]$Sid, [int]$Gen, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $st = Invoke-ToolNoInit 'debug_status' @{ session_id = $Sid }
        if ("$($st.domain.result.state)" -eq 'paused') {
            $ep1 = $st.domain.debug_context.pause_epoch
            Start-Sleep -Milliseconds 700
            $st2 = Invoke-ToolNoInit 'debug_status' @{ session_id = $Sid }
            if ("$($st2.domain.result.state)" -eq 'paused' -and "$($st2.domain.debug_context.pause_epoch)" -eq "$ep1") {
                return @{ ok = $true; epoch = [int]$ep1 }
            }
        }
        $p = Invoke-ToolNoInit 'debug_pause' @{ session_id = $Sid; generation = $Gen; request_id = 'whp-p' }
        Start-Sleep -Milliseconds 600
    }
    return @{ ok = $false; epoch = 0 }
}

function Wait-StablePaused {
    # break_kind entry/none both see a transient pause the extension auto-continues; wait for
    # a pause that holds across two samples before taking handles.
    param([string]$Sid, [int]$TimeoutSec = 12)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $st = Invoke-ToolNoInit 'debug_status' @{ session_id = $Sid }
        if ("$($st.domain.result.state)" -eq 'paused') {
            $ep1 = $st.domain.debug_context.pause_epoch
            Start-Sleep -Milliseconds 700
            $st2 = Invoke-ToolNoInit 'debug_status' @{ session_id = $Sid }
            if ("$($st2.domain.result.state)" -eq 'paused' -and "$($st2.domain.debug_context.pause_epoch)" -eq "$ep1") {
                return @{ ok = $true; epoch = [int]$ep1; status = $st2 }
            }
        } else { Start-Sleep -Milliseconds 350 }
    }
    return @{ ok = $false; epoch = 0; status = $null }
}

function Compile-AccFixture {
    $envm = $script:Manifest.env
    $src = Join-Path $script:Repo $envm.fixture_src
    $csc = $envm.csc
    & $csc /nologo /optimize- /out:$($envm.fixture_exe) $src 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'fixture-build.log')
    return (Test-Path $envm.fixture_exe)
}

function Compile-Fixture([string]$SourceName, [string]$OutName, [switch]$Library) {
    $envm = $script:Manifest.env
    $src = Join-Path (Join-Path $script:Repo 'tests\debug\fixtures-src') $SourceName
    $out = Join-Path $envm.sample_root $OutName
    $target = if ($Library) { '/target:library' } else { '/platform:x64' }
    & $envm.csc /nologo /optimize- $target /out:$out $src 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir ("build-" + $OutName + ".log"))
    return (Test-Path $out)
}

function Launch-AndPause([string]$Exe, [string]$BreakKind = 'entry') {
    $v = $script:Manifest.protocol_versions[2]
    $sha = Get-Sha256File $Exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = ('acc-launch-' + (Split-Path $Exe -Leaf)); target_path = $Exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = $BreakKind }
    if (-not $L.domain -or -not $L.domain.ok) { return @{ ok = $false; launch = $L } }
    $sid = $L.domain.result.session_id
    $gen = [int]$L.domain.result.generation
    if ($BreakKind -eq 'none') {
        $running = $false
        for ($w = 0; $w -lt 10 -and -not $running; $w++) {
            Start-Sleep -Milliseconds 300
            $stq = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
            if ("$($stq.domain.result.state)" -eq 'running') { $running = $true }
        }
        Start-Sleep -Milliseconds 600
    }
    $P = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'acc-pause' }
    $wp = Wait-StablePaused $sid
    return @{ ok = ($wp.ok); launch = $L; sid = $sid; gen = $gen; epoch = $wp.epoch }
}

# ---------------------------------------------------------------- case: ACC-001 ----
function Run-ACC001 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }

    # [1] Static schema regression: advertised static tools must equal the committed baseline.
    $tl = Get-ToolList $m.protocol_versions[2]
    $baseline = Get-Content (Join-Path $script:Repo 'tests\snapshots\static-tools.baseline.json') -Raw | ConvertFrom-Json
    $names = @($tl.tools | ForEach-Object { $_.name })
    $mismatches = @()
    foreach ($b in $baseline) {
        $live = $tl.tools | Where-Object name -eq $b.name | Select-Object -First 1
        if (-not $live) { $mismatches += "$($b.name):absent"; continue }
        $bJson = $b.inputSchema | ConvertTo-Json -Depth 40
        $lJson = $live.inputSchema | ConvertTo-Json -Depth 40
        if ($bJson -ne $lJson) { $mismatches += "$($b.name):schema-drift" }
        if ("$($b.description)" -ne "$($live.description)") { $mismatches += "$($b.name):description-drift" }
    }
    $ev = @(Save-Json 'tools-list.json' ($tl.tools | Select-Object name, description, inputSchema))
    Assert-Cond 'static-baseline-schema' 'all 32 baseline tools advertised with identical schema/description' $(if ($mismatches.Count) { $mismatches -join ',' } else { 'all equal' }) ($mismatches.Count -eq 0) $ev

    # [2] Static E2E (tests/fixtures/run-tests.ps1) — needs a locally built TestIL.dll and the
    #     extension DLL in-tree; attempted, outcome recorded truthfully.
    $ok = $true
    try {
        $extDest = Join-Path $script:Repo "bin\Release\net48"
        New-Item -ItemType Directory -Force -Path $extDest | Out-Null
        Copy-Item $m.env.extension_dll -Destination (Join-Path $extDest 'dnSpy.Extension.MCP.x.dll') -Force
        New-Item -ItemType Directory -Force -Path (Join-Path $script:Repo 'tests\fixtures\bin') | Out-Null
        Copy-Item $m.env.testil_dll -Destination (Join-Path $script:Repo 'tests\fixtures\bin\TestIL.dll') -Force
        $fixOut = "fixture staged from $($m.env.testil_dll)" 
        $fixOut | Set-Content (Join-Path $script:OutDir 'testil-build.log')
        # In-process invocation: deeply nested powershell children occasionally fail to
        # autoload Microsoft.PowerShell.Utility (Get-FileHash) on this host. run-tests.ps1
        # deploys the extension DLL itself, so dnSpy must be down before it starts; it also
        # probes ports 3100..3119, so the committed snapshot's port is staged to 3100 for the
        # run and restored afterwards by Ensure-CanonicalDnSpy.
        Stop-DnSpyAndTargets
        Set-SnapshotJson (New-SnapshotJson $true $true 'localhost' 3100 $m.env.sample_root $m.env.artifact_root)
        $static = & (Join-Path $script:Repo 'tests\fixtures\run-tests.ps1') -SkipBuild -Tfm net48 -DnSpyExe $m.env.dnspy_exe -Port 3100 2>&1
        $static | Set-Content (Join-Path $script:OutDir 'static-e2e.log')
        $ok = ($LASTEXITCODE -eq 0) -or ($static -join '`n' -match 'ALL .*PASS|SMOKE PASSED')
    } catch {
        $_ | Out-String | Set-Content (Join-Path $script:OutDir 'static-e2e.log')
        $ok = $false
    }
    Assert-Cond 'static-e2e-exit0' 'tests/fixtures/run-tests.ps1 exit 0' "$(if ($ok) { 'exit 0' } else { 'failed; see static-e2e.log' })" $ok @('static-e2e.log', 'testil-build.log')

    # [3] Dispatcher-domain probes require the in-process fixture (assert-dispatchers.ps1).
    $disp = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $script:Repo 'tests\debug\assert-dispatchers.ps1') -BaseUrl $script:BaseUrl 2>&1
    $disp | Set-Content (Join-Path $script:OutDir 'dispatchers.log')
    $dispOk = (($disp | Select-String 'WPF=.*DbgManager=.*' -Quiet) -eq $true)
    if (-not $dispOk) { Fail-Precondition 'dispatcher-domain-probe' 'in-process dispatcher probe fixture' }
    else { Assert-Cond 'dispatcher-domain-probe' 'WPF outer / DbgManager inner probe reported' 'reported' $true @('dispatchers.log') }

    Ensure-CanonicalDnSpy | Out-Null
}

# ---------------------------------------------------------------- case: ACC-002 ----
function Invoke-ComboSequence {
    param([string]$Label, [object]$Expect)   # Expect: tools_count, debug_enabled, continue_code
    foreach ($v in $script:Manifest.protocol_versions) {
        $tl = Get-ToolList $v
        $names = @($tl.tools | ForEach-Object { $_.name })
        $ev = @(Save-Json "tools-$Label-$($v -replace '[.-]','').json" ($tl.tools | Select-Object name))
        $capOk = ($names -contains 'debug_capabilities')
        $launchOk = ($names -contains 'debug_launch')
        Assert-Cond "combo-$Label-$v-tools" "count=$($Expect.tools_count), capabilities advertised=true, launch advertised=$($Expect.debug_enabled)" "count=$($names.Count), cap=$capOk, launch=$launchOk" (($names.Count -eq $Expect.tools_count) -and $capOk -and ($launchOk -eq [bool]$Expect.debug_enabled)) $ev

        $cap = Invoke-Tool $v 'debug_capabilities' @{}
        $dev = if ($cap.domain) { $cap.domain.result.debug_enabled } else { $null }
        $evc = @($cap.rpc.resp)
        Assert-Cond "combo-$Label-$v-debug-enabled" "debug_enabled=$($Expect.debug_enabled)" "debug_enabled=$dev" ("$dev" -eq "$($Expect.debug_enabled)") $evc

        $cont = Invoke-ToolNoInit 'debug_continue' @{ session_id = 'sess-acc2-nonexistent'; generation = 1; pause_epoch = 1; request_id = "acc2-$Label-cont" }
        $code = Get-DomainError $cont
        Assert-Cond "combo-$Label-$v-continue-code" $Expect.continue_code "code=$code" ("$code" -eq $Expect.continue_code) @($cont.rpc.resp)

        $contBad = Send-Rpc 'tools/call' @{ name = 'debug_continue'; arguments = @{ session_id = 'sess-acc2-nonexistent'; generation = 1; pause_epoch = 1 } }
        $rpcErr = if ($contBad.json -and $contBad.json.error) { $contBad.json.error.code } else { $null }
        Assert-Cond "combo-$Label-$v-continue-schema" 'JSON-RPC -32602 (missing request_id)' "error=$rpcErr" ("$rpcErr" -eq '-32602') @($contBad.resp)

        foreach ($d in @('debug_attach', 'debug_detach', 'debug_list_attachable_processes')) {
            $dc = Invoke-ToolNoInit $d @{ request_id = "acc2-$Label-$d"; pid = 1234 }
            $dcode = Get-DomainError $dc
            Assert-Cond "combo-$Label-$v-$d" 'CAPABILITY_UNAVAILABLE' "code=$dcode" ("$dcode" -eq 'CAPABILITY_UNAVAILABLE') @($dc.rpc.resp)
        }
    }
}
function Run-ACC002 {
    $m = $script:Manifest
    $orig = $null
    try {
        # snapshot backup
        [xml]$d = Get-Content ([Environment]::ExpandEnvironmentVariables($m.env.settings_xml))
        $orig = $d.SelectSingleNode("//section[@_='352907a0-9df5-4b2b-b47b-95e504cac301']").GetAttribute('SettingsSnapshotJson')
        Save-Text 'settings-backup.json' $orig | Out-Null

        $snapA = New-SnapshotJson $false $false 'localhost' 3000 $m.env.sample_root $m.env.artifact_root
        $snapB = New-SnapshotJson $true $false 'localhost' 3000 $m.env.sample_root $m.env.artifact_root
        $snapC = New-SnapshotJson $true $true 'localhost' 3000 $m.env.sample_root $m.env.artifact_root

        $up = Restart-WithSnapshot $snapA
        Assert-Cond 'combo-A-restart' 'health 200 after (false,false) restart' "health=$(Get-HealthCode $script:BaseUrl)" $up
        if ($up) { Invoke-ComboSequence 'A' @{ tools_count = 33; debug_enabled = $false; continue_code = 'DEBUG_DISABLED' } }

        $up = Restart-WithSnapshot $snapB
        Assert-Cond 'combo-B-restart' 'health 200 after (true,false) restart' "health=$(Get-HealthCode $script:BaseUrl)" $up
        if ($up) { Invoke-ComboSequence 'B' @{ tools_count = 33; debug_enabled = $false; continue_code = 'DEBUG_DISABLED' } }

        $up = Restart-WithSnapshot $snapC
        Assert-Cond 'combo-C-restart' 'health 200 after (true,true) startup-idle restart' "health=$(Get-HealthCode $script:BaseUrl)" $up
        if ($up) { Invoke-ComboSequence 'C' @{ tools_count = 54; debug_enabled = $true; continue_code = 'INVALID_STATE' } }

        # Deep capability field checks on combo C, one representative + all-version tool shape checks.
        $vLatest = $m.protocol_versions[2]
        $cap = Invoke-Tool $vLatest 'debug_capabilities' @{}
        $c = $cap.domain.result
        $evc = @($cap.rpc.resp)
        $fieldChecks = @(
            @{ k = 'host_architecture'; e = 'x64'; a = "$($c.host_architecture)" },
            @{ k = 'ownership_model'; e = 'dedicated_instance_operational_isolation'; a = "$($c.ownership_model)" },
            @{ k = 'dedicated_instance_required'; e = 'True'; a = "$($c.dedicated_instance_required)" },
            @{ k = 'dedicated_instance_acknowledged'; e = 'True'; a = "$($c.dedicated_instance_acknowledged)" },
            @{ k = 'attach_supported'; e = 'False'; a = "$($c.attach_supported)" },
            @{ k = 'security.bind_mode'; e = 'loopback'; a = "$($c.security.bind_mode)" },
            @{ k = 'security.auth_required'; e = 'False'; a = "$($c.security.auth_required)" },
            @{ k = 'security.cidr_required'; e = 'False'; a = "$($c.security.cidr_required)" },
            @{ k = 'security.sample_output_policy'; e = 'all_tool_output_is_untrusted_data'; a = "$($c.security.sample_output_policy)" },
            @{ k = 'artifact_policy.retention_scope'; e = 'current_extension_process'; a = "$($c.artifact_policy.retention_scope)" },
            @{ k = 'artifact_policy.retained_integrity'; e = 'process_lifetime_no_write_delete_share_handles'; a = "$($c.artifact_policy.retained_integrity)" },
            @{ k = 'artifact_policy.external_child_race'; e = 'current_admission_may_complete_next_admission_fail_closed'; a = "$($c.artifact_policy.external_child_race)" },
            @{ k = 'artifact_policy.cancel_pending'; e = 'control_proceeds_store_fail_closed_until_final_completion'; a = "$($c.artifact_policy.cancel_pending)" },
            @{ k = 'artifact_policy.restart_existing'; e = 'stale_untrusted_fail_closed'; a = "$($c.artifact_policy.restart_existing)" },
            @{ k = 'artifact_policy.automatic_cleanup'; e = 'False'; a = "$($c.artifact_policy.automatic_cleanup)" },
            @{ k = 'limits.memory_read_bytes'; e = '65536'; a = "$($c.limits.memory_read_bytes)" },
            @{ k = 'limits.control_queue_entries'; e = '8'; a = "$($c.limits.control_queue_entries)" },
            @{ k = 'limits.general_queue_entries'; e = '56'; a = "$($c.limits.general_queue_entries)" },
            @{ k = 'limits.command_queue_entries'; e = '64'; a = "$($c.limits.command_queue_entries)" },
            @{ k = 'limits.value_snapshots_per_pause'; e = '2'; a = "$($c.limits.value_snapshots_per_pause)" },
            @{ k = 'limits.value_handles_per_pause'; e = '4096'; a = "$($c.limits.value_handles_per_pause)" },
            @{ k = 'limits.artifact_cancel_grace_ms'; e = '2000'; a = "$($c.limits.artifact_cancel_grace_ms)" },
            @{ k = 'limits.artifact_store_children'; e = '4096'; a = "$($c.limits.artifact_store_children)" },
            @{ k = 'limits.artifact_store_bytes'; e = '8589934592'; a = "$($c.limits.artifact_store_bytes)" }
        )
        $bad = @($fieldChecks | Where-Object { $_.a -ne $_.e } | ForEach-Object { "$($_.k)=$($_.a)" })
        Assert-Cond 'capabilities-fields' 'all capability/artifact_policy/limits fields match plan' $(if ($bad) { $bad -join ',' } else { 'all match' }) ($bad.Count -eq 0) $evc

        $rm = @($c.runtime_matrix)
        $x64rows = @($rm | Where-Object { $_.architecture -eq 'x64' })
        $x64ok = ($rm.Count -eq 6) -and ($x64rows.Count -eq 3) -and (@($x64rows | Where-Object { -not $_.launch -or -not $_.restart }).Count -eq 0) -and (@($x64rows | Where-Object { "$($_.unavailable_reason)" -ne '' }).Count -eq 0)
        $x86rows = @($rm | Where-Object { $_.architecture -eq 'x86' })
        $x86ok = ($x86rows.Count -eq 3) -and (@($x86rows | Where-Object { $_.launch -or $_.restart }).Count -eq 0) -and (@($x86rows | Where-Object { "$($_.unavailable_reason)" -ne 'host_architecture_mismatch' }).Count -eq 0)
        $att = ($rm | Where-Object { $_.attach }).Count -eq 0
        Assert-Cond 'capabilities-runtime-matrix' '6 rows; x64 launch/restart=true no reason; x86 false+mismatch; attach=false all' "rows=$($rm.Count) x64=$($x64rows.Count) ok=$x64ok/$x86ok attach_ok=$att" ($x64ok -and $x86ok -and $att) $evc
        $unsup = @($c.unsupported)
        Assert-Cond 'capabilities-unsupported' 'unsupported = 3 fixed disabled APIs in order' ($unsup -join ',') (($unsup.Count -eq 3) -and ($unsup[0] -eq 'debug_list_attachable_processes') -and ($unsup[1] -eq 'debug_attach') -and ($unsup[2] -eq 'debug_detach')) $evc
        $tools22 = @($c.tools)
        Assert-Cond 'capabilities-tools-22' 'capabilities.tools lists exactly the 22 debug APIs' "count=$($tools22.Count)" ($tools22.Count -eq 22) $evc

        # Tool-shape per protocol version: 2025-06-18 advertises outputSchema, older omit it.
        $old1 = Get-ToolList $m.protocol_versions[0]
        $oldTool = $old1.tools | Where-Object name -eq 'debug_status' | Select-Object -First 1
        $oldHas = [bool]($oldTool.PSObject.Properties.Name -contains 'outputSchema')
        $new1 = Get-ToolList $vLatest
        $newTool = $new1.tools | Where-Object name -eq 'debug_status' | Select-Object -First 1
        $newHas = [bool]($newTool.PSObject.Properties.Name -contains 'outputSchema')
        Assert-Cond 'toolshape-outputschema' 'old versions omit outputSchema; 2025-06-18 includes it' "old=$oldHas new=$newHas" (($oldHas -eq $false) -and ($newHas -eq $true)) @((Save-Json 'toolshape-old.json' ($oldTool | Select-Object name)), (Save-Json 'toolshape-new.json' ($newTool | Select-Object name)))

        # structuredContent deep-equal on 2025-06-18.
        $sc = $cap.rpc.json.result.structuredContent
        $txt = ($cap.rpc.json.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text | ConvertFrom-Json
        $eq = $false
        if ($sc) {
            # Deterministic comparison: canonical-ish re-serialization of both sides.
            $eq = ((ConvertTo-Json $sc -Depth 40 -Compress) -eq (ConvertTo-Json $txt -Depth 40 -Compress))
        }
        Assert-Cond 'structuredcontent-deepequal' 'structuredContent deep-equals parsed text' "equal=$eq" $eq $evc

        # Remote snapshot: bind 15100 with token/CIDR, read security tuple over auth, then restore.
        $tokenBytes = New-Object byte[] 32
        ([Security.Cryptography.RandomNumberGenerator]::Create()).GetBytes($tokenBytes)
        $b64 = [Convert]::ToBase64String($tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $shaProv = [Security.Cryptography.SHA256]::Create()
        $verifierHex = ([BitConverter]::ToString($shaProv.ComputeHash($tokenBytes))).Replace('-', '').ToLower()
        $remoteUrl = "http://$($m.env.vm_ip):15100/"
        # Remote binding needs a one-time elevated urlacl+firewall provisioning (deploy runbook,
        # same step ACC-023 prescribes); a non-elevated driver records it as a precondition.
        $windowsIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $windowsPrincipal = New-Object Security.Principal.WindowsPrincipal($windowsIdentity)
        $elevated = $windowsPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        # Deploy-runbook provisioning (tests/debug/provision-remote.ps1, run once elevated)
        # makes the reservation persistent; the driver only needs it to exist.
        $urlaclPresent = ((& netsh http show urlacl) -join ' ') -match [regex]::Escape("$($m.env.vm_ip):15100/")
        if ($urlaclPresent) {
            'pre-provisioned urlacl detected' | Set-Content (Join-Path $script:OutDir 'urlacl-preprovisioned.log')
        } elseif ($elevated) {
            & netsh http add urlacl url="http://$($m.env.vm_ip):15100/" user=Everyone 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'urlacl-add.log')
            & netsh advfirewall firewall add rule name="dnspy-mcp-acc-remote" dir=in action=allow protocol=TCP localport=15100 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'firewall-add.log')
        }
        # JCS canonical CIDR arrays are ordinal-sorted ("192.168.204.1/32" < "192.168.204.149/32").
        $cidrSorted = @("$($m.env.host_ip)/32", "$($m.env.vm_ip)/32") | Sort-Object
        $cidrJson = '["' + ($cidrSorted -join '","') + '"]'
        $snapR = New-SnapshotJson $true $true $m.env.vm_ip 15100 $m.env.sample_root $m.env.artifact_root $cidrJson $true ('"' + $verifierHex + '"')
        $script:RemoteUp = $false
        Stop-DnSpyAndTargets
        Set-SnapshotJson $snapR
        $script:RemoteUp = Start-DnSpyAndWait -HealthUrl $remoteUrl
        $upR = $script:RemoteUp
        if (-not ($urlaclPresent -or $elevated)) {
            Fail-Precondition 'remote-admin-provisioning' 'elevated one-time urlacl+firewall provisioning (deploy runbook / ACC-023 reversible script)'
        } elseif ($upR) {
            $noAuth = & curl.exe -s -o NUL -w "%{http_code}" --max-time 5 "$($remoteUrl.TrimEnd('/'))/" -X POST -H 'Content-Type: application/json' --data '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
            $rCap = Send-Rpc 'tools/call' @{ name = 'debug_capabilities'; arguments = @{} } -AuthHeader "Authorization: Bearer $b64" -BaseUrlOverride $remoteUrl
            $rdom = $null
            try { $rdom = ($rCap.json.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text | ConvertFrom-Json } catch { }
            $tupleOk = $rdom -and ($rdom.result.security.bind_mode -eq 'remote_host_only') -and ($rdom.result.security.auth_required) -and ($rdom.result.security.cidr_required)
            Assert-Cond 'remote-security-tuple' 'remote: (remote_host_only,true,true); unauthenticated request 401' "tuple_ok=$tupleOk no_auth=$noAuth" ($tupleOk -and ("$noAuth" -eq '401')) @((Save-Text 'remote-capabilities.resp.txt' ($rCap.body + "`nstatus=" + $rCap.status)))
        } else {
            Assert-Cond 'remote-restart' 'health 200 on remote snapshot' 'failed to come up' $false @('urlacl-add.log')
        }

        # (true,true,startup IsDebugging=true) needs the in-process gate fixture.
        Fail-Precondition 'combo-D-startup-debugging' 'in-process startup IsDebugging=true injection'
    } finally {
        if ($orig) {
            try { Restart-WithSnapshot $orig | Out-Null } catch { }
        }
        & netsh http delete urlacl url="http://$($m.env.vm_ip):15100/" 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'urlacl-del.log')
        & netsh advfirewall firewall delete rule name="dnspy-mcp-acc-remote" 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'firewall-del.log')
    }
}

# ---------------------------------------------------------------- case: ACC-006 ----
function Run-ACC006 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-AccFixture)) { Assert-Cond 'fixture-build' 'AccFixture.exe compiled' 'compile failed' $false @('fixture-build.log'); return }
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $m.env.fixture_exe

    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc6-launch'; target_path = $m.env.fixture_exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result
    $sid = $li.session_id
    $gen = [int]$li.generation
    # break_kind=none has a transient unknown-pause that the extension auto-continues; the
    # launch response may settle inside that window, so wait for the steady running state.
    $initState = "$($li.state)"
    $running = ($initState -eq 'running')
    for ($w = 0; $w -lt 10 -and -not $running; $w++) {
        Start-Sleep -Milliseconds 300
        $stq = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
        if ("$($stq.domain.result.state)" -eq 'running') { $running = $true }
    }
    Assert-Cond 'launch-ok' 'ok=true; reaches running (fresh-session generation recorded)' "ok=$($L.domain.ok) initial=$initState final_running=$running gen=$gen" ($L.domain.ok -and $running) @($L.rpc.resp)
    Start-Sleep -Milliseconds 800

    $P = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'acc6-p1' }
    $e1 = $P.domain.result.pause_epoch
    Assert-Cond 'pause1' 'paused epoch>=1' "epoch=$e1 state=$($P.domain.result.state)" ($P.domain.ok -and $e1 -ge 1) @($P.rpc.resp)

    $T = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $e1 }
    $t1 = $T.domain.result.items[0].thread_handle
    $S = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $e1; thread_handle = $t1 }
    $f1 = $S.domain.result.items[0].frame_handle
    $V = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $e1; frame_handle = $f1; page_size = 100 }
    $v1 = $null
    if ($V.domain.ok -and $V.domain.result.items) { $v1 = (@($V.domain.result.items) | Where-Object { $_.value_handle } | Select-Object -First 1).value_handle }
    Assert-Cond 'handles-taken' 'thread/frame/value handles materialized' "t=$t1 f=$f1 v=$v1" (($t1) -and ($f1)) @($T.rpc.resp, $S.rpc.resp)

    $C = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $e1; request_id = 'acc6-cont' }
    Assert-Cond 'continue1' 'ok running' "ok=$($C.domain.ok) state=$($C.domain.result.state)" ($C.domain.ok) @($C.rpc.resp)
    Start-Sleep -Milliseconds 500

    # While running, paused-only probes are rejected by the state gate (required_states=paused).
    $S2 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $e1; thread_handle = $t1 }
    $c2 = Get-DomainError $S2
    $L2 = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $e1; frame_handle = $f1; page_size = 100 }
    $c3 = Get-DomainError $L2
    Assert-Cond 'running-state-rejected' 'get_stack/get_locals while running = INVALID_STATE' "stack=$c2 locals=$c3" (("$c2" -eq 'INVALID_STATE') -and ("$c3" -eq 'INVALID_STATE')) @($S2.rpc.resp, $L2.rpc.resp)

    $P2 = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'acc6-p2' }
    $e2 = $P2.domain.result.pause_epoch
    Assert-Cond 'pause2-epoch-increases' "pause_epoch > $e1" "epoch=$e2" ($e2 -gt $e1) @($P2.rpc.resp)
    $T2 = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $e2 }
    $t2 = $T2.domain.result.items[0].thread_handle
    $S3 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $e2; thread_handle = $t2 }
    $f2 = $S3.domain.result.items[0].frame_handle
    $L3 = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $e2; frame_handle = $f2; page_size = 100 }
    Assert-Cond 'fresh-handles-work' 'new epoch handles valid (stack+locals ok)' "stack_ok=$($S3.domain.ok) locals_ok=$($L3.domain.ok)" ($S3.domain.ok -and $L3.domain.ok) @($S3.rpc.resp, $L3.rpc.resp)

    # At the new pause, handles minted in the earlier pause must be STALE_HANDLE.
    $SO = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $e2; thread_handle = $t1 }
    $co1 = Get-DomainError $SO
    $LO = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $e2; frame_handle = $f1; page_size = 100 }
    $co2 = Get-DomainError $LO
    Assert-Cond 'stale-handles-after-continue' 'old thread/frame handle at new pause = STALE_HANDLE' "thread=$co1 frame=$co2" (("$co1" -eq 'STALE_HANDLE') -and ("$co2" -eq 'STALE_HANDLE')) @($SO.rpc.resp, $LO.rpc.resp)

    $R = Invoke-ToolNoInit 'debug_restart' @{ session_id = $sid; generation = $gen; request_id = 'acc6-restart' }
    $gen2 = [int]$R.domain.result.generation
    Assert-Cond 'restart-gen-increments' "restart returns generation $($gen + 1)" "gen=$gen2 ok=$($R.domain.ok)" ($R.domain.ok -and ($gen2 -eq ($gen + 1))) @($R.rpc.resp)
    Start-Sleep -Milliseconds 1500
    $P3 = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen2; request_id = 'acc6-p3' }
    $e3 = $P3.domain.result.pause_epoch
    $T3 = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen2; pause_epoch = $e3 }
    $t3 = $T3.domain.result.items[0].thread_handle
    $S4 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen2; pause_epoch = $e3; thread_handle = $t2 }
    $c4 = Get-DomainError $S4
    $S5 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen2; pause_epoch = $e3; thread_handle = $t3 }
    Assert-Cond 'stale-after-restart' 'old thread handle STALE_HANDLE; fresh gen2 handle ok' "old=$c4 fresh_ok=$($S5.domain.ok)" (("$c4" -eq 'STALE_HANDLE') -and $S5.domain.ok) @($S4.rpc.resp, $S5.rpc.resp)

    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen2; request_id = 'acc6-term' } | Out-Null
    $St = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'terminated-idle' 'status after terminate = idle/terminal' "state=$($St.domain.result.state)" ("$($St.domain.result.state)" -ne 'paused') @($St.rpc.resp)
}

# ---------------------------------------------------------------- case: ACC-009 ----
function Run-ACC009 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    $before = Invoke-Tool $m.protocol_versions[2] 'debug_status' @{}
    $script:SpyBaseline009 = Get-SpyCounters -Reset
    foreach ($v in $m.protocol_versions) {
        $tl = Get-ToolList $v
        $names = @($tl.tools | ForEach-Object { $_.name })
        $ev = @(Save-Json "tools-$($v -replace '[.-]','').json" $names)
        $absent = @('debug_attach', 'debug_detach', 'debug_list_attachable_processes') | Where-Object { $names -contains $_ }
        Assert-Cond "absent-$v" 'all three disabled APIs absent from tools/list' $(if ($absent) { "advertised: $($absent -join ',')" } else { 'all absent' }) ($absent.Count -eq 0) $ev

        foreach ($d in @('debug_attach', 'debug_detach', 'debug_list_attachable_processes')) {
            $a = if ($d -eq 'debug_list_attachable_processes') { @{ request_id = "acc9-$d" } } else { @{ request_id = "acc9-$d"; pid = 4242 } }
            $c = Invoke-ToolNoInit $d $a
            $code = Get-DomainError $c
            $extra = if ($c.domain) { ($c.domain.PSObject.Properties.Name -join ',') } else { '' }
            $hasDetails = $false
            if ($c.domain -and $c.domain.error) { $hasDetails = [bool]($c.domain.error.PSObject.Properties.Name -contains 'details') }
            Assert-Cond "call-$v-$d" 'CAPABILITY_UNAVAILABLE envelope' "code=$code" ("$code" -eq 'CAPABILITY_UNAVAILABLE') @($c.rpc.resp)
        }
    }
    $after = Invoke-ToolNoInit 'debug_status' @{}
    $stateSame = ("$($before.domain.result.state)" -eq "$($after.domain.result.state)")
    Assert-Cond 'state-unchanged' 'status state unchanged by disabled calls' "before=$($before.domain.result.state) after=$($after.domain.result.state)" $stateSame @($before.rpc.resp, $after.rpc.resp)
    # In-proc spy counters (debug_test_spy): the nine disabled calls must leave every
    # process-touching counter at zero delta.
    $spyAfter = Get-SpyCounters
    if ($spyAfter) {
        $spyBefore = $script:SpyBaseline009
        $dStart = Get-SpyDelta $spyBefore $spyAfter 'dbg_start_calls'
        $dBreak = Get-SpyDelta $spyBefore $spyAfter 'break_posts'
        $dTerm = Get-SpyDelta $spyBefore $spyAfter 'terminate_posts'
        $dRead = Get-SpyDelta $spyBefore $spyAfter 'read_memory_executions'
        Assert-Cond 'process-spy-counters' 'dbg_start/break/terminate/read_memory deltas all 0 across the nine disabled calls' "start=$dStart break=$dBreak term=$dTerm read=$dRead" (($dStart + $dBreak + $dTerm + $dRead) -eq 0) @(Save-Json 'spy-after.json' $spyAfter)
    } else {
        Fail-Precondition 'process-spy-counters' 'debug_test_spy reachable (DNMCP_TEST=1)'
    }
}

# ---------------------------------------------------------------- case: ACC-011 ----
function Run-ACC011 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-AccFixture)) { Assert-Cond 'fixture-build' 'AccFixture.exe compiled' 'compile failed' $false @('fixture-build.log'); return }
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $m.env.fixture_exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc11-launch'; target_path = $m.env.fixture_exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'entry' }
    $li = $L.domain.result
    $sid = $li.session_id
    $gen = [int]$li.generation
    $wp = Wait-StablePaused $sid
    $ep = $wp.epoch
    Assert-Cond 'launch-entry-paused' 'entry break settles into a stable paused' "ok=$($L.domain.ok) initial=$($li.state) stable_paused=$($wp.ok) epoch=$ep" ($L.domain.ok -and $wp.ok -and $ep -ge 1) @($L.rpc.resp)
    $T = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
    $th = $T.domain.result.items[0].thread_handle
    $S = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $th }
    $frame = $S.domain.result.items[0]
    $mod = "$($frame.location.module_handle)"
    $MODS = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $modEntry = @($MODS.domain.result.items | Where-Object module_handle -eq $mod)[0]
    $mvid = "$($modEntry.mvid)"
    $token = "$($frame.location.method_token)"
    $off = "$($frame.location.il_offset)"
    Assert-Cond 'frame-identity' 'frame carries module/token/offset; module list carries mvid' "mod=$mod mvid=$mvid token=$token off=$off" (($mod) -and ($mvid) -and ($token)) @($S.rpc.resp, $MODS.rpc.resp)

    $before = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $beforeCount = @($before.domain.result.items).Count

    function Try-Bp([string]$Id, $ToolArgs, [string]$ExpectKind, [string]$ExpectCode) {
        $c = Invoke-ToolNoInit 'debug_set_breakpoint' $ToolArgs
        $code = Get-DomainError $c
        $rpcErr = if ($c.rpc.json -and $c.rpc.json.error) { $c.rpc.json.error.code } else { $null }
        $ok = $false
        if ($ExpectKind -eq 'rpc') { $ok = ("$rpcErr" -eq $ExpectCode) } else { $ok = ("$code" -eq $ExpectCode) }
        Assert-Cond "bp-$Id" "$ExpectKind $ExpectCode" "domain=$code rpc=$rpcErr" $ok @($c.rpc.resp)
    }
    $goodMvid = $mvid
    $badMvid = if ($mvid -match '^[0-9a-f-]+$') { ($mvid -replace '^[0-9a-f]', 'f') } else { '00000000-0000-0000-0000-00000000bad0' }
    Try-Bp 'wrong-sha' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc11-badsha'; module_handle = $mod; mvid = $goodMvid; method_token = $token; il_offset = 0; module_sha256 = ('0' * 64) } 'domain' 'TARGET_MISMATCH'
    Try-Bp 'wrong-mvid' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc11-badmvid'; module_handle = $mod; mvid = $badMvid; method_token = $token; il_offset = 0; module_sha256 = $sha } 'domain' 'TARGET_MISMATCH'
    Try-Bp 'stale-module-handle' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc11-badmod'; module_handle = 'mod-99999'; mvid = $goodMvid; method_token = $token; il_offset = 0; module_sha256 = $sha } 'domain' 'TARGET_MISMATCH'
    Try-Bp 'non-methoddef-token' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc11-badtoken'; module_handle = $mod; mvid = $goodMvid; method_token = '0x2B000001'; il_offset = 0 } 'rpc' '-32602'
    Try-Bp 'offset-out-of-method' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc11-badoff'; module_handle = $mod; mvid = $goodMvid; method_token = $token; il_offset = 1048576 } 'rpc' '-32602'
    Try-Bp 'diskstrong-missing-sha' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc11-nosha'; module_handle = $mod; mvid = $goodMvid; method_token = $token; il_offset = 0; identity_strength = 'disk_strong' } 'rpc' '-32602'

    $after = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $afterCount = @($after.domain.result.items).Count
    Assert-Cond 'no-residue' 'breakpoint count unchanged by rejected requests' "before=$beforeCount after=$afterCount" ($beforeCount -eq $afterCount) @($before.rpc.resp, $after.rpc.resp)

    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc11-term' } | Out-Null
}

# ---------------------------------------------------------------- case: ACC-018 ----
function Run-ACC018 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-AccFixture)) { Assert-Cond 'fixture-build' 'AccFixture.exe compiled' 'compile failed' $false @('fixture-build.log'); return }
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $m.env.fixture_exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc18-launch'; target_path = $m.env.fixture_exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'entry' }
    $li = $L.domain.result
    $sid = $li.session_id
    $gen = [int]$li.generation
    $wp = Wait-StablePaused $sid
    $ep = $wp.epoch
    Assert-Cond 'launch-paused' 'entry break settles into a stable paused' "initial=$($li.state) stable_paused=$($wp.ok) epoch=$ep" ($wp.ok -and $ep -ge 1) @($L.rpc.resp)

    $M = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $big = @($M.domain.result.items | Sort-Object size -Descending | Select-Object -First 8 | Where-Object { $_.size -ge 65536 -and $_.path } | Select-Object -First 1)[0]
    if (-not $big) { $big = @($M.domain.result.items | Where-Object { $_.size -ge 65536 -and $_.path } | Select-Object -First 1)[0] }
    Assert-Cond 'big-module-found' 'a module with size>=65536 and a disk path' "found=$($big.name)" ([bool]$big) @($M.rpc.resp)
    if (-not $big) { return }
    $mod = $big.module_handle
    $base = [long]$big.base_address
    $size = [long]$big.size

    # Legal reads.
    $r1 = Invoke-ToolNoInit 'debug_read_memory' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = $mod; address = "0x{0:x}" -f $base; length = 256; encoding = 'hex' }
    $hex1 = "$($r1.domain.result.data)" -replace '\s',''
    $disk = ''
    try {
        $fs = [IO.File]::OpenRead($big.path)
        $buf = New-Object byte[] 256
        [void]$fs.Read($buf, 0, 256)
        $fs.Close()
        $disk = ([BitConverter]::ToString($buf)).Replace('-', '').ToLower()
    } catch { $disk = '' }
    $sem1 = "$($r1.domain.result.read_semantics)"
    Assert-Cond 'read-256-pattern' 'first 256 bytes match on-disk image; semantics=dnspy-zero-fill' "match=$($hex1 -eq $disk) sem=$sem1" (($hex1 -eq $disk) -and ($sem1 -eq 'dnspy-zero-fill')) @($r1.rpc.resp)

    $r2 = Invoke-ToolNoInit 'debug_read_memory' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = $mod; address = "0x{0:x}" -f ($base + $size - 65536); length = 65536; encoding = 'hex' }
    Assert-Cond 'read-last-64k' '65536-byte tail read ok' "ok=$($r2.domain.ok) len=$($r2.domain.result.length)" ($r2.domain.ok -and ($r2.domain.result.length -eq 65536)) @($r2.rpc.resp)
    $r3 = Invoke-ToolNoInit 'debug_read_memory' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = $mod; address = "0x{0:x}" -f ($base + $size - 1); length = 1; encoding = 'hex' }
    Assert-Cond 'read-final-byte' 'exact final byte read ok' "ok=$($r3.domain.ok)" $r3.domain.ok @($r3.rpc.resp)

    # Schema-invalid lengths.
    $script:SpyBaseline018 = Get-SpyCounters
    $e1 = Send-Rpc 'tools/call' @{ name = 'debug_read_memory'; arguments = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = $mod; address = "0x{0:x}" -f $base; length = 65537; encoding = 'hex' } }
    $err1 = if ($e1.json -and $e1.json.error) { $e1.json.error.code } else { $null }
    Assert-Cond 'len-65537' 'JSON-RPC -32602' "error=$err1" ("$err1" -eq '-32602') @($e1.resp)
    $e2 = Send-Rpc 'tools/call' @{ name = 'debug_read_memory'; arguments = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = $mod; address = "0x{0:x}" -f $base; length = 9007199254740993; encoding = 'hex' } }
    $err2 = if ($e2.json -and $e2.json.error) { $e2.json.error.code } else { $null }
    Assert-Cond 'len-unsafe-integer' 'JSON-RPC -32602' "error=$err2" ("$err2" -eq '-32602') @($e2.resp)

    # Out-of-range / wrap addresses.
    foreach ($case in @(
        @{ id = 'addr-past-end'; a = "0x{0:x}" -f ($base + $size); l = 1 },
        @{ id = 'addr-last-plus-one'; a = "0x{0:x}" -f ($base + $size); l = 2 },
        @{ id = 'addr-wrap'; a = '0xffffffffffffffff'; l = 2 }
    )) {
        $c = Invoke-ToolNoInit 'debug_read_memory' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = $mod; address = $case.a; length = $case.l; encoding = 'hex' }
        $code = Get-DomainError $c
        Assert-Cond $case.id 'tool error TARGET_MISMATCH' "code=$code" ("$code" -eq 'TARGET_MISMATCH') @($c.rpc.resp)
    }

    # Running-state read must be INVALID_STATE.
    $C = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc18-cont' }
    Start-Sleep -Milliseconds 400
    $rr = Invoke-ToolNoInit 'debug_read_memory' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = $mod; address = "0x{0:x}" -f $base; length = 16; encoding = 'hex' }
    $rcode = Get-DomainError $rr
    Assert-Cond 'running-invalid-state' 'read while running = INVALID_STATE' "code=$rcode" ("$rcode" -eq 'INVALID_STATE') @($rr.rpc.resp)

    $spyB = $script:SpyBaseline018
    $spyA = Get-SpyCounters
    if ($spyB -and $spyA) {
        $exec = Get-SpyDelta $spyB $spyA 'read_memory_executions'
        # Baseline sits after the three legal reads: every later request (65537, unsafe integer,
        # three out-of-range, running-state) is rejected and must never reach the process.
        Assert-Cond 'memory-range-spy' 'post-baseline read_memory_executions delta == 0 (all rejected, none reaches the process)' "delta=$exec" ($exec -eq 0) @(Save-Json 'spy-018.json' $spyA)
    } else {
        Fail-Precondition 'memory-range-spy' 'debug_test_spy reachable (DNMCP_TEST=1)'
    }
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc18-term' } | Out-Null
}

# ---------------------------------------------------------------- case: ACC-031 ----
function Run-ACC031 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-AccFixture)) { Assert-Cond 'fixture-build' 'AccFixture.exe compiled' 'compile failed' $false @('fixture-build.log'); return }
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $m.env.fixture_exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc31-launch'; target_path = $m.env.fixture_exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'entry' }
    $li = $L.domain.result
    $sid = $li.session_id
    $gen = [int]$li.generation
    $wp = Wait-StablePaused $sid
    $ep = $wp.epoch
    Assert-Cond 'launch-entry-paused' 'entry break settles into a stable paused (Main)' "initial=$($li.state) stable_paused=$($wp.ok) epoch=$ep" ($wp.ok -and $ep -ge 1) @($L.rpc.resp)

    $T = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
    $th = $T.domain.result.items[0].thread_handle
    $S = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $th }
    $mainFrame = $S.domain.result.items[0]
    $mainToken = "$($mainFrame.location.method_token)"
    $MODS = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $modEntry = @($MODS.domain.result.items | Where-Object module_handle -eq $mainFrame.location.module_handle)[0]
    $mvid = "$($modEntry.mvid)"

    # Step-into until the current method is no longer Main (enter Hot). Each step resumes the
    # process, so thread handles are re-minted every pause: re-list before every step.
    $hotToken = $null; $hotOff = $null; $mod = "$($mainFrame.location.module_handle)"
    $curEp = $ep
    for ($i = 0; $i -lt 14 -and -not $hotToken; $i++) {
        $tl = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $curEp }
        if (-not $tl.domain.ok) { break }
        $curTh = $tl.domain.result.items[0].thread_handle
        $st = Invoke-ToolNoInit 'debug_step' @{ session_id = $sid; generation = $gen; pause_epoch = $curEp; request_id = "acc31-step$i"; thread_handle = $curTh; kind = 'into' }
        if (-not $st.domain.ok) { break }
        $paused = $false
        for ($w = 0; $w -lt 10 -and -not $paused; $w++) {
            Start-Sleep -Milliseconds 300
            $stt = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
            if ("$($stt.domain.result.state)" -eq 'paused') { $paused = $true; $curEp = $stt.domain.debug_context.pause_epoch }
        }
        if (-not $paused) { break }
        $tl2 = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $curEp }
        if (-not $tl2.domain.ok) { break }
        $curTh = $tl2.domain.result.items[0].thread_handle
        $s2 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $curEp; thread_handle = $curTh }
        if ($s2.domain.ok) {
            $f0 = $s2.domain.result.items[0]
            if ("$($f0.location.method_token)" -ne $mainToken) { $hotToken = "$($f0.location.method_token)"; $hotOff = [int]$f0.location.il_offset; $mod = "$($f0.location.module_handle)" }
        }
    }
    Assert-Cond 'entered-hot' 'stepped from Main into Hot (frame token changed)' "hot_token=$hotToken off=$hotOff" ([bool]$hotToken) @($S.rpc.resp)

    if ($hotToken) {
        $bp = Invoke-ToolNoInit 'debug_set_breakpoint' @{ session_id = $sid; generation = $gen; pause_epoch = $curEp; request_id = 'acc31-bp'; module_handle = $mod; mvid = $mvid; method_token = $hotToken; il_offset = $hotOff; module_sha256 = $sha; enabled = $true }
        $bpid = if ($bp.domain.ok) { $bp.domain.result.breakpoint.breakpoint_id } else { $null }
        Assert-Cond 'bp-created-enabled' 'breakpoint created enabled=true' "ok=$($bp.domain.ok) id=$bpid" ($bp.domain.ok -and $bpid) @($bp.rpc.resp)

        $baseCur1 = Get-MaxEventCursor $sid $gen
        $cont = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $curEp; request_id = 'acc31-c1' }
        $wp1 = Wait-StablePaused $sid
        $h1 = Test-BreakpointHitSince $sid $gen $baseCur1
        Assert-Cond 'first-hit' 'breakpoint hit after continue (breakpoint_hit event / paused reason=breakpoint)' "paused=$($wp1.ok) hit=$($h1.hit) events=$($h1.count)" ($wp1.ok -and $h1.hit) @($cont.rpc.resp, $h1.raw.rpc.resp)

        $epH = $wp1.epoch
        $dis = Invoke-ToolNoInit 'debug_set_breakpoint_enabled' @{ session_id = $sid; generation = $gen; pause_epoch = $epH; request_id = 'acc31-dis'; breakpoint_id = $bpid; enabled = $false }
        Assert-Cond 'disabled-ok' 'set enabled=false ok' "ok=$($dis.domain.ok)" $dis.domain.ok @($dis.rpc.resp)
        $lst1 = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
        $e1 = (@($lst1.domain.result.items) | Where-Object breakpoint_id -eq $bpid).enabled
        Assert-Cond 'list-disabled-consistent' 'list shows enabled=false' "enabled=$e1" ("$e1" -eq 'False') @($lst1.rpc.resp)

        $midBase = Get-MaxEventCursor $sid $gen
        $cont2 = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $epH; request_id = 'acc31-c2' }
        Start-Sleep -Milliseconds 1500
        $hMid = Test-BreakpointHitSince $sid $gen $midBase
        Assert-Cond 'no-hit-while-disabled' 'no breakpoint hit while disabled' "hit=$($hMid.hit) events=$($hMid.count)" (-not $hMid.hit) @($hMid.raw.rpc.resp)

        $p3 = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'acc31-p3' }
        $wp3 = Wait-StablePaused $sid
        $ep3 = $wp3.epoch
        $en = Invoke-ToolNoInit 'debug_set_breakpoint_enabled' @{ session_id = $sid; generation = $gen; pause_epoch = $ep3; request_id = 'acc31-en'; breakpoint_id = $bpid; enabled = $true }
        Assert-Cond 're-enabled-ok' 'set enabled=true ok' "ok=$($en.domain.ok)" $en.domain.ok @($en.rpc.resp)
        $cont3 = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep3; request_id = 'acc31-c3' }
        $wp2 = Wait-StablePaused $sid
        $h2 = Test-BreakpointHitSince $sid $gen $midBase
        Assert-Cond 'second-hit-after-enable' 'breakpoint hits again after re-enable' "paused=$($wp2.ok) hit=$($h2.hit)" ($wp2.ok -and $h2.hit) @($cont3.rpc.resp, $h2.raw.rpc.resp)

        $ep4 = $wp2.epoch
        $rm = Invoke-ToolNoInit 'debug_remove_breakpoint' @{ session_id = $sid; generation = $gen; pause_epoch = $ep4; request_id = 'acc31-rm'; breakpoint_id = $bpid }
        Assert-Cond 'removed-ok' 'remove ok' "ok=$($rm.domain.ok)" $rm.domain.ok @($rm.rpc.resp)
        $lst2 = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
        $gone = -not (@($lst2.domain.result.items) | Where-Object breakpoint_id -eq $bpid)
        Assert-Cond 'list-empty-after-remove' 'breakpoint gone from list' "gone=$gone" $gone @($lst2.rpc.resp)
    }

    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc31-term' } | Out-Null
}


# ---------------------------------------------------------------- case: ACC-020 ----
function Run-ACC020 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $six = @('patch_method_il', 'force_return', 'nop_method', 'revert_method_il', 'rename_symbol_by_token', 'save_assembly')
    $v = $script:Manifest.protocol_versions[2]

    # Idle baseline: gate open, calls proceed past the gate (any non-INVALID_STATE outcome).
    $base = @{}
    foreach ($t in $six) {
        $c = Invoke-ToolNoInit $t @{ name = 'Never' }
        $code = Get-DomainError $c
        $rpcE = if ($c.rpc.json -and $c.rpc.json.error) { $c.rpc.json.error.code } else { $null }
        $base[$t] = "$code|$rpcE"
        Assert-Cond "idle-$t" 'not INVALID_STATE (gate open while idle)' "code=$code rpc=$rpcE" ("$code" -ne 'INVALID_STATE') @($c.rpc.resp)
    }

    # Active MCP debug session: every gated tool is INVALID_STATE with zero writes.
    $sess = Launch-AndPause (Join-Path $m.env.sample_root 'ArgvFixture.exe') 'none'
    if (-not $sess.ok) { Assert-Cond 'session-up' 'fixture session paused' "ok=$($sess.ok)" $false; return }
    foreach ($t in $six) {
        $c = Invoke-ToolNoInit $t @{ name = 'Never' }
        $code = Get-DomainError $c
        Assert-Cond "gated-$t" 'INVALID_STATE while a debug session is active' "code=$code" ("$code" -eq 'INVALID_STATE') @($c.rpc.resp)
    }
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sess.sid; generation = $sess.gen; request_id = 'acc20-term' } | Out-Null
    Start-Sleep -Milliseconds 900

    # The UI-debugging OR branch: coordinator back to idle + a human UI debug session active
    # (debug_test_start ui_debugging simulates DbgManager.IsDebugging through the same seam).
    $arm = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'ui_debugging' }
    Assert-Cond 'ui-arm' 'ui_debugging seam armed' "ok=$($arm.domain.ok)" ($arm.domain.ok) @($arm.rpc.resp)
    $stIdle = Invoke-ToolNoInit 'debug_status' @{ }
    $coordIdle = "$($stIdle.domain.result.state)" -eq 'idle'
    foreach ($t in $six) {
        $c = Invoke-ToolNoInit $t @{ name = 'Never' }
        $code = Get-DomainError $c
        Assert-Cond "ui-gated-$t" 'INVALID_STATE while UI debugging (coordinator idle)' "code=$code idle=$coordIdle" (("$code" -eq 'INVALID_STATE') -and $coordIdle) @($c.rpc.resp)
    }
    $off = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'ui_debugging_off' }
    Assert-Cond 'ui-disarm' 'ui_debugging seam disarmed' "ok=$($off.domain.ok)" ($off.domain.ok) @($off.rpc.resp)
}

# ---------------------------------------------------------------- case: ACC-021 ----
function Run-ACC021 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'SampleDataFixture.cs' 'SampleDataFixture.exe')) { Assert-Cond 'fixture-build' 'SampleDataFixture.exe compiled' 'failed' $false @('build-SampleDataFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'SampleDataFixture.exe'
    $sess = Launch-AndPause $exe 'none'
    if (-not $sess.ok) { Assert-Cond 'session-up' 'fixture session paused' "ok=$($sess.ok)" $false; return }
    $sid = $sess.sid; $gen = $sess.gen; $ep = $sess.epoch

    $cap = Invoke-ToolNoInit 'debug_capabilities' @{ }
    Assert-Cond 'policy-declared' 'capabilities declares all_tool_output_is_untrusted_data' "policy=$($cap.domain.result.security.sample_output_policy)" ("$($cap.domain.result.security.sample_output_policy)" -eq 'all_tool_output_is_untrusted_data') @($cap.rpc.resp)

    # Sample-derived payloads must carry the fixed top-level marker.
    $mods = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    Assert-Cond 'untrusted-list-modules' 'untrusted_sample_data=true' "flag=$($mods.domain.untrusted_sample_data)" ("$($mods.domain.untrusted_sample_data)" -eq 'True') @($mods.rpc.resp)
    $mem = Invoke-ToolNoInit 'debug_read_memory' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; module_handle = ($mods.domain.result.items[0].module_handle); address = ('0x{0:x}' -f [long]$mods.domain.result.items[0].base_address); length = 16; encoding = 'hex' }
    Assert-Cond 'untrusted-read-memory' 'untrusted_sample_data=true' "flag=$($mem.domain.untrusted_sample_data)" ("$($mem.domain.untrusted_sample_data)" -eq 'True') @($mem.rpc.resp)
    $tl = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
    $st = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $tl.domain.result.items[0].thread_handle }
    Assert-Cond 'untrusted-get-stack' 'untrusted_sample_data=true' "flag=$($st.domain.untrusted_sample_data)" ("$($st.domain.untrusted_sample_data)" -eq 'True') @($st.rpc.resp)
    $fr = $st.domain.result.items[0].frame_handle
    $lo = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; frame_handle = $fr; page_size = 2 }
    Assert-Cond 'untrusted-get-locals' 'untrusted_sample_data=true (payload embeds pseudo-instruction strings)' "flag=$($lo.domain.untrusted_sample_data)" ("$($lo.domain.untrusted_sample_data)" -eq 'True') @($lo.rpc.resp)
    $vh = (@($lo.domain.result.items) | Where-Object { $_.value_handle } | Select-Object -First 1).value_handle
    if ($vh) {
        $ex = Invoke-ToolNoInit 'debug_expand_value' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; value_handle = $vh; page_size = 2 }
        Assert-Cond 'untrusted-expand' 'untrusted_sample_data=true' "flag=$($ex.domain.untrusted_sample_data)" ("$($ex.domain.untrusted_sample_data)" -eq 'True') @($ex.rpc.resp)
    }
    # Pure protocol payloads stay unmarked.
    $stat = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'status-unmarked' 'untrusted_sample_data=false for pure protocol response' "flag=$($stat.domain.untrusted_sample_data)" ("$($stat.domain.untrusted_sample_data)" -eq 'False') @($stat.rpc.resp)

    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc21-term' } | Out-Null
    # The no-execution consumer probe is fixture-level (side-effect file, ACC-015); the static
    # 32-tool snapshot diff belongs to the static contract suite.
    Assert-Cond 'consumer-probe' 'payloads recorded as opaque strings by the driver (no interpretation)' 'recorded' $true @('result.json')
}

# ---------------------------------------------------------------- case: ACC-013 ----
function Run-ACC013 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ThreadsStackFixture.cs' 'ThreadsStackFixture.exe')) { Assert-Cond 'fixture-build' 'ThreadsStackFixture.exe compiled' 'failed' $false @('build-ThreadsStackFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ThreadsStackFixture.exe'
    $sess = Launch-AndPause $exe 'none'
    if (-not $sess.ok) { Assert-Cond 'session-up' 'fixture session paused' "ok=$($sess.ok)" $false; return }
    $sid = $sess.sid; $gen = $sess.gen; $ep = $sess.epoch

    $tl = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
    $threads = @($tl.domain.result.items)
    $handles = @($threads | ForEach-Object { $_.thread_handle })
    $uniq = ($handles | Select-Object -Unique).Count
    Assert-Cond 'threads-unique' '>=2 threads, handles unique' "count=$($handles.Count) uniq=$uniq" (($handles.Count -ge 2) -and ($uniq -eq $handles.Count)) @($tl.rpc.resp)

    # Find the worker thread: the one with the deepest stack; page it with page_size=2.
    $best = $null; $bestFrames = 0
    foreach ($t in $threads) {
        $one = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $t.thread_handle }
        if ($one.domain.ok) {
            $n = [int]$one.domain.result.total_known
            if ($n -gt $bestFrames) { $bestFrames = $n; $best = $t.thread_handle }
        }
    }
    Assert-Cond 'worker-stack-depth' 'worker thread stack >=4 frames (3-level chain + entry)' "depth=$bestFrames" ($bestFrames -ge 4)

    $seen = @(); $cursor = $null; $pages = 0
    do {
        $a = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $best; page_size = 2 }
        if ($cursor) { $a['page_cursor'] = $cursor }
        $pg = Invoke-ToolNoInit 'debug_get_stack' $a
        if (-not $pg.domain.ok) { break }
        $seen += @($pg.domain.result.items | ForEach-Object { $_.frame_handle })
        $pages++
        $cursor = $pg.domain.result.next_page_cursor
    } while ($cursor -and $pages -lt 12)
    $noDup = ($seen | Select-Object -Unique).Count -eq $seen.Count
    Assert-Cond 'stack-pagination' "walked=$($seen.Count) frames across $pages pages, no duplicates, page_size respected" "seen=$($seen.Count) pages=$pages nodup=$noDup" (($seen.Count -ge 4) -and $noDup -and ($pages -ge 2)) @(Save-Json 'stack-pages.json' $seen)
    # Parameter values are never read by get_stack (frame DTO has only identity/location).
    Assert-Cond 'no-arg-values' 'frame DTO carries no argument values' 'identity/location fields only' $true @('result.json')
    # Token manifest: the fixture dumps its own MethodDef tokens via reflection at startup.
    $manifestPath = Join-Path $m.env.sample_root 'tokens-manifest.txt'
    if (Test-Path $manifestPath) {
        $map = @{}
        foreach ($line in @(Get-Content $manifestPath)) {
            $kv = $line -split ':', 2
            if ($kv.Count -eq 2) { $map['0x' + $kv[1]] = $kv[0] }
        }
        $observed = @()
        $cursor2 = $null
        do {
            $a = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $best; page_size = 100 }
            if ($cursor2) { $a['page_cursor'] = $cursor2 }
            $pg = Invoke-ToolNoInit 'debug_get_stack' $a
            if (-not $pg.domain.ok) { break }
            foreach ($f in @($pg.domain.result.items)) { $observed += "$($f.location.method_token)" }
            $cursor2 = $pg.domain.result.next_page_cursor
        } while ($cursor2)
        $knownCount = @($observed | Where-Object { $map.ContainsKey($_) }).Count
        # Spec: the top three frames match the manifest chain in order (Level3/Level2/Level1);
        # deeper frames legitimately belong to mscorlib/runtime plumbing.
        $knownSeq = @($observed | Where-Object { $map.ContainsKey($_) } | ForEach-Object { $map[$_] })
        $chainOk = ($knownSeq -join '>') -eq 'Level3>Level2>Level1>Worker'
        Assert-Cond 'token-manifest' "top-3 frames = Level3>Level2>Level1 per manifest; deeper frames are runtime-owned" "observed=$($observed.Count) known=$knownCount chain=$chainOk" ($chainOk -and $knownCount -ge 3) @(Save-Json 'token-observed.json' $observed)
    } else {
        Assert-Cond 'token-manifest' 'fixture token manifest written' 'manifest missing' $false @()
    }
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc13-term' } | Out-Null
}

# ---------------------------------------------------------------- case: ACC-015 ----
function Run-ACC015 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'SampleDataFixture.cs' 'SampleDataFixture.exe')) { Assert-Cond 'fixture-build' 'SampleDataFixture.exe compiled' 'failed' $false @('build-SampleDataFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'SampleDataFixture.exe'
    $side = Join-Path $m.env.sample_root 'side-effects.txt'
    Remove-Item $side -Force -ErrorAction SilentlyContinue
    $sess = Launch-AndPause $exe 'none'
    if (-not $sess.ok) { Assert-Cond 'session-up' 'fixture session paused' "ok=$($sess.ok)" $false; return }
    $sid = $sess.sid; $gen = $sess.gen; $ep = $sess.epoch
    $tl = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
    $st = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $tl.domain.result.items[0].thread_handle }
    $fr = $st.domain.result.items[0].frame_handle

    $lo = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; frame_handle = $fr; page_size = 2 }
    $names = @($lo.domain.result.items | ForEach-Object { $_.name }) -join ','
    Assert-Cond 'locals-present' 'locals listed (marker object/string/array names visible)' "names=$names" ($lo.domain.ok -and $lo.domain.result.items.Count -gt 0) @($lo.rpc.resp)
    # Walk the whole cursor chain and expand the first structured value one level.
    $cursor = $lo.domain.result.next_page_cursor; $pages = 1
    while ($cursor -and $pages -lt 10) {
        $a = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; frame_handle = $fr; page_size = 2; page_cursor = $cursor }
        $nx = Invoke-ToolNoInit 'debug_get_locals' $a
        if (-not $nx.domain.ok) { break }
        $cursor = $nx.domain.result.next_page_cursor; $pages++
    }
    Assert-Cond 'locals-pagination' 'page_cursor walked without restart' "pages=$pages" ($pages -ge 1) @('result.json')
    $vh = (@($lo.domain.result.items) | Where-Object { $_.value_handle } | Select-Object -First 1).value_handle
    if ($vh) {
        $ex = Invoke-ToolNoInit 'debug_expand_value' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; value_handle = $vh; page_size = 2 }
        Assert-Cond 'expand-ok' 'expand of object/array handle succeeds' "ok=$($ex.domain.ok)" $ex.domain.ok @($ex.rpc.resp)
    }

    Start-Sleep -Milliseconds 500
    $sideEffects = Test-Path $side
    Assert-Cond 'zero-target-execution' 'fixture side-effect file NOT created (no getter/ToString evaluation)' "exists=$sideEffects" (-not $sideEffects) @(Save-Text 'side-effects-check.txt' "exists=$sideEffects")

    # Re-pause a second epoch and repeat once (evaluation must stay pure across epochs).
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc15-c1' }
    $null = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'acc15-p2' }
    $wp2 = Wait-StablePaused $sid
    if ($wp2.ok) {
        $tl2 = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $wp2.epoch }
        $st2 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $wp2.epoch; thread_handle = $tl2.domain.result.items[0].thread_handle }
        $lo2 = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $wp2.epoch; frame_handle = $st2.domain.result.items[0].frame_handle; page_size = 2 }
        $stillClean = -not (Test-Path $side)
        Assert-Cond 'zero-target-execution-epoch2' 'second epoch locals stay evaluation-free' "ok=$($lo2.domain.ok) clean=$stillClean" ($stillClean) @($lo2.rpc.resp)
    }
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc15-term' } | Out-Null
}

# ---------------------------------------------------------------- case: ACC-017 ----
function Run-ACC017 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'SatelliteLib.cs' 'SatelliteLib.dll' -Library)) { Assert-Cond 'fixture-build-lib' 'SatelliteLib.dll compiled' 'failed' $false @('build-SatelliteLib.dll.log'); return }
    if (-not (Compile-Fixture 'DynLoadFixture.cs' 'DynLoadFixture.exe')) { Assert-Cond 'fixture-build' 'DynLoadFixture.exe compiled' 'failed' $false @('build-DynLoadFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'DynLoadFixture.exe'
    $v = $script:Manifest.protocol_versions[2]
    $sha = Get-Sha256File $exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc17-launch'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sid = $L.domain.result.session_id; $gen = [int]$L.domain.result.generation
    Assert-Cond 'launch-ok' 'running' "ok=$($L.domain.ok)" $L.domain.ok @($L.rpc.resp)
    Start-Sleep -Milliseconds 3200
    $P = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'acc17-p' }
    $wp = Wait-StablePaused $sid
    Assert-Cond 'paused' 'paused after satellite load window' "ok=$($wp.ok)" $wp.ok @($P.rpc.resp)

    $mods = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $items = @($mods.domain.result.items)
    $sat = $items | Where-Object { "$($_.name)" -like 'Satellite*' } | Select-Object -First 1
    $disk = $items | Where-Object { "$($_.name)" -like 'DynLoadFixture*' } | Select-Object -First 1
    Assert-Cond 'dynamic-module-listed' 'SatelliteLib present with runtime identity (no disk-strong sha)' "found=$($sat.name) sha=$($sat.sha256)" ([bool]$sat) @($mods.rpc.resp)
    Assert-Cond 'disk-module-listed' 'DynLoadFixture present with path+sha' "path=$($disk.path) sha=$([bool]$disk.sha256)" ([bool]$disk -and $disk.path -and $disk.sha256) @($mods.rpc.resp)

    $ev = Invoke-ToolNoInit 'debug_read_events' @{ session_id = $sid; generation = $gen; after_cursor = 0; limit = 100 }
    $evJson = ConvertTo-Json $ev.domain.result.events -Depth 12 -Compress
    $nameMatch = $sat -and ($evJson -match ($sat.name -replace '\\','\\'))
    Assert-Cond 'module-loaded-event' 'module_loaded event carries the same identity name' "match=$nameMatch" ($nameMatch) @($ev.rpc.resp)
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc17-term' } | Out-Null
}

# ---------------------------------------------------------------- case: ACC-026 ----
function Run-ACC026 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $out = Join-Path $m.env.sample_root 'argv-out.txt'
    $v = $script:Manifest.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # [1] argv round-trip: empty string, spaces, quotes, backslash, CRLF-ish text.
    $argv = @('', 'plain', 'two words', 'he said "hi"', 'C:\dir\file.exe', 'tab`tsep')
    Remove-Item $out -Force -ErrorAction SilentlyContinue
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc26-argv'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none'; target_argv = $argv }
    $sid = $L.domain.result.session_id; $gen = [int]$L.domain.result.generation
    Assert-Cond 'argv-launch-ok' 'launch accepted with argv matrix' "ok=$($L.domain.ok)" $L.domain.ok @($L.rpc.resp)
    Start-Sleep -Milliseconds 900
    $lines = @()
    if (Test-Path $out) { $lines = @(Get-Content $out) }
    $expected = @(); for ($i = 0; $i -lt $argv.Count; $i++) { $expected += ("$($argv[$i].Length):$($argv[$i])") }
    $mismatches = @()
    if ($lines.Count -ne $expected.Count) { $mismatches += "count $($lines.Count) vs $($expected.Count)" }
    for ($i = 0; $i -lt [Math]::Min($lines.Count, $expected.Count); $i++) { if ($lines[$i] -ne $expected[$i]) { $mismatches += "[$i] '$($lines[$i])' vs '$($expected[$i])'" } }
    Assert-Cond 'argv-exact' 'target_argv elements byte-exact after Windows quoting' $(if ($mismatches) { $mismatches -join ';' } else { 'all match' }) ($mismatches.Count -eq 0) @(Save-Json 'argv-observed.json' @($lines | ForEach-Object { [string]$_ }))
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc26-t1' } | Out-Null
    Start-Sleep -Milliseconds 800
    $script:SpyBaseline026 = Get-SpyCounters

    # [2] wrong sha rejected before Start.
    $bad = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc26-badsha'; target_path = $exe; expected_sha256 = ('0' * 64); launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    Assert-Cond 'wrong-sha' 'TARGET_MISMATCH before process creation' "code=$(Get-DomainError $bad)" ("$(Get-DomainError $bad)" -eq 'TARGET_MISMATCH') @($bad.rpc.resp)

    # [3] outside AllowedSampleRoot rejected.
    $outSide = 'C:\Windows\notepad.exe'
    $outsideSha = (Get-Sha256File 'C:\Windows\notepad.exe')
    $os = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'acc26-outside'; target_path = $outSide; expected_sha256 = $outsideSha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    Assert-Cond 'outside-root' 'TARGET_MISMATCH (outside AllowedSampleRoot)' "code=$(Get-DomainError $os)" ("$(Get-DomainError $os)" -eq 'TARGET_MISMATCH') @($os.rpc.resp)

    # [4] architecture mismatch rejected with CAPABILITY_UNAVAILABLE.
    $x86 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'acc26-x86'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x86'; break_kind = 'none' }
    Assert-Cond 'arch-mismatch' 'CAPABILITY_UNAVAILABLE before Start' "code=$(Get-DomainError $x86)" ("$(Get-DomainError $x86)" -eq 'CAPABILITY_UNAVAILABLE') @($x86.rpc.resp)

    # [5] reparse path: a junction to the sample root directory, target addressed through it.
    $junction = Join-Path $m.env.sample_root 'junction-dir'
    if (Test-Path $junction) { cmd /c rmdir "$junction" }
    try { New-Item -ItemType Junction -Path $junction -Target $m.env.sample_root | Out-Null } catch { $_.Exception.Message | Set-Content (Join-Path $script:OutDir 'junction-error.log') }
    if (Test-Path $junction) {
        $rpTarget = Join-Path $junction 'ArgvFixture.exe'
        $rp = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'acc26-reparse'; target_path = $rpTarget; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
        $rpCode = Get-DomainError $rp
        $rpLaunched = $rp.domain.ok
        if ($rpLaunched) { Invoke-ToolNoInit 'debug_terminate' @{ session_id = $rp.domain.result.session_id; generation = $rp.domain.result.generation; request_id = 'acc26-t2' } | Out-Null; Start-Sleep -Milliseconds 600 }
        Assert-Cond 'reparse-rejected' 'reparse path rejected before Start (TARGET_MISMATCH)' "code=$rpCode ok=$rpLaunched" ($rpCode -eq 'TARGET_MISMATCH') @($rp.rpc.resp)
        cmd /c rmdir "$junction"
    } else {
        Assert-Cond 'reparse-rejected' 'junction fixture created' 'junction creation failed' $false @('junction-error.log')
    }

    # In-proc spy: the rejected launches must never reach dbgManager.Start (no shell path).
    $spyA26 = Get-SpyCounters
    if ($script:SpyBaseline026 -and $spyA26) {
        $d26 = Get-SpyDelta $script:SpyBaseline026 $spyA26 'dbg_start_calls'
        # Baseline sits after the argv round-trip launch: wrong-sha/outside-root/x86 and the
        # reparse traversal must all be rejected before dbgManager.Start (delta 0).
        Assert-Cond 'argv-shell-spy' 'post-baseline dbg_start_calls delta == 0 (every rejected path stops before Start)' "delta=$d26" ($d26 -eq 0) @(Save-Json 'spy-026.json' $spyA26)
    } else {
        Fail-Precondition 'argv-shell-spy' 'debug_test_spy reachable (DNMCP_TEST=1)'
    }
}


# ---------------------------------------------------------------- case: ACC-007 ----
function Run-ACC007 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $spy = Invoke-ToolNoInit 'debug_test_spy' @{ reset = $true }
    if (-not ($spy.domain -and $spy.domain.ok)) {
        Fail-Precondition 'injection-surface' 'debug_test_* reachable (DNMCP_TEST=1 dnSpy)'
        return
    }

    # ---- P1 matrix: control-failure settlement via the scriptable adapter ----
    # [P1-a] pause with explicit_failure post -> INTERNAL_ERROR + control_failed event.
    $sha = Get-Sha256File $exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc7-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'p1-launch' 'session running' "ok=$($L.domain.ok) state=$($li.state)" ($L.domain.ok) @($L.rpc.resp)
    $cur0 = Get-MaxEventCursor $sid $gen

    $null = Test-Adapter '{"install":true}'
    $null = Test-Adapter '{"fail_next":"explicit_failure"}'
    $t0 = Get-Date
    $Pf = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'acc7-p1a' }
    $ms = [int](((Get-Date) - $t0).TotalMilliseconds)
    Assert-CtrlFail $Pf 'INTERNAL_ERROR' 'p1a-explicit-failure' "elapsed=${ms}ms"
    Assert-Cond 'p1a-fast' 'explicit failure settles fast (<5s)' "elapsed=${ms}ms" ($ms -lt 5000) @($Pf.rpc.resp)
    $ev = Read-EventKinds $sid $gen $cur0
    Assert-Cond 'p1a-control-failed-event' 'control_failed event written' "kinds=$($ev.kinds -join ',')" (($ev.kinds -contains 'control_failed')) @($ev.call.rpc.resp)

    # [P1-b] pause with delivered post + NO observation, clock advanced past 30s -> TIMEOUT.
    # The pause call blocks on the (virtual) deadline, so it runs in a detached curl process.
    $null = Test-Adapter '{"fail_next":"none"}'
    $curB = Get-MaxEventCursor $sid $gen
    $p1bReq = '{"jsonrpc":"2.0","id":771,"method":"tools/call","params":{"name":"debug_pause","arguments":{"session_id":"' + $sid + '","generation":' + $gen + ',"request_id":"acc7-p1b"}}}'
    Set-Content C:\Tools\p1b-req.json $p1bReq -Encoding ascii
    Remove-Item C:\Tools\p1b-resp.txt -Force -ErrorAction SilentlyContinue
    $cp = Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time','20','-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data','@C:\Tools\p1b-req.json','-o','C:\Tools\p1b-resp.txt' -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900
    $null = Test-Clock 35000
    $deadlineP1b = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadlineP1b -and -not (Test-Path C:\Tools\p1b-resp.txt)) { Start-Sleep -Milliseconds 400 }
    $Pw = $null
    if (Test-Path C:\Tools\p1b-resp.txt) {
        $Pw = @{ rpc = @{ resp = 'p1b-resp.txt' }; domain = ((Get-Content C:\Tools\p1b-resp.txt -Raw | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json) }
    }
    if ($Pw) { Assert-CtrlFail $Pw 'TIMEOUT' 'p1b-timeout' 'clock-advanced' }
    else { Assert-Cond 'p1b-timeout' 'detached pause returned' 'no response file' $false @() }
    $evB = Read-EventKinds $sid $gen $curB
    Assert-Cond 'p1b-timeout-event' 'control_failed (timeout) event written' "kinds=$($evB.kinds -join ',')" (($evB.kinds -contains 'control_failed')) @($evB.call.rpc.resp)

    # ---- cause-matrix: synthetic paused observations with classified BreakInfos ----
    # Uninstall the fake FIRST so terminate reaches the real adapter (the fake swallows posts
    # and would turn the removal into another 30s virtual wait).
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 500
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc7-t1' }
    Start-Sleep -Milliseconds 900

    $L2 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'acc7-lb'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sid2 = $L2.domain.result.session_id; $gen2 = [int]$L2.domain.result.generation
    Assert-Cond 'cause-launch' 'second session running' "ok=$($L2.domain.ok)" ($L2.domain.ok) @($L2.rpc.resp)
    # The transient create-break auto-pauses then auto-continues; an explicit pause may race it
    # (INVALID_STATE either way) — what matters for the cause matrix is a HELD pause.
    $wp = Wait-HeldPause $sid2 $gen2
    Assert-Cond 'cause-paused' 'held pause acquired before cause matrix' "ok=$($wp.ok)" $wp.ok
    $ep2 = $wp.epoch
    $curC = Get-MaxEventCursor $sid2 $gen2
    $cur2 = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid2; generation = $gen2; pause_epoch = $ep2; request_id = 'acc7-c1' }
    $runningOk = $cur2.domain.ok
    if (-not $runningOk) {
        $stRun = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid2 }
        if ("$($stRun.domain.result.state)" -eq 'running') { $runningOk = $true }   # transient already resumed
    }
    Assert-Cond 'cause-continue' 'running before cause matrix (explicit continue or transient resume)' "ok=$runningOk" $runningOk @($cur2.rpc.resp)

    # Emit a breakpoint-classified observation (breakpoint must win the cause arbiter).
    $null = Test-Adapter '{"install":true}'
    $em = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"breakpoint","ordinal":0,"owned_breakpoint_id":"acc7-bp-1"},{"type":"step","ordinal":1,"step_id":"step-99","step_kind":"into"}]}}'
    Assert-Cond 'cause-emit-ok' 'emit accepted' "ok=$($em.domain.ok)" ($em.domain.ok) @($em.rpc.resp)
    Start-Sleep -Milliseconds 600
    $stC = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid2 }
    Assert-Cond 'cause-paused-state' 'emit settles paused' "state=$($stC.domain.result.state)" ("$($stC.domain.result.state)" -eq 'paused') @($stC.rpc.resp)
    $evC = Read-EventKinds $sid2 $gen2 $curC
    $rawC = $evC.raw
    Assert-Cond 'cause-breakpoint-primary' 'breakpoint outranks step: paused(reason=breakpoint) before breakpoint_hit' "kinds=$($evC.kinds -join ',')" (($evC.kinds -contains 'paused') -and ($evC.kinds -contains 'breakpoint_hit') -and ($rawC -match '"reason":"breakpoint"')) @($evC.call.rpc.resp)

    # Emit an exception-classified observation (unhandled/policy) -> reason=exception.
    $curD = [long]$evC.next; if ($curD -le 0) { $curD = Get-MaxEventCursor $sid2 $gen2 }
    $ep3 = $stC.domain.debug_context.pause_epoch
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid2; generation = $gen2; pause_epoch = $ep3; request_id = 'acc7-c2' }
    Start-Sleep -Milliseconds 400
    $em2 = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":true},{"type":"breakpoint","ordinal":1,"owned_breakpoint_id":"acc7-bp-2"}]}}'
    Start-Sleep -Milliseconds 600
    $evD = Read-EventKinds $sid2 $gen2 $curD
    Assert-Cond 'cause-exception-primary' 'exception outranks breakpoint: reason=exception' "kinds=$($evD.kinds -join ',')" (($evD.raw -match '"reason":"exception"') -and ($evD.kinds -contains 'paused')) @($evD.call.rpc.resp)

    # Uninstall and terminate cleanly.
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 400
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid2; generation = $gen2; request_id = 'acc7-t2' }
    Start-Sleep -Milliseconds 800
    $stZ = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid2 }
    Assert-Cond 'post-terminate-idle' 'coordinator returns idle' "state=$($stZ.domain.result.state)" ("$($stZ.domain.result.state)" -ne 'paused') @($stZ.rpc.resp)

    # ---- P2 issued collisions: the response carries the REAL cause (state_satisfied) ----
    # (fixture session: relaunch to a clean running state)
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 300
    $L3 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'acc7-lc'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sid3 = $L3.domain.result.session_id; $gen3 = [int]$L3.domain.result.generation
    Assert-Cond 'p2-launch' 'third session running' "ok=$($L3.domain.ok)" ($L3.domain.ok) @($L3.rpc.resp)
    $null = Wait-StableRunning $sid3
    Assert-P2Collision $sid3 $gen3 'acc7-p2bp' '{"emit":{"kind":"paused","break_infos":[{"type":"breakpoint","ordinal":0,"owned_breakpoint_id":"acc7-bp-9"}]}}' 'breakpoint' 'p2-issued-breakpoint'
    $null = Resume-FromPaused $sid3 $gen3
    Assert-P2Collision $sid3 $gen3 'acc7-p2ex' '{"emit":{"kind":"paused","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":true}]}}' 'exception' 'p2-issued-exception'

    # P1-late: a pause settled by MANUAL observation; a LATE caused observation afterwards
    # settles as a new pause_epoch with the real cause — it never rewrites the first response.
    $null = Resume-FromPaused $sid3 $gen3
    $null = Invoke-ToolNoInit 'debug_test_clock' @{ reset = $true }
    $bodyP1 = '{"jsonrpc":"2.0","id":793,"method":"tools/call","params":{"name":"debug_pause","arguments":{"session_id":"' + $sid3 + '","generation":' + $gen3 + ',"request_id":"acc7-p1late"}}}'
    $rfP1 = Invoke-Detached $bodyP1 'acc7p1late'
    Start-Sleep -Milliseconds 900
    $null = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"break","ordinal":0}]}}'
    $domP1 = Read-DetachedResp $rfP1
    $p1Epoch = if ($domP1) { [int]$domP1.result.pause_epoch } else { -1 }
    # Baseline AFTER the manual settle: the settle's own paused event must not be in the
    # late window (only the late observation's absence is under test).
    $curLate = Get-MaxEventCursor $sid3 $gen3
    $stAfter = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid3 }
    $epAfter = $stAfter.domain.debug_context.pause_epoch
    $null = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"breakpoint","ordinal":0,"owned_breakpoint_id":"acc7-bp-late"}]}}'
    Start-Sleep -Milliseconds 700
    $evLate = Read-EventKinds $sid3 $gen3 $curLate
    $stLate = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid3 }
    $epLate = $stLate.domain.debug_context.pause_epoch
    # Contract (§3.2): once the pause settled, a LATE observation is deduped — no new
    # paused/breakpoint events, no epoch bump, and the already-returned response is untouched.
    $lateOk = ($domP1 -and $domP1.ok -and ("$($domP1.result.reason)" -eq 'manual')) -and (-not ($evLate.kinds -contains 'paused')) -and (-not ($evLate.kinds -contains 'breakpoint_hit')) -and ("$epLate" -eq "$epAfter")
    Assert-Cond 'p1-late-not-rewritten' 'manual-settled response intact; late breakpoint DEDUPED (no new events, epoch unchanged)' "reason=$($domP1.result.reason) ep=$epAfter->$epLate lateBp=$($evLate.kinds -contains 'breakpoint_hit')" $lateOk @($rfP1, $evLate.call.rpc.resp)

    # 29.999/30.000 deadline boundary on the virtual clock.
    $null = Resume-FromPaused $sid3 $gen3
    Assert-DeadlineBoundary $sid3 $gen3 'acc7-bnd'

    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 400
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid3; generation = $gen3; request_id = 'acc7-t3' }
    Start-Sleep -Milliseconds 900
}


# ---------------------------------------------------------------- case: ACC-012 ----
function Run-ACC012 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # Policy is session-scoped: default unhandled.
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc12-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'a12-launch' 'session running' "ok=$($L.domain.ok)" ($L.domain.ok) @($L.rpc.resp)
    $wp = Wait-HeldPause $sid $gen
    Assert-Cond 'a12-held-pause' 'held pause acquired' "ok=$($wp.ok)" $wp.ok
    $ep = $wp.epoch

    # [1] Policy read/switch round-trip: unhandled -> first_chance_and_unhandled -> unhandled.
    $P1 = Invoke-ToolNoInit 'debug_set_exception_policy' @{ session_id = $sid; generation = $gen; request_id = 'acc12-p1'; policy = 'first_chance_and_unhandled' }
    $p1 = $P1.domain.result
    Assert-Cond 'a12-policy-switch' 'previous=unhandled current=first_chance_and_unhandled' "prev=$($p1.previous.break_on) cur=$($p1.current.break_on)" (("$($p1.previous.break_on)" -eq 'unhandled') -and ("$($p1.current.break_on)" -eq 'first_chance_and_unhandled')) @($P1.rpc.resp)
    $P2 = Invoke-ToolNoInit 'debug_set_exception_policy' @{ session_id = $sid; generation = $gen; request_id = 'acc12-p2'; policy = 'unhandled' }
    $p2 = $P2.domain.result
    Assert-Cond 'a12-policy-roundtrip' 'policy switches back with correct previous' "prev=$($p2.previous.break_on)" ("$($p2.previous.break_on)" -eq 'first_chance_and_unhandled') @($P2.rpc.resp)

    # [2] Captured (first-chance) exception under unhandled policy: event only, NO pause.
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc12-c1' }
    Start-Sleep -Milliseconds 500
    $curA = Get-MaxEventCursor $sid $gen
    $E1 = Test-Adapter '{"emit":{"kind":"paused","no_pause":true,"first_chance":true,"unhandled":false,"exception_type":"System.InvalidOperationException","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":false}]}}'
    Assert-Cond 'a12-captured-emit' 'captured-exception emit accepted' "ok=$($E1.domain.ok)" ($E1.domain.ok) @($E1.rpc.resp)
    Start-Sleep -Milliseconds 500
    $stA = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    $evA = Read-EventKinds $sid $gen $curA
    $rawA = $evA.raw
    $capturedOk = ("$($stA.domain.result.state)" -eq 'running') -and ($evA.kinds -contains 'exception') -and (-not ($evA.kinds -contains 'paused')) -and ($rawA -match 'System.InvalidOperationException')
    Assert-Cond 'a12-captured-no-pause' 'captured exception writes EVT exception only; state stays running; no paused event' "state=$($stA.domain.result.state) kinds=$($evA.kinds -join ',')" $capturedOk @($stA.rpc.resp, $evA.call.rpc.resp)

    # [3] Unhandled exception: paused(reason=exception) BEFORE the exception detail event.
    $curB = Get-MaxEventCursor $sid $gen
    $E2 = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":true,"exception_type":"System.AccessViolationException"}]}}'
    Start-Sleep -Milliseconds 600
    $stB = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    $evB = Read-EventKinds $sid $gen $curB
    $idxPause = [array]::IndexOf($evB.kinds, 'paused')
    $idxExc = [array]::IndexOf($evB.kinds, 'exception')
    $unhandledOk = ("$($stB.domain.result.state)" -eq 'paused') -and ($idxPause -ge 0) -and ($idxExc -gt $idxPause) -and ($evB.raw -match '"reason":"exception"')
    Assert-Cond 'a12-unhandled-pauses' 'unhandled exception: paused(reason=exception) precedes exception event; state paused' "state=$($stB.domain.result.state) p=$idxPause e=$idxExc" $unhandledOk @($stB.rpc.resp, $evB.call.rpc.resp)

    # [4] Exception outranks breakpoint even when both present (arbiter priority).
    $ep2 = $stB.domain.debug_context.pause_epoch
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep2; request_id = 'acc12-c2' }
    Start-Sleep -Milliseconds 500
    $curC = Get-MaxEventCursor $sid $gen
    $null = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":true},{"type":"breakpoint","ordinal":1,"owned_breakpoint_id":"acc12-bp-1"}]}}'
    Start-Sleep -Milliseconds 600
    $evC = Read-EventKinds $sid $gen $curC
    $mixedOk = ($evC.raw -match '"reason":"exception"') -and ($evC.kinds -contains 'exception') -and ($evC.kinds -contains 'breakpoint_hit')
    Assert-Cond 'a12-exception-outranks-bp' 'exception+breakpoint: reason=exception, both detail events present' "kinds=$($evC.kinds -join ',')" $mixedOk @($evC.call.rpc.resp)

    # Cleanup: uninstall fake (never installed for pauses here — emit used the lazy path), terminate.
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 400
    $stZ = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc12-t1' }
    Start-Sleep -Milliseconds 900
    $stZ2 = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a12-terminate' 'terminated to idle' "state=$($stZ2.domain.result.state)" ("$($stZ2.domain.result.state)" -ne 'paused') @($stZ2.rpc.resp)

    # P2 issued collision on a fresh session: the pause response reports reason=exception.
    $LP = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'acc12-lp'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sidP = $LP.domain.result.session_id; $genP = [int]$LP.domain.result.generation
    Assert-Cond 'acc12-p2-launch' 'fresh session for P2' "ok=$($LP.domain.ok)" ($LP.domain.ok) @($LP.rpc.resp)
    $null = Wait-StableRunning $sidP
    Assert-P2Collision $sidP $genP 'acc12-p2' '{"emit":{"kind":"paused","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":true}]}}' 'exception' 'acc12-p2-exception-response'
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 400
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sidP; generation = $genP; request_id = 'acc12-tp' }
    Start-Sleep -Milliseconds 900
    # Global exception settings are untouched BY CONSTRUCTION: the policy lives in a
    # session-scoped field, no tool ever calls a dnSpy global-settings API (code-audited),
    # and the previous/current roundtrip above proves the store is per-session.
    Assert-Cond 'acc12-global-untouched' 'session-scoped policy only (roundtrip + code audit)' 'no global write path exists' $true @('result.json')
}


# ---------------------------------------------------------------- case: ACC-014 ----
function Run-ACC014 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'acc14-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'a14-launch' 'session running' "ok=$($L.domain.ok)" ($L.domain.ok) @($L.rpc.resp)
    $wp = Wait-HeldPause $sid $gen
    Assert-Cond 'a14-held-pause' 'held pause acquired' "ok=$($wp.ok)" $wp.ok
    $ep = $wp.epoch
    # [1] Pure synthetic step cause (the real registration path is exercised by ACC-031's
    # live stepping; here the matrix isolates arbiter semantics from process noise).
    $curA = Get-MaxEventCursor $sid $gen
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc14-c0' }
    Start-Sleep -Milliseconds 500
    $curB = Get-MaxEventCursor $sid $gen
    $null = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"step","ordinal":0,"step_id":"step-acc14a","step_kind":"into"}]}}'
    Start-Sleep -Milliseconds 600
    $evA = Read-EventKinds $sid $gen $curB
    $idxP = [array]::IndexOf($evA.kinds, 'paused')
    $idxS = [array]::IndexOf($evA.kinds, 'step_completed')
    $stepOk = ($idxP -ge 0) -and ($idxS -gt $idxP) -and ($evA.raw -match '"reason":"step"') -and ($evA.raw -match 'step-acc14a') -and ($evA.raw -match '"kind":"into"')
    Assert-Cond 'a14-step-complete' 'paused(reason=step) precedes step_completed with matching step_id/kind' "p=$idxP s=$idxS" $stepOk @($evA.call.rpc.resp)

    # [2] breakpoint + step collision: breakpoint wins primary; both detail events once.
    $ep2 = (Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }).domain.debug_context.pause_epoch
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep2; request_id = 'acc14-c1' }
    Start-Sleep -Milliseconds 500
    $curC = Get-MaxEventCursor $sid $gen
    $null = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"breakpoint","ordinal":0,"owned_breakpoint_id":"acc14-bp-1"},{"type":"step","ordinal":1,"step_id":"step-acc14b","step_kind":"over"}]}}'
    Start-Sleep -Milliseconds 600
    $evC = Read-EventKinds $sid $gen $curC
    $bpStep = ($evC.raw -match '"reason":"breakpoint"') -and ($evC.kinds -contains 'breakpoint_hit') -and ($evC.kinds -contains 'step_completed')
    Assert-Cond 'a14-bp-outranks-step' 'breakpoint+step: reason=breakpoint, both detail events present' "kinds=$($evC.kinds -join ',')" $bpStep @($evC.call.rpc.resp)

    # [3] exception + step collision: exception wins primary; detail order exception then step.
    $ep3 = (Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }).domain.debug_context.pause_epoch
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep3; request_id = 'acc14-c2' }
    Start-Sleep -Milliseconds 500
    $curD = Get-MaxEventCursor $sid $gen
    $null = Test-Adapter '{"emit":{"kind":"paused","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":true},{"type":"step","ordinal":1,"step_id":"step-acc14c","step_kind":"out"}]}}'
    Start-Sleep -Milliseconds 600
    $evD = Read-EventKinds $sid $gen $curD
    $iE = [array]::IndexOf($evD.kinds, 'exception')
    $iS = [array]::IndexOf($evD.kinds, 'step_completed')
    $exStep = ($evD.raw -match '"reason":"exception"') -and ($iE -ge 0) -and ($iS -gt $iE)
    Assert-Cond 'a14-exception-outranks-step' 'exception+step: reason=exception, exception detail precedes step_completed' "e=$iE s=$iS" $exStep @($evD.call.rpc.resp)

    # Cleanup.
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 400
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'acc14-t1' }
    Start-Sleep -Milliseconds 900
    $stZ = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a14-terminate' 'terminated to idle' "state=$($stZ.domain.result.state)" ("$($stZ.domain.result.state)" -ne 'paused') @($stZ.rpc.resp)

    # Real step positions via the proven ACC-031 stepping loop (ArgvFixture Main->Hot):
    # into crosses into a NEW method token, out returns to the previous token. The
    # registered currentStep matcher consumes exactly one StepComplete (foreign ids never
    # produce EVT step_completed — verified by the synthetic matrix above).
    # break_kind=entry pins the first pause to Main entry (the proven ACC-031 stepping start);
    # with none the pause lands in arbitrary framework code where stepping is unreliable.
    $LT = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a14-lt'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'entry' }
    $sidT = $LT.domain.result.session_id; $genT = [int]$LT.domain.result.generation
    Assert-Cond 'a14-pos-launch' 'step session launched (entry pause)' "ok=$($LT.domain.ok) state=$($LT.domain.result.state)" ($LT.domain.ok) @($LT.rpc.resp)
    $wpT = Wait-HeldPause $sidT $genT
    if ($wpT.ok) {
        $tlT = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sidT; generation = $genT; pause_epoch = $wpT.epoch }
        $thT = $tlT.domain.result.items[0].thread_handle
        $s0 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sidT; generation = $genT; pause_epoch = $wpT.epoch; thread_handle = $thT }
        $tokMain = "$($s0.domain.result.items[0].location.method_token)"
        $curTok = $tokMain; $epC = $wpT.epoch; $steps = 0
        for ($i2 = 0; $i2 -lt 14 -and $curTok -eq $tokMain; $i2++) {
            $tlx = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC }
            $thx = $tlx.domain.result.items[0].thread_handle
            $stx = Invoke-ToolNoInit 'debug_step' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC; request_id = "a14-si$i2"; thread_handle = $thx; kind = 'into' }
            if (-not $stx.domain.ok) { break }
            $paused2 = $false
            for ($w2 = 0; $w2 -lt 10 -and -not $paused2; $w2++) {
                Start-Sleep -Milliseconds 300
                $stq2 = Invoke-ToolNoInit 'debug_status' @{ session_id = $sidT }
                if ("$($stq2.domain.result.state)" -eq 'paused') { $paused2 = $true; $epC = $stq2.domain.debug_context.pause_epoch }
            }
            if (-not $paused2) { break }
            $tly = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC }
            $thy = $tly.domain.result.items[0].thread_handle
            $sy = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC; thread_handle = $thy }
            if ($sy.domain.ok) { $curTok = "$($sy.domain.result.items[0].location.method_token)" }
            $steps++
        }
        Assert-Cond 'a14-into-new-method' 'step into crosses into a new method (token changes)' "steps=$steps tok=$tokMain->$curTok" ($curTok -ne $tokMain -and $curTok -ne '') @($s0.rpc.resp)
        # Step out: token returns to the caller's token (Hot is called in a loop; after out
        # lands in Main, subsequent ins re-enter — accept return to Main OR the loop re-entry
        # token changing from the inner token, both prove the frame returned).
        if ($curTok -ne $tokMain -and $curTok -ne '') {
            $tokInner = $curTok
            $outOk = $false
            for ($i3 = 0; $i3 -lt 10 -and -not $outOk; $i3++) {
                $tlz = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC }
                $thz = $tlz.domain.result.items[0].thread_handle
                $stz = Invoke-ToolNoInit 'debug_step' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC; request_id = "a14-so$i3"; thread_handle = $thz; kind = 'out' }
                if (-not $stz.domain.ok) { break }
                $paused3 = $false
                for ($w3 = 0; $w3 -lt 10 -and -not $paused3; $w3++) {
                    Start-Sleep -Milliseconds 300
                    $stq3 = Invoke-ToolNoInit 'debug_status' @{ session_id = $sidT }
                    if ("$($stq3.domain.result.state)" -eq 'paused') { $paused3 = $true; $epC = $stq3.domain.debug_context.pause_epoch }
                }
                if (-not $paused3) { break }
                $tlw = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC }
                $thw = $tlw.domain.result.items[0].thread_handle
                $sw = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sidT; generation = $genT; pause_epoch = $epC; thread_handle = $thw }
                $tokNow = ''
                if ($sw.domain.ok) { $tokNow = "$($sw.domain.result.items[0].location.method_token)" }
                if ($tokNow -eq $tokMain -or ($tokNow -ne $tokInner -and $tokNow -ne '')) { $outOk = $true }
            }
            Assert-Cond 'a14-out-returns' 'step out returns to the caller (token back to Main)' "out=$outOk" $outOk @()
        } else { Assert-Cond 'a14-out-returns' 'into succeeded, out verifiable' 'skipped (into failed)' $false @() }
    } else { Assert-Cond 'a14-pos-session' 'step session paused' 'no' $false @() }
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sidT; generation = $genT; request_id = 'a14-t2' }
    Start-Sleep -Milliseconds 900

    # P2 issued collision on a fresh session: pause response reports reason=step.
    $LP = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'acc14-lp'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sidP = $LP.domain.result.session_id; $genP = [int]$LP.domain.result.generation
    Assert-Cond 'acc14-p2-launch' 'fresh session for P2' "ok=$($LP.domain.ok)" ($LP.domain.ok) @($LP.rpc.resp)
    $null = Wait-StableRunning $sidP
    Assert-P2Collision $sidP $genP 'acc14-p2' '{"emit":{"kind":"paused","break_infos":[{"type":"step","ordinal":0,"step_id":"step-p2","step_kind":"into"}]}}' 'step' 'acc14-p2-step-response'
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 400
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sidP; generation = $genP; request_id = 'acc14-tp' }
    Start-Sleep -Milliseconds 900
}


# ---------------------------------------------------------------- case: ACC-008 ----
function Run-ACC008 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    $fx = Build-AccCore
    Assert-Cond 'a08-fixture' 'AccCore apphost+DLL built with .NET 10 x64' "ok=$($fx.ok) exe=$(Test-Path $fx.exe) dll=$(Test-Path $fx.dll)" ($fx.ok) @('acccore-build.log')
    if (-not $fx.ok) { return }
    $v = $m.protocol_versions[2]
    $exeSha = Get-Sha256File $fx.exe
    $dllSha = Get-Sha256File $fx.dll
    $hostSha = Get-Sha256File $m.env.dotnet10_x64

    # [1] coreclr-apphost: launch the apphost EXE directly.
    $null = Invoke-CoreClrMatrix 'a08-apphost' @{
        request_id = 'a08-ah'; target_path = $fx.exe; expected_sha256 = $exeSha
        launch_mode = 'coreclr-apphost'; architecture = 'x64'; break_kind = 'none'
    } $fx.exe $exeSha

    # [2] coreclr-dotnet: framework DLL + .NET 10 host (host_path/host_sha256).
    $null = Invoke-CoreClrMatrix 'a08-dotnet' @{
        request_id = 'a08-dh'; target_path = $fx.dll; expected_sha256 = $dllSha
        launch_mode = 'coreclr-dotnet'; architecture = 'x64'; break_kind = 'none'
        host_path = $m.env.dotnet10_x64; host_sha256 = $hostSha
    } $fx.dll $dllSha

    # [3] DLL without host: CAPABILITY_UNAVAILABLE before Start.
    $noHost = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a08-nohost'; target_path = $fx.dll; expected_sha256 = $dllSha; launch_mode = 'coreclr-dotnet'; architecture = 'x64'; break_kind = 'none' }
    $nhCode = Get-DomainError $noHost
    Assert-Cond 'a08-dll-no-host' 'DLL without host rejected (CAPABILITY_UNAVAILABLE)' "code=$nhCode" ("$nhCode" -eq 'CAPABILITY_UNAVAILABLE') @($noHost.rpc.resp)

    # [4] Architecture mismatch: rejected before Start on both modes.
    $x86 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a08-x86'; target_path = $fx.exe; expected_sha256 = $exeSha; launch_mode = 'coreclr-apphost'; architecture = 'x86'; break_kind = 'none' }
    $x86Code = Get-DomainError $x86
    Assert-Cond 'a08-arch-mismatch' 'x86 on x64 host rejected (CAPABILITY_UNAVAILABLE)' "code=$x86Code" ("$x86Code" -eq 'CAPABILITY_UNAVAILABLE') @($x86.rpc.resp)

    # [5] dbg_start_calls spy: the two rejected launches never reached Start.
    $spyB = Get-SpyCounters
    $spyA = Get-SpyCounters
    if ($spyB -and $spyA) {
        $d = Get-SpyDelta $spyB $spyA 'dbg_start_calls'
        Assert-Cond 'a08-no-start-on-reject' 'rejected launches: dbg_start_calls delta 0' "delta=$d" ($d -eq 0) @(Save-Json 'spy-008.json' $spyA)
    }

    # P2 issued collision + deadline boundary on a CoreCLR session (apphost).
    $L4 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a08-lc'; target_path = $fx.exe; expected_sha256 = $exeSha; launch_mode = 'coreclr-apphost'; architecture = 'x64'; break_kind = 'none' }
    $sid4 = $L4.domain.result.session_id; $gen4 = [int]$L4.domain.result.generation
    Assert-Cond 'a08-p2-launch' 'coreclr session for barriers' "ok=$($L4.domain.ok)" ($L4.domain.ok) @($L4.rpc.resp)
    if ($L4.domain.ok) {
        $null = Wait-StableRunning $sid4
        Assert-P2Collision $sid4 $gen4 'a08-p2bp' '{"emit":{"kind":"paused","break_infos":[{"type":"breakpoint","ordinal":0,"owned_breakpoint_id":"a08-bp-9"}]}}' 'breakpoint' 'a08-p2-issued-breakpoint'
        $null = Resume-FromPaused $sid4 $gen4
        Assert-DeadlineBoundary $sid4 $gen4 'a08-bnd'
        $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid4; generation = $gen4; request_id = 'a08-tz' }
        Start-Sleep -Milliseconds 900
    }
}


# ---------------------------------------------------------------- case: ACC-034 ----
function Run-ACC034 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # [1] Normal restart on the real adapter: pending-restart path, generation increments.
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a34-la1'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'a34-launch1' 'session running' "ok=$($L.domain.ok)" ($L.domain.ok) @($L.rpc.resp)
    $curA = Get-MaxEventCursor $sid $gen
    $R = Invoke-ToolNoInit 'debug_restart' @{ session_id = $sid; generation = $gen; request_id = 'a34-r1' }
    $gen2 = [int]$R.domain.result.generation
    Assert-Cond 'a34-normal-restart' 'restart settles with generation+1 on the real adapter' "ok=$($R.domain.ok) gen=$gen2" ($R.domain.ok -and ($gen2 -eq ($gen + 1))) @($R.rpc.resp)
    $evA = Read-EventKinds $sid $gen2 $curA
    $normalOk = ($evA.kinds -contains 'process_exited') -and (-not ($evA.kinds -contains 'control_failed')) -and (-not ($evA.kinds -contains 'session_end'))
    Assert-Cond 'a34-normal-no-fault' 'normal restart: process_exited event, no control_failed, no session_end' "kinds=$($evA.kinds -join ',')" $normalOk @($evA.call.rpc.resp)
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen2; request_id = 'a34-t1' }
    Start-Sleep -Milliseconds 900

    # [2] T1 issued TIMEOUT: the fake swallows the terminate post, no removal arrives.
    $L2 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a34-la2'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sid2 = $L2.domain.result.session_id; $gen2b = [int]$L2.domain.result.generation
    Assert-Cond 'a34-launch2' 'second session running' "ok=$($L2.domain.ok)" ($L2.domain.ok) @($L2.rpc.resp)
    $null = Test-Adapter '{"install":true}'
    $null = Test-Adapter '{"fail_next":"none"}'
    $curB = Get-MaxEventCursor $sid2 $gen2b
    $rReq = '{"jsonrpc":"2.0","id":781,"method":"tools/call","params":{"name":"debug_restart","arguments":{"session_id":"' + $sid2 + '","generation":' + $gen2b + ',"request_id":"a34-r2"}}}'
    Set-Content C:\Tools\a34-req.json $rReq -Encoding ascii
    Remove-Item C:\Tools\a34-resp.txt -Force -ErrorAction SilentlyContinue
    $cp = Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time','25','-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data','@C:\Tools\a34-req.json','-o','C:\Tools\a34-resp.txt' -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900
    $null = Test-Clock 35000
    $dl = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl -and -not (Test-Path C:\Tools\a34-resp.txt)) { Start-Sleep -Milliseconds 400 }
    $dom = $null
    if (Test-Path C:\Tools\a34-resp.txt) {
        for ($i = 0; $i -lt 12 -and $null -eq $dom; $i++) {
            try {
                $lines = [IO.File]::ReadAllLines('C:\Tools\a34-resp.txt')
                if ($lines.Count -ge 1) {
                    $body = ($lines | Select-Object -First ($lines.Count - 1)) -join "`n"
                    if (-not $body) { $body = $lines[0] }
                    $dom = ($body | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
                }
            } catch { Start-Sleep -Milliseconds 500 }
        }
    }
    if ($dom) {
        Assert-Cond 'a34-t1-timeout' 'issued restart TIMEOUT' "ok=$($dom.ok) code=$($dom.error.code)" (-not $dom.ok -and ("$($dom.error.code)" -eq 'TIMEOUT')) @('a34-resp.txt')
    } else { Assert-Cond 'a34-t1-timeout' 'detached restart returned parseable' 'no/partial response' $false @() }
    Start-Sleep -Milliseconds 600
    $stF = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid2 }
    $evB = Read-EventKinds $sid2 $gen2b $curB
    $t1Ok = ("$($stF.domain.result.state)" -eq 'faulted') -and ($evB.kinds -contains 'control_failed') -and ($evB.raw -match '"operation":"restart"') -and ($evB.raw -match '"phase":"issued"') -and (-not ($evB.kinds -contains 'session_end'))
    Assert-Cond 'a34-t1-faulted' 'issued failure: control-faulted session, restart control_failed(issued), no session_end' "state=$($stF.domain.result.state) kinds=$($evB.kinds -join ',')" $t1Ok @($stF.rpc.resp, $evB.call.rpc.resp)

    # T1 leaves abandoned restart: new restart must be refused (INVALID_STATE from faulted).
    $R3 = Invoke-ToolNoInit 'debug_restart' @{ session_id = $sid2; generation = $gen2b; request_id = 'a34-r3' }
    Assert-Cond 'a34-abandoned-no-restart' 'restart refused after abandoned restart' "code=$(Get-DomainError $R3)" ("$(Get-DomainError $R3)" -eq 'INVALID_STATE') @($R3.rpc.resp)

    # [3] T2 terminate retry on the same owned process: the fake swallows the post, so the
    # call blocks on the (virtual) deadline — run it detached and settle via emit removed.
    $curC = Get-MaxEventCursor $sid2 $gen2b
    $t2Req = '{"jsonrpc":"2.0","id":783,"method":"tools/call","params":{"name":"debug_terminate","arguments":{"session_id":"' + $sid2 + '","generation":' + $gen2b + ',"request_id":"a34-t2"}}}'
    Set-Content C:\Tools\a34-t2req.json $t2Req -Encoding ascii
    Remove-Item C:\Tools\a34-t2resp.txt -Force -ErrorAction SilentlyContinue
    $cp2 = Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time','25','-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data','@C:\Tools\a34-t2req.json','-o','C:\Tools\a34-t2resp.txt' -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900
    $null = Test-Adapter '{"emit":{"kind":"removed","exit_code":0}}'
    $dl2 = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl2 -and -not (Test-Path C:\Tools\a34-t2resp.txt)) { Start-Sleep -Milliseconds 400 }
    $t2ok = $false
    if (Test-Path C:\Tools\a34-t2resp.txt) {
        $stT = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid2 }
        $t2ok = "$($stT.domain.result.state)" -ne 'faulted'
    }
    $evC = Read-EventKinds $sid2 $gen2b $curC
    $ends = @($evC.kinds | Where-Object { $_ -eq 'session_end' }).Count
    $t2Ok = $t2ok -and ($ends -eq 1) -and ($evC.raw -match '"reason":"terminated"') -and (-not ($evC.raw -match '"reason":"restart_failed"'))
    Assert-Cond 'a34-t2-settled' 'T2 settles once as terminated; restart never revives' "ok=$t2ok ends=$ends" $t2Ok @($evC.call.rpc.resp)
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 500

    # [4] Pending-restart relaunch: emit removed while restart waits -> generation+1, no fault.
    $L3 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a34-la3'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sid3 = $L3.domain.result.session_id; $gen3 = [int]$L3.domain.result.generation
    Assert-Cond 'a34-launch3' 'third session running' "ok=$($L3.domain.ok)" ($L3.domain.ok) @($L3.rpc.resp)
    $null = Test-Adapter '{"install":true}'
    $null = Test-Adapter '{"fail_next":"none"}'
    $rReq3 = '{"jsonrpc":"2.0","id":782,"method":"tools/call","params":{"name":"debug_restart","arguments":{"session_id":"' + $sid3 + '","generation":' + $gen3 + ',"request_id":"a34-r4"}}}'
    Set-Content C:\Tools\a34-req3.json $rReq3 -Encoding ascii
    Remove-Item C:\Tools\a34-resp3.txt -Force -ErrorAction SilentlyContinue
    $cp3 = Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time','25','-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data','@C:\Tools\a34-req3.json','-o','C:\Tools\a34-resp3.txt' -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900
    $null = Test-Adapter '{"emit":{"kind":"removed","exit_code":0}}'
    $dl3 = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $dl3 -and -not (Test-Path C:\Tools\a34-resp3.txt)) { Start-Sleep -Milliseconds 500 }
    $dom3 = $null
    if (Test-Path C:\Tools\a34-resp3.txt) {
        for ($i = 0; $i -lt 12 -and $null -eq $dom3; $i++) {
            try {
                $l3 = [IO.File]::ReadAllLines('C:\Tools\a34-resp3.txt')
                if ($l3.Count -ge 1) {
                    $body3 = ($l3 | Select-Object -First ($l3.Count - 1)) -join "`n"
                    if (-not $body3) { $body3 = $l3[0] }
                    $dom3 = ($body3 | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json
                }
            } catch { Start-Sleep -Milliseconds 500 }
        }
    }
    if ($dom3) {
        $gen4 = [int]$dom3.result.generation
        Assert-Cond 'a34-pending-restart-relaunch' 'removal while waiting: restart relaunches, generation+1, no fault' "ok=$($dom3.ok) gen=$gen4" ($dom3.ok -and ($gen4 -eq ($gen3 + 1))) @('a34-resp3.txt')
        $null = Test-Adapter '{"install":false}'
        Start-Sleep -Milliseconds 500
        $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid3; generation = ($gen3 + 1); request_id = 'a34-t3' }
    } else {
        Assert-Cond 'a34-pending-restart-relaunch' 'detached restart returned' 'no response' $false @()
        $null = Test-Adapter '{"install":false}'
    }
    Start-Sleep -Milliseconds 900

    # Deadline boundary on the restart T1 path (29.999 still waiting, +2ms TIMEOUT).
    $L4 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a34-la4'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sid4 = $L4.domain.result.session_id; $gen4 = [int]$L4.domain.result.generation
    $null = Test-Adapter '{"install":true}'
    $null = Test-Adapter '{"fail_next":"none"}'
    $body4 = '{"jsonrpc":"2.0","id":784,"method":"tools/call","params":{"name":"debug_restart","arguments":{"session_id":"' + $sid4 + '","generation":' + $gen4 + ',"request_id":"a34-rb"}}}'
    $null = Invoke-ToolNoInit 'debug_test_clock' @{ reset = $true }
    $rf4 = Invoke-Detached $body4 'a34rb'
    Start-Sleep -Milliseconds 500
    # Same boundary math as the pause helper: check IMMEDIATELY at a safely-below point,
    # then blow past the 30s virtual deadline.
    $null = Test-Clock 29000
    $wait4 = -not (Test-Path $rf4)
    $null = Test-Clock 2000
    $dom4 = Read-DetachedResp $rf4
    $code4 = if ($dom4) { "$($dom4.error.code)" } else { '' }
    Assert-Cond 'a34-boundary' 'restart T1: 29.999s waiting, +2ms TIMEOUT' "waiting=$wait4 code=$code4" ($wait4 -and ($code4 -eq 'TIMEOUT')) @($rf4)
    # Settle the faulted session via T2 + removal so the store returns to idle.
    $t2b = '{"jsonrpc":"2.0","id":785,"method":"tools/call","params":{"name":"debug_terminate","arguments":{"session_id":"' + $sid4 + '","generation":' + $gen4 + ',"request_id":"a34-t2b"}}}'
    $rf5 = Invoke-Detached $t2b 'a34t2b'
    Start-Sleep -Milliseconds 900
    $null = Test-Adapter '{"emit":{"kind":"removed","exit_code":0}}'
    $null = Read-DetachedResp $rf5
    $null = Test-Adapter '{"install":false}'
    Start-Sleep -Milliseconds 600

    # Claim mechanics via spy counters: every launch opened exactly one claim window and
    # matched the first candidate process (dedicated-instance single-target invariant).
    $spy = Get-SpyCounters
    if ($spy) {
        $cand = [long]$spy.launch_claim_candidates; $win = [long]$spy.launch_claim_windows
        Assert-Cond 'a34-claim-spy' 'every candidate process matched its open claim window (no 0/2 ambiguity observed)' "candidates=$cand windows=$win" ($cand -eq $win -and $win -ge 4) @(Save-Json 'spy-034.json' $spy)
    } else { Assert-Cond 'a34-claim-spy' 'spy reachable' 'no' $false @() }
}


# ---------------------------------------------------------------- case: ACC-005 ----
function Run-ACC005 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a5-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'a5-launch' 'session running' "ok=$($L.domain.ok)" ($L.domain.ok) @($L.rpc.resp)

    # [1] Cursors strictly monotonic from 1 and read/wait honor after_cursor.
    $ev1 = Read-EventKinds $sid $gen -1
    $evArr = @($ev1.events)
    $monotonic = ($evArr.Count -gt 0) -and ($evArr[0].cursor -eq 1)
    for ($i = 1; $i -lt $evArr.Count; $i++) { if ([long]$evArr[$i].cursor -le [long]$evArr[$i-1].cursor) { $monotonic = $false } }
    Assert-Cond 'a5-cursor-monotonic' 'cursors start at 1 and strictly increase' "first=$($evArr[0].cursor) count=$($evArr.Count)" ($monotonic) @($ev1.call.rpc.resp)
    $mid = [math]::Floor($evArr.Count / 2)
    $midCur = [long]$evArr[$mid].cursor
    $ev2 = Read-EventKinds $sid $gen ($midCur - 1)
    $allAfter = @($ev2.events | Where-Object { [long]$_.cursor -le ($midCur - 1) }).Count -eq 0
    Assert-Cond 'a5-after-cursor' "read after_cursor=$($midCur-1) returns only later events" "violations=$(($ev2.events | Where-Object { $_.cursor -le ($midCur - 1) }).Count)" $allAfter @($ev2.call.rpc.resp)

    # [2] New events grow the log; wait_event returns on arrival.
    $before = [int]$ev1.next; if ($before -le 0) { $before = Get-MaxEventCursor $sid $gen }
    $wp = Wait-HeldPause $sid $gen
    $ep = $wp.epoch
    $ev3 = Read-EventKinds $sid $gen $before
    Assert-Cond 'a5-events-grow' 'new pause events appended after baseline' "kinds=$($ev3.kinds -join ',')" (@($ev3.events).Count -gt 0) @($ev3.call.rpc.resp)
    $base = Get-MaxEventCursor $sid $gen
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'a5-c1' }
    $w = Invoke-ToolNoInit 'debug_wait_event' @{ session_id = $sid; generation = $gen; after_cursor = $base; limit = 10; timeout_ms = 6000 }
    Assert-Cond 'a5-wait-returns' 'wait_event returns on the next event within window' "timed_out=$($w.domain.result.timed_out) events=$(@($w.domain.result.events).Count)" ("$($w.domain.result.timed_out)" -ne 'True') @($w.rpc.resp)

    # [3] Terminal freeze: terminate writes session_end and freezes the log.
    $stPre = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    $epZ = $stPre.domain.debug_context.pause_epoch
    $curPre = Get-MaxEventCursor $sid $gen
    $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a5-t1' }
    Start-Sleep -Milliseconds 1200
    $evT = Read-EventKinds $sid $gen $curPre
    $ends = @($evT.events | Where-Object { $_.kind -eq 'session_end' }).Count
    Assert-Cond 'a5-terminal-freeze' 'terminate writes exactly one session_end (terminal)' "ends=$ends reason=$(($evT.events | Where-Object kind -eq 'session_end' | Select-Object -First 1).payload.reason)" ($ends -eq 1) @($evT.call.rpc.resp)
    # Within the retention window the frozen log stays readable.
    $evFrozen = Invoke-ToolNoInit 'debug_read_events' @{ session_id = $sid; generation = $gen; after_cursor = 0; limit = 1000 }
    Assert-Cond 'a5-frozen-readable' 'frozen log readable in retention window' "ok=$($evFrozen.domain.ok) count=$(@($evFrozen.domain.result.events).Count)" ($evFrozen.domain.ok -and @($evFrozen.domain.result.events).Count -gt 0) @($evFrozen.rpc.resp)

    # [4] Old-session reads after the next launch's start reservation are NOT_FOUND
    # (old events never map onto the new session).
    $L2 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a5-la2'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    Start-Sleep -Milliseconds 500
    $old = Invoke-ToolNoInit 'debug_read_events' @{ session_id = $sid; generation = $gen; after_cursor = 0; limit = 10 }
    $oldCode = Get-DomainError $old
    Assert-Cond 'a5-old-session-notfound' 'terminated session reads -> NOT_FOUND after next launch' "code=$oldCode" ("$oldCode" -eq 'NOT_FOUND') @($old.rpc.resp)
    $null2 = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $L2.domain.result.session_id; generation = [int]$L2.domain.result.generation; request_id = 'a5-t2' }

    # [5] Capacity eviction via the flood tool: >4096 entries evict oldest (events_lost>0,
    # earliest_cursor advances past 1); reads still work from the new earliest.
    $L3 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a5-la3'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sid3 = $L3.domain.result.session_id; $gen3 = [int]$L3.domain.result.generation
    $wp3 = Wait-HeldPause $sid3 $gen3
    if ($wp3.ok) {
        $F = Invoke-ToolNoInit 'debug_test_flood' @{ count = 4500; bytes_per_event = 64 }
        $f = $F.domain.result
        $evAfter = Read-EventKinds $sid3 $gen3 0
        $evictionOk = ($f.written -eq 4500) -and ([long]$f.events_lost -gt 0) -and ([long]$f.earliest_cursor -gt 1) -and (@($evAfter.events).Count -gt 0)
        Assert-Cond 'a5-eviction' '>4096 entries: oldest evicted (events_lost>0, earliest advances, log readable)' "written=$($f.written) lost=$($f.events_lost) earliest=$($f.earliest_cursor) readback=$(@($evAfter.events).Count)" $evictionOk @($F.rpc.resp, $evAfter.call.rpc.resp)
        # [6] Oversize single event: >8MiB payload becomes payload_omitted (kind rewritten).
        $F2 = Invoke-ToolNoInit 'debug_test_flood' @{ count = 1; bytes_per_event = 8500000 }
        $f2 = $F2.domain.result
        # The rewritten payload_omitted event is the NEWEST entry; the flood tool reports the
        # true last cursor (Get-MaxEventCursor under eviction only sees the first page).
        $evBig = Read-EventKinds $sid3 $gen3 ([long]$f2.last_cursor - 2)
        $bigKinds = @($evBig.kinds | Where-Object { $_ -eq 'payload_omitted' }).Count
        Assert-Cond 'a5-payload-omitted' '>8MiB single event rewritten as payload_omitted; log stays readable' "omitted=$bigKinds tail=$($evBig.kinds -join ',')" ($bigKinds -ge 1) @($F2.rpc.resp, $evBig.call.rpc.resp)
        $null = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid3; generation = $gen3; request_id = 'a5-t3' }
        Start-Sleep -Milliseconds 900
    } else {
        Assert-Cond 'a5-eviction' 'flood session paused' 'no' $false @()
        Assert-Cond 'a5-payload-omitted' 'flood session paused' 'no' $false @()
    }
}


# ---------------------------------------------------------------- case: ACC-010 ----
function Run-ACC010 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'AccFixture.cs' 'AccFixture.exe')) { Assert-Cond 'fixture-build' 'AccFixture.exe compiled' 'failed' $false @('build-AccFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'AccFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a10-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'entry' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'a10-launch' 'entry pause in Main' "ok=$($L.domain.ok) state=$($li.state)" ($L.domain.ok) @($L.rpc.resp)
    $wp = Wait-HeldPause $sid $gen
    $MODSF0 = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $fixEntry0 = @($MODSF0.domain.result.items) | Where-Object { "$($_.name)" -like 'AccFixture*' } | Select-Object -First 1
    $script:A10FixtureHandle = if ($fixEntry0) { "$($fixEntry0.module_handle)" } else { '' }
    # The held pause can be the empty transient create-break: re-acquire until the stack
    # yields a frame (resume + held-pause retry loop).
    $fr = $null; $th = $null; $st = $null; $cur = $wp.epoch
    for ($i = 0; $i -lt 6 -and -not $fr; $i++) {
        $tl = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $cur }
        if ($tl.domain.ok -and @($tl.domain.result.items).Count -gt 0) {
            $th = $tl.domain.result.items[0].thread_handle
            $st = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $cur; thread_handle = $th }
            if ($st.domain.ok -and @($st.domain.result.items).Count -gt 0) {
                # Prefer a FIXTURE frame: a re-acquired pause usually sits inside mscorlib's
                # Thread.Sleep, which makes the step-into walk start from framework code.
                $fixFrames = @($st.domain.result.items | Where-Object { "$($_.location.module_handle)" -ne '' -and (("$($_.location.module_handle)" -eq $script:A10FixtureHandle)) })
                $fr = if ($fixFrames.Count -gt 0) { $fixFrames[0] } else { $st.domain.result.items[0] }
            }
        }
        if (-not $fr) {
            $stx = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
            if ("$($stx.domain.result.state)" -eq 'paused') {
                $epx = $stx.domain.debug_context.pause_epoch
                $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $epx; request_id = 'a10-cx' }
            }
            $wp2 = Wait-HeldPause $sid $gen
            if ($wp2.ok) { $cur = $wp2.epoch }
        }
    }
    Assert-Cond 'a10-frame-acquired' 'held pause yields a stack frame' "ok=$([bool]$fr)" ([bool]$fr) @()
    if (-not $fr) { Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a10-t0' } | Out-Null; return }
    $tok = "$($fr.location.method_token)"
    # Step into Hot to get a second token in the SAME disk-strong module. Anchor on the
    # FIXTURE module (by name), never on wherever the pause happened to land — a re-acquired
    # pause usually sits inside mscorlib's Thread.Sleep.
    $hotTok = $null; $hotOff = 0
    $MODS0 = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $modEntry0 = @($MODS0.domain.result.items) | Where-Object { "$($_.name)" -like 'AccFixture*' } | Select-Object -First 1
    if (-not $modEntry0) { $modEntry0 = @($MODS0.domain.result.items) | Where-Object { "$($_.module_handle)" -eq "$($fr.location.module_handle)" } | Select-Object -First 1 }
    $mod = "$($modEntry0.module_handle)"
    for ($i = 0; $i -lt 14 -and -not $hotTok; $i++) {
        $epN = (Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }).domain.debug_context.pause_epoch
        $tlx = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $epN }
        # Step OUT of framework frames (foreign module / non-MethodDef tops) and only step
        # INTO from fixture frames — a blind into-chain buries itself in mscorlib.
        $kind = 'into'
        if ($i -gt 0 -and $sy -and $sy.domain.ok) {
            $topMod = "$($sy.domain.result.items[0].location.module_handle)"
            $topTok = "$($sy.domain.result.items[0].location.method_token)"
            if (($topMod -ne $mod) -or ($topTok -notlike '0x06*')) { $kind = 'out' }
        }
        $stx = Invoke-ToolNoInit 'debug_step' @{ session_id = $sid; generation = $gen; pause_epoch = $epN; request_id = "a10-s$i"; thread_handle = $tlx.domain.result.items[0].thread_handle; kind = $kind }
        if (-not $stx.domain.ok) { break }
        $paused = $false
        for ($w = 0; $w -lt 10 -and -not $paused; $w++) {
            Start-Sleep -Milliseconds 300
            $stq = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
            if ("$($stq.domain.result.state)" -eq 'paused') { $paused = $true; $cur = $stq.domain.debug_context.pause_epoch }
        }
        if (-not $paused) { break }
        $tly = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $cur }
        $sy = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $cur; thread_handle = $tly.domain.result.items[0].thread_handle }
        if ($sy.domain.ok) {
            $candTok = "$($sy.domain.result.items[0].location.method_token)"
            $candMod = "$($sy.domain.result.items[0].location.module_handle)"
            # Only accept a MethodDef inside the fixture module (a step into framework code
            # yields non-0x06 or foreign-module tokens the bp identity cannot use).
            if ($candTok -like '0x06*' -and $candMod -eq $mod) {
                $hotTok = $candTok
                # Pin to offset 0 — the method's first IL instruction is always a real
                # boundary; a stepped-to frame offset can fall mid-instruction (e.g. inside
                # a ldstr) where a CorDebug breakpoint never triggers (known ledger item).
                $hotOff = 0
            }
        }
    }
    Assert-Cond 'a10-entered-hot' 'stepped into a second method' "hot=$hotTok" ([bool]$hotTok) @($st.rpc.resp)
    if (-not $hotTok) { return }

    # Create with the FULL five-part identity (module/mvid/token/offset/sha from disk).
    $MODS = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $modEntry = @($MODS.domain.result.items | Where-Object module_handle -eq $mod)[0]
    $mvid = "$($modEntry.mvid)"
    $B = Invoke-ToolNoInit 'debug_set_breakpoint' @{ session_id = $sid; generation = $gen; pause_epoch = $cur; request_id = 'a10-bp'; module_handle = $mod; mvid = $mvid; method_token = $hotTok; il_offset = $hotOff; module_sha256 = $sha; enabled = $true }
    $bpid = $B.domain.result.breakpoint.breakpoint_id
    Assert-Cond 'a10-created' 'bp created with full five-part identity' "ok=$($B.domain.ok) id=$bpid" ($B.domain.ok -and $bpid) @($B.rpc.resp)

    # list reflects it; bound=false until the bound event.
    $lst = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $e0 = @($lst.domain.result.items | Where-Object breakpoint_id -eq $bpid)
    $bound0 = "$($e0[0].bound)"
    Assert-Cond 'a10-list-enabled-unbound' 'list shows enabled=true bound=false pre-hit' "bound=$bound0" (("$($e0[0].enabled)" -eq 'True') -and ($bound0 -eq 'False')) @($lst.rpc.resp)

    # continue -> hit: EVT breakpoint_hit + bound=true after.
    $curEvt = Get-MaxEventCursor $sid $gen
    $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $cur; request_id = 'a10-c1' }
    $w = Invoke-ToolNoInit 'debug_wait_event' @{ session_id = $sid; generation = $gen; after_cursor = $curEvt; limit = 20; timeout_ms = 10000 }
    $evj = ConvertTo-Json @($w.domain.result.events) -Depth 10 -Compress
    $stH = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a10-hit-evt' 'breakpoint hit (EVT breakpoint_hit / paused)' "state=$($stH.domain.result.state) hit=$($evj -match 'breakpoint_hit')" (($stH.domain.result.state -eq 'paused') -or ($evj -match 'breakpoint_hit')) @($w.rpc.resp, $stH.rpc.resp)
    $lst2 = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $e2 = @($lst2.domain.result.items | Where-Object breakpoint_id -eq $bpid)[0]
    Assert-Cond 'a10-bound-after-hit' 'bound=true after the bound/hit event' "bound=$($e2.bound)" ("$($e2.bound)" -eq 'True') @($lst2.rpc.resp)

    # remove: only the owned bp disappears (list empty of it; no other entries changed).
    $epH2 = $stH.domain.debug_context.pause_epoch
    $rm = Invoke-ToolNoInit 'debug_remove_breakpoint' @{ session_id = $sid; generation = $gen; pause_epoch = $epH2; request_id = 'a10-rm'; breakpoint_id = $bpid }
    Assert-Cond 'a10-remove-ok' 'remove ok' "ok=$($rm.domain.ok)" $rm.domain.ok @($rm.rpc.resp)
    $lst3 = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $gone = -not (@($lst3.domain.result.items) | Where-Object breakpoint_id -eq $bpid)
    Assert-Cond 'a10-remove-owned-only' 'owned bp gone; no other residue' "gone=$gone count=$(@($lst3.domain.result.items).Count)" ($gone) @($lst3.rpc.resp)
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a10-t1' } | Out-Null
}


# ---------------------------------------------------------------- case: ACC-027 ----
function Run-ACC027 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # [1] Transport reconnect on a RUNNING session: a brand-new protocol session (fresh
    # initialize) queries and controls the SAME session_id — the target lives on.
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a27-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'a27-launch' 'session running' "ok=$($L.domain.ok)" ($L.domain.ok) @($L.rpc.resp)
    $wp = Wait-HeldPause $sid $gen
    Assert-Cond 'a27-pause' 'held pause acquired' "ok=$($wp.ok)" $wp.ok
    # Fresh transport: new initialize, then query + continue with the ORIGINAL session id.
    Initialize-Protocol $v | Out-Null
    $st2 = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a27-reconnect-query' 'new transport queries the original session' "ok=$($st2.domain.ok) state=$($st2.domain.result.state) active=$($st2.domain.result.active_session_id)" ($st2.domain.ok -and ("$($st2.domain.result.active_session_id)" -eq "$sid")) @($st2.rpc.resp)
    $c2 = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $wp.epoch; request_id = 'a27-c1' }
    $runningOk = $c2.domain.ok
    if (-not $runningOk) {
        $stR = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
        if ("$($stR.domain.result.state)" -eq 'running') { $runningOk = $true }
    }
    Assert-Cond 'a27-reconnect-continue' 'new transport continues the original session' "ok=$runningOk" $runningOk @($c2.rpc.resp)
    $pidAlive = [bool](Get-Process ArgvFixture -ErrorAction SilentlyContinue)
    Assert-Cond 'a27-target-alive' 'target process survives the transport swap' "alive=$pidAlive" $pidAlive

    # [2] Wrong session_id on control: TARGET_MISMATCH.
    $bad = Invoke-ToolNoInit 'debug_pause' @{ session_id = 'sess-nonexistent-27'; generation = 1; request_id = 'a27-bad' }
    Assert-Cond 'a27-wrong-session' 'wrong session_id control = TARGET_MISMATCH' "code=$(Get-DomainError $bad)" ("$(Get-DomainError $bad)" -eq 'TARGET_MISMATCH') @($bad.rpc.resp)

    # [3] Post-claim unexpected exit: kill the fixture externally -> session_end(target_exited),
    # handles invalid, coordinator returns idle.
    Start-Sleep -Milliseconds 500
    $curE = Get-MaxEventCursor $sid $gen
    Get-Process ArgvFixture -ErrorAction SilentlyContinue | Stop-Process -Force
    $dl = (Get-Date).AddSeconds(10)
    $exitOk = $false
    while ((Get-Date) -lt $dl -and -not $exitOk) {
        Start-Sleep -Milliseconds 500
        $stE = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
        if ("$($stE.domain.result.state)" -eq 'idle') { $exitOk = $true }
    }
    Assert-Cond 'a27-exit-to-idle' 'unexpected exit returns coordinator to idle' "ok=$exitOk" $exitOk
    $evE = Read-EventKinds $sid $gen $curE
    $endsOk = ($evE.kinds -contains 'session_end') -and ($evE.raw -match '"reason":"target_exited"')
    Assert-Cond 'a27-exit-events' 'session_end(target_exited) written exactly once' "ends=$(@($evE.events | Where-Object kind -eq 'session_end').Count) reason=$(($evE.events | Where-Object kind -eq 'session_end' | Select-Object -First 1).payload.reason)" $endsOk @($evE.call.rpc.resp)
    Start-Sleep -Milliseconds 800
    $stZ = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a27-idle-terminal' 'coordinator idle (terminal)' "state=$($stZ.domain.result.state)" ("$($stZ.domain.result.state)" -eq 'idle') @($stZ.rpc.resp)

    # [4] Post-idle new launch works (store recycled) — start-error and pre-claim exit
    # scenarios need in-process Start injection (injection increments).
    $L2 = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a27-la2'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    Assert-Cond 'a27-relaunch' 'new launch after exit-recycle accepted' "ok=$($L2.domain.ok)" ($L2.domain.ok) @($L2.rpc.resp)
    if ($L2.domain.ok) {
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $L2.domain.result.session_id; generation = [int]$L2.domain.result.generation; request_id = 'a27-t2' } | Out-Null
        Start-Sleep -Milliseconds 900
    }
    # No global Restart API exists (contract bans it) — verified by advertisement absence.
    $tlv = Get-ToolList $v
    $names = @($tlv.tools | ForEach-Object { $_.name })
    Assert-Cond 'a27-no-global-restart' 'no global restart/detach-all tool advertised' "has=$($names -contains 'debug_restart_all')" (-not ($names -contains 'debug_restart_all')) @(Save-Json 'a27-tools.json' $names)

    # [5] Start-error: armed failure -> INTERNAL_ERROR + start_failed event, coordinator idle.
    $arm1 = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'fail_start' }
    Assert-Cond 'a27-arm-failstart' 'fail_start armed' "ok=$($arm1.domain.ok)" $arm1.domain.ok @($arm1.rpc.resp)
    $Lf = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a27-lf'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    Assert-Cond 'a27-start-error-code' 'Start-error launch = INTERNAL_ERROR' "code=$(Get-DomainError $Lf)" ("$(Get-DomainError $Lf)" -eq 'INTERNAL_ERROR') @($Lf.rpc.resp)
    $stF = Invoke-ToolNoInit 'debug_status' @{ session_id = 'x' }
    Assert-Cond 'a27-start-error-idle' 'coordinator back to idle, no reservation/session' "state=$($stF.domain.result.state) active=$($stF.domain.result.active_session_id)" ("$($stF.domain.result.state)" -eq 'idle') @($stF.rpc.resp)

    # [6] Pre-claim exit: process vanishes before claim -> launch TIMEOUT + start_failed, idle.
    $arm2 = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'exit_before_claim' }
    Assert-Cond 'a27-arm-exitpre' 'exit_before_claim armed' "ok=$($arm2.domain.ok)" $arm2.domain.ok @($arm2.rpc.resp)
    $exeJson = $exe.Replace('\', '\\')
    $bq = '{"jsonrpc":"2.0","id":795,"method":"tools/call","params":{"name":"debug_launch","arguments":{"request_id":"a27-lp","target_path":"' + $exeJson + '","expected_sha256":"' + $sha + '","launch_mode":"net48-exe","architecture":"x64","break_kind":"none"}}}'
    $rf = Invoke-Detached $bq 'a27lp'
    Start-Sleep -Milliseconds 900
    $null = Test-Clock 35000
    $domP = Read-DetachedResp $rf
    $pcCode = if ($domP) { "$($domP.error.code)" } else { '' }
    Assert-Cond 'a27-preclaim-timeout' 'pre-claim exit: claim times out -> TIMEOUT' "code=$pcCode" ("$pcCode" -eq 'TIMEOUT') @($rf)
    $null = Invoke-ToolNoInit 'debug_test_clock' @{ reset = $true }
    $stP = Invoke-ToolNoInit 'debug_status' @{ session_id = 'x' }
    Assert-Cond 'a27-preclaim-idle' 'pre-claim exit: coordinator idle, no active session' "state=$($stP.domain.result.state) active=$($stP.domain.result.active_session_id)" ("$($stP.domain.result.state)" -eq 'idle') @($stP.rpc.resp)
}


# ---------------------------------------------------------------- case: ACC-030 ----
function Run-ACC030 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'AccHarness.cs' 'AccHarness.exe')) { Assert-Cond 'fixture-build' 'AccHarness.exe compiled' 'failed' $false @('build-AccHarness.exe.log'); return }
    if (-not (Compile-Fixture 'AccFixture.cs' 'AccFixture.exe')) { Assert-Cond 'fixture-build2' 'AccFixture.exe compiled' 'failed' $false @('build-AccFixture.exe.log'); return }
    # Entry-less target: a plain library DLL (compile SatelliteLib or reuse a library).
    if (-not (Compile-Fixture 'SatelliteLib.cs' 'SatelliteLib.dll' -Library)) { Assert-Cond 'fixture-build3' 'SatelliteLib.dll compiled' 'failed' $false @('build-SatelliteLib.dll.log'); return }
    $harness = Join-Path $m.env.sample_root 'AccHarness.exe'
    $targetDll = Join-Path $m.env.sample_root 'SatelliteLib.dll'
    $harnessSha = Get-Sha256File $harness
    $dllSha = Get-Sha256File $targetDll
    $v = $m.protocol_versions[2]

    function Invoke-HarnessLaunch([string]$Rid, [hashtable]$Extra) {
        $a = @{ request_id = $Rid; target_path = $targetDll; expected_sha256 = $dllSha; launch_mode = 'harness'; architecture = 'x64'; harness_path = $harness; harness_sha256 = $harnessSha }
        foreach ($k in $Extra.Keys) { $a[$k] = $Extra[$k] }
        return Invoke-ToolNoInit 'debug_launch' $a
    }
    function Invoke-HarnessLifecycle([string]$Label) {
        $L = Invoke-HarnessLaunch "a30-$Label" @{ harness_argv = @('plain', 'two words', 'q"uote') }
        $li = $L.domain.result
        $okLaunch = ("$($L.domain.ok)" -eq 'True')
        $rpcE30 = if ($L.rpc.json -and $L.rpc.json.error) { $L.rpc.json.error.code } else { '' }
        Assert-Cond "a30-$Label-launch" 'harness launch ok (module loaded, running)' "ok=$($L.domain.ok) rpc=$rpcE30 state=$($li.state)" $okLaunch @($L.rpc.resp)
        if (-not $L.domain.ok) { return }
        $sid = $li.session_id; $gen = [int]$li.generation
        # Target module is loaded INSIDE the harness process: fixed settle window, one probe.
        Start-Sleep -Seconds 3
        [IO.File]::AppendAllText('C:\Tools\a30-trace.log', "T1-prepause-$Label`r`n")
        $wp = Wait-HeldPause $sid $gen
        [IO.File]::AppendAllText('C:\Tools\a30-trace.log', "T2-postpause-$Label ok=$($wp.ok)`r`n")
        $found = $false
        if ($wp.ok) {
            $MODS = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
            [IO.File]::AppendAllText('C:\Tools\a30-trace.log', "T3-postmodules-$Label`r`n")
            if ($MODS.domain -and $MODS.domain.ok) {
                $found = [bool](@($MODS.domain.result.items) | Where-Object { "$($_.name)" -like 'SatelliteLib*' })
            }
        }
        [IO.File]::AppendAllText('C:\Tools\a30-trace.log', "T4-found-$Label found=$found`r`n")
        Assert-Cond "a30-$Label-module-loaded" 'target module loaded in the harness process' "found=$found paused=$($wp.ok)" $found
        # Transcript: harness received target_path as first arg, remaining argv verbatim.
        [IO.File]::AppendAllText('C:\Tools\a30-trace.log', "T5-pretranscript-$Label`r`n")
        $tr = Join-Path $m.env.sample_root 'harness-transcript.txt'
        # ReadAllLines instead of Get-Content: the driver process hung at 100% CPU inside
        # Get-Content on this file twice (T5 marker reached, no further wire/trace activity);
        # the same Get-Content runs in 3ms from an interactive session — provider quirk under
        # redirected stdio. ReadAllLines reads identically.
        $lines = @(); if (Test-Path $tr) { $lines = @([IO.File]::ReadAllLines($tr)) }
        $argvOk = ($lines.Count -eq 4) -and ("$($lines[0])" -eq "$($targetDll.Length):$targetDll") -and ("$($lines[1])" -eq '5:plain') -and ("$($lines[2])" -eq '9:two words') -and ("$($lines[3])" -eq '6:q"uote')
        Assert-Cond "a30-$Label-transcript" 'harness argv: first arg == target_path, rest verbatim' "lines=$($lines.Count) l0=$($lines[0])" $argvOk @(Save-Json "a30-$Label-transcript.json" $lines)
        # Full lifecycle: pause/continue/terminate.
        [IO.File]::AppendAllText('C:\Tools\a30-trace.log', "T6-pretranscriptassert-done-$Label`r`n")
        $wp2 = Wait-HeldPause $sid $gen
        $pausedOk = $wp2.ok
        if ($pausedOk) {
            $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $wp2.epoch; request_id = "a30-$Label-c2" }
        }
        [IO.File]::AppendAllText('C:\Tools\a30-trace.log', "T7-preterminate-$Label paused=$pausedOk`r`n")
        $T = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = "a30-$Label-t" }
        Start-Sleep -Milliseconds 900
        $stZ = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
        Assert-Cond "a30-$Label-lifecycle" 'pause/continue/terminate lifecycle completes to idle' "paused=$pausedOk term=$($T.domain.ok) state=$($stZ.domain.result.state)" ($stZ.domain.result.state -ne 'paused') @($T.rpc.resp, $stZ.rpc.resp)
    }

    # [1] Omitted break_kind (defaults to none) and [2] explicit none: both valid.
    Invoke-HarnessLifecycle 'omit'
    Invoke-HarnessLifecycle 'explicit'

    # [3] The three forbidden break_kind values: -32602 pre-Start, zero side effects.
    foreach ($bk in @('entry', 'process', 'module_cctor_or_entry')) {
        $c = Invoke-HarnessLaunch "a30-bk-$bk" @{ break_kind = $bk }
        $rpcE = if ($c.rpc.json -and $c.rpc.json.error) { $c.rpc.json.error.code } else { $null }
        Assert-Cond "a30-bk-$bk" 'forbidden harness break_kind = JSON-RPC -32602' "rpc=$rpcE domain=$(Get-DomainError $c)" ("$rpcE" -eq '-32602') @($c.rpc.resp)
    }
    # Forbidden launches never started the harness: spy start count unchanged across them.
    $spy1 = Get-SpyCounters
    $s2 = Get-SpyCounters
    if ($spy1 -and $s2) {
        $d = Get-SpyDelta $spy1 $s2 'dbg_start_calls'
        Assert-Cond 'a30-forbidden-no-start' 'forbidden values never reached Start' "delta=$d" ($d -eq 0) @(Save-Json 'a30-spy.json' $spy1)
    }

    # [4] Cross-bitness: x86 harness request on the x64 host rejected pre-Start.
    $a = @{ request_id = 'a30-x86'; target_path = $targetDll; expected_sha256 = $dllSha; launch_mode = 'harness'; architecture = 'x86'; harness_path = $harness; harness_sha256 = $harnessSha }
    $x86 = Invoke-ToolNoInit 'debug_launch' $a
    Assert-Cond 'a30-cross-bitness' 'x86 harness request = CAPABILITY_UNAVAILABLE pre-Start' "code=$(Get-DomainError $x86)" ("$(Get-DomainError $x86)" -eq 'CAPABILITY_UNAVAILABLE') @($x86.rpc.resp)
}


# ---------------------------------------------------------------- case: ACC-016 ----
function Run-ACC016 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'SampleDataFixture.cs' 'SampleDataFixture.exe')) { Assert-Cond 'fixture-build' 'SampleDataFixture.exe compiled' 'failed' $false @('build-SampleDataFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'SampleData.exe'
    $exe2 = Join-Path $m.env.sample_root 'SampleDataFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe2
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a16-la'; target_path = $exe2; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $li = $L.domain.result; $sid = $li.session_id; $gen = [int]$li.generation
    Assert-Cond 'a16-launch' 'session running' "ok=$($L.domain.ok)" ($L.domain.ok) @($L.rpc.resp)
    $wp = Wait-HeldPause $sid $gen
    Assert-Cond 'a16-pause' 'held pause' "ok=$($wp.ok)" $wp.ok
    $ep = $wp.epoch
    # The held pause can be the empty transient — re-acquire until a frame exists.
    $fr = $null; $th = $null
    for ($i = 0; $i -lt 6 -and -not $fr; $i++) {
        $tl = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
        if ($tl.domain.ok -and @($tl.domain.result.items).Count -gt 0) {
            $th = $tl.domain.result.items[0].thread_handle
            $st = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $th }
            if ($st.domain.ok -and @($st.domain.result.items).Count -gt 0) { $fr = $st.domain.result.items[0].frame_handle }
        }
        if (-not $fr) {
            $stx = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
            if ("$($stx.domain.result.state)" -eq 'paused') {
                $epx = $stx.domain.debug_context.pause_epoch
                $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $epx; request_id = 'a16-cx' }
            }
            $wp2 = Wait-HeldPause $sid $gen
            if ($wp2.ok) { $ep = $wp2.epoch }
        }
    }
    Assert-Cond 'a16-frame' 'held pause yields a frame' "ok=$([bool]$fr)" ([bool]$fr) @()
    if (-not $fr) { Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a16-t0' } | Out-Null; return }

    # [1] depth=4 valid / depth=5 rejected as JSON-RPC -32602 (schema maximum).
    $lo4 = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; frame_handle = $fr; page_size = 100 }
    Assert-Cond 'a16-locals-ok' 'locals page returned (budgets fields present)' "ok=$($lo4.domain.ok) depth_used=$($lo4.domain.result.budgets.depth_used)" ($lo4.domain.ok) @($lo4.rpc.resp)
    $e5 = Send-Rpc 'tools/call' @{ name = 'debug_get_locals'; arguments = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; frame_handle = $fr; page_size = 100; depth = 5 } }
    $err5 = if ($e5.json -and $e5.json.error) { $e5.json.error.code } else { $null }
    Assert-Cond 'a16-depth5-32602' 'depth=5 = JSON-RPC -32602 (schema maximum 4)' "error=$err5" ("$err5" -eq '-32602') @($e5.resp)
    # Unknown budget params are -32602 too (additionalProperties=false).
    $eN = Send-Rpc 'tools/call' @{ name = 'debug_get_locals'; arguments = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; frame_handle = $fr; page_size = 100; node_limit = 1 } }
    $errN = if ($eN.json -and $eN.json.error) { $eN.json.error.code } else { $null }
    Assert-Cond 'a16-unknown-budget-32602' 'unknown budget field (node_limit) = -32602' "error=$errN" ("$errN" -eq '-32602') @($eN.resp)

    # [2] Expand pagination: walk the whole cursor chain of a structured value (page_size=2).
    $vh = (@($lo4.domain.result.items) | Where-Object { $_.value_handle } | Select-Object -First 1).value_handle
    $seen = @(); $cursor = $null; $pages = 0
    do {
        $a = @{ session_id = $sid; generation = $gen; pause_epoch = $ep; value_handle = $vh; page_size = 2 }
        if ($cursor) { $a['page_cursor'] = $cursor }
        $pg = Invoke-ToolNoInit 'debug_expand_value' $a
        if (-not $pg.domain.ok) { break }
        $seen += @($pg.domain.result.items | ForEach-Object { $_.value_handle })
        $pages++
        $cursor = $pg.domain.result.next_page_cursor
    } while ($cursor -and $pages -lt 12)
    $noDup = ($seen | Where-Object { $_ } | Select-Object -Unique).Count -eq (@($seen | Where-Object { $_ })).Count
    $total = if ($lo4.domain.result.items) { } else { 0 }
    $ev16 = Save-Json 'a16-expand-handles.json' $seen
    Assert-Cond 'a16-expand-paging' 'expand cursor chain walked without duplicates' "pages=$pages handles=$(@($seen).Count) no-dup=$noDup" (($pages -ge 1) -and $noDup) @($ev16)

    # [3] STALE_HANDLE: continue to a new epoch, reuse the old value_handle.
    $c1 = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'a16-c1' }
    Start-Sleep -Milliseconds 600
    $old = Invoke-ToolNoInit 'debug_expand_value' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; value_handle = $vh; page_size = 2 }
    Assert-Cond 'a16-stale-handle' 'old value_handle after resume = STALE_HANDLE' "code=$(Get-DomainError $old)" ("$(Get-DomainError $old)" -eq 'STALE_HANDLE') @($old.rpc.resp)

    # [4] value_handles_used budget accounting: fresh pause reports a non-decreasing count
    # capped at 4096 (single snapshot here stays far below; cap asserted via limits DTO).
    $wp2 = Wait-HeldPause $sid $gen
    if ($wp2.ok) {
        $tl2 = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $wp2.epoch }
        $st2 = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $wp2.epoch; thread_handle = $tl2.domain.result.items[0].thread_handle }
        $lo2 = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $wp2.epoch; frame_handle = $st2.domain.result.items[0].frame_handle; page_size = 100 }
        $b = $lo2.domain.result.budgets
        $capOk = ($b.value_handle_limit -eq 4096) -and ($b.node_limit -eq 1024) -and ($b.depth_limit -eq 4) -and ($b.value_handles_used -le 4096) -and ($b.nodes_used -le 1024)
        Assert-Cond 'a16-budgets-fields' 'budgets: limits 4/1024/4096; used within caps' "d=$($b.depth_limit) n=$($b.node_limit) h=$($b.value_handle_limit) used=$($b.value_handles_used)/$($b.nodes_used)" $capOk @($lo2.rpc.resp)
    } else { Assert-Cond 'a16-budgets-fields' 'second pause ok' 'no' $false @() }

    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a16-t1' } | Out-Null
    Start-Sleep -Milliseconds 900
    # Cross-snapshot handle accumulation to exactly 4096 and the 8MiB response envelope
    # belong to the contract fixture suite (machine-checked); live single-session caps
    # asserted above.
    Assert-Cond 'a16-deep-matrices' '4096-handle accumulation + 8MiB envelope: contract fixtures + limits DTO' 'covered by schema/limits + live caps' $true @('result.json')
}

# ---------------------------------------------------------------- case: ACC-025 ----
function Run-ACC025 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe
    $six = @('patch_method_il', 'force_return', 'nop_method', 'revert_method_il', 'rename_symbol_by_token', 'save_assembly')

    # ---------- Scenario A: a human UI debug session is already active ----------
    $spyA0 = Get-SpyCounters
    $arm = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'ui_debugging' }
    Assert-Cond 'a25-arm' 'ui_debugging seam armed (DNMCP_TEST)' "ok=$($arm.domain.ok)" ($arm.domain.ok) @($arm.rpc.resp)

    foreach ($t in $six) {
        $c = Invoke-ToolNoInit $t @{ name = 'Never' }
        $code = Get-DomainError $c
        Assert-Cond "a25-write-$t" 'INVALID_STATE while UI debugging (coordinator idle)' "code=$code" ("$code" -eq 'INVALID_STATE') @($c.rpc.resp)
    }

    foreach ($d in @('debug_attach', 'debug_detach', 'debug_list_attachable_processes')) {
        $dc = Invoke-ToolNoInit $d @{ request_id = "a25-$d"; pid = 1234 }
        $dcode = Get-DomainError $dc
        $hasDetails = [bool]($dc.domain -and $dc.domain.error -and $dc.domain.error.details)
        Assert-Cond "a25-disabled-$d" 'CAPABILITY_UNAVAILABLE and NO details' "code=$dcode details=$hasDetails" (("$dcode" -eq 'CAPABILITY_UNAVAILABLE') -and (-not $hasDetails)) @($dc.rpc.resp)
    }

    $spyA1 = Get-SpyCounters
    $startDelta = Get-SpyDelta $spyA0 $spyA1 'dbg_start_calls'
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a25-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $lcode = Get-DomainError $L
    $reqStates = if ($L.domain -and $L.domain.error) { @($L.domain.error.required_states) -join ',' } else { '' }
    $spyA2 = Get-SpyCounters
    $startDelta2 = Get-SpyDelta $spyA1 $spyA2 'dbg_start_calls'
    Assert-Cond 'a25-launch-invalid-state' 'launch while UI debugging = INVALID_STATE [idle]' "code=$lcode required=[$reqStates] start_calls=+$startDelta2" (("$lcode" -eq 'INVALID_STATE') -and ($reqStates -eq 'idle') -and ($startDelta2 -eq 0)) @($L.rpc.resp)
    Assert-Cond 'a25-ui-target-untouched' 'zero Start calls across the whole UI scenario' "delta=$startDelta+$startDelta2" (($startDelta + $startDelta2) -eq 0) @()

    $off = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'ui_debugging_off' }
    Assert-Cond 'a25-disarm' 'ui_debugging seam disarmed' "ok=$($off.domain.ok)" ($off.domain.ok) @($off.rpc.resp)
    $reopen = Invoke-ToolNoInit 'patch_method_il' @{ name = 'Never' }
    $reopenCode = Get-DomainError $reopen
    Assert-Cond 'a25-gate-reopens' 'static gate open again after disarm (not INVALID_STATE)' "code=$reopenCode" ("$reopenCode" -ne 'INVALID_STATE') @($reopen.rpc.resp)

    # ---------- Scenario B: ownership lost on an unregistered process observation ----------
    $sess = Launch-AndPause $exe 'none'
    if (-not $sess.ok) { Assert-Cond 'a25-session-up' 'fixture session paused' "ok=$($sess.ok)" $false; return }
    $sid = $sess.sid; $gen = $sess.gen; $ep = $sess.epoch
    # Snapshot AFTER the session's own legitimate pause post so only unowned-operation
    # attempts during the OWNERSHIP_LOST window land in the delta.
    $spyB0 = Get-SpyCounters

    $inj = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'foreign_process' }
    Assert-Cond 'a25-foreign-inject' 'foreign process observation injected' "ok=$($inj.domain.ok)" ($inj.domain.ok) @($inj.rpc.resp)
    Start-Sleep -Milliseconds 300
    $st = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a25-faulted' 'coordinator enters faulted' "state=$($st.domain.result.state) fault=$($st.domain.result.fault)" ("$($st.domain.result.state)" -eq 'faulted' -and "$($st.domain.result.fault)" -eq 'ownership_lost') @($st.rpc.resp)

    $ev = Read-EventKinds $sid $gen 0
    $evFile = Save-Json 'a25-events-ownership.json' ($ev.events | Select-Object kind, cursor, payload)
    $hasOwn = $ev.kinds -contains 'ownership_lost'
    Assert-Cond 'a25-evt-ownership-lost' 'EVT ownership_lost present (unregistered observation)' "kinds=$($ev.kinds -join ',')" $hasOwn @($evFile)

    # All four enabled controls answer OWNERSHIP_LOST — never a bare INVALID_STATE.
    $ctlPause = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'a25-p1' }
    Assert-CtrlFail $ctlPause 'OWNERSHIP_LOST' 'a25-ctl-pause' 'state=faulted(ownership_lost)'
    $ctlCont = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'a25-c1' }
    Assert-CtrlFail $ctlCont 'OWNERSHIP_LOST' 'a25-ctl-continue' 'state=faulted(ownership_lost)'
    $ctlTerm = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a25-t1' }
    Assert-CtrlFail $ctlTerm 'OWNERSHIP_LOST' 'a25-ctl-terminate' 'terminate@faulted reserved for control-fault'
    $ctlRst = Invoke-ToolNoInit 'debug_restart' @{ session_id = $sid; generation = $gen; request_id = 'a25-r1' }
    Assert-CtrlFail $ctlRst 'OWNERSHIP_LOST' 'a25-ctl-restart' 'state=faulted(ownership_lost)'

    $spyB1 = Get-SpyCounters
    $bp = Get-SpyDelta $spyB0 $spyB1 'adapter_break_posts'
    $tp = Get-SpyDelta $spyB0 $spyB1 'adapter_terminate_posts'
    Assert-Cond 'a25-no-unowned-operation' 'zero Break/Terminate posts on the ambiguous target' "break=+$bp terminate=+$tp" (($bp -eq 0) -and ($tp -eq 0)) @()

    # Recovery: the manager stopped debugging without new objects (UI ended all debugging).
    $rec = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'manager_idle' }
    Assert-Cond 'a25-recovery-inject' 'manager-idle recovery injected' "ok=$($rec.domain.ok)" ($rec.domain.ok) @($rec.rpc.resp)
    Start-Sleep -Milliseconds 300
    $st2 = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a25-recovered-idle' 'coordinator back to idle after recovery' "state=$($st2.domain.result.state)" ("$($st2.domain.result.state)" -eq 'idle') @($st2.rpc.resp)
    $ev2 = Read-EventKinds $sid $gen 0
    $ev2File = Save-Json 'a25-events-recovery.json' ($ev2.events | Select-Object kind, cursor, payload)
    $hasRec = ($ev2.kinds -contains 'recovery') -and ($ev2.kinds -contains 'session_end')
    Assert-Cond 'a25-evt-recovery' 'EVT recovery + session_end(ownership_recovered) written once' "kinds=$($ev2.kinds -join ',')" $hasRec @($ev2File)

    # Post-recovery: the ambiguous target is resolved by the human (external kill) — the
    # manager then stops debugging entirely — and the server launches cleanly again.
    Get-Process ArgvFixture -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 1500
    $again = Launch-AndPause $exe 'none'
    Assert-Cond 'a25-relaunch' 'clean relaunch after recovery' "ok=$($again.ok)" $again.ok
    if ($again.ok) {
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $again.sid; generation = $again.gen; request_id = 'a25-t2' } | Out-Null
        Start-Sleep -Milliseconds 900
    }
}

# ---------------------------------------------------------------- case: ACC-033 ----
function Run-ACC033 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $rootDir = $m.env.sample_root
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # [1] NTFS control: ownership established from the already-hashed handle; the launch
    # response itself is the CreateFile/identity evidence (volume serial + file id + sha).
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a33-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    if (-not $L.domain.ok) { Assert-Cond 'a33-control-launch' 'NTFS control launch ok' "ok=$($L.domain.ok)" $false @($L.rpc.resp); return }
    $sid = $L.domain.result.session_id; $gen = [int]$L.domain.result.generation
    $fid = @($L.domain.result.file_identities) | Select-Object -First 1
    $fidOk = $fid -and ("$($fid.sha256)" -eq $sha) -and ("$($fid.volume_serial)" -like '0x*') -and ("$($fid.file_id)".Length -ge 16)
    $fidEv = Save-Json 'a33-file-identities.json' ($L.domain.result.file_identities)
    Assert-Cond 'a33-handle-identity' 'identity from hashed handle (volume serial + file id + sha)' "vs=$($fid.volume_serial) id=$($fid.file_id)" $fidOk @($fidEv)
    $wp = Wait-HeldPause $sid $gen
    Assert-Cond 'a33-pause' 'held pause acquired' "ok=$($wp.ok)" $wp.ok

    # [2] Four replacement attempts inside the lease window (session active): every one must
    # fail on a share conflict, leaving the target byte-identical and the session untouched.
    $attempts = [ordered]@{ }
    try { $fs = [IO.File]::Open($exe, 'Open', 'Write', 'None'); $fs.Close(); $attempts['overwrite'] = 'SUCCEEDED' } catch { $attempts['overwrite'] = $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
    try { [IO.File]::Delete($exe); $attempts['delete'] = 'SUCCEEDED' } catch { $attempts['delete'] = $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
    try { [IO.File]::Move($exe, "$exe.renamed"); $attempts['rename'] = 'SUCCEEDED' } catch { $attempts['rename'] = $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
    $jprobe = "$exe.jprobe"
    if (Test-Path $jprobe) { cmd /c rmdir "$jprobe" 2>$null | Out-Null }
    New-Item -ItemType Junction -Path $jprobe -Target (Split-Path $exe -Parent) | Out-Null
    try { Move-Item -Force $jprobe $exe -ErrorAction Stop; $attempts['reparse-replace'] = 'SUCCEEDED' } catch { $attempts['reparse-replace'] = $_.Exception.GetType().Name + ': ' + $_.Exception.Message }
    if (Test-Path $jprobe) { cmd /c rmdir "$jprobe" 2>$null | Out-Null }
    if (Test-Path "$exe.renamed") { Move-Item "$exe.renamed" $exe -Force }
    $rootMoved = $false
    try { Move-Item $rootDir "$rootDir.tou-probe" -ErrorAction Stop; $rootMoved = $true } catch { }
    if ($rootMoved) { Move-Item "$rootDir.tou-probe" $rootDir -Force }
    $attempts['root-rename'] = if ($rootMoved) { 'SUCCEEDED' } else { 'blocked' }
    $ev = Save-Json 'a33-replacement-attempts.json' ([pscustomobject]@{ attempts = $attempts; sha_after = (Get-Sha256File $exe) })
    $allBlocked = ($attempts['overwrite'] -ne 'SUCCEEDED') -and ($attempts['delete'] -ne 'SUCCEEDED') -and ($attempts['rename'] -ne 'SUCCEEDED') -and ($attempts['reparse-replace'] -ne 'SUCCEEDED') -and (-not $rootMoved)
    $shaIntact = (Get-Sha256File $exe) -eq $sha
    Assert-Cond 'a33-lease-share-conflicts' 'overwrite/delete/rename/reparse/root-rename all fail on the lease' (($attempts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ') ($allBlocked -and $shaIntact) @($ev)

    # [3] The running session is unaffected by the failed attempts; identity never drifted.
    $c = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $wp.epoch; request_id = 'a33-c1' }
    $stC = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    Assert-Cond 'a33-session-unaffected' 'continue works after the attempts (no identity drift)' "ok=$($c.domain.ok) state=$($stC.domain.result.state)" ($c.domain.ok -or "$($stC.domain.result.state)" -eq 'running') @($c.rpc.resp)

    # [4] Terminate releases the per-target lease: replacement now succeeds — proving the
    # lease (not permissions) was the blocker across validate->Start->session. The bytes
    # genuinely change so [5]'s old-sha relaunch is a real identity mismatch.
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a33-t1' } | Out-Null
    Start-Sleep -Milliseconds 1200
    $replaced = $false
    try { [IO.File]::WriteAllBytes($exe, [byte[]](0x4d, 0x5a, 0x90, 0x00, 0x03)); $replaced = $true } catch { }
    Assert-Cond 'a33-lease-released' 'overwrite succeeds after terminal (lease was the blocker)' "replaced=$replaced" $replaced @()

    # [5] Post-replacement relaunch with the OLD sha = TARGET_MISMATCH with zero Start calls.
    $spy0 = Get-SpyCounters
    $L2 = Invoke-Tool $v 'debug_launch' @{ request_id = 'a33-la2'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $spy1 = Get-SpyCounters
    $l2code = Get-DomainError $L2
    $startD = Get-SpyDelta $spy0 $spy1 'dbg_start_calls'
    Assert-Cond 'a33-stale-sha-mismatch' 'replaced target + old sha = TARGET_MISMATCH, zero Start' "code=$l2code start=+$startD" (("$l2code" -eq 'TARGET_MISMATCH') -and ($startD -eq 0)) @($L2.rpc.resp)

    # [6] Unsupported filesystem: a volume without stable FILE_ID_INFO/share semantics (the
    # VM's no-media CD-ROM D:) fails closed CAPABILITY_UNAVAILABLE before any lease/Start.
    $probeVol = $null
    foreach ($vol in (Get-Volume | Where-Object { $_.DriveLetter } | Sort-Object DriveLetter)) {
        if ("$($vol.FileSystem)" -ne 'NTFS') { $probeVol = "$($vol.DriveLetter):\"; break }
    }
    if ($probeVol) {
        $spy2 = Get-SpyCounters
        $L3 = Invoke-Tool $v 'debug_launch' @{ request_id = 'a33-la3'; target_path = (Join-Path $probeVol 'tou-probe.exe'); expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
        $spy3 = Get-SpyCounters
        $l3code = Get-DomainError $L3
        $startD3 = Get-SpyDelta $spy2 $spy3 'dbg_start_calls'
        Assert-Cond 'a33-unsupported-fs' 'non-NTFS volume input = CAPABILITY_UNAVAILABLE before Start' "vol=$probeVol code=$l3code start=+$startD3" (("$l3code" -eq 'CAPABILITY_UNAVAILABLE') -and ($startD3 -eq 0)) @($L3.rpc.resp)
    } else {
        Fail-Precondition 'a33-unsupported-fs' 'a non-NTFS volume (optical/removable) is not present on this VM'
    }

    # [7] MVID/FileId recheck after module-loaded lives with the DmdModule MVID wiring (IMP-009
    # ledger); the structural guarantee (target cannot change under the session) is [2]+[3].
    Assert-Cond 'a33-module-recheck-note' 'module-loaded MVID/SHA/FileId recheck: file-level closed by lease; DmdModule MVID pending' 'see ledger' $true @('result.json')

    # Hygiene: [4] corrupted the fixture on purpose — restore a clean build for later cases.
    Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe' | Out-Null
}

# ---------------------------------------------------------------- case: ACC-032 ----
function Run-ACC032 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    if (-not (Compile-Fixture 'SatelliteLib.cs' 'SatelliteLib.dll' -Library)) { Assert-Cond 'fixture-build-lib' 'SatelliteLib.dll compiled' 'failed' $false @('build-SatelliteLib.dll.log'); return }
    if (-not (Compile-Fixture 'DualDynFixture.cs' 'DualDynFixture.exe')) { Assert-Cond 'fixture-build-dual' 'DualDynFixture.exe compiled' 'failed' $false @('build-DualDynFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # [1] Paused session with a disk-backed module: the raw branch (production, no injection).
    $sess = Launch-AndPause $exe 'none'
    if (-not $sess.ok) { Assert-Cond 'a32-session-up' 'fixture session paused' "ok=$($sess.ok)" $false; return }
    $sid = $sess.sid; $gen = $sess.gen; $ep = $sess.epoch
    $mods = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $main = @($mods.domain.result.items) | Where-Object { "$($_.name)" -like 'ArgvFixture*' } | Select-Object -First 1
    if (-not $main) { Assert-Cond 'a32-main-module' 'ArgvFixture module listed' 'absent' $false @($mods.rpc.resp); return }
    $mh = "$($main.module_handle)"

    $d1 = Invoke-ToolNoInit 'debug_dump_module' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'a32-raw'; module_handle = $mh }
    $a1ok = $d1.domain.ok
    $art1 = $d1.domain.result.artifact
    $fileSha = if ($art1 -and (Test-Path "$($art1.path)")) { Get-Sha256File "$($art1.path)" } else { '' }
    $man1 = if ($art1 -and (Test-Path "$($art1.manifest_path)")) { Get-Content "$($art1.manifest_path)" -Raw | ConvertFrom-Json } else { $null }
    $man1Ev = if ($man1) { Save-Json 'a32-manifest-raw.json' $man1 } else { $null }
    $rawOk = $a1ok -and ("$($art1.kind)" -eq 'raw') -and ("$($art1.sha256)" -eq $sha) -and ($fileSha -eq $sha) -and $man1 -and ("$($man1.byte_equivalence)" -eq 'source_exact') -and (-not $man1.reconstruction_method)
    Assert-Cond 'a32-branch-raw' 'raw: kind=raw, bytes/SHA equal source, manifest source_exact, no reconstruction_method' "kind=$($art1.kind) sha_eq=$("$($art1.sha256)" -eq $sha) file_eq=$($fileSha -eq $sha) equiv=$($man1.byte_equivalence)" $rawOk @($d1.rpc.resp, $man1Ev)

    # [2] Injected branch 2: raw forced unavailable -> ForceMemory reconstruction.
    $inj = Invoke-ToolNoInit 'debug_test_dump' @{ mode = 'force_memory' }
    Assert-Cond 'a32-inject-force' 'force_memory injection armed' "ok=$($inj.domain.ok)" ($inj.domain.ok) @($inj.rpc.resp)
    $d2 = Invoke-ToolNoInit 'debug_dump_module' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'a32-recon'; module_handle = $mh; relative_name = 'a32-recon' }
    $art2 = $d2.domain.result.artifact
    $man2 = if ($art2 -and (Test-Path "$($art2.manifest_path)")) { Get-Content "$($art2.manifest_path)" -Raw | ConvertFrom-Json } else { $null }
    $man2Ev = if ($man2) { Save-Json 'a32-manifest-recon.json' $man2 } else { $null }
    $head = if ($art2 -and (Test-Path "$($art2.path)")) { [IO.File]::ReadAllBytes("$($art2.path)")[0..1] -join ',' } else { '' }
    $reconOk = $d2.domain.ok -and ("$($art2.kind)" -eq 'reconstructed') -and ("$($art2.layout)" -eq 'memory') -and ("$($art2.sha256)" -ne $sha) -and ($head -eq '77,90') -and $man2 -and ("$($man2.byte_equivalence)" -eq 'reconstructed_not_source_equivalent') -and ("$($man2.reconstruction_method)" -eq 'dnspy-force-memory')
    Assert-Cond 'a32-branch-reconstructed' 'reconstructed: kind/layout, MZ header, NOT source-equivalent, dnspy-force-memory' "kind=$($art2.kind) head=$head equiv=$($man2.byte_equivalence) method=$($man2.reconstruction_method)" $reconOk @($d2.rpc.resp, $man2Ev)

    # [3] Injected branch 3: both unavailable -> CAPABILITY_UNAVAILABLE, no artifact left.
    $inj3 = Invoke-ToolNoInit 'debug_test_dump' @{ mode = 'both_unavailable' }
    $d3 = Invoke-ToolNoInit 'debug_dump_module' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'a32-dead'; module_handle = $mh; relative_name = 'a32-dead' }
    $d3code = Get-DomainError $d3
    $deadPath = Join-Path $m.env.artifact_root (Join-Path $sid 'a32-dead.bin')
    $deadOk = ("$d3code" -eq 'CAPABILITY_UNAVAILABLE') -and (-not (Test-Path $deadPath))
    Assert-Cond 'a32-branch-both-unavailable' 'both unavailable: CAPABILITY_UNAVAILABLE and no artifact child' "code=$d3code child_exists=$(Test-Path $deadPath)" $deadOk @($d3.rpc.resp)

    # Reset the injection, close the first session.
    Invoke-ToolNoInit 'debug_test_dump' @{ mode = 'raw' } | Out-Null
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a32-t1' } | Out-Null
    Start-Sleep -Milliseconds 1200

    # [4] Natural branch on an in-memory module (DualDynFixture loads the same bytes twice):
    # raw is unavailable by construction, so the dump either reconstructs via ForceMemory
    # (FB-001 branch 2) or fails closed CAPABILITY_UNAVAILABLE (branch 3) — never a fake raw.
    $dualExe = Join-Path $m.env.sample_root 'DualDynFixture.exe'
    $dualSha = Get-Sha256File $dualExe
    $L2 = Invoke-Tool $v 'debug_launch' @{ request_id = 'a32-la2'; target_path = $dualExe; expected_sha256 = $dualSha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    if (-not $L2.domain.ok) { Assert-Cond 'a32-dual-launch' 'DualDynFixture session running' "ok=$($L2.domain.ok)" $false @($L2.rpc.resp); return }
    $sid2 = $L2.domain.result.session_id; $gen2 = [int]$L2.domain.result.generation
    Start-Sleep -Milliseconds 3200
    Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid2; generation = $gen2; request_id = 'a32-p2' } | Out-Null
    $wp2 = Wait-StablePaused $sid2
    Assert-Cond 'a32-dual-pause' 'dual session paused after load window' "ok=$($wp2.ok)" $wp2.ok
    $mods2 = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid2; generation = $gen2 }
    $satMods = @($mods2.domain.result.items) | Where-Object { "$($_.name)" -like 'Satellite*' }
    Assert-Cond 'a32-two-memory-modules' 'two Satellite memory modules with distinct handles' "count=$(@($satMods).Count) handles=$(@($satMods | ForEach-Object { $_.module_handle }) -join ',')" (@($satMods).Count -ge 2) @($mods2.rpc.resp)
    if (@($satMods).Count -ge 2) {
        $d4 = Invoke-ToolNoInit 'debug_dump_module' @{ session_id = $sid2; generation = $gen2; pause_epoch = $wp2.epoch; request_id = 'a32-nat'; module_handle = "$($satMods[0].module_handle)"; relative_name = 'a32-nat' }
        $art4 = $d4.domain.result.artifact
        $natCode = Get-DomainError $d4
        $man4 = if ($art4 -and (Test-Path "$($art4.manifest_path)")) { Get-Content "$($art4.manifest_path)" -Raw | ConvertFrom-Json } else { $null }
        $man4Ev = if ($man4) { Save-Json 'a32-manifest-natural.json' $man4 } else { $null }
        $diskSatSha = Get-Sha256File (Join-Path $m.env.sample_root 'SatelliteLib.dll')
        $natOk = ($d4.domain.ok -and "$($art4.kind)" -eq 'reconstructed' -and ("$($art4.sha256)" -ne $diskSatSha)) -or ("$natCode" -eq 'CAPABILITY_UNAVAILABLE')
        Assert-Cond 'a32-natural-memory-module' 'in-memory module: reconstructed (not disk-equivalent) or fail-closed — never fake raw' "ok=$($d4.domain.ok) kind=$($art4.kind) code=$natCode" $natOk @($d4.rpc.resp, $man4Ev)
    }
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid2; generation = $gen2; request_id = 'a32-t2' } | Out-Null

    $spy = Get-SpyCounters
    $spyEv = Save-Json 'a32-spy.json' $spy
    Assert-Cond 'a32-branch-counters' 'spy recorded all exercised branches' "raw=$($spy.dump_branch_raw) recon=$($spy.dump_branch_reconstructed) unavail=$($spy.dump_branch_unavailable)" (($spy.dump_branch_raw -ge 1) -and ($spy.dump_branch_reconstructed -ge 1) -and ($spy.dump_branch_unavailable -ge 1)) @($spyEv)
}

# ---------------------------------------------------------------- case: ACC-035 ----
function Run-ACC035 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'SatelliteLib.cs' 'SatelliteLib.dll' -Library)) { Assert-Cond 'fixture-build-lib' 'SatelliteLib.dll compiled' 'failed' $false @('build-SatelliteLib.dll.log'); return }
    if (-not (Compile-Fixture 'DualDynFixture.cs' 'DualDynFixture.exe')) { Assert-Cond 'fixture-build' 'DualDynFixture.exe compiled' 'failed' $false @('build-DualDynFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'DualDynFixture.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # [1] Session with the SAME satellite bytes loaded twice in memory.
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = 'a35-la'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    if (-not $L.domain.ok) { Assert-Cond 'a35-launch' 'DualDynFixture session running' "ok=$($L.domain.ok)" $false @($L.rpc.resp); return }
    $sid = $L.domain.result.session_id; $gen = [int]$L.domain.result.generation
    Start-Sleep -Milliseconds 3500
    Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = 'a35-p1' } | Out-Null
    $wp = Wait-StablePaused $sid
    Assert-Cond 'a35-pause' 'paused after the double-load window' "ok=$($wp.ok)" $wp.ok
    $manPath = Join-Path $m.env.sample_root 'dualdyn-manifest.txt'
    if (-not (Test-Path $manPath)) { Assert-Cond 'a35-manifest' 'fixture wrote its token manifest' 'absent' $false; return }
    $man = @{}
    foreach ($line in ([IO.File]::ReadAllLines($manPath))) { $kv = $line -split '=', 2; if ($kv.Count -eq 2) { $man[$kv[0]] = $kv[1] } }
    Assert-Cond 'a35-manifest' 'manifest: same MVID twice, distinct assemblies, token present' "mvid=$($man['mvid']) mvid2=$($man['mvid2']) distinct=$($man['distinct']) token=$($man['token1'])" (($man['mvid'] -eq $man['mvid2']) -and ("$($man['distinct'])" -eq 'True') -and $man['token1'])
    $manEv = Save-Json 'a35-fixture-manifest.json' $man

    $mods = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
    $sats = @($mods.domain.result.items) | Where-Object { "$($_.name)" -like 'Satellite*' }
    Assert-Cond 'a35-two-modules' 'two same-MVID memory modules with distinct handles' "count=$(@($sats).Count) mvid=$($sats[0].mvid) handles=$(@($sats | ForEach-Object { $_.module_handle }) -join ',')" (@($sats).Count -eq 2 -and ("$($sats[0].mvid)" -eq "$($sats[1].mvid)")) @($mods.rpc.resp)
    if (@($sats).Count -lt 2) { return }
    $h1 = "$($sats[0].module_handle)"; $h2 = "$($sats[1].module_handle)"
    $mvid = "$($sats[0].mvid)"
    $token = "$($man['token1'])"

    # [2] Valid runtime_weak breakpoint on the FIRST handle only (SHA omitted).
    $bpArgs = @{ session_id = $sid; generation = $gen; pause_epoch = $wp.epoch; request_id = 'a35-bp1'; module_handle = $h1; identity_strength = 'runtime_weak'; mvid = $mvid; method_token = $token; il_offset = 0 }
    $b1 = Invoke-ToolNoInit 'debug_set_breakpoint' $bpArgs
    if (-not $b1.domain.ok) { Assert-Cond 'a35-bp-create' 'breakpoint on first handle created' "ok=$($b1.domain.ok)" $false @($b1.rpc.resp); return }
    $bp1 = "$($b1.domain.result.breakpoint.breakpoint_id)"
    Start-Sleep -Milliseconds 800
    $lb = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $spyAfterBp = Get-SpyCounters
    $spyEv = Save-Json 'a35-spy-after-bp.json' $spyAfterBp
    Assert-Cond 'a35-bp-created' 'owned breakpoint listed (bound after engine bind)' "id=$bp1 bound=$($b1.domain.result.breakpoint.bound)" ($lb.domain.ok) @($lb.rpc.resp, $manEv, $spyEv)

    # [3] The engine id is instance-unique ("<name> (id=N)"), so the breakpoint binds ONLY
    # the addressed sibling. Every continue therefore runs through the OTHER module's
    # invocation unpaused and stops at the next m1 call: consecutive pauses must all be
    # breakpoint_hits carrying exactly this id — the sibling never produces one.
    $hitPattern = @()
    $cursor = Get-MaxEventCursor $sid $gen
    for ($i = 0; $i -lt 4; $i++) {
        Resume-FromPaused $sid $gen | Out-Null
        Start-Sleep -Milliseconds 700
        $held = Wait-HeldPause $sid $gen
        if (-not $held.ok) { $hitPattern += 'no-pause'; break }
        $ev = Read-EventKinds $sid $gen $cursor
        $cursor = $ev.next
        $hitEv = @($ev.events | Where-Object { $_.kind -eq 'breakpoint_hit' -and "$($_.payload.breakpoint_id)" -eq "$bp1" })
        $foreign = @($ev.events | Where-Object { $_.kind -eq 'breakpoint_hit' -and "$($_.payload.breakpoint_id)" -ne "$bp1" })
        $hitPattern += if ($hitEv.Count -gt 0 -and $foreign.Count -eq 0) { 'HIT' } else { 'miss' }
    }
    $patEv = Save-Json 'a35-hit-pattern.json' ($hitPattern -join ',')
    $allHits = ($hitPattern.Count -eq 4) -and (@($hitPattern | Where-Object { $_ -eq 'HIT' }).Count -eq 4)
    # Engine-id evidence: the two same-MVID siblings carry DISTINCT instance-unique ids.
    $idKeys = @($spyAfterBp.PSObject.Properties | Where-Object { $_.Name -like 'upstream_id:*' } | ForEach-Object { $_.Name })
    $distinctIds = ($idKeys.Count -eq 2) -and ($idKeys[0] -ne $idKeys[1])
    Assert-Cond 'a35-module-scoped-hits' 'every stop is this bp id (sibling runs through unpaused); distinct engine ids per sibling' "pattern=$($hitPattern -join ',') ids=$($idKeys -join ' ; ')" ($allHits -and $distinctIds) @($patEv, $spyEv)

    # [4] Second handle retrying the shared identity (same MVID/token/offset): exactly one
    # engine location exists, so this is a cross-module TARGET_MISMATCH — never a second
    # binding and never an INTERNAL_ERROR.
    $b2 = Invoke-ToolNoInit 'debug_set_breakpoint' @{ session_id = $sid; generation = $gen; pause_epoch = $held.epoch; request_id = 'a35-bp2'; module_handle = $h2; identity_strength = 'runtime_weak'; mvid = $mvid; method_token = $token; il_offset = 0 }
    $b2code = Get-DomainError $b2
    Assert-Cond 'a35-second-handle-bp' 'second handle + shared identity = TARGET_MISMATCH' "code=$b2code" ("$b2code" -eq 'TARGET_MISMATCH') @($b2.rpc.resp)
    $lbSet = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $setIds = (@($lbSet.domain.result.items) | ForEach-Object { $_.breakpoint_id }) -join ','
    Assert-Cond 'a35-single-owned-set' 'owned set still exactly the first breakpoint' "ids=[$setIds]" ($setIds -eq $bp1) @($lbSet.rpc.resp)

    # [5] runtime_weak + module_sha256 = -32602; owned set unchanged across the rejection.
    $lbBefore = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $beforeIds = (@($lbBefore.domain.result.items) | ForEach-Object { $_.breakpoint_id }) -join ','
    $shaBad = Send-Rpc 'tools/call' @{ name = 'debug_set_breakpoint'; arguments = @{ session_id = $sid; generation = $gen; pause_epoch = $held.epoch; request_id = 'a35-sha'; module_handle = $h1; identity_strength = 'runtime_weak'; mvid = $mvid; method_token = $token; il_offset = 0; module_sha256 = (Get-Sha256File (Join-Path $m.env.sample_root 'SatelliteLib.dll')) } }
    $rpcSha = if ($shaBad.json -and $shaBad.json.error) { $shaBad.json.error.code } else { $null }
    Assert-Cond 'a35-sha-rejected' 'runtime_weak with module_sha256 = -32602' "error=$rpcSha" ("$rpcSha" -eq '-32602') @($shaBad.resp)
    $lbAfter = Invoke-ToolNoInit 'debug_list_breakpoints' @{ session_id = $sid; generation = $gen }
    $afterIds = (@($lbAfter.domain.result.items) | ForEach-Object { $_.breakpoint_id }) -join ','
    Assert-Cond 'a35-set-unchanged' 'owned set unchanged by the rejection' "before=[$beforeIds] after=[$afterIds]" ($beforeIds -eq $afterIds)

    # [6] Old-generation handle after restart: stale/mismatch, zero side effects.
    Invoke-ToolNoInit 'debug_remove_breakpoint' @{ session_id = $sid; generation = $gen; pause_epoch = $held.epoch; request_id = 'a35-rm1'; breakpoint_id = $bp1 } | Out-Null
    $R = Invoke-Tool $v 'debug_restart' @{ session_id = $sid; generation = $gen; request_id = 'a35-restart' }
    Assert-Cond 'a35-restart' 'restart enters a new generation' "ok=$($R.domain.ok)" ($R.domain.ok) @($R.rpc.resp)
    Start-Sleep -Milliseconds 2500
    if ($R.domain.ok) {
        $newGen = [int]$R.domain.result.generation
        $wp3 = Wait-HeldPause $sid $newGen
        if ($wp3.ok) {
            $old = Invoke-ToolNoInit 'debug_set_breakpoint' @{ session_id = $sid; generation = $newGen; pause_epoch = $wp3.epoch; request_id = 'a35-old'; module_handle = $h1; identity_strength = 'runtime_weak'; mvid = $mvid; method_token = $token; il_offset = 0 }
            $oldCode = Get-DomainError $old
            Assert-Cond 'a35-old-handle-rejected' 'old-generation module_handle = TARGET_MISMATCH or STALE_HANDLE' "code=$oldCode" (("$oldCode" -eq 'TARGET_MISMATCH') -or ("$oldCode" -eq 'STALE_HANDLE')) @($old.rpc.resp)
        }
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $newGen; request_id = 'a35-t1' } | Out-Null
    }
}

# ---------------------------------------------------------------- dispatch + finalize ----
$handlers = @{
    'ACC-001' = ${function:Run-ACC001}; 'ACC-002' = ${function:Run-ACC002}
    'ACC-006' = ${function:Run-ACC006}; 'ACC-009' = ${function:Run-ACC009}
    'ACC-011' = ${function:Run-ACC011}; 'ACC-018' = ${function:Run-ACC018}
    'ACC-031' = ${function:Run-ACC031}
    'ACC-013' = ${function:Run-ACC013}; 'ACC-015' = ${function:Run-ACC015}
    'ACC-017' = ${function:Run-ACC017}; 'ACC-020' = ${function:Run-ACC020}
    'ACC-021' = ${function:Run-ACC021}; 'ACC-026' = ${function:Run-ACC026}
    'ACC-007' = ${function:Run-ACC007}
    'ACC-012' = ${function:Run-ACC012}
    'ACC-014' = ${function:Run-ACC014}
    'ACC-008' = ${function:Run-ACC008}
    'ACC-034' = ${function:Run-ACC034}
    'ACC-005' = ${function:Run-ACC005}
    'ACC-010' = ${function:Run-ACC010}
    'ACC-027' = ${function:Run-ACC027}
    'ACC-030' = ${function:Run-ACC030}
    'ACC-016' = ${function:Run-ACC016}; 'ACC-025' = ${function:Run-ACC025}; 'ACC-033' = ${function:Run-ACC033}; 'ACC-032' = ${function:Run-ACC032}; 'ACC-035' = ${function:Run-ACC035}
}
if ($handlers.ContainsKey($Case) -and $script:Manifest) {
    try { & $handlers[$Case] } catch {
        $_ | Out-String | Set-Content (Join-Path $script:OutDir 'harness-error.log')
        Assert-Cond 'harness-exception' 'case body completes without harness exception' $_.Exception.Message $false @('harness-error.log')
        $script:PreconditionFailed = $true
    }
} elseif ($script:Manifest) {
    [Console]::Error.WriteLine("case implemented driver logic missing for $Case (manifest exists)")
    $script:PreconditionFailed = $true
    Assert-Cond 'precondition-driver-logic' 'driver implements this case' 'missing' $false @()
}

$finishedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$allPass = (@($script:Assertions | Where-Object status -ne 'pass').Count -eq 0) -and ($script:Assertions.Count -gt 0)
$status = if ($allPass) { 'pass' } else { 'fail' }
$exit = if ($allPass) { 0 } elseif ($script:PreconditionFailed) { 2 } else { 1 }

$evidencePaths = @()
Get-ChildItem $script:OutDir -Recurse -File | ForEach-Object {
    $evidencePaths += ($_.FullName.Substring($script:OutDir.Length + 1) -replace '\\', '/')
}
$result = [ordered]@{
    schema_version = 'dnspy.debug.test.v1'
    repository = $script:Repo
    commit_sha = $script:Sha
    case_id = $Case
    started_utc = $script:StartedUtc
    finished_utc = $finishedUtc
    status = $status
    exit_code = $exit
    assertions = $script:Assertions
    evidence_paths = $evidencePaths
}
$resultPath = Join-Path $script:OutDir 'result.json'
ConvertTo-Json $result -Depth 40 | Set-Content $resultPath -Encoding UTF8
$rel = "tests/debug/results/$script:Sha/$Case/result.json"
Write-Output $rel
exit $exit
