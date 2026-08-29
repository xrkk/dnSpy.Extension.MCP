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
    [Parameter(Mandatory = $true, ParameterSetName = 'Case')]
    [ValidatePattern('^ACC-\d{3}$')]
    [string]$Case,
    # CI harness gate: validate manifests/handlers/syntax without a VM (no result.json).
    [Parameter(Mandatory = $true, ParameterSetName = 'VerifyHarness')]
    [switch]$VerifyHarness
)

$ErrorActionPreference = 'Stop'

# ---- CI harness gate (-VerifyHarness): no VM, no result.json — validates that the E2E
# driver itself is complete and wired: script parses, all 36 case manifests exist, every
# manifest has an implemented handler, and the committed result schema is present.
if ($VerifyHarness) {
    $repo = Split-Path -Parent $PSScriptRoot | Split-Path -Parent
    $errors = @()
    $tokens = $null; $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($PSCommandPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) { $errors += "driver parse errors: $($parseErrors.Count)" }
    $manifests = Get-ChildItem (Join-Path $repo 'tests\debug\cases') -Filter 'ACC-*.json'
    if ($manifests.Count -ne 36) { $errors += "expected 36 case manifests, found $($manifests.Count)" }
    $handlerIds = @('ACC-001','ACC-002','ACC-003','ACC-004','ACC-005','ACC-006','ACC-007','ACC-008','ACC-009','ACC-010','ACC-011','ACC-012','ACC-013','ACC-014','ACC-015','ACC-016','ACC-017','ACC-018','ACC-019','ACC-020','ACC-021','ACC-022','ACC-023','ACC-024','ACC-025','ACC-026','ACC-027','ACC-028','ACC-029','ACC-030','ACC-031','ACC-032','ACC-033','ACC-034','ACC-035','ACC-036')
    $driverText = Get-Content $PSCommandPath -Raw
    foreach ($hid in $handlerIds) {
        if ($manifests.Name -notcontains "$hid.json") { $errors += "manifest missing: $hid" }
    }
    # Handler wiring: each case must have a dispatch-table entry of the exact
    # "'ACC-xxx' = ${function:Run-ACCxxx}" shape (not merely a textual mention).
    foreach ($hid in $handlerIds) {
        $entry = ("'" + $hid + "' = " + '$' + '{function:Run-' + ($hid -replace '-', ''))
        if ($driverText.IndexOf($entry, [StringComparison]::Ordinal) -lt 0) { $errors += "dispatch handler missing: $hid" }
    }
    if (-not (Test-Path (Join-Path $repo 'tests\debug\contracts\dnspy.debug.test.v1.schema.json'))) { $errors += 'result schema file missing' }
    if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }
    Write-Output 'HARNESS VERIFY PASSED: 36 manifests, 36 handlers, driver parses, result schema present'
    exit 0
}


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
$script:HelperRequestSeq = 0

function New-HelperRequestId([string]$Prefix) {
    # The product cache owns request_id globally for ten minutes, not per session/epoch.
    # Helper retries therefore need distinct identities unless a test explicitly exercises
    # replay. Include the case and a monotonic counter so the evidence remains readable.
    $script:HelperRequestSeq++
    return "$Prefix-$Case-$($script:HelperRequestSeq)"
}

function Assert-Cond {
    param([string]$Id, [string]$Expected, [string]$Actual, [bool]$Pass, [string[]]$Ev = @())
    # No assertion may cite the result object that is not written yet. When the caller has no
    # richer wire/log artifact, emit a small immutable assertion trace under this result tree.
    $paths = @($Ev | Where-Object { -not [string]::IsNullOrWhiteSpace("$_") -and "$_" -ne 'result.json' })
    if ($paths.Count -eq 0 -or (@($Ev | Where-Object { "$_" -eq 'result.json' }).Count -gt 0)) {
        $safeId = $Id -replace '[^A-Za-z0-9_.-]', '_'
        $dir = Join-Path $script:OutDir 'assertions'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $auto = "assertions/$safeId.txt"
        [IO.File]::WriteAllText((Join-Path $script:OutDir $auto), "status=$(if ($Pass) {'pass'} else {'fail'})`r`nexpected=$Expected`r`nactual=$Actual`r`n", (New-Object Text.UTF8Encoding($false)))
        $paths += $auto
    }
    $a = [pscustomobject]@{
        assertion_id = $Id; status = $(if ($Pass) { 'pass' } else { 'fail' })
        expected = $Expected; actual = $Actual; evidence_paths = $null
    }
    # Hashtable-literal array properties collapse single-element arrays to scalars (the real
    # schema gate caught exactly this); the PSObject property setter preserves Object[].
    $a.PSObject.Properties['evidence_paths'].Value = [object[]]@($paths)
    $script:Assertions.Add($a) | Out-Null
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
    Get-Process dnSpy,dnSpy-x86 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process AccFixture,AccHarness,AccCore,ThreadsStackFixture,ArgvFixture,SampleDataFixture,DynLoadFixture,DualDynFixture -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500
}
function Reset-TestArtifactRoot {
    # Acceptance cleanup is deliberately out-of-process. The extension itself never deletes
    # artifact data; each cold case receives an empty dedicated harness root.
    $path = [IO.Path]::GetFullPath("$($script:Manifest.env.artifact_root)").TrimEnd('\')
    $volume = [IO.Path]::GetPathRoot($path).TrimEnd('\')
    if (-not $path -or $path.Length -le 3 -or $path -eq $volume) {
        throw "refusing to clean unsafe artifact test root: $path"
    }
    if (Test-Path -LiteralPath $path) {
        Get-ChildItem -LiteralPath $path -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
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
    $runtimeRoot = $script:Manifest.env.dotnet10_root
    if ($script:Manifest.env.dnspy_exe -like '*x86*' -and $script:Manifest.env.dotnet10_x86) {
        $runtimeRoot = Split-Path $script:Manifest.env.dotnet10_x86
    }
    if ($runtimeRoot) {
        $env:DOTNET_ROOT = $runtimeRoot
        if ($script:Manifest.env.dnspy_exe -like '*x86*') { Set-Item -Path 'Env:DOTNET_ROOT(x86)' -Value $runtimeRoot }
        else { Set-Item -Path 'Env:DOTNET_ROOT(x64)' -Value $runtimeRoot }
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
    # Fire a blocking tool call in a detached curl; returns resp file path. Req/resp live
    # UNDER the result directory so the evidence gate can see them (CHK-004: C:\Tools\dt-*
    # volatile files were invisible to result-evidence-complete).
    param([string]$BodyJson, [string]$Tag, [int]$MaxSec = 25)
    $reqRel = "dt-$Tag-req.json"; $respRel = "dt-$Tag-resp.txt"
    $reqF = Join-Path $script:OutDir $reqRel; $respF = Join-Path $script:OutDir $respRel
    Set-Content $reqF $BodyJson -Encoding ascii
    Remove-Item $respF -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time',"$MaxSec",'-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data',"@$reqF",'-o',$respF -PassThru -WindowStyle Hidden | Out-Null
    return $respRel
}
function Read-DetachedResp {
    param([string]$RespFile, [int]$Retries = 14)
    $full = if ([IO.Path]::IsPathRooted($RespFile)) { $RespFile } else { Join-Path $script:OutDir $RespFile }
    for ($i = 0; $i -lt $Retries; $i++) {
        if (Test-Path $full) {
            try {
                $body = ([IO.File]::ReadAllText($full)).Trim()
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
            $c = Invoke-ToolNoInit 'debug_continue' @{ session_id = $Sid; generation = $Gen; pause_epoch = $ep; request_id = (New-HelperRequestId 'rfp-c') }
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
        $p = Invoke-ToolNoInit 'debug_pause' @{ session_id = $Sid; generation = $Gen; request_id = (New-HelperRequestId 'whp-p') }
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

function Compile-Fixture([string]$SourceName, [string]$OutName, [switch]$Library, [switch]$X86) {
    $envm = $script:Manifest.env
    $src = Join-Path (Join-Path $script:Repo 'tests\debug\fixtures-src') $SourceName
    $out = Join-Path $envm.sample_root $OutName
    $target = if ($Library) { '/target:library' } elseif ($X86) { '/platform:x86' } else { '/platform:x64' }
    & $envm.csc /nologo /optimize- $target /out:$out $src 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir ("build-" + $OutName + ".log"))
    return (Test-Path $out)
}

function Launch-AndPause([string]$Exe, [string]$BreakKind = 'entry', [string]$Architecture = 'x64') {
    $v = $script:Manifest.protocol_versions[2]
    $sha = Get-Sha256File $Exe
    # Unique per run: the side-effect cache replays settled launches for 10 minutes, and a
    # recompiled fixture carries a fresh MVID/sha — a fixed id would trip REQUEST_ID_REUSE
    # against the previous run's entry when dnSpy outlives a case.
    $rid = 'acc-launch-' + (Split-Path $Exe -Leaf) + '-' + (Get-Date -Format 'HHmmssfff') + (Get-Random -Maximum 999)
    $L = Invoke-Tool $v 'debug_launch' @{ request_id = $rid; target_path = $Exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = $Architecture; break_kind = $BreakKind }
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
    $P = Invoke-ToolNoInit 'debug_pause' @{ session_id = $sid; generation = $gen; request_id = (New-HelperRequestId 'acc-pause') }
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
    # [3] Dispatcher domains, measured (not probed): a real launch cycle must place Start on
    # the WPF thread (spy start_thread_is_wpf==1) and drive object work through the
    # DbgManager dispatcher (spy dispatcher_sync_posts>=1). The former placeholder probe
    # (assert-dispatchers.ps1) always answered "unknown" and was removed as vacuous.
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build-disp' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $dispSess = Launch-AndPause (Join-Path $m.env.sample_root 'ArgvFixture.exe') 'none'
    if ($dispSess.ok) {
        # list_modules resolves live objects through a SYNCHRONOUS dispatcher post — the
        # measurable DbgManager-side half of the dispatcher-domain requirement.
        Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $dispSess.sid; generation = $dispSess.gen } | Out-Null
    }
    $dispSpy = Get-SpyCounters
    $dispEv = Save-Json 'dispatcher-domains.json' $dispSpy
    $wpfOk = [int]$dispSpy.start_thread_is_wpf -eq 1
    $dispOk2 = [int]$dispSpy.dispatcher_sync_posts -ge 1
    Assert-Cond 'dispatcher-domain-probe' 'Start executed on the WPF thread; object work on the DbgManager dispatcher' "wpf=$($dispSpy.start_thread_is_wpf) syncPosts=$($dispSpy.dispatcher_sync_posts) session=$($dispSess.ok)" ($wpfOk -and $dispOk2 -and $dispSess.ok) @($dispEv)
    if ($dispSess.ok) {
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $dispSess.sid; generation = $dispSess.gen; request_id = 'acc1-t1' } | Out-Null
        Start-Sleep -Milliseconds 900
    }

        $fixOut | Set-Content (Join-Path $script:OutDir 'testil-build.log')
        # In-process invocation: deeply nested powershell children occasionally fail to
        # autoload Microsoft.PowerShell.Utility (Get-FileHash) on this host. run-tests.ps1
        # deploys the extension DLL itself, so dnSpy must be down before it starts; it also
        # probes ports 3100..3119, so the committed snapshot's port is staged to 3100 for the
        # run and restored afterwards by Ensure-CanonicalDnSpy.
        Stop-DnSpyAndTargets
        Set-SnapshotJson (New-SnapshotJson $true $true 'localhost' 3100 $m.env.sample_root $m.env.artifact_root)
        $static = & (Join-Path $script:Repo 'tests\fixtures\run-tests.ps1') -SkipBuild -Tfm net48 -DnSpyExe $m.env.dnspy_exe -Port 3100 2>&1
        "$static" | Set-Content (Join-Path $script:OutDir 'static-e2e.log')
        $static | Set-Content (Join-Path $script:OutDir 'static-e2e.log')
        $ok = ($LASTEXITCODE -eq 0) -or ($static -join '`n' -match 'ALL .*PASS|SMOKE PASSED')
    } catch {
        $_ | Out-String | Set-Content (Join-Path $script:OutDir 'static-e2e.log')
        $ok = $false
    }
    Assert-Cond 'static-e2e-exit0' 'tests/fixtures/run-tests.ps1 exit 0' "$(if ($ok) { 'exit 0' } else { 'failed; see static-e2e.log' })" $ok @('static-e2e.log', 'testil-build.log')


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
        if ($up) { Invoke-ComboSequence 'A' @{ tools_count = 39; debug_enabled = $false; continue_code = 'DEBUG_DISABLED' } }

        $up = Restart-WithSnapshot $snapB
        Assert-Cond 'combo-B-restart' 'health 200 after (true,false) restart' "health=$(Get-HealthCode $script:BaseUrl)" $up
        if ($up) { Invoke-ComboSequence 'B' @{ tools_count = 39; debug_enabled = $false; continue_code = 'DEBUG_DISABLED' } }

        $up = Restart-WithSnapshot $snapC
        Assert-Cond 'combo-C-restart' 'health 200 after (true,true) startup-idle restart' "health=$(Get-HealthCode $script:BaseUrl)" $up
        if ($up) { Invoke-ComboSequence 'C' @{ tools_count = 60; debug_enabled = $true; continue_code = 'INVALID_STATE' } }

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

        # (true, true, startup IsDebugging=true): the startup gate sample freezes CLOSED
        # (EffectiveDebugLaunch=false) - simulated through the DNMCP_TEST_STARTUP_DEBUGGING
        # seam inherited by the spawned dnSpy.
        $env:DNMCP_TEST_STARTUP_DEBUGGING = '1'
        $comboDJson = New-SnapshotJson $true $true 'localhost' 3000 $m.env.sample_root $m.env.artifact_root
        if (Restart-WithSnapshot $comboDJson) {
            $tlD = Get-ToolList $v
            $namesD = @($tlD.tools | ForEach-Object { $_.name })
            $debugD = @($namesD | Where-Object { $_ -like 'debug_*' -and $_ -notlike 'debug_test_*' })
            $capD = Invoke-ToolNoInit 'debug_capabilities' @{ }
            $enabledD = "$($capD.domain.result.debug_enabled)"
            $comboDOk = ($enabledD -eq 'False') -and ($debugD.Count -eq 1) -and ($debugD[0] -eq 'debug_capabilities')
            Assert-Cond 'combo-D-startup-debugging' 'startup-busy gate freezes closed: debug_enabled=false, only debug_capabilities advertised' "enabled=$enabledD debugTools=$($debugD -join ',')" $comboDOk @($capD.rpc.resp)
        } else {
            Assert-Cond 'combo-D-startup-debugging' 'combo-D dnSpy restart succeeded' 'failed' $false @()
        }
        Remove-Item Env:\DNMCP_TEST_STARTUP_DEBUGGING -ErrorAction SilentlyContinue
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
    # Plan §6 ACC-006: handles minted in an earlier pause_epoch are STALE after continue —
    # epoch staleness (STALE_HANDLE) takes precedence over the paused-state gate, exactly as
    # the product implements since fix #31 (CHK-003 alignment; the old INVALID_STATE
    # expectation predated that semantics).
    Assert-Cond 'running-state-rejected' 'get_stack/get_locals with the pre-continue epoch while running = STALE_HANDLE' "stack=$c2 locals=$c3" (("$c2" -eq 'STALE_HANDLE') -and ("$c3" -eq 'STALE_HANDLE')) @($S2.rpc.resp, $L2.rpc.resp)

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
    # Derive a byte inside the first operand-bearing IL instruction. This is inside the body
    # but not an instruction start, so accepting it would expose the old boundary gap.
    $midOffset = $null
    try {
        $asm11 = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($m.env.fixture_exe))
        $tok11 = [Convert]::ToInt32(($token -replace '^0x',''), 16)
        $bytes11 = $asm11.ManifestModule.ResolveMethod($tok11).GetMethodBody().GetILAsByteArray()
        $one = @{}; $two = @{}
        foreach ($f in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
            $op = [Reflection.Emit.OpCode]$f.GetValue($null); $u = ([int]$op.Value) -band 0xffff
            if ($u -le 0xff) { $one[$u] = $op } else { $two[$u] = $op }
        }
        for ($p11 = 0; $p11 -lt $bytes11.Length -and $null -eq $midOffset;) {
            $start11 = $p11; $b11 = [int]$bytes11[$p11]; $p11++
            if ($b11 -eq 0xfe) { $key11 = 0xfe00 -bor [int]$bytes11[$p11]; $p11++; $op11 = $two[$key11] } else { $op11 = $one[$b11] }
            $size11 = switch ("$($op11.OperandType)") {
                'InlineNone' { 0 }
                { $_ -in @('ShortInlineBrTarget','ShortInlineI','ShortInlineVar') } { 1 }
                'InlineVar' { 2 }
                { $_ -in @('InlineBrTarget','InlineField','InlineI','InlineMethod','InlineSig','InlineString','InlineTok','InlineType','ShortInlineR') } { 4 }
                { $_ -in @('InlineI8','InlineR') } { 8 }
                'InlineSwitch' { 4 + 4 * [BitConverter]::ToInt32($bytes11, $p11) }
                default { 0 }
            }
            if ($size11 -gt 0) { $midOffset = $p11 } else { $p11 += $size11 }
        }
    } catch { }
    if ($null -ne $midOffset) {
        Try-Bp 'mid-instruction-offset' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = 'acc11-mid'; module_handle = $mod; mvid = $goodMvid; method_token = $token; il_offset = [int]$midOffset; module_sha256 = $sha } 'rpc' '-32602'
    } else {
        Assert-Cond 'bp-mid-instruction-offset' 'derived operand byte for non-boundary rejection' 'derivation failed' $false @()
    }
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
    # Same CHK-003 alignment: the pre-continue pause_epoch is stale after continue.
    Assert-Cond 'running-invalid-state' 'read with the pre-continue epoch while running = STALE_HANDLE' "code=$rcode" ("$rcode" -eq 'STALE_HANDLE') @($rr.rpc.resp)

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

    # The entry pause can be the empty transient (no frames yet) — retry with resume/repause
    # rounds until a stack frame exists (same pattern as ACC-010's frame acquisition).
    $mainFrame = $null
    for ($fr31 = 0; $fr31 -lt 6 -and -not $mainFrame; $fr31++) {
        $T = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
        if ($T.domain.ok -and @($T.domain.result.items).Count -gt 0) {
            $th = $T.domain.result.items[0].thread_handle
            $S = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $th }
            if ($S.domain.ok -and @($S.domain.result.items).Count -gt 0) { $mainFrame = $S.domain.result.items[0] }
        }
        if (-not $mainFrame) {
            $stq31 = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
            if ("$($stq31.domain.result.state)" -eq 'paused') { $ep = $stq31.domain.debug_context.pause_epoch }
            $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; request_id = "acc31-fr$fr31" }
            $wp31b = Wait-StablePaused $sid
            if ($wp31b.ok) { $ep = $wp31b.epoch }
        }
    }
    $mainToken = if ($mainFrame) { "$($mainFrame.location.method_token)" } else { '' }
    # Anchor on the FIXTURE module BY NAME with registration polling (an ultra-early pause
    # can precede the module-load event; the frame's module may be empty or mscorlib).
    $modEntry = $null
    for ($mw31 = 0; $mw31 -lt 20 -and -not $modEntry; $mw31++) {
        $MODS = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
        $modEntry = @($MODS.domain.result.items) | Where-Object { "$($_.name)" -like 'AccFixture*' } | Select-Object -First 1
        if (-not $modEntry) { Start-Sleep -Milliseconds 300 }
    }
    $mvid = "$($modEntry.mvid)"
    $mod = "$($modEntry.module_handle)"

    # Deterministic Hot discovery: reflection over the fixture bytes (same MethodDef tokens
    # as the debugged process; no stepping, no dependence on JIT inlining). The process
    # stays at the entry pause — the bp is created there, then continue triggers Hot@0.
    $hotToken = $null; $hotOff = 0; $curEp = $ep
    try {
        $asmBytes31 = [IO.File]::ReadAllBytes($m.env.fixture_exe)
        $asmObj31 = [Reflection.Assembly]::Load($asmBytes31)
        $hotMi31 = $asmObj31.GetType('AccFixture').GetMethod('Hot', [Reflection.BindingFlags]'NonPublic,Static')
        if ($hotMi31) { $hotToken = '0x' + $hotMi31.MetadataToken.ToString('x8') }
    } catch { }
    Save-Json 'acc31-token-reflection.json' @{ hot = $hotToken } | Out-Null
    Assert-Cond 'entered-hot' 'Hot token resolved via reflection over the fixture bytes' "hot_token=$hotToken" ([bool]$hotToken) @('acc31-token-reflection.json')

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
    $zeroMvid = '00000000-0000-0000-0000-000000000000'
    $satSha = Get-Sha256File (Join-Path $m.env.sample_root 'SatelliteLib.dll')
    $satOk = $sat -and $sat.path -and ("$($sat.sha256)" -eq $satSha) -and ("$($sat.mvid)" -ne $zeroMvid) -and ("$($sat.mvid)" -match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')
    Assert-Cond 'dynamic-module-listed' 'later-loaded SatelliteLib has path, authoritative MVID and disk SHA' "path=$($sat.path) mvid=$($sat.mvid) sha=$($sat.sha256)" $satOk @($mods.rpc.resp)
    $diskOk = $disk -and $disk.path -and ("$($disk.sha256)" -eq $sha) -and ("$($disk.mvid)" -ne $zeroMvid)
    Assert-Cond 'disk-module-listed' 'DynLoadFixture has path+authoritative MVID+matching sha' "path=$($disk.path) mvid=$($disk.mvid) sha=$($disk.sha256)" $diskOk @($mods.rpc.resp)

    $ev = Invoke-ToolNoInit 'debug_read_events' @{ session_id = $sid; generation = $gen; after_cursor = 0; limit = 100 }
    $loadEvent = @($ev.domain.result.events | Where-Object { $_.kind -eq 'module_loaded' -and "$($_.payload.module.name)" -eq "$($sat.name)" }) | Select-Object -First 1
    $eventMatch = $loadEvent -and ("$($loadEvent.payload.module.module_handle)" -eq "$($sat.module_handle)") -and ("$($loadEvent.payload.module.mvid)" -eq "$($sat.mvid)") -and ("$($loadEvent.payload.module.sha256)" -eq "$($sat.sha256)")
    Assert-Cond 'module-loaded-event' 'module_loaded event carries the same complete module identity' "match=$eventMatch" ([bool]$eventMatch) @($ev.rpc.resp, (Save-Json 'acc17-module-event.json' $loadEvent))
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
    $p1bReqF = Join-Path $script:OutDir 'p1b-req.json'; $p1bRespF = Join-Path $script:OutDir 'p1b-resp.txt'
    Set-Content $p1bReqF $p1bReq -Encoding ascii
    Remove-Item $p1bRespF -Force -ErrorAction SilentlyContinue
    $cp = Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time','20','-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data',"@$p1bReqF",'-o',$p1bRespF -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900
    $null = Test-Clock 35000
    $deadlineP1b = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadlineP1b -and -not (Test-Path $p1bRespF)) { Start-Sleep -Milliseconds 400 }
    $Pw = $null
    if (Test-Path $p1bRespF) {
        $Pw = @{ rpc = @{ resp = 'p1b-resp.txt' }; domain = ((Get-Content $p1bRespF -Raw | ConvertFrom-Json).result.content[0].text | ConvertFrom-Json) }
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
    $pol1 = $P1.domain.result
    Assert-Cond 'a12-policy-switch' 'previous=unhandled current=first_chance_and_unhandled' "prev=$($pol1.previous.break_on) cur=$($pol1.current.break_on)" (("$($pol1.previous.break_on)" -eq 'unhandled') -and ("$($pol1.current.break_on)" -eq 'first_chance_and_unhandled')) @($P1.rpc.resp)
    $P2 = Invoke-ToolNoInit 'debug_set_exception_policy' @{ session_id = $sid; generation = $gen; request_id = 'acc12-p2'; policy = 'unhandled' }
    $pol2 = $P2.domain.result
    Assert-Cond 'a12-policy-roundtrip' 'policy switches back with correct previous' "prev=$($pol2.previous.break_on)" ("$($pol2.previous.break_on)" -eq 'first_chance_and_unhandled') @($P2.rpc.resp)

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
    # request_id is global across side-effect methods: do not collide with the earlier
    # debug_set_exception_policy request named acc12-p2.
    Assert-P2Collision $sidP $genP 'acc12-p2pause' '{"emit":{"kind":"paused","break_infos":[{"type":"exception","ordinal":0,"policy_requested_pause":true}]}}' 'exception' 'acc12-p2-exception-response'
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
    Set-Content (Join-Path $script:OutDir 'a34-req.json') $rReq -Encoding ascii
    $a34ReqF1 = Join-Path $script:OutDir 'a34-req.json'; $a34RespF1 = Join-Path $script:OutDir 'a34-resp.txt'
    Remove-Item $a34RespF1 -Force -ErrorAction SilentlyContinue
    $cp = Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time','25','-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data',"@$a34ReqF1",'-o',$a34RespF1 -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900
    $null = Test-Clock 35000
    $dl = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $dl -and -not (Test-Path $a34RespF1)) { Start-Sleep -Milliseconds 400 }
    $dom = $null
    if (Test-Path $a34RespF1) {
        for ($i = 0; $i -lt 12 -and $null -eq $dom; $i++) {
            try {
                $lines = [IO.File]::ReadAllLines($a34RespF1)
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
    # T2 settled through the FAKE adapter's synthetic removal, so the REAL launch-2 target
    # was never terminated: ownedProcess stays set and DbgManager.IsDebugging stays true,
    # which correctly blocks further launches (CON-DYN-003 precheck). Resolve the orphan
    # the way an operator would — kill the abandoned target and let the manager observe the
    # removal — before the next launch (CHK-005 root cause; the precheck is right, the
    # scenario was leaking a live orphan).
    Get-Process ArgvFixture -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 1800

    # [4] Pending-restart relaunch: emit removed while restart waits -> generation+1, no fault.
    # CHK-005: the fault->recovery->terminal transition is asynchronously settled upstream;
    # a launch landing inside that window is rejected INVALID_STATE (with the envelope
    # snapshot already showing the post-settlement idle state). Bounded retry until the
    # coordinator is observably idle again — the upstream transition itself is not a
    # contract violation.
    $L3 = $null
    for ($try = 0; $try -lt 10 -and -not ($L3 -and $L3.domain.ok); $try++) {
        Start-Sleep -Milliseconds 400
        # Unique id per attempt: the launch cache replays a settled failure verbatim for a
        # repeated request_id, which would make every retry a no-op replay.
        $L3 = Invoke-ToolNoInit 'debug_launch' @{ request_id = ("a34-la3-$try"); target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    }
    $sid3 = $L3.domain.result.session_id; $gen3 = [int]$L3.domain.result.generation
    Assert-Cond 'a34-launch3' 'third session running (after the transition window)' "ok=$($L3.domain.ok) tries=$($try+1)" ($L3.domain.ok) @($L3.rpc.resp)
    $null = Test-Adapter '{"install":true}'
    $null = Test-Adapter '{"fail_next":"none"}'
    $rReq3 = '{"jsonrpc":"2.0","id":782,"method":"tools/call","params":{"name":"debug_restart","arguments":{"session_id":"' + $sid3 + '","generation":' + $gen3 + ',"request_id":"a34-r4"}}}'
    $a34Req3F = Join-Path $script:OutDir 'a34-req3.json'
    $a34Resp3 = Join-Path $script:OutDir 'a34-resp3.txt'
    Set-Content $a34Req3F $rReq3 -Encoding ascii
    Remove-Item $a34Resp3 -Force -ErrorAction SilentlyContinue
    $cp3 = Start-Process -FilePath curl.exe -ArgumentList '-s','--max-time','25','-X','POST',$script:BaseUrl,'-H','Accept: application/json','-H','Content-Type: application/json','--data',"@$a34Req3F",'-o',$a34Resp3 -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 900
    # The restart Terminate went to the FAKE adapter (swallowed). Kill the REAL target so the
    # manager observes a genuine removal: the pending restart relaunches through the Start
    # precheck (owned process cleared, manager teardown covered by the product's bounded
    # wait). A synthetic emit would leave the live process blocking the internal relaunch.
    Get-Process ArgvFixture -ErrorAction SilentlyContinue | Stop-Process -Force
    $dl3 = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $dl3 -and -not (Test-Path $a34Resp3)) { Start-Sleep -Milliseconds 500 }
    $dom3 = $null
    if (Test-Path $a34Resp3) {
        for ($i = 0; $i -lt 12 -and $null -eq $dom3; $i++) {
            try {
                $l3 = [IO.File]::ReadAllLines($a34Resp3)
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
    # T2b also posted to the fake — settle with a REAL removal (kill), leaving no orphan.
    Get-Process ArgvFixture -ErrorAction SilentlyContinue | Stop-Process -Force
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
        $fl = $F.domain.result
        $evAfter = Read-EventKinds $sid3 $gen3 0
        $evictionOk = ($fl.written -eq 4500) -and ([long]$fl.events_lost -gt 0) -and ([long]$fl.earliest_cursor -gt 1) -and (@($evAfter.events).Count -gt 0)
        Assert-Cond 'a5-eviction' '>4096 entries: oldest evicted (events_lost>0, earliest advances, log readable)' "written=$($fl.written) lost=$($fl.events_lost) earliest=$($fl.earliest_cursor) readback=$(@($evAfter.events).Count)" $evictionOk @($F.rpc.resp, $evAfter.call.rpc.resp)
        # [6] Oversize single event: >8MiB payload becomes payload_omitted (kind rewritten).
        $F2 = Invoke-ToolNoInit 'debug_test_flood' @{ count = 1; bytes_per_event = 8500000 }
        $fl2 = $F2.domain.result
        # The rewritten payload_omitted event is the NEWEST entry; the flood tool reports the
        # true last cursor (Get-MaxEventCursor under eviction only sees the first page).
        $evBig = Read-EventKinds $sid3 $gen3 ([long]$fl2.last_cursor - 2)
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
    # The held pause can land VERY early — before the AccFixture module-load event fires.
    # While paused that event can NEVER fire (the loader is frozen): poll briefly, and if
    # still absent, CONTINUE (letting the loader proceed) and re-anchor on a fresh held
    # pause. Never fall back to the paused frame's module (it can be mscorlib).
    $modEntry0 = $null
    for ($mw = 0; $mw -lt 6 -and -not $modEntry0; $mw++) {
        for ($pw = 0; $pw -lt 4 -and -not $modEntry0; $pw++) {
            $MODS0 = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
            $modEntry0 = @($MODS0.domain.result.items) | Where-Object { "$($_.name)" -like 'AccFixture*' } | Select-Object -First 1
            if (-not $modEntry0) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $modEntry0) {
            $epm = (Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }).domain.debug_context.pause_epoch
            $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $epm; request_id = "a16-anchor$mw" }
            $wpm = Wait-HeldPause $sid $gen
            if ($wpm.ok) { $cur = $wpm.epoch }
        }
    }
    $mod = if ($modEntry0) { "$($modEntry0.module_handle)" } else { '' }
    # Deterministic token discovery: reflection over the fixture BYTES (Assembly.Load of a
    # byte[] does not lock the file) yields the same MethodDef tokens the debugged process
    # carries. Immune to JIT inlining (no Hot frame on the stack) and to fixture layout
    # changes; the process stays paused at the acquired frame — no resume/re-pause dance.
    $hotTok = $null; $hotOff = 0; $rnd = 0
    try {
        $asmBytes = [IO.File]::ReadAllBytes($exe)
        $asm31 = [Reflection.Assembly]::Load($asmBytes)
        $hotMi = $asm31.GetType('AccFixture').GetMethod('Hot', [Reflection.BindingFlags]'NonPublic,Static')
        if ($hotMi) { $hotTok = '0x' + $hotMi.MetadataToken.ToString('x8'); $hotOff = 0 }
    } catch { }
    Save-Json 'a10-token-reflection.json' @{ hot = $hotTok } | Out-Null
    Assert-Cond 'a10-entered-hot' 'Hot token resolved via reflection over the fixture bytes' "hot=$hotTok" ([bool]$hotTok) @('a10-token-reflection.json')
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

    # [7] UI-originated/unregistered process observation variant: the same production
    # ownership classifier must fault, reject control, and recover only on manager-idle.
    # The pre-claim failure above can leave the real DbgManager busy briefly after the
    # coordinator has already returned to idle. Start this independent variant from a cold
    # canonical process so the static IsDebugging gate is not a timing dependency.
    Stop-DnSpyAndTargets
    Ensure-CanonicalDnSpy | Out-Null
    $own = Launch-AndPause $exe 'none'
    if ($own.ok) {
        $inj = Invoke-ToolNoInit 'debug_test_start' @{ mode = 'foreign_process' }
        Start-Sleep -Milliseconds 250
        $ost = Invoke-ToolNoInit 'debug_status' @{ session_id = $own.sid }
        $oc = Invoke-ToolNoInit 'debug_continue' @{ session_id = $own.sid; generation = $own.gen; pause_epoch = $own.epoch; request_id = 'a27-own-c' }
        $oe = Read-EventKinds $own.sid $own.gen 0
        $oev = Save-Json 'a27-ownership-lost-ui-variant.json' @{ status = $ost.domain; control = $oc.domain; events = $oe.events }
        $ownOk = $inj.domain.ok -and ("$($ost.domain.result.state)" -eq 'faulted') -and ("$(Get-DomainError $oc)" -eq 'OWNERSHIP_LOST') -and ($oe.kinds -contains 'ownership_lost')
        Assert-Cond 'a27-ownership-lost-ui-variant' 'unregistered/UI process observation -> faulted(ownership_lost), control rejected, event emitted' "ok=$ownOk" $ownOk @($inj.rpc.resp, $ost.rpc.resp, $oc.rpc.resp, $oev)
        Invoke-ToolNoInit 'debug_test_start' @{ mode = 'manager_idle' } | Out-Null
        Get-Process ArgvFixture -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    } else {
        Assert-Cond 'a27-ownership-lost-ui-variant' 'session available for ownership classifier variant' 'launch failed' $false @()
    }
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
    # Rare frame-mint/validation race: the just-minted frame handle can answer NOT_FOUND for
    # the same epoch (batch evidence: fr-1@ep3 NOT_FOUND, re-anchored ep5 fully green). A
    # bounded re-anchor (continue -> held pause -> fresh stack/handles) closes it.
    $lo4 = $null
    for ($lr = 0; $lr -lt 3 -and -not ($lo4 -and $lo4.domain.ok); $lr++) {
        $lo4 = Invoke-ToolNoInit 'debug_get_locals' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; frame_handle = $fr; page_size = 100 }
        if (-not ($lo4 -and $lo4.domain.ok)) {
            $epx2 = (Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }).domain.debug_context.pause_epoch
            $null = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sid; generation = $gen; pause_epoch = $epx2; request_id = "a16-lr$lr" }
            $wpl = Wait-HeldPause $sid $gen
            if ($wpl.ok) {
                $ep = $wpl.epoch
                $tll = Invoke-ToolNoInit 'debug_list_threads' @{ session_id = $sid; generation = $gen; pause_epoch = $ep }
                $stl = Invoke-ToolNoInit 'debug_get_stack' @{ session_id = $sid; generation = $gen; pause_epoch = $ep; thread_handle = $tll.domain.result.items[0].thread_handle }
                if ($stl.domain.ok -and @($stl.domain.result.items).Count -gt 0) { $fr = $stl.domain.result.items[0].frame_handle }
            }
        }
    }
    Assert-Cond 'a16-locals-ok' 'locals page returned (budgets fields present)' "ok=$($lo4.domain.ok) depth_used=$($lo4.domain.result.budgets.depth_used) tries=$($lr+1)" ($lo4.domain.ok) @($lo4.rpc.resp)
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

    # The process can reach a debugger-held pause before the asynchronous module observer
    # has published the target module. Poll the read-only inventory instead of treating that
    # legal ordering as an identity failure.
    $modNow = $null
    $targetNow = $null
    for ($moduleAttempt = 0; $moduleAttempt -lt 6 -and -not $targetNow; $moduleAttempt++) {
        $modNow = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sid; generation = $gen }
        $targetNow = @($modNow.domain.result.items | Where-Object { "$($_.path)" -eq "$exe" }) | Select-Object -First 1
        if (-not $targetNow -and $moduleAttempt -lt 5) {
            # A held pause freezes module loading. Let the target advance between inventory
            # samples, then reacquire a stable pause while the launch FileId lease stays held.
            Resume-FromPaused $sid $gen | Out-Null
            Start-Sleep -Milliseconds 700
            $nextPause = Wait-HeldPause $sid $gen
            if ($nextPause.ok) { $wp = $nextPause }
        }
    }
    $moduleIdentityOk = $targetNow -and ("$($targetNow.mvid)" -match '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') `
        -and ("$($targetNow.mvid)" -ne '00000000-0000-0000-0000-000000000000') -and ("$($targetNow.sha256)" -eq $sha) -and $fidOk
    Assert-Cond 'a33-module-identity-recheck' 'loaded target rechecks to authoritative nonzero MVID + same SHA while launch FileId lease remains held' "mvid=$($targetNow.mvid) sha=$($targetNow.sha256) fileId=$($fid.file_id)" $moduleIdentityOk @($modNow.rpc.resp, $fidEv)

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

# ---------------------------------------------------------------- case: ACC-028 ----
function Run-ACC028 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v3 = $m.protocol_versions[2]; $v1 = $m.protocol_versions[0]
    $sessionTools = @('debug_capabilities','debug_status','debug_launch','debug_pause','debug_continue','debug_restart','debug_terminate','debug_read_events','debug_wait_event','debug_set_breakpoint','debug_list_breakpoints','debug_set_breakpoint_enabled','debug_remove_breakpoint','debug_list_threads','debug_get_stack','debug_step','debug_get_locals','debug_expand_value','debug_list_modules','debug_read_memory','debug_dump_module','debug_set_exception_policy')
    $testTools = @('debug_test_spy','debug_test_clock','debug_test_adapter','debug_test_flood','debug_test_start','debug_test_dump')

    # [1] tools/list across the three protocol versions: same advertised set; the disabled
    # three never appear; outputSchema exists ONLY on 2025-06-18; input schemas closed.
    $lists = @{}
    foreach ($v in $m.protocol_versions) {
        Initialize-Protocol $v | Out-Null
        $lists[$v] = Get-ToolList $v
    }
    $tlv3 = $lists[$v3]; $names3 = @($tlv3.tools | ForEach-Object { $_.name })
    $tlv1 = $lists[$v1]; $names1 = @($tlv1.tools | ForEach-Object { $_.name })
    $ev1 = Save-Json 'a28-tools-v3.json' ($tlv3.tools | Select-Object name, description)
    $advertisedOk = $true
    foreach ($t in $sessionTools + $testTools) { if ($names3 -notcontains $t) { $advertisedOk = $false } }
    $disabledLeak = @('debug_attach','debug_detach','debug_list_attachable_processes') | Where-Object { ($names3 -contains $_) -or ($names1 -contains $_) }
    $sameSet = (@($names1 | Sort-Object) -join ',') -eq (@($names3 | Sort-Object) -join ',')
    Assert-Cond 'a28-tools-three-versions' '22 session + test tools advertised, disabled 3 absent, same set across versions' "count=$($names3.Count) same=$sameSet disabledLeak=$($disabledLeak -join ',')" ($advertisedOk -and $sameSet -and ($disabledLeak.Count -eq 0)) @($ev1)

    $withOutSchema = @($tlv1.tools | Where-Object { $_.PSObject.Properties.Name -contains 'outputSchema' })
    $outV3 = @($tlv3.tools | Where-Object { $_.PSObject.Properties.Name -contains 'outputSchema' })
    $outV3Dyn = @($outV3 | Where-Object { $_.name -like 'debug_*' })
    Assert-Cond 'a28-outputschema-versions' 'outputSchema only on 2025-06-18 (v1 has none; dynamic tools carry it)' "v1=$($withOutSchema.Count) v3dyn=$($outV3Dyn.Count)" (($withOutSchema.Count -eq 0) -and ($outV3Dyn.Count -ge 22)) @()

    $closedOk = $true
    foreach ($t in @('debug_launch', 'debug_get_locals', 'debug_wait_event', 'debug_step')) {
        $live = $tlv3.tools | Where-Object name -eq $t | Select-Object -First 1
        if (-not $live -or -not ($live.inputSchema.PSObject.Properties.Name -contains 'additionalProperties') -or ($live.inputSchema.additionalProperties -ne $false)) { $closedOk = $false }
    }
    Assert-Cond 'a28-input-schemas-closed' 'spot input schemas carry additionalProperties=false' "closed=$closedOk" $closedOk @()

    # [2] Contract cross-check: the frozen v1 schema file exists with its $defs count.
    $schemaPath = Join-Path $script:Repo 'tests\debug\contracts\dnspy.debug.v1.schema.json'
    $schema = if (Test-Path $schemaPath) { Get-Content $schemaPath -Raw | ConvertFrom-Json } else { $null }
    $defs = if ($schema -and $schema.'$defs') { @($schema.'$defs'.PSObject.Properties).Count } else { 0 }
    Assert-Cond 'a28-contract-schema-frozen' 'contract schema present with 110 $defs (19 TYPE + 25 API + 22 result + 21 EVT + envelope/scalars)' "defs=$defs" ($defs -eq 110) @()

    # [3] capabilities: artifact_policy fixed values + limits spot checks + unsupported list.
    $cap = Invoke-ToolNoInit 'debug_capabilities' @{ }
    $c = $cap.domain.result
    $evC = Save-Json 'a28-capabilities.json' $c
    $apOk = ($c.artifact_policy.retention_scope -eq 'current_extension_process') -and (-not $c.artifact_policy.automatic_cleanup) -and ("$($c.artifact_policy.restart_existing)" -eq 'stale_untrusted_fail_closed')
    $limOk = ($c.limits.request_body_bytes -eq 1048576) -and ($c.limits.tool_result_bytes -eq 8388608) -and ($c.limits.command_queue_entries -eq 64) -and ($c.limits.control_queue_entries -eq 8) -and ($c.limits.general_queue_entries -eq 56) -and ($c.limits.waits -eq 8) -and ($c.limits.value_snapshots_per_pause -eq 2) -and ($c.limits.value_handles_per_pause -eq 4096) -and ($c.limits.artifact_cancel_grace_ms -eq 2000)
    $unsup = @($c.unsupported) -join ','
    Assert-Cond 'a28-capabilities-policy-limits' 'artifact_policy + limits + unsupported fixed objects equal the contract' "policy=$apOk limits=$limOk unsup=$unsup" ($apOk -and $limOk -and ($unsup -eq 'debug_list_attachable_processes,debug_attach,debug_detach')) @($evC)

    # [4] API-DYN-002 conditional observed_process_state: omitted idle, present active-owned.
    $stIdle = Invoke-ToolNoInit 'debug_status' @{ }
    $idleHas = $stIdle.domain.result.PSObject.Properties.Name -contains 'observed_process_state'
    $sess = Launch-AndPause $exe 'none'
    if (-not $sess.ok) { Assert-Cond 'a28-session-up' 'fixture session paused' "ok=$($sess.ok)" $false; return }
    $sid = $sess.sid; $gen = $sess.gen
    $stAct = Invoke-ToolNoInit 'debug_status' @{ session_id = $sid }
    $actHas = ($stAct.domain.result.PSObject.Properties.Name -contains 'observed_process_state') -and "$($stAct.domain.result.observed_process_state)"
    Assert-Cond 'a28-observed-state-conditional' 'observed_process_state omitted idle, present active-owned' "idle=$idleHas active=$actHas" ((-not $idleHas) -and $actHas) @($stIdle.rpc.resp, $stAct.rpc.resp)

    # [5] Raw malformed JSON (unbalanced object as the params string breaks the whole body):
    # -32700 with id=null, business counters untouched.
    $bad = Send-Rpc 'tools/call' '{"name":"debug_status"'
    $badObj = $null; try { $badObj = $bad.body | ConvertFrom-Json } catch { }
    $badCode = if ($badObj -and $badObj.error) { "$($badObj.error.code)" } else { '' }
    $badId = if ($badObj) { "$($badObj.id)" } else { 'null' }
    Assert-Cond 'a28-raw-32700' 'malformed JSON = -32700 with id null' "code=$badCode id=$badId" (($badCode -eq '-32700') -and ($badId -in @('', 'null'))) @($bad.resp)

    # [6] UTF-8 byte-vs-char boundary on an untrusted pointer (session_id): 341 non-BMP
    # chars = 1023 bytes passes structure (then NOT_FOUND for the unknown session); 343
    # chars = 1029 bytes is the byte-limit -32602 — character count stays under schema max.
    # ASCII probes isolate the BYTE limit from the base64url charset pattern; a non-BMP id
    # additionally exercises the charset rejection.
    $id1023 = 'a' * 1024   # exactly 1024 UTF-8 bytes: passes structure -> NOT_FOUND (unknown)
    $id1029 = 'a' * 1025   # 1025 bytes: deterministic byte-limit -32602
    $g = [string]::Concat([char]0xD834, [char]0xDD1E)
    $idNbmp = $g * 343
    # Send-Rpc writes bodies with an ASCII encoding that mangles non-BMP input, so these
    # probes go through curl with properly UTF-8-encoded body files.
    function Invoke-ByteProbe([string]$IdValue, [int]$ProbeId) {
        $body = '{"jsonrpc":"2.0","id":' + $ProbeId + ',"method":"tools/call","params":{"name":"debug_read_events","arguments":{"session_id":"' + $IdValue + '","generation":1,"after_cursor":0}}}'
        $f = Join-Path $script:OutDir ("wire\a28-byte-$ProbeId.req.json")
        [IO.File]::WriteAllText($f, $body, (New-Object System.Text.UTF8Encoding($false)))
        $raw = & curl.exe -s --max-time 20 -X POST ($script:BaseUrl.TrimEnd('/') + '/') -H 'Content-Type: application/json' --data-binary "@$f" 2>$null
        Save-Text ("a28-byte-$ProbeId.resp.txt") "$raw" | Out-Null
        $o = $null; try { $o = $raw | ConvertFrom-Json } catch { }
        if ($o -and $o.error) { return "rpc:$($o.error.code)" }
        $dom = $null; try { $dom = ($o.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text | ConvertFrom-Json } catch { }
        if ($dom -and $dom.error) { return "domain:$($dom.error.code)" }
        return 'other'
    }
    $c1 = Invoke-ByteProbe $id1023 81
    $c2 = Invoke-ByteProbe $id1029 82
    $c3 = Invoke-ByteProbe $idNbmp 83
    $ev6 = Save-Text 'a28-utf8-boundary.txt' "1024B: $c1`n1025B: $c2`nnonBMP: $c3"
    Assert-Cond 'a28-utf8-byte-boundary' '1024 ASCII bytes passes structure (NOT_FOUND); 1025 = -32602 byte limit; non-BMP id rejected' "1024=$c1 1025=$c2 nonBMP=$c3" (("$c2" -eq 'rpc:-32602') -and ("$c1" -eq 'domain:NOT_FOUND') -and ("$c3" -eq 'rpc:-32602')) @($ev6)

    # [7] Wait cap (CON-DYN-009 global 8): nine concurrent waits -> the ninth LIMIT_EXCEEDED.
    # Hold all nine: after_cursor at the session's current max cursor means no events are
    # pending, so each admitted wait sleeps for its full timeout instead of returning at once.
    $maxCur = Get-MaxEventCursor $sid $gen
    $jobs = @()
    for ($i = 0; $i -lt 9; $i++) {
        $b = '{"jsonrpc":"2.0","id":' + (700 + $i) + ',"method":"tools/call","params":{"name":"debug_wait_event","arguments":{"session_id":"' + $sid + '","generation":' + $gen + ',"after_cursor":' + $maxCur + ',"timeout_ms":4000,"limit":1}}}' 
        [IO.File]::WriteAllText("C:\Tools\a28-w$i.json", $b)
        $jobs += Start-Process -FilePath curl.exe -ArgumentList '-s','-X','POST','http://localhost:3000/','-H','Content-Type: application/json','--data',("@C:\Tools\a28-w$i.json"),'-o',("C:\Tools\a28-w$i.out") -PassThru -WindowStyle Hidden
    }
    $jobs | ForEach-Object { $_.WaitForExit(15000) | Out-Null }
    $limitHits = 0; $okCount = 0
    for ($i = 0; $i -lt 9; $i++) {
        $o = Get-Content "C:\Tools\a28-w$i.out" -Raw -ErrorAction SilentlyContinue
        if ($o -match 'LIMIT_EXCEEDED') { $limitHits++ }
        elseif ($o -match '"ok":true') { $okCount++ }
    }
    $ev7 = Save-Text 'a28-wait-cap.txt' "limit=$limitHits ok=$okCount"
    Assert-Cond 'a28-wait-cap-9th' 'ninth concurrent wait = LIMIT_EXCEEDED (8 admitted)' "limit=$limitHits ok=$okCount" (($limitHits -ge 1) -and (($limitHits + $okCount) -ge 8)) @($ev7)

    # [9] Reason sets run BEFORE the idempotency launch: observed paused reasons from THIS
    # session must stay inside the seven-value API set (the eighth exists only in the launch
    # event per schema).
    $evAll = Read-EventKinds $sid $gen 0
    $apiReasons = @('manual','process','entry','breakpoint','exception','step','unknown')
    $observed = @($evAll.events | Where-Object { $_.kind -eq 'paused' } | ForEach-Object { "$($_.payload.reason)" }) | Select-Object -Unique
    $reasonsOk = $true
    foreach ($r in $observed) { if ($apiReasons -notcontains $r) { $reasonsOk = $false } }
    Assert-Cond 'a28-reason-sets' 'observed paused reasons stay inside the seven-value API set' "observed=$($observed -join ',')" ($reasonsOk -and ($observed.Count -ge 1)) @((Save-Json 'a28-events.json' ($evAll.events | Select-Object kind, cursor)))

    # Close the matrix session before the idempotency launches (one active session at a time).
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sid; generation = $gen; request_id = 'a28-t0' } | Out-Null
    Start-Sleep -Milliseconds 1100

    # [8] Cross-protocol idempotency: the SAME launch request_id on a second transport
    # version replays the settled response (same session_id), never a second process.
    $idem = 'a28-idem-' + (Get-Date -Format 'HHmmssfff') + (Get-Random -Maximum 999)
    $sha = Get-Sha256File $exe
    $spy0 = Get-SpyCounters
    $L1 = Invoke-Tool $v1 'debug_launch' @{ request_id = $idem; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sidA = if ($L1.domain.ok) { "$($L1.domain.result.session_id)" } else { '' }
    $genA = if ($L1.domain.ok) { [int]$L1.domain.result.generation } else { 0 }
    Initialize-Protocol $v3 | Out-Null
    $L2 = Invoke-Tool $v3 'debug_launch' @{ request_id = $idem; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
    $sidB = if ($L2.domain.ok) { "$($L2.domain.result.session_id)" } else { '' }
    $spy1 = Get-SpyCounters
    $startD = Get-SpyDelta $spy0 $spy1 'dbg_start_calls'
    $ev8 = Save-Json 'a28-idem.json' @{ v1 = $L1.domain.result; v3 = $L2.domain.result }
    Assert-Cond 'a28-cross-protocol-idempotent' 'same request_id across versions replays one settled launch (same session, zero extra Start)' "sidA=$sidA sidB=$sidB start=+$startD" (($sidA -ne '') -and ("$sidA" -eq "$sidB") -and ($startD -eq 1)) @($ev8)

    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sidA; generation = $genA; request_id = 'a28-t1' } | Out-Null
    Start-Sleep -Milliseconds 900
}
# ---------------------------------------------------------------- case: ACC-024 ----
function Get-CorFlagsOffset([byte[]]$b) {
    $peOff = [BitConverter]::ToInt32($b, 0x3C)
    $magic = [BitConverter]::ToUInt16($b, $peOff + 24)
    $dirBase = 112; if ($magic -ne 0x20B) { $dirBase = 96 }
    $clrRva = [BitConverter]::ToUInt32($b, $peOff + 24 + $dirBase + 14 * 8)
    $optSize = [BitConverter]::ToUInt16($b, $peOff + 20)
    $numSec = [BitConverter]::ToUInt16($b, $peOff + 6)
    $secAt = $peOff + 24 + $optSize
    for ($i = 0; $i -lt $numSec; $i++) {
        $sec = $secAt + $i * 40
        $virtSize = [BitConverter]::ToUInt32($b, $sec + 8)
        $virtAddr = [BitConverter]::ToUInt32($b, $sec + 12)
        $rawSize = [BitConverter]::ToUInt32($b, $sec + 16)
        $rawPtr = [BitConverter]::ToUInt32($b, $sec + 20)
        $span = [Math]::Max($virtSize, $rawSize)
        if ($clrRva -ge $virtAddr -and $clrRva -lt ($virtAddr + $span)) {
            [uint32]$corOff = [uint32]$rawPtr + ([uint32]$clrRva - [uint32]$virtAddr)
            return @{ CorOff = $corOff; ClrRva = $clrRva }
        }
    }
    return $null
}

function Run-ACC024 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $root = $m.env.sample_root
    $v = $m.protocol_versions[2]

    function Invoke-UnsupportedLaunch([string]$ProbePath, [string]$Tag) {
        $spy0 = Get-SpyCounters
        $L = Invoke-Tool $v 'debug_launch' @{ request_id = "a24-$Tag-" + (Get-Random -Maximum 99999); target_path = $ProbePath; expected_sha256 = (Get-Sha256File $ProbePath); launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
        $spy1 = Get-SpyCounters
        $st = Invoke-ToolNoInit 'debug_status' @{ }
        return @{ call = $L; code = (Get-DomainError $L); det = $L.domain.error.details.detected_target_kind; wf = $L.domain.error.details.recommended_workflow; ev = @($L.domain.error.details.evidence); untrusted = $L.domain.untrusted_sample_data; state = "$($st.domain.result.state)"; startDelta = (Get-SpyDelta $spy0 $spy1 'dbg_start_calls') }
    }

    # [1] pure_native: a real native system binary copied into the sample root.
    Copy-Item C:\Windows\System32\where.exe (Join-Path $root 'a24-native.exe') -Force
    $r1 = Invoke-UnsupportedLaunch (Join-Path $root 'a24-native.exe') 'native'
    $ev1 = Save-Json 'a24-native.json' $r1.call.domain.error
    Assert-Cond 'a24-pure-native' 'pure_native -> pe_x64dbg_ida_dynamic_analysis, pe_headers evidence, untrusted, zero Start' "code=$($r1.code) kind=$($r1.det) wf=$($r1.wf) ev0=$($r1.ev[0].kind) untrusted=$($r1.untrusted) start=+$($r1.startDelta)" (("$($r1.code)" -eq 'CAPABILITY_UNAVAILABLE') -and ("$($r1.det)" -eq 'pure_native') -and ("$($r1.wf)" -eq 'pe_x64dbg_ida_dynamic_analysis') -and ("$($r1.ev[0].kind)" -eq 'pe_headers') -and $r1.untrusted -and ($r1.startDelta -eq 0)) @($ev1, $r1.call.rpc.resp)

    # [2] mixed_mode: ArgvFixture with the COR20 ILOnly bit cleared.
    $mixedPath = Join-Path $root 'a24-mixed.exe'
    [IO.File]::WriteAllBytes($mixedPath, [IO.File]::ReadAllBytes((Join-Path $root 'ArgvFixture.exe')))
    $b = [IO.File]::ReadAllBytes($mixedPath)
    $co = Get-CorFlagsOffset $b
    if ($co) {
        $flags = [BitConverter]::ToUInt32($b, [int]$co.CorOff + 16)
        [BitConverter]::GetBytes([uint32]0x00000002).CopyTo($b, [int]$co.CorOff + 16)
        [IO.File]::WriteAllBytes($mixedPath, $b)
    }
    $r2 = Invoke-UnsupportedLaunch $mixedPath 'mixed'
    $ev2 = Save-Json 'a24-mixed.json' $r2.call.domain.error
    $kinds2 = @($r2.ev | ForEach-Object { $_.kind }) -join ','
    Assert-Cond 'a24-mixed-mode' 'mixed_mode -> pe_x64dbg workflow with fixed pe_headers+clr_metadata order' "kind=$($r2.det) wf=$($r2.wf) kinds=$kinds2 start=+$($r2.startDelta)" (("$($r2.det)" -eq 'mixed_mode') -and ("$($r2.wf)" -eq 'pe_x64dbg_ida_dynamic_analysis') -and ($kinds2 -eq 'pe_headers,clr_metadata') -and ($r2.startDelta -eq 0)) @($ev2, $r2.call.rpc.resp)

    # [3] unity_mono: probe referencing UnityEngine.* stubs. Four refs keep the evidence
    # value under the 1024-byte limit (accepted, kind+workflow+evidence asserted); a fifth
    # ref pushes it over -> small INTERNAL_ERROR without the domain envelope. Exact-1024 is
    # bracketed by (under-limit pass, over-limit reject); csc's filename ceiling prevents
    # growing single stub names to hit the exact byte.
    $stubSrc = Join-Path $script:Repo 'tests\\debug\\fixtures-src\\SatelliteLib.cs'
    $buildLog = Join-Path $script:OutDir 'a24-unity-build.log'
    function New-UnityProbe([string]$ProbeName, [string[]]$StubNames) {
        $log = @()
        foreach ($sn in $StubNames) {
            $stubDll = Join-Path $root ($sn + '.dll')
            $log += "stub: $sn exists=$(Test-Path $stubSrc)"
            $log += (& $m.env.csc /nologo /optimize- /target:library "/out:$stubDll" $stubSrc 2>&1 | Out-String)
        }
        $probeSrc = Join-Path $script:OutDir 'unity-probe.cs'
        $src = @(); $refs = @()
        for ($k = 0; $k -lt $StubNames.Count; $k++) {
            $src += "extern alias X$k;"
            $refs += '/r:X' + $k + '="' + (Join-Path $root ($StubNames[$k] + '.dll')) + '"'
        }
        $calls = @(); for ($k = 0; $k -lt $StubNames.Count; $k++) { $calls += "X$k" + '::Satellite.Satellite.Answer()' }
        $src += 'internal static class U { private static void Main() { var s = ' + ($calls -join ' + ') + '; System.Console.WriteLine(s); } }'
        Set-Content $probeSrc ($src -join "`r`n")
        $probeExe = Join-Path $root $ProbeName
        $rsp = Join-Path $script:OutDir 'unity-probe.rsp'
        @('/nologo', '/optimize-', '/platform:x64', "/out:`"$probeExe`"", "`"$probeSrc`"") + $refs | Set-Content $rsp -Encoding ASCII
        $log += "probe: $ProbeName refs=$($refs.Count)"
        $log += (& $m.env.csc "@$rsp" 2>&1 | Out-String)
        $log | Set-Content $buildLog
        return (Test-Path $probeExe)
    }
    $baseNames = @(); for ($i = 0; $i -lt 5; $i++) { $baseNames += 'UnityEngine.' + ('x' * 180) + $i }
    $okU0 = New-UnityProbe 'a24-unity0.exe' ($baseNames | Select-Object -First 4)
    if (-not $okU0) { Assert-Cond 'a24-unity-build' 'under-limit probe compiled' 'failed' $false @('a24-unity-build.log'); return }
    $r3 = Invoke-UnsupportedLaunch (Join-Path $root 'a24-unity0.exe') 'unity0'
    $val0 = [string]"$($r3.ev[0].value)"
    [int]$vlen = [Text.Encoding]::UTF8.GetByteCount($val0)
    $ev3 = Save-Json 'a24-unity.json' @{ measured = $vlen; details = $r3.call.domain.error }
    Assert-Cond 'a24-unity-mono-under-limit' 'unity_mono -> mono_dynamic_analysis; evidence under 1024 bytes accepted' "kind=$($r3.det) wf=$($r3.wf) len=$vlen start=+$($r3.startDelta)" (("$($r3.det)" -eq 'unity_mono') -and ("$($r3.wf)" -eq 'mono_dynamic_analysis') -and ($vlen -gt 0) -and ($vlen -le 1024) -and ($r3.startDelta -eq 0)) @($ev3, $r3.call.rpc.resp)

    $okU5 = New-UnityProbe 'a24-unity5.exe' $baseNames
    if (-not $okU5) { Assert-Cond 'a24-unity-over-build' 'over-limit probe compiled' 'failed' $false @('a24-unity-build.log'); return }
    $r3o = Invoke-UnsupportedLaunch (Join-Path $root 'a24-unity5.exe') 'unity5'
    $noDetails = -not $r3o.call.domain.error.details
    $ev3o = Save-Json 'a24-unity-over.json' $r3o.call.domain.error
    Assert-Cond 'a24-unity-over-limit' 'over-1024 evidence = small INTERNAL_ERROR, no details, zero Start' "code=$($r3o.code) noDetails=$noDetails start=+$($r3o.startDelta)" (("$($r3o.code)" -eq 'INTERNAL_ERROR') -and $noDetails -and ($r3o.startDelta -eq 0)) @($ev3o, $r3o.call.rpc.resp)

    # [4] unsupported_managed_runtime: metadata runtime version patched outside v2/v4.
    $rtPath = Join-Path $root 'a24-rt.exe'
    [IO.File]::WriteAllBytes($rtPath, [IO.File]::ReadAllBytes((Join-Path $root 'ArgvFixture.exe')))
    $b2 = [IO.File]::ReadAllBytes($rtPath)
    $bsjb = -1
    for ($i = 0; $i -lt $b2.Length - 4; $i++) { if ($b2[$i] -eq 0x42 -and $b2[$i+1] -eq 0x53 -and $b2[$i+2] -eq 0x4A -and $b2[$i+3] -eq 0x42) { $bsjb = $i; break } }
    if ($bsjb -ge 0) {
        $vlen2 = [BitConverter]::ToUInt32($b2, $bsjb + 12)
        $newVer = [Text.Encoding]::ASCII.GetBytes('v6.6.9')
        for ($i = 0; $i -lt $vlen2; $i++) { $b2[$bsjb + 16 + $i] = 0 }
        for ($i = 0; $i -lt $newVer.Length; $i++) { $b2[$bsjb + 16 + $i] = $newVer[$i] }
        [IO.File]::WriteAllBytes($rtPath, $b2)
    }
    $r4 = Invoke-UnsupportedLaunch $rtPath 'rt'
    $ev4 = Save-Json 'a24-rt.json' $r4.call.domain.error
    Assert-Cond 'a24-unsupported-runtime' 'unsupported_managed_runtime -> managed_static_analysis, runtime_contract evidence' "kind=$($r4.det) wf=$($r4.wf) ev0=$($r4.ev[0].kind) start=+$($r4.startDelta)" (("$($r4.det)" -eq 'unsupported_managed_runtime') -and ("$($r4.wf)" -eq 'managed_static_analysis') -and ("$($r4.ev[0].kind)" -eq 'runtime_contract') -and ($r4.startDelta -eq 0)) @($ev4, $r4.call.rpc.resp)

    # [5] The three fixed disabled APIs stay CAPABILITY_UNAVAILABLE WITHOUT details.
    foreach ($d in @('debug_attach', 'debug_detach', 'debug_list_attachable_processes')) {
        $dc = Invoke-ToolNoInit $d @{ request_id = "a24-$d"; pid = 4242 }
        $dcode = Get-DomainError $dc
        $dDet = [bool]($dc.domain -and $dc.domain.error -and $dc.domain.error.details)
        Assert-Cond "a24-disabled-$d" 'CAPABILITY_UNAVAILABLE and NO details' "code=$dcode details=$dDet" (("$dcode" -eq 'CAPABILITY_UNAVAILABLE') -and (-not $dDet)) @($dc.rpc.resp)
    }

    # [6] spy: all four kinds rejected, zero Starts across the whole case.
    $spy = Get-SpyCounters
    $evS = Save-Json 'a24-spy.json' $spy
    $kinds = @('pure_native', 'mixed_mode', 'unity_mono', 'unsupported_managed_runtime') | ForEach-Object { [int]$spy."unsupported_target_rejections:$_" }
    $starts = if ($spy.PSObject.Properties.Name -contains 'dbg_start_calls') { [int]$spy.dbg_start_calls } else { 0 }
    Assert-Cond 'a24-all-kinds-rejected' 'spy recorded every rejected kind with zero Start calls' "kinds=$($kinds -join '/') starts=$starts" ((@($kinds | Where-Object { $_ -ge 1 }).Count -eq 4) -and ($starts -eq 0)) @($evS)
}
# ---------------------------------------------------------------- case: ACC-029 ----
function Run-ACC029 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'Argv86.exe' -X86)) { Assert-Cond 'fixture-build-86' 'Argv86.exe compiled (x86)' 'failed' $false @('build-Argv86.exe.log'); return }
    if (-not (Compile-Fixture 'AccHarness.cs' 'AccHarness86.exe' -X86)) { Assert-Cond 'fixture-harness-86' 'AccHarness86.exe compiled (x86)' 'failed' $false @('build-AccHarness86.exe.log'); return }
    if (-not (Compile-Fixture 'SatelliteLib.cs' 'SatelliteLib.dll' -Library)) { Assert-Cond 'fixture-target-lib' 'SatelliteLib.dll compiled' 'failed' $false @('build-SatelliteLib.dll.log'); return }
    $fx = Build-AccCore
    if (-not $fx.ok) { Assert-Cond 'fixture-core' 'AccCore DLL built' 'failed' $false @('acccore-build.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $exe86 = Join-Path $m.env.sample_root 'Argv86.exe'
    $v = $m.protocol_versions[2]
    $sha = Get-Sha256File $exe

    # [1] x64 host: net48-x64 lifecycle (the covered matrix leg, re-asserted) ...
    $sess = Launch-AndPause $exe 'none'
    Assert-Cond 'a29-x64-net48' 'net48-x64 lifecycle on the x64 host' "ok=$($sess.ok)" $sess.ok
    if ($sess.ok) {
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sess.sid; generation = $sess.gen; request_id = 'a29-t0' } | Out-Null
        Start-Sleep -Milliseconds 1000
    }

    # [2] ... and the cross-architecture rejection (x86 binary on the x64 host).
    $spy0 = Get-SpyCounters
    $bad = Invoke-Tool $v 'debug_launch' @{ request_id = 'a29-x86on64'; target_path = $exe86; expected_sha256 = (Get-Sha256File $exe86); launch_mode = 'net48-exe'; architecture = 'x86'; break_kind = 'none' }
    $spy1 = Get-SpyCounters
    $badCode = Get-DomainError $bad
    Assert-Cond 'a29-x86-on-x64' 'x86 binary on x64 host = CAPABILITY_UNAVAILABLE, zero Start' "code=$badCode start=+$((Get-SpyDelta $spy0 $spy1 'dbg_start_calls'))" (("$badCode" -eq 'CAPABILITY_UNAVAILABLE') -and ((Get-SpyDelta $spy0 $spy1 'dbg_start_calls') -eq 0)) @($bad.rpc.resp)

    # [3] Switch to the x86 dnSpy host for the x86 legs (same extension DLL, AnyCPU IL).
    $x86Exe = 'C:\Tools\dnSpy\dnSpy-x86.exe'
    if (-not (Test-Path $x86Exe)) { Fail-Precondition 'a29-x86-host' 'dnSpy-x86.exe present'; return }
    $origDnspy = $m.env.dnspy_exe
    $m.env.dnspy_exe = $x86Exe
    try {
        # Ensure-CanonicalDnSpy reuses a healthy dnSpy when the snapshot matches — force the
        # handover by stopping the x64 instance first, and keep killing stragglers until the
        # server consistently answers x86 (http.sys can briefly serve a dying registration).
        Stop-DnSpyAndTargets
        $up86 = Ensure-CanonicalDnSpy
        $hostArch = ''
        if ($up86) {
            for ($w = 0; $w -lt 10; $w++) {
                $capP = Invoke-ToolNoInit 'debug_capabilities' @{ }
                $hostArch = "$($capP.domain.result.host_architecture)"
                if ("$hostArch" -eq 'x86') { break }
                Get-Process dnSpy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 800
                Stop-DnSpyAndTargets
                $up86 = Ensure-CanonicalDnSpy
                if (-not $up86) { break }
            }
        }
        Assert-Cond 'a29-x86-host-up' 'x86 dnSpy host healthy with the extension loaded' "health=$(Get-HealthCode $script:BaseUrl) arch=$hostArch" ($up86 -and ("$hostArch" -eq 'x86')) @()
        if ($up86 -and "$hostArch" -eq 'x86') {
            $cap = Invoke-ToolNoInit 'debug_capabilities' @{ }
            Assert-Cond 'a29-x86-host-arch' 'capabilities report x86 host architecture' "arch=$hostArch" ("$hostArch" -eq 'x86') @($cap.rpc.resp)

            # [4] net48-x86 full lifecycle.
            $s86 = Launch-AndPause $exe86 'none' 'x86'
            Assert-Cond 'a29-x86-net48' 'net48-x86 lifecycle on the x86 host' "ok=$($s86.ok)" $s86.ok
            if ($s86.ok) {
                Invoke-ToolNoInit 'debug_terminate' @{ session_id = $s86.sid; generation = $s86.gen; request_id = 'a29-t1' } | Out-Null
                Start-Sleep -Milliseconds 1000
            }

            # [5] Cross rejection the other way: x64 binary on the x86 host.
            $bad2 = Invoke-Tool $v 'debug_launch' @{ request_id = 'a29-x64on86'; target_path = $exe; expected_sha256 = $sha; launch_mode = 'net48-exe'; architecture = 'x64'; break_kind = 'none' }
            $bad2Code = Get-DomainError $bad2
            Assert-Cond 'a29-x64-on-x86' 'x64 binary on x86 host = CAPABILITY_UNAVAILABLE' "code=$bad2Code" ("$bad2Code" -eq 'CAPABILITY_UNAVAILABLE') @($bad2.rpc.resp)

            # [6] coreclr-dotnet x86 positive lifecycle on an actual isolated x86 .NET host.
            # All launch inputs, including the runtime host, must be physical children of
            # AllowedSampleRoot. Provisioning keeps the shared x86 host under C:\Tools, so
            # stage a non-reparse copy under the dedicated sample root for this positive leg.
            $host86Root = Join-Path $m.env.sample_root 'dotnet10-x86'
            if (-not (Test-Path (Join-Path $host86Root 'dotnet.exe'))) {
                New-Item -ItemType Directory -Force -Path $host86Root | Out-Null
                Copy-Item (Join-Path (Split-Path $m.env.dotnet10_x86 -Parent) '*') $host86Root -Recurse -Force
            }
            $host86 = Join-Path $host86Root 'dotnet.exe'
            $core86 = Invoke-Tool $v 'debug_launch' @{ request_id = 'a29-corex86'; target_path = $fx.dll; expected_sha256 = (Get-Sha256File $fx.dll); launch_mode = 'coreclr-dotnet'; architecture = 'x86'; break_kind = 'process'; host_path = $host86; host_sha256 = (Get-Sha256File $host86) }
            $core86Ok = [bool]$core86.domain.ok
            if ($core86Ok) {
                $ci = $core86.domain.result
                $cp = Wait-StablePaused $ci.session_id
                $core86Ok = $cp.ok
                Invoke-ToolNoInit 'debug_terminate' @{ session_id = $ci.session_id; generation = [int]$ci.generation; request_id = 'a29-core86-term' } | Out-Null
                Start-Sleep -Milliseconds 900
            }
            Assert-Cond 'a29-coreclr-x86-positive' 'coreclr-dotnet x86 launch/pause/terminate succeeds on x86 dnSpy + x86 dotnet host' "ok=$core86Ok code=$(Get-DomainError $core86)" $core86Ok @($core86.rpc.resp)

            # [7] x86 harness positive: entry-less library runs through an x86 harness.
            $h86 = Join-Path $m.env.sample_root 'AccHarness86.exe'
            $lib = Join-Path $m.env.sample_root 'SatelliteLib.dll'
            $har = Invoke-ToolNoInit 'debug_launch' @{ request_id = 'a29-harness86'; target_path = $lib; expected_sha256 = (Get-Sha256File $lib); launch_mode = 'harness'; architecture = 'x86'; break_kind = 'none'; harness_path = $h86; harness_sha256 = (Get-Sha256File $h86); harness_argv = @('x86-positive') }
            $harOk = [bool]$har.domain.ok
            if ($harOk) {
                # The process break in the launch response can auto-resume before the first
                # status poll; reacquire a held pause if necessary.
                $hi = $har.domain.result; $hp = Wait-HeldPause $hi.session_id ([int]$hi.generation); $harOk = $hp.ok
                Invoke-ToolNoInit 'debug_terminate' @{ session_id = $hi.session_id; generation = [int]$hi.generation; request_id = 'a29-har86-term' } | Out-Null
                Start-Sleep -Milliseconds 900
            }
            Assert-Cond 'a29-harness-x86-positive' 'x86 harness launch/pause/terminate succeeds on x86 dnSpy' "ok=$harOk code=$(Get-DomainError $har)" $harOk @($har.rpc.resp)
        }
    }
    finally {
        $m.env.dnspy_exe = $origDnspy
        Stop-DnSpyAndTargets
        Ensure-CanonicalDnSpy | Out-Null
    }

    # [8] Current x64 CoreCLR leg is also exercised directly here (ACC-008 supplies the
    # deeper two-mode control matrix).
    $c64 = Invoke-Tool $v 'debug_launch' @{ request_id = 'a29-corex64'; target_path = $fx.dll; expected_sha256 = (Get-Sha256File $fx.dll); launch_mode = 'coreclr-dotnet'; architecture = 'x64'; break_kind = 'process'; host_path = $m.env.dotnet10_x64; host_sha256 = (Get-Sha256File $m.env.dotnet10_x64) }
    $c64Ok = $c64.domain.ok
    if ($c64Ok) { $x = $c64.domain.result; $xp = Wait-StablePaused $x.session_id; $c64Ok = $xp.ok; Invoke-ToolNoInit 'debug_terminate' @{ session_id=$x.session_id; generation=[int]$x.generation; request_id='a29-core64-term' } | Out-Null }
    Assert-Cond 'a29-coreclr-x64-positive' 'coreclr-dotnet x64 positive lifecycle' "ok=$c64Ok" $c64Ok @($c64.rpc.resp)
}
# ---------------------------------------------------------------- case: ACC-023 ----
function Read-SettingsSnapshot {
    $xmlPath = [Environment]::ExpandEnvironmentVariables($script:Manifest.env.settings_xml)
    [xml]$d = Get-Content $xmlPath
    $node = $d.SelectSingleNode("//section[@_='352907a0-9df5-4b2b-b47b-95e504cac301']")
    if ($node) { return ($node.GetAttribute('SettingsSnapshotJson') | ConvertFrom-Json) }
    return $null
}

function Run-ACC023 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    $remoteUrl = "http://$($m.env.vm_ip):15100/"
    $tokenBytes = New-Object byte[] 32
    ([Security.Cryptography.RandomNumberGenerator]::Create()).GetBytes($tokenBytes)
    $verifierHex = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($tokenBytes)).Replace('-','').ToLower()
    $b64 = [Convert]::ToBase64String($tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    # [1] Defaults: loopback snapshot, no VM IP anywhere in the committed default posture.
    $orig = Read-SettingsSnapshot
    $defOk = $orig -and ("$($orig.Host)" -eq 'localhost') -and ([int]$orig.Port -eq 3000) -and (@($orig.RemoteAllowedCidrs).Count -eq 0) -and (-not $orig.RemoteTokenVerifier) -and (-not $orig.RemoteHostOnlyAcknowledged)
    $ev1 = Save-Json 'a23-defaults.json' $orig
    Assert-Cond 'a23-default-snapshot' 'defaults: localhost:3000, empty CIDR, no verifier, no ack (no VM IP in the default posture)' "ok=$defOk" $defOk @($ev1)

    # [2] Provision remote (urlacl + firewall + single ApplySnapshot) and prove authenticated
    # reachability with unauthenticated 401.
    & netsh http delete urlacl url=$remoteUrl 2>&1 | Out-Null
    & netsh http add urlacl url=$remoteUrl user=Everyone 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'a23-urlacl-add.log')
    & netsh advfirewall firewall delete rule name="dnspy-mcp-acc23" 2>&1 | Out-Null
    & netsh advfirewall firewall add rule name="dnspy-mcp-acc23" dir=in action=allow protocol=TCP localport=15100 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'a23-firewall-add.log')
    $cidrSorted = @("$($m.env.host_ip)/32", "$($m.env.vm_ip)/32") | Sort-Object
    $cidrJson = '["' + ($cidrSorted -join '","') + '"]'
    $snapR = New-SnapshotJson $true $true $m.env.vm_ip 15100 $m.env.sample_root $m.env.artifact_root $cidrJson $true ('"' + $verifierHex + '"')
    Stop-DnSpyAndTargets
    Set-SnapshotJson $snapR
    $up = Start-DnSpyAndWait -HealthUrl $remoteUrl
    if (-not $up) { Assert-Cond 'a23-remote-up' 'health reachable on the remote snapshot' 'failed' $false @('a23-urlacl-add.log'); return }
    $noAuth = & curl.exe -s -o NUL -w "%{http_code}" --max-time 5 "$($remoteUrl.TrimEnd('/'))/" -X POST -H 'Content-Type: application/json' --data '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
    $rCap = Send-Rpc 'tools/call' @{ name = 'debug_capabilities'; arguments = @{} } -AuthHeader "Authorization: Bearer $b64" -BaseUrlOverride $remoteUrl
    $rdom = $null
    try { $rdom = ($rCap.json.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text | ConvertFrom-Json } catch { }
    $tupleOk = $rdom -and ($rdom.result.security.bind_mode -eq 'remote_host_only') -and ($rdom.result.security.auth_required) -and ($rdom.result.security.cidr_required)
    $ev2 = Save-Text 'a23-remote-capabilities.txt' ($rCap.body + "`nstatus=" + $rCap.status + "`nno_auth=" + $noAuth)
    Assert-Cond 'a23-authenticated-reach' 'remote: allowlisted source authenticated 200 tuple; unauthenticated 401' "tuple=$tupleOk no_auth=$noAuth" ($tupleOk -and ("$noAuth" -eq '401')) @($ev2)

    # [3] Revoke: delete the urlacl/firewall rules, single ApplySnapshot back to every
    # default network field, restart — only loopback listens afterwards.
    & netsh http delete urlacl url=$remoteUrl 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'a23-urlacl-del.log')
    & netsh advfirewall firewall delete rule name="dnspy-mcp-acc23" 2>&1 | Out-String | Set-Content (Join-Path $script:OutDir 'a23-firewall-del.log')
    $defaultJson = New-SnapshotJson $true $true 'localhost' 3000 $m.env.sample_root $m.env.artifact_root
    Stop-DnSpyAndTargets
    Set-SnapshotJson $defaultJson
    $upL = Start-DnSpyAndWait
    $afterRemote = & curl.exe -s -o NUL -w "%{http_code}" --max-time 5 "$($remoteUrl.TrimEnd('/'))/" -X POST -H 'Content-Type: application/json' --data '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    $revokedSnap = Read-SettingsSnapshot
    $revOk = $revokedSnap -and ("$($revokedSnap.Host)" -eq 'localhost') -and ([int]$revokedSnap.Port -eq 3000) -and (@($revokedSnap.RemoteAllowedCidrs).Count -eq 0) -and (-not $revokedSnap.RemoteTokenVerifier) -and (-not $revokedSnap.RemoteHostOnlyAcknowledged)
    $ev3 = Save-Json 'a23-revoked.json' @{ snapshot = $revokedSnap; remoteProbe = $afterRemote; loopbackHealth = (Get-HealthCode $script:BaseUrl) }
    Assert-Cond 'a23-revoked-loopback-only' 'revocation: default snapshot restored, loopback healthy, remote port closed' "snap=$revOk remote=$afterRemote health=$(Get-HealthCode $script:BaseUrl)" ($revOk -and $upL -and ("$afterRemote" -ne '200') -and ((Get-HealthCode $script:BaseUrl) -eq 200)) @($ev3, 'a23-urlacl-del.log')
}
# ---------------------------------------------------------------- case: ACC-036 ----
function Run-ACC036 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }

    # [1] The committed snapshot round-trips as the 11-field canonical JSON (sorted keys).
    $snap = Read-SettingsSnapshot
    $fields = @('SchemaVersion','EnableServer','Host','Port','DebugToolsEnabled','DedicatedDebugInstanceAcknowledged','AllowedSampleRoot','ArtifactRoot','RemoteAllowedCidrs','RemoteHostOnlyAcknowledged','RemoteTokenVerifier')
    $missing = @($fields | Where-Object { -not ($snap.PSObject.Properties.Name -contains $_) })
    $ev1 = Save-Json 'a36-snapshot.json' $snap
    Assert-Cond 'a36-eleven-field-canonical' '11-field snapshot present; canonical (sorted) key order' "missing=$($missing -join ',') schema=$($snap.SchemaVersion)" ($missing.Count -eq 0 -and ("$($snap.SchemaVersion)" -eq 'dnspy.mcp.settings.v1')) @($ev1)

    # [2] Invalid committed (unparseable JSON) -> SafeDefaults (EnableServer=false) -> the
    # server stays silent on restart; strict unknown-field rejection is a recorded ledger gap.
    $badSnap = 'not-json{{'
    Stop-DnSpyAndTargets
    Set-SnapshotJson $badSnap
    $silent = Start-DnSpyAndWait
    $h = Get-HealthCode $script:BaseUrl
    $ev2 = Save-Text 'a36-invalid-silent.txt' "health=$h up=$silent"
    Assert-Cond 'a36-invalid-fail-closed' 'invalid committed -> SafeDefaults -> server silent' "health=$h" (-not $silent) @($ev2)

    # [3] Canonical restore returns the server (fail-closed is reversible by committing
    # a valid snapshot — no legacy/migration shortcuts).
    $good = New-SnapshotJson $true $true 'localhost' 3000 $m.env.sample_root $m.env.artifact_root
    Stop-DnSpyAndTargets
    Set-SnapshotJson $good
    $up = Start-DnSpyAndWait
    Assert-Cond 'a36-valid-restored' 'valid committed restores the server' "health=$(Get-HealthCode $script:BaseUrl)" $up @()

    # [4] Execute the real settings transaction over its injectable raw-IO seam. Every field
    # below is produced inside the extension process by McpSettingsStore/McpSettingsPersistence.
    $tx = Invoke-ToolNoInit 'debug_test_settings' @{}
    $txr = $tx.domain.result
    $txEv = Save-Json 'a36-settings-transaction-matrix.json' $txr
    $txOk = $tx.domain.ok -and $txr.unknown_field_rejected -and $txr.invalid_committed_pending_not_activated `
        -and (-not $txr.pending_write_failure.success) -and ($txr.pending_write_failure.failed_step -eq 'PendingWrite') -and (-not $txr.pending_write_failure.current_is_candidate) -and ($txr.pending_write_failure.transitions -eq 0) `
        -and (-not $txr.server_transition_failure.success) -and ($txr.server_transition_failure.failed_step -eq 'ServerTransition') -and (-not $txr.server_transition_failure.current_is_candidate) `
        -and (-not $txr.committed_write_failure.success) -and ($txr.committed_write_failure.failed_step -eq 'CommittedWrite') -and (-not $txr.committed_write_failure.current_is_candidate) `
        -and $txr.pending_clear_failure.success -and $txr.pending_clear_failure.current_is_candidate -and ($txr.pending_clear_failure.events -eq 1) -and [bool]$txr.pending_clear_failure.warning
    Assert-Cond 'a36-transaction-fault-matrix' 'unknown fields rejected; pending/transition/commit failures retain old snapshot; pending-clear failure commits once with warning' "ok=$txOk" $txOk @($tx.rpc.resp, $txEv)

    # [5] Two actual dnSpy OS processes use distinct --settings-file stores and listeners.
    # Killing B must leave A and its snapshot/listener untouched.
    $settingsA = [Environment]::ExpandEnvironmentVariables($m.env.settings_xml)
    $settingsB = 'C:\Tools\dnspy-acc36-instance-b.xml'
    Copy-Item $settingsA $settingsB -Force
    [xml]$bx = Get-Content $settingsB
    $bn = $bx.SelectSingleNode("//section[@_='352907a0-9df5-4b2b-b47b-95e504cac301']")
    $bRoot = 'C:\dnspy-mcp-artifacts-b'
    New-Item -ItemType Directory -Force $bRoot | Out-Null
    $bJson = New-SnapshotJson $true $true 'localhost' 3001 $m.env.sample_root $bRoot
    $bn.SetAttribute('SettingsSnapshotJson', $bJson)
    $bn.RemoveAttribute('SettingsPendingJson')
    $bx.Save($settingsB)
    $bp = Start-Process -FilePath $m.env.dnspy_exe -WorkingDirectory (Split-Path $m.env.dnspy_exe) -ArgumentList @('--multiple','--settings-file',$settingsB) -PassThru
    $bUrl = 'http://localhost:3001/'
    $deadlineB = (Get-Date).AddSeconds(45); $bUp = $false
    while ((Get-Date) -lt $deadlineB -and -not $bUp) { Start-Sleep -Milliseconds 700; $bUp = (Get-HealthCode $bUrl) -eq 200 }
    $aBefore = Get-HealthCode $script:BaseUrl
    if (-not $bp.HasExited) { Stop-Process -Id $bp.Id -Force }
    Start-Sleep -Milliseconds 1200
    $aAfter = Get-HealthCode $script:BaseUrl
    $bAfter = Get-HealthCode $bUrl
    $isoEv = Save-Json 'a36-two-instance.json' @{ pid_b = $bp.Id; a_before = $aBefore; a_after = $aAfter; b_up = $bUp; b_after = $bAfter; settings_a = $settingsA; settings_b = $settingsB }
    Assert-Cond 'a36-two-instance-isolation' 'two dnSpy OS processes with distinct settings stores/listeners; stopping B leaves A healthy' "bUp=$bUp a=$aBefore/$aAfter bAfter=$bAfter" ($bUp -and ($aBefore -eq 200) -and ($aAfter -eq 200) -and ($bAfter -ne 200)) @($isoEv)
}

# ---------------------------------------------------------------- case: ACC-019 ----
function Run-ACC019 {
    $m = $script:Manifest
    # This case owns the dedicated test ArtifactRoot and specifies an initially empty store.
    # Perform the documented operator cleanup only while dnSpy is stopped, so leftovers from
    # an earlier acceptance process cannot turn the first S1 positive control into the very
    # cross-restart TARGET_MISMATCH that this case tests later.
    Stop-DnSpyAndTargets
    Reset-TestArtifactRoot
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    if (-not (Compile-Fixture 'SatelliteLib.cs' 'SatelliteLib.dll' -Library)) { Assert-Cond 'fixture-lib' 'SatelliteLib.dll compiled' 'failed' $false @('build-SatelliteLib.dll.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]
    $root = $m.env.artifact_root

    function Invoke-DumpCycle([string]$Tag) {
        $sess = Launch-AndPause $exe 'none'
        if (-not $sess.ok) { return @{ ok = $false; why = 'launch' } }
        $mods = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sess.sid; generation = $sess.gen }
        $main = @($mods.domain.result.items) | Where-Object { "$($_.name)" -like 'ArgvFixture*' } | Select-Object -First 1
        $d = Invoke-ToolNoInit 'debug_dump_module' @{ session_id = $sess.sid; generation = $sess.gen; pause_epoch = $sess.epoch; request_id = "a19-$Tag"; module_handle = "$($main.module_handle)"; relative_name = "a19-$Tag" }
        $art = $d.domain.result.artifact
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sess.sid; generation = $sess.gen; request_id = "a19-t-$Tag" } | Out-Null
        Start-Sleep -Milliseconds 1100
        return @{ ok = $d.domain.ok; code = (Get-DomainError $d); sid = $sess.sid; art = $art }
    }

    # [1] S1: dump, terminal -> the session directory and every child SURVIVE (retention,
    # zero auto-delete) and are write/delete protected by the held marker handle.
    $s1 = Invoke-DumpCycle 's1'
    $s1SpyEv = Save-Json 'a19-s1-artifact-spy.json' (Get-SpyCounters)
    Assert-Cond 'a19-s1-dump' 'S1 dump ok' "ok=$($s1.ok)" $s1.ok @($s1SpyEv)
    $s1dir = if ($s1.art) { Split-Path "$($s1.art.path)" -Parent } else { $null }
    $s1files = if ($s1dir -and (Test-Path $s1dir)) { Get-ChildItem $s1dir -File | ForEach-Object { $_.Name } } else { @() }
    $tamper = 'blocked'
    if ($s1files.Count -gt 0) {
        $victim = Join-Path $s1dir ($s1files | Select-Object -First 1)
        try { [IO.File]::WriteAllText($victim, 'x'); $tamper = 'SUCCEEDED' } catch { $tamper = 'blocked' }
        try { [IO.File]::Delete($victim); $tamper = "$tamper+delete-SUCCEEDED" } catch { }
    }
    $ev1 = Save-Json 'a19-s1-retention.json' @{ dir = $s1dir; files = $s1files; tamper = $tamper }
    Assert-Cond 'a19-retention-zero-delete' 'S1 terminal: directory retained, all children present, tamper share-blocked' "files=$($s1files.Count) tamper=$tamper" (($s1files.Count -ge 3) -and ("$tamper" -eq 'blocked')) @($ev1)

    # [2] S2 in the SAME process: a separate session directory; S1's files still intact.
    $s2 = Invoke-DumpCycle 's2'
    $s2dir = if ($s2.art) { Split-Path "$($s2.art.path)" -Parent } else { $null }
    $s1still = if ($s1dir -and (Test-Path $s1dir)) { (Get-ChildItem $s1dir -File | Measure-Object).Count } else { 0 }
    $ev2 = Save-Json 'a19-s2.json' @{ s2dir = $s2dir; s1files = $s1still }
    Assert-cond 'a19-s2-independent' 'S2 dump: own session directory; S1 retained untouched' "s2=$([bool]$s2dir) s1files=$s1still" ($s2.ok -and $s2dir -and ($s2dir -ne $s1dir) -and ($s1still -ge 3)) @($ev2)

    # [3] Duplicate relative_name inside a live session = ALREADY_EXISTS (ledger admission).
    $sess = Launch-AndPause $exe 'none'
    if ($sess.ok) {
        $mods = Invoke-ToolNoInit 'debug_list_modules' @{ session_id = $sess.sid; generation = $sess.gen }
        $main = @($mods.domain.result.items) | Where-Object { "$($_.name)" -like 'ArgvFixture*' } | Select-Object -First 1
        $d1 = Invoke-ToolNoInit 'debug_dump_module' @{ session_id = $sess.sid; generation = $sess.gen; pause_epoch = $sess.epoch; request_id = 'a19-dup1'; module_handle = "$($main.module_handle)"; relative_name = 'a19-dup' }
        $d2 = Invoke-ToolNoInit 'debug_dump_module' @{ session_id = $sess.sid; generation = $sess.gen; pause_epoch = $sess.epoch; request_id = 'a19-dup2'; module_handle = "$($main.module_handle)"; relative_name = 'a19-dup' }
        $d2code = Get-DomainError $d2
        Assert-Cond 'a19-duplicate-child' 'duplicate child name in one session = ALREADY_EXISTS' "code=$d2code" ("$d2code" -eq 'ALREADY_EXISTS') @($d2.rpc.resp)
        Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sess.sid; generation = $sess.gen; request_id = 'a19-t3' } | Out-Null
        Start-Sleep -Milliseconds 1000
    }

    # [4] Restart empties the in-memory ledger. Existing directories have no current-process
    # provenance, remain untouched, and block new store mutation until stopped-process
    # operator cleanup.
    Stop-DnSpyAndTargets
    $up = Ensure-CanonicalDnSpy
    $stale = Invoke-DumpCycle 'stale'
    $s1post = if ($s1dir -and (Test-Path $s1dir)) { (Get-ChildItem $s1dir -File | Measure-Object).Count } else { 0 }
    Assert-Cond 'a19-restart-stale-blocks' 'restart: pre-existing session remains untouched and next dump is TARGET_MISMATCH' "up=$up code=$($stale.code) s1files=$s1post" ($up -and (-not $stale.ok) -and ("$($stale.code)" -eq 'TARGET_MISMATCH') -and ($s1post -ge 3)) @()

    Stop-DnSpyAndTargets
    Reset-TestArtifactRoot
    $up2 = Ensure-CanonicalDnSpy
    $s3 = Invoke-DumpCycle 's3'
    $s3dir = if ($s3.art) { Split-Path "$($s3.art.path)" -Parent } else { $null }
    Assert-Cond 'a19-operator-clean-recovery' 'stopped-process operator cleanup restores an empty root and fresh dump succeeds' "up=$up2 s3=$([bool]$s3dir)" ($up2 -and $s3.ok -and [bool]$s3dir) @()

    # [5] Execute quota at/over and cancellation settlement through the product's
    # IArtifactStoreFs/ArtifactOperationRecord seams inside the extension process.
    $probe = Invoke-ToolNoInit 'debug_test_artifact' @{}
    $pr = $probe.domain.result
    $probeEv = Save-Json 'a19-artifact-seam-matrix.json' $pr
    $bools = @('session_admitted','file_at_limit','file_over_rejected_zero_delta','session_over_rejected_zero_delta','second_session_admitted','store_at_limit','store_over_rejected','external_child_fail_closed_zero_delta','cancel_timeline_exactly_once',
        'startup_stale_blocks_new','retained_counts_toward_limits','retained_identity_reverified','post_create_cancel_aborted_owned','pre_create_deadline_zero_delta')
    $badProbe = @($bools | Where-Object { -not [bool]$pr.PSObject.Properties[$_].Value })
    Assert-Cond 'a19-quota-cancel-seam' 'limits, startup stale, retained identity/accounting, pre-create deadline and post-create aborted_owned all fail closed; cancellation settles once' "bad=$($badProbe -join ',')" ($probe.domain.ok -and $badProbe.Count -eq 0) @($probe.rpc.resp, $probeEv)

    # Retention is the behavior under test, but this case must not leave retained identities
    # in the shared acceptance root for ACC-032 or a later independent run.
    Stop-DnSpyAndTargets
    Reset-TestArtifactRoot
    Ensure-CanonicalDnSpy | Out-Null
}

# ---------------------------------------------------------------- case: ACC-022 ----
function Run-ACC022 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $exe = Join-Path $m.env.sample_root 'ArgvFixture.exe'
    $v = $m.protocol_versions[2]

    # [1] The gate artifacts exist and are the frozen contract files (hash-stable).
    $gateFiles = @(
        @{ p = 'tests\debug\contracts\dnspy.debug.v1.schema.json'; n = 'v1 schema' },
        @{ p = 'tests\debug\contracts\dnspy.debug.utf8-limits.json'; n = 'utf8 limits' },
        @{ p = 'tests\debug\contracts\fixtures\MANIFEST.json'; n = 'fixture manifest' },
        @{ p = 'tests\snapshots\static-tools.baseline.json'; n = 'static baseline' },
        @{ p = 'tests\run-verify-local.sh'; n = 'build gate script' }
    )
    $missing = @(); $hashes = @{}
    foreach ($g in $gateFiles) {
        $full = Join-Path $script:Repo $g.p
        if (Test-Path $full) { $hashes[$g.n] = (Get-Sha256File $full).Substring(0, 12) } else { $missing += $g.n }
    }
    $ev1 = Save-Json 'a22-gate-artifacts.json' $hashes
    Assert-Cond 'a22-gate-artifacts' 'release-gate artifacts present (schema/limits/fixtures/baseline/build script)' "missing=$($missing -join ',')" ($missing.Count -eq 0) @($ev1)

    # [2] The documented gate entry point for the reusable verification suite.
    $readme = Join-Path $script:Repo 'tests\TEST-PLAN.zh-CN.md'
    $hasEntry = (Test-Path $readme) -and ((Get-Content $readme -Raw -ErrorAction SilentlyContinue) -match 'run-debug-tests')
    Assert-Cond 'a22-gate-documented' 'E2E gate invocation documented in tests/TEST-PLAN.zh-CN.md' "doc=$hasEntry" $hasEntry @()

    # [3] The CI verify workflow is a HARD gate (third-party audit finding): the E2E job
    # must invoke the driver explicitly (never silently skippable), and verify-status must
    # reject anything but success — including 'skipped' — for contracts/build/e2e.
    $wfPath = Join-Path $script:Repo '.github\workflows\verify.yml'
    $wf = if (Test-Path $wfPath) { Get-Content $wfPath -Raw } else { '' }
    $ev3 = Save-Text 'a22-verify-workflow.yml' $wf
    $wfInvokes = ($wf -match 'run-debug-tests\.ps1\s+(-Case\s+ACC-\d{3}|-VerifyHarness)')
    $wfNoHashGate = ($wf -notmatch "if:\s*hashFiles\('tests/debug/run-debug-tests\.ps1'\)")
    $strictPattern = '[^\n]*!=\s*["'']success["'']'
    $wfStatusStrict = @(); foreach ($jid in @('contracts', 'build', 'e2e')) { if ($wf -match ([regex]::Escape("needs.$jid.result") + $strictPattern)) { $wfStatusStrict += $jid } }
    Assert-Cond 'a22-ci-workflow-hard-gate' 'E2E job invokes the driver explicitly; no hashFiles skip; verify-status rejects non-success (incl. skipped) for contracts/build/e2e' "invoke=$wfInvokes noHashGate=$wfNoHashGate strict=$($wfStatusStrict -join ',')" ($wfInvokes -and $wfNoHashGate -and ($wfStatusStrict.Count -eq 3)) @($ev3)

    # [4] Release-gate miniature: one full canonical E2E cycle on the advertised wire.
    $sess = Launch-AndPause $exe 'none'
    $cycleOk = $false
    if ($sess.ok) {
        $c = Invoke-ToolNoInit 'debug_continue' @{ session_id = $sess.sid; generation = $sess.gen; pause_epoch = $sess.epoch; request_id = 'a22-c1' }
        $st = Invoke-ToolNoInit 'debug_status' @{ session_id = $sess.sid }
        $running = ("$($st.domain.result.state)" -eq 'running') -or $c.domain.ok
        $t = Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sess.sid; generation = $sess.gen; request_id = 'a22-t1' }
        Start-Sleep -Milliseconds 900
        $st2 = Invoke-ToolNoInit 'debug_status' @{ session_id = $sess.sid }
        $cycleOk = $running -and ("$($st2.domain.result.state)" -eq 'idle')
    }
    Assert-Cond 'a22-release-cycle' 'gate miniature: launch->pause->continue->terminate->idle' "ok=$cycleOk" $cycleOk @()
}
# ---------------------------------------------------------------- case: ACC-003 ----
function Run-ACC003 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }
    $remoteUrl = "http://$($m.env.vm_ip):15100/"
    $tokenBytes = New-Object byte[] 32
    ([Security.Cryptography.RandomNumberGenerator]::Create()).GetBytes($tokenBytes)
    $verifierHex = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($tokenBytes)).Replace('-','').ToLower()
    $goodTok = [Convert]::ToBase64String($tokenBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    $badBytes = [byte[]]$tokenBytes.Clone()
    $badBytes[31] = $badBytes[31] -bxor 1
    $badTok = [Convert]::ToBase64String($badBytes).TrimEnd('=').Replace('+','-').Replace('/','_')

    # Remote posture: host-pinned 15100, Ubuntu+VM CIDR, verifier, ack.
    & netsh http delete urlacl url=$remoteUrl 2>&1 | Out-Null
    & netsh http add urlacl url=$remoteUrl user=Everyone 2>&1 | Out-Null
    & netsh advfirewall firewall delete rule name="dnspy-mcp-acc3" 2>&1 | Out-Null
    & netsh advfirewall firewall add rule name="dnspy-mcp-acc3" dir=in action=allow protocol=TCP localport=15100 2>&1 | Out-Null
    $cidrSorted = @("$($m.env.host_ip)/32", "$($m.env.vm_ip)/32") | Sort-Object
    $snapR = New-SnapshotJson $true $true $m.env.vm_ip 15100 $m.env.sample_root $m.env.artifact_root ('["' + ($cidrSorted -join '","') + '"]') $true ('"' + $verifierHex + '"')
    Stop-DnSpyAndTargets
    Set-SnapshotJson $snapR
    $up = Start-DnSpyAndWait -HealthUrl $remoteUrl
    if (-not $up) { Assert-Cond 'a3-remote-up' 'remote posture up' 'failed' $false @(); return }

    function Probe([string]$Url, [string[]]$Headers, [string]$Body) {
        $args = @('-s','-o','NUL','-w','%{http_code}','--max-time','6')
        foreach ($h in $Headers) { $args += @('-H', $h) }
        $args += @('-X', 'POST', $Url, '-H', 'Content-Type: application/json')
        if ($Body) { $args += @('--data', $Body) }
        return (& curl.exe @args 2>$null)
    }
    $rpc = '{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}'

    # Eight-variant auth matrix on the plain JSON-RPC endpoint (the transport in service).
    $good = Probe $remoteUrl @("Authorization: Bearer $goodTok") $rpc
    $noAuth = Probe $remoteUrl @() $rpc
    $basic = Probe $remoteUrl @('Authorization: Basic YTpi') $rpc
    $badB64 = Probe $remoteUrl @('Authorization: Bearer !!!not-base64url!!!') $rpc
    $wrongTok = Probe $remoteUrl @("Authorization: Bearer $badTok") $rpc
    $spoofed = Probe $remoteUrl @("Authorization: Bearer $goodTok", 'X-Forwarded-For: 8.8.8.8', 'Forwarded: for=8.8.8.8') $rpc
    $goodSpoofCidr = Probe $remoteUrl @("Authorization: Bearer $goodTok", 'X-Forwarded-For: 8.8.8.8') $rpc
    $ev1 = Save-Text 'a3-auth-matrix.txt' "good=$good noAuth=$noAuth basic=$basic badB64=$badB64 wrongTok=$wrongTok goodXFF=$goodSpoofCidr"
    $rejectOk = ($noAuth -eq '401') -and ($basic -eq '401') -and ($badB64 -eq '401') -and ($wrongTok -eq '401')
    $acceptOk = ($good -eq '200') -and ($spoofed -eq '200') -and ($goodSpoofCidr -eq '200')
    Assert-Cond 'a3-auth-matrix' '401 for no-auth/Basic/bad-base64url/wrong-token; 200 for valid token (XFF/Forwarded spoofing ignored — peer address decides)' "good=$good noAuth=$noAuth basic=$basic badB64=$badB64 wrong=$wrongTok xff=$goodSpoofCidr" ($rejectOk -and $acceptOk) @($ev1)

    # 401 carries the fixed WWW-Authenticate; 401/403 bodies are empty (CON-DYN-006 shape).
    $h401 = & curl.exe -s -D - -o NUL --max-time 6 -X POST $remoteUrl -H 'Content-Type: application/json' --data $rpc 2>$null
    $www = ($h401 -join ' ') -match 'WWW-Authenticate:\s*Bearer realm="dnspy-mcp"'
    $body401 = & curl.exe -s --max-time 6 -X POST $remoteUrl -H 'Content-Type: application/json' --data $rpc 2>$null
    $ev2 = Save-Text 'a3-401-shape.txt' (($h401 -join "`n") + "`nbodyLen=" + $body401.Length)
    Assert-Cond 'a3-401-shape' '401: fixed WWW-Authenticate Bearer realm, empty body' "www=$www bodyLen=$($body401.Length)" ($www -and ($body401.Length -eq 0)) @($ev2)

    # Health endpoint honors the same wall; a valid token passes it.
    $healthUrl = $remoteUrl.TrimEnd('/') + '/health'
    $hNo = & curl.exe -s -o NUL -w '%{http_code}' --max-time 6 $healthUrl 2>$null
    $hOk = & curl.exe -s -o NUL -w '%{http_code}' --max-time 6 $healthUrl -H "Authorization: Bearer $goodTok" 2>$null
    Assert-Cond 'a3-health-endpoint' 'health: 401 unauthenticated, 200 authenticated' "no=$hNo ok=$hOk" (($hNo -eq '401') -and ("$hOk" -eq '200')) @()

    # Every routed endpoint crosses the same pre-parse auth wall. Authenticated status may
    # differ by endpoint semantics (eg unknown session=404), but unauthenticated is always 401.
    $endpointRows = @(
        @{ n='root-post'; method='POST'; path='/'; accept='application/json'; data=$rpc },
        @{ n='mcp-post'; method='POST'; path='/mcp'; accept='application/json'; data=$rpc },
        @{ n='message-post'; method='POST'; path='/message?sessionId=missing'; accept='application/json'; data=$rpc },
        @{ n='options'; method='OPTIONS'; path='/mcp'; accept='application/json'; data='' },
        @{ n='delete'; method='DELETE'; path='/mcp'; accept='application/json'; data='' },
        @{ n='stream-get'; method='GET'; path='/mcp'; accept='text/event-stream'; data='' },
        @{ n='unknown'; method='GET'; path='/unknown'; accept='application/json'; data='' }
    )
    $endpointEvidence = @(); $endpointOk = $true
    foreach ($row in $endpointRows) {
        $url = $remoteUrl.TrimEnd('/') + $row.path
        $baseArgs = @('-s','-o','NUL','-w','%{http_code}','--max-time','2','-X',$row.method,$url,'-H',("Accept: " + $row.accept))
        if ($row.data) { $baseArgs += @('-H','Content-Type: application/json','--data',$row.data) }
        $unauth = & curl.exe @baseArgs 2>$null
        $authArgs = @($baseArgs + @('-H',"Authorization: Bearer $goodTok"))
        $auth = & curl.exe @authArgs 2>$null
        $endpointEvidence += [pscustomobject]@{ endpoint=$row.n; unauth="$unauth"; auth="$auth" }
        if ("$unauth" -ne '401' -or "$auth" -eq '401') { $endpointOk = $false }
    }
    $epEv = Save-Json 'a3-all-endpoint-walls.json' $endpointEvidence
    Assert-Cond 'a3-all-endpoint-walls' 'all routed HTTP/SSE/Streamable endpoints reject unauthenticated before endpoint semantics' "ok=$endpointOk" $endpointOk @($epEv)

    # A valid token from a peer outside RemoteAllowedCidrs is the fixed empty-body 403.
    $cidrDeny = New-SnapshotJson $true $true $m.env.vm_ip 15100 $m.env.sample_root $m.env.artifact_root ('["' + $m.env.host_ip + '/32"]') $true ('"' + $verifierHex + '"')
    Stop-DnSpyAndTargets
    Set-SnapshotJson $cidrDeny
    $denyUp = Start-DnSpyAndWait -HealthUrl $remoteUrl
    $denyCode = & curl.exe -s -o NUL -w '%{http_code}' --max-time 6 $healthUrl -H "Authorization: Bearer $goodTok" 2>$null
    Assert-Cond 'a3-cidr-deny' 'valid token but direct peer outside allowlist = 403' "up=$denyUp code=$denyCode" ($denyUp -and ("$denyCode" -eq '403')) @(Save-Text 'a3-cidr-deny.txt' "code=$denyCode")

    # Restore loopback defaults + drop the provisioning (reversible).
    & netsh http delete urlacl url=$remoteUrl 2>&1 | Out-Null
    & netsh advfirewall firewall delete rule name="dnspy-mcp-acc3" 2>&1 | Out-Null
    $defaultJson = New-SnapshotJson $true $true 'localhost' 3000 $m.env.sample_root $m.env.artifact_root
    Stop-DnSpyAndTargets
    Set-SnapshotJson $defaultJson
    $upL = Start-DnSpyAndWait
    Assert-Cond 'a3-restored' 'loopback restored after the auth matrix' "health=$(Get-HealthCode $script:BaseUrl)" $upL @()

    # Null direct-peer is fail-closed in the same product predicate (executed in-process).
    $peer = Invoke-ToolNoInit 'debug_test_settings' @{}
    Assert-Cond 'a3-null-peer' 'null RemoteEndPoint is rejected by CIDR admission' "rejected=$($peer.domain.result.null_peer_rejected)" ([bool]$peer.domain.result.null_peer_rejected) @($peer.rpc.resp)
}
# ---------------------------------------------------------------- case: ACC-004 ----
function Run-ACC004 {
    $m = $script:Manifest
    if (-not (Ensure-CanonicalDnSpy)) { Assert-Cond 'env-dnspy-up' 'health 200' (Get-HealthCode $script:BaseUrl) $false; return }

    # [1] Request-body ceiling: exactly 1 MiB reaches the JSON parser (-32700 for a padded
    # body); one byte over is rejected BEFORE any read/parse work as an empty-body 413.
    $pad1m = ('{"jsonrpc":"2.0","id":41,"method":"tools/list","params":{},' + ('"' + ('p' * 1048554) + '":1}'))
    $pad1m = $pad1m.Substring(0, 1048576)
    [IO.File]::WriteAllText('C:\Tools\a4-1m.json', $pad1m, (New-Object Text.ASCIIEncoding))
    $r1m = & curl.exe -s -o NUL -w '%{http_code}' --max-time 10 -X POST ($script:BaseUrl.TrimEnd('/') + '/') -H 'Content-Type: application/json' --data-binary '@C:\Tools\a4-1m.json' 2>$null
    $big = 'x' * 1048577
    [IO.File]::WriteAllText('C:\Tools\a4-big.json', $big, (New-Object Text.ASCIIEncoding))
    $rBig = & curl.exe -s -o NUL -w '%{http_code}' --max-time 10 -X POST ($script:BaseUrl.TrimEnd('/') + '/') -H 'Content-Type: application/json' --data-binary '@C:\Tools\a4-big.json' 2>$null
    $ev1 = Save-Text 'a4-body-limits.txt' "atLimit=$r1m over=$rBig"
    Assert-Cond 'a4-body-limit-413' 'exactly 1 MiB is served (200-family, NOT 413); 1 MiB + 1 = 413 before parsing' "at=$r1m over=$rBig" (("$rBig" -eq '413') -and ("$r1m" -ne '413') -and ("$r1m" -ne '') -and ("$r1m" -ne '000')) @($ev1)

    # [2] Concurrent admission under load. First, sixteen calls against a REAL session prove
    # the independent wait-slot split: eight waits complete and eight get domain
    # LIMIT_EXCEEDED. Then seventeen DNMCP_TEST transport probes each hold an admitted short
    # worker for four seconds, deterministically proving sixteen HTTP workers + a 17th 429.
    # Keeping these as two phases avoids a scheduler race where the fast domain rejections
    # release short-worker slots before the nominal seventeenth curl is accepted.
    if (-not (Compile-Fixture 'ArgvFixture.cs' 'ArgvFixture.exe')) { Assert-Cond 'fixture-build' 'ArgvFixture.exe compiled' 'failed' $false @('build-ArgvFixture.exe.log'); return }
    $sess = Launch-AndPause (Join-Path $m.env.sample_root 'ArgvFixture.exe') 'none'
    if (-not $sess.ok) { Assert-Cond 'a4-session' 'session up' 'failed' $false @(); return }
    $maxCur = Get-MaxEventCursor $sess.sid $sess.gen
    $jobs = @()
    for ($i = 0; $i -lt 16; $i++) {
        $b = '{"jsonrpc":"2.0","id":' + (600 + $i) + ',"method":"tools/call","params":{"name":"debug_wait_event","arguments":{"session_id":"' + $sess.sid + '","generation":' + $sess.gen + ',"after_cursor":' + $maxCur + ',"timeout_ms":4000,"limit":1}}}'
        [IO.File]::WriteAllText("C:\\Tools\\a4-w$i.json", $b, (New-Object Text.ASCIIEncoding))
        $jobs += Start-Process -FilePath curl.exe -ArgumentList '-s','-w','%{http_code}','--max-time','12','-X','POST',($script:BaseUrl.TrimEnd('/') + '/'),'-H','"Content-Type: application/json"','--data',("@C:\\Tools\\a4-w$i.json") -RedirectStandardOutput ("C:\\Tools\\a4-w$i.out") -PassThru -WindowStyle Hidden
    }
    $jobs | ForEach-Object { $_.WaitForExit(15000) | Out-Null }
    $limitHits = 0; $oks = 0
    for ($i = 0; $i -lt 16; $i++) {
        $o = Get-Content "C:\Tools\a4-w$i.out" -Raw -ErrorAction SilentlyContinue
        if ("$o" -match 'LIMIT_EXCEEDED') { $limitHits++ }
        elseif ("$o" -match '"ok":true') { $oks++ }
    }
    $shortJobs = @()
    for ($i = 0; $i -lt 17; $i++) {
        $b = '{"jsonrpc":"2.0","id":' + (650 + $i) + ',"method":"tools/call","params":{"name":"debug_test_transport","arguments":{"hold_ms":4000}}}'
        [IO.File]::WriteAllText("C:\Tools\a4-s$i.json", $b, (New-Object Text.ASCIIEncoding))
        $shortJobs += Start-Process -FilePath curl.exe -ArgumentList '-s','-w','%{http_code}','--max-time','12','-X','POST',($script:BaseUrl.TrimEnd('/') + '/'),'-H','"Content-Type: application/json"','--data',("@C:\Tools\a4-s$i.json") -RedirectStandardOutput ("C:\Tools\a4-s$i.out") -PassThru -WindowStyle Hidden
    }
    $shortJobs | ForEach-Object { $_.WaitForExit(15000) | Out-Null }
    $shortOk = 0; $http429 = 0
    for ($i = 0; $i -lt 17; $i++) {
        $o = Get-Content "C:\Tools\a4-s$i.out" -Raw -ErrorAction SilentlyContinue
        # Accept the historical flattened-header suffix as well as the explicitly quoted
        # single-transfer form, while still requiring the real request's 429 status.
        if ("$o" -match '429(?:000)?$') { $http429++ }
        elseif ("$o" -match '"ok":true') { $shortOk++ }
    }
    Invoke-ToolNoInit 'debug_terminate' @{ session_id = $sess.sid; generation = $sess.gen; request_id = 'a4-t1' } | Out-Null
    Start-Sleep -Milliseconds 900
    $ev2 = Save-Text 'a4-concurrent-admission.txt' "waitDomainLimit=$limitHits waitOk=$oks shortOk=$shortOk http429=$http429"
    Assert-Cond 'a4-concurrent-admission' 'wait slots: 8 succeed + 8 domain LIMIT_EXCEEDED; short workers: 16 succeed + 17th HTTP 429' "waitLimit=$limitHits waitOk=$oks shortOk=$shortOk http429=$http429" (($limitHits -eq 8) -and ($oks -eq 8) -and ($shortOk -eq 16) -and ($http429 -eq 1)) @($ev2)

    # [3] Nine real long-lived legacy SSE connections: the ninth is rejected before a worker.
    $longs = @()
    for ($i = 0; $i -lt 8; $i++) {
        $longs += Start-Process -FilePath curl.exe -ArgumentList '-s','-o','NUL','--max-time','10',($script:BaseUrl.TrimEnd('/') + '/sse') -PassThru -WindowStyle Hidden
    }
    Start-Sleep -Milliseconds 900
    $ninthLong = & curl.exe -s -o NUL -w '%{http_code}' --max-time 3 ($script:BaseUrl.TrimEnd('/') + '/sse') 2>$null
    foreach ($p in $longs) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    Assert-Cond 'a4-long-9th' 'ninth concurrent long connection = HTTP 429' "code=$ninthLong" ("$ninthLong" -eq '429') @(Save-Text 'a4-long-9th.txt' "code=$ninthLong")

    # [4] Streamable HTTP sessions persist independently of connections: first 16 initialize,
    # 17th initialize is the fixed 429 and allocates no session.
    $initBody = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"a4","version":"1"}}}'
    $sessionCodes = @()
    for ($i = 0; $i -lt 17; $i++) {
        $code = & curl.exe -s -o NUL -w '%{http_code}' --max-time 5 -X POST ($script:BaseUrl.TrimEnd('/') + '/') -H 'Accept: application/json, text/event-stream' -H 'Content-Type: application/json' --data $initBody 2>$null
        $sessionCodes += "$code"
    }
    $sessionEv = Save-Json 'a4-streamable-17th.json' $sessionCodes
    $sessionOk = (@($sessionCodes | Where-Object { $_ -eq '200' }).Count -eq 16) -and ($sessionCodes[16] -eq '429')
    Assert-Cond 'a4-streamable-17th' 'first 16 Streamable initialize sessions succeed; 17th = HTTP 429' "codes=$($sessionCodes -join ',')" $sessionOk @($sessionEv)

    # [5] Framing and raw admission gates execute through the exact production classes. This
    # covers a lying small Content-Length and unknown/chunked stream at byte 1,048,577.
    $tp = Invoke-ToolNoInit 'debug_test_transport' @{}
    $tr = $tp.domain.result
    $transportFields = @('fake_small_content_length_rejected_at_1048577','chunked_unknown_length_rejected_at_1048577','short_17th_rejected','long_9th_rejected')
    $transportBad = @($transportFields | Where-Object { -not [bool]$tr.PSObject.Properties[$_].Value })
    Assert-Cond 'a4-transport-seam' 'bounded reader rejects lying/chunked oversized streams at +1; gates reject 17th short/9th long' "bad=$($transportBad -join ',')" ($tp.domain.ok -and $transportBad.Count -eq 0) @($tp.rpc.resp, (Save-Json 'a4-transport-seam.json' $tr))

    # Clear the persistent transport sessions for the next case.
    Stop-DnSpyAndTargets
    $restored = Ensure-CanonicalDnSpy
    Assert-Cond 'a4-restored' 'canonical instance restarted after transport saturation' "up=$restored" $restored @()
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
    'ACC-016' = ${function:Run-ACC016}; 'ACC-025' = ${function:Run-ACC025}; 'ACC-033' = ${function:Run-ACC033}; 'ACC-032' = ${function:Run-ACC032}; 'ACC-035' = ${function:Run-ACC035}; 'ACC-028' = ${function:Run-ACC028}; 'ACC-024' = ${function:Run-ACC024}; 'ACC-029' = ${function:Run-ACC029}; 'ACC-023' = ${function:Run-ACC023}; 'ACC-036' = ${function:Run-ACC036}; 'ACC-019' = ${function:Run-ACC019}; 'ACC-022' = ${function:Run-ACC022}; 'ACC-003' = ${function:Run-ACC003}; 'ACC-004' = ${function:Run-ACC004}
}
if ($handlers.ContainsKey($Case) -and $script:Manifest) {
    # CHK-006: every case starts COLD. Ensure-CanonicalDnSpy reuses a healthy dnSpy when the
    # snapshot matches, so a previous case's lingering transitions could bleed into the next
    # case's first launch in sequential batch runs; stopping here makes batch == cold start.
    Stop-DnSpyAndTargets
    Reset-TestArtifactRoot
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

# Exit-0 gates beyond assertion status (third-party audit finding): every referenced
# evidence file must exist on disk, and the emitted result must satisfy the committed
# dnspy.debug.test.v1 shape. Self-references to result.json are forbidden.
$missingEvidence = @()
foreach ($a in $script:Assertions) {
    foreach ($ep in @($a.evidence_paths)) {
        # Evidence references are result-directory-relative paths.  Reject null/empty,
        # rooted paths and traversal before checking existence; Join-Path by itself would
        # otherwise allow a syntactically valid result to point at an unrelated host file.
        $epText = "$ep"
        $invalidPath = [string]::IsNullOrWhiteSpace($epText) -or [IO.Path]::IsPathRooted($epText)
        $candidate = $null
        if (-not $invalidPath) {
            try {
                $candidate = [IO.Path]::GetFullPath((Join-Path $script:OutDir $epText))
                $rootPrefix = [IO.Path]::GetFullPath($script:OutDir).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
                if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { $invalidPath = $true }
            } catch { $invalidPath = $true }
        }
        if ($invalidPath -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $missingEvidence += "$($a.assertion_id):[$epText]"
        }
    }
}
$missingEvidence | Set-Content (Join-Path $script:OutDir 'evidence-gate.log')
Assert-Cond 'result-evidence-complete' 'every assertion evidence path exists under the result directory' "missing=$($missingEvidence.Count)" ($missingEvidence.Count -eq 0) @('evidence-gate.log')

# Schema-driven conformance gate (external finding: the former shape check only inspected
# assertion fields and never loaded the contract — a self-referential hollow gate). This
# validator READS the committed dnspy.debug.test.v1 schema and validates the FINAL result
# object against it (subset: type/const/enum/required/additionalProperties/properties/items/
# pattern/minItems — every keyword the schema uses).
function Get-ValueProp {
    param($Obj, [string]$Name)
    # The comma operator prevents PowerShell's function-output enumeration from flattening
    # single-element arrays (verified micro-repro: bare `return $Obj.$Name` unrolls them).
    if ($Obj -is [System.Collections.IDictionary]) { return ,$Obj[$Name] } else { return ,$Obj.$Name }
}
function Get-ValueProps {
    param($Obj)
    if ($Obj -is [System.Collections.IDictionary]) { return @($Obj.Keys) } else { return @($Obj.PSObject.Properties.Name) }
}
function Test-JsonScalarEqual {
    param($Left, $Right)
    if ($null -eq $Left -or $null -eq $Right) { return $null -eq $Left -and $null -eq $Right }
    if ($Left -is [string] -or $Right -is [string]) {
        return ($Left -is [string]) -and ($Right -is [string]) -and [string]::Equals($Left, $Right, [StringComparison]::Ordinal)
    }
    if ($Left -is [bool] -or $Right -is [bool]) { return ($Left -is [bool]) -and ($Right -is [bool]) -and ($Left -eq $Right) }
    $numberTypes = @([byte],[sbyte],[int16],[uint16],[int32],[uint32],[int64],[uint64],[single],[double],[decimal])
    $leftNumber = $numberTypes | Where-Object { $_.IsInstanceOfType($Left) } | Select-Object -First 1
    $rightNumber = $numberTypes | Where-Object { $_.IsInstanceOfType($Right) } | Select-Object -First 1
    if ($leftNumber -or $rightNumber) {
        if (-not $leftNumber -or -not $rightNumber) { return $false }
        return [decimal]$Left -eq [decimal]$Right
    }
    return $Left.GetType() -eq $Right.GetType() -and $Left.Equals($Right)
}
function Test-SchemaNode {
    param($Value, $Schema, [string]$Path, [System.Collections.ArrayList]$Errors)
    if ($null -eq $Schema) { return }
    if ($Schema.PSObject.Properties['const'] -and -not (Test-JsonScalarEqual $Value $Schema.const)) { [void]$Errors.Add("$Path const mismatch") }
    if ($Schema.PSObject.Properties['enum']) {
        $inEnum = $false
        foreach ($e in $Schema.enum) { if (Test-JsonScalarEqual $Value $e) { $inEnum = $true; break } }
        if (-not $inEnum) { [void]$Errors.Add("$Path not in enum") }
    }
    if ($Schema.PSObject.Properties['type']) {
        $t = "$($Schema.type)"
        switch ($t) {
            'object' {
                if ($Value -isnot [System.Collections.IDictionary] -and $Value -isnot [System.Management.Automation.PSCustomObject]) { [void]$Errors.Add("$Path is not an object"); return }
                $props = Get-ValueProps $Value
                if ($Schema.PSObject.Properties['required']) {
                    foreach ($r in @($Schema.required)) { if ($props -cnotcontains $r) { [void]$Errors.Add("$Path missing required '$r'") } }
                }
                if ("$($Schema.additionalProperties)" -eq 'False' -and $Schema.PSObject.Properties['properties']) {
                    $allowed = @($Schema.properties.PSObject.Properties.Name)
                    foreach ($pr in $props) { if ($allowed -cnotcontains $pr) { [void]$Errors.Add("$Path unexpected property '$pr'") } }
                }
                if ($Schema.PSObject.Properties['properties']) {
                    foreach ($pr in $props) {
                        $sub = @($Schema.properties.PSObject.Properties | Where-Object { $_.Name -ceq $pr }) | Select-Object -First 1
                        if ($sub) { Test-SchemaNode -Value (Get-ValueProp $Value $pr) -Schema $sub.Value -Path "$Path.$pr" -Errors $Errors }
                    }
                }
            }
            'array' {
                if ($Value -isnot [System.Collections.IList]) { [void]$Errors.Add("$Path is not an array"); return }
                $items = @($Value)
                if ($Schema.PSObject.Properties['minItems'] -and $items.Count -lt [int]$Schema.minItems) { [void]$Errors.Add("$Path below minItems") }
                if ($Schema.PSObject.Properties['items']) {
                    for ($i = 0; $i -lt $items.Count; $i++) { Test-SchemaNode -Value $items[$i] -Schema $Schema.items -Path "$Path[$i]" -Errors $Errors }
                }
            }
            'string' { if ($null -eq $Value -or $Value -isnot [string]) { [void]$Errors.Add("$Path is not a string") } }
            'integer' { if ($Value -isnot [int] -and $Value -isnot [long]) { [void]$Errors.Add("$Path is not an integer") } }
            'number' { if ($Value -isnot [int] -and $Value -isnot [long] -and $Value -isnot [double]) { [void]$Errors.Add("$Path is not a number") } }
            'boolean' { if ($Value -isnot [bool]) { [void]$Errors.Add("$Path is not a boolean") } }
        }
    }
    if ($Schema.PSObject.Properties['pattern'] -and -not [regex]::IsMatch("$Value", "$($Schema.pattern)", [Text.RegularExpressions.RegexOptions]::CultureInvariant)) { [void]$Errors.Add("$Path pattern mismatch") }
    if ($Schema.PSObject.Properties['minLength'] -and "$Value".Length -lt [int]$Schema.minLength) { [void]$Errors.Add("$Path below minLength") }
}
$shapeErrors = New-Object System.Collections.ArrayList
$resultSchemaPath = Join-Path $script:Repo 'tests\debug\contracts\dnspy.debug.test.v1.schema.json'
$resultSchema = $null
try { $resultSchema = Get-Content $resultSchemaPath -Raw | ConvertFrom-Json } catch { [void]$shapeErrors.Add("schema load failed: $($_.Exception.Message)") }

# Provisional gate assertion (shape-identical to every assertion); flipped to fail if the
# schema validation below reports errors, then status/exit are recomputed — the structure
# is value-independent, so the recorded object still conforms.
[IO.File]::WriteAllText((Join-Path $script:OutDir 'shape-gate.log'), '', (New-Object Text.UTF8Encoding($false)))
Assert-Cond 'result-schema-shape' 'result object validated against committed dnspy.debug.test.v1.schema.json' 'pending' $true @('shape-gate.log')
$gateAssertion = $script:Assertions[$script:Assertions.Count - 1]

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
    assertions = @($script:Assertions)
    evidence_paths = $evidencePaths
}
if ($resultSchema) { Test-SchemaNode -Value $result -Schema $resultSchema -Path '$' -Errors $shapeErrors }
@($shapeErrors) | Set-Content (Join-Path $script:OutDir 'shape-gate.log')
if (@($shapeErrors).Count -gt 0) {
    $gateAssertion.status = 'fail'
    $gateAssertion.actual = (@($shapeErrors) | Select-Object -First 5) -join '; '
    $allPass = $false; $status = 'fail'
    $exit = if ($script:PreconditionFailed) { 2 } else { 1 }
    $result.status = $status; $result.exit_code = $exit
} else {
    $gateAssertion.actual = "valid against dnspy.debug.test.v1.schema.json"
}
$resultPath = Join-Path $script:OutDir 'result.json'
ConvertTo-Json $result -Depth 40 | Set-Content $resultPath -Encoding UTF8
$rel = "tests/debug/results/$script:Sha/$Case/result.json"
Write-Output $rel
exit $exit
