#Requires -Version 5.0
<#
.SYNOPSIS
  Regression tests for the static E2E client's boundary/teardown primitives.
  Loads THE SAME functions run-tests.ps1 defines (via -ProbeMode) and exercises them.
#>
param(
    [Parameter(Mandatory = $true)][string]$SettingsFile,
    [Parameter(Mandatory = $true)][string]$DnSpyExe,
    [string]$FakeServicePy = '',
    [string]$FakePort = 16260,
    [string]$OutJson = ''
)
$ErrorActionPreference = 'Stop'
$fixtureDir = $PSScriptRoot
$results = [System.Collections.ArrayList]::new()
function Record([string]$Name, [bool]$Pass, [string]$Detail = '') {
    [void]$results.Add([pscustomobject]@{ name = $Name; pass = $Pass; detail = $Detail })
    Write-Host ("{0}  {1}  {2}" -f ($(if ($Pass) { 'PASS' } else { 'FAIL' })), $Name, $Detail)
}

. (Join-Path $fixtureDir 'run-tests.ps1') -ProbeMode -SettingsFile $SettingsFile -DnSpyExe $DnSpyExe

# ---- T1 containment matrix (item 3) ----
$root = [IO.Path]::GetTempPath().TrimEnd('\') + '\e2e-boundary-test'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$root\sample\a" | Out-Null   # 'a': single-char child
Record 'contains: parent holds single-char child' (Test-PathContains "$root\sample" "$root\sample\a")
Record 'contains: reverse direction is false' (-not (Test-PathContains "$root\sample\a" "$root\sample"))
Record 'contains: shared prefix but sibling dir is NOT contained' (-not (Test-PathContains "$root\sample" ($root + 'sample2')))
Record 'contains: identical path is NOT containment (equality gate owns it)' (-not (Test-PathContains "$root\sample" "$root\sample"))
Record 'contains: deeper child recognized' (Test-PathContains "$root\sample" "$root\sample\a\b\c\d")

# ---- T2 ancestry reparse rejection (item 3) ----
Record 'ancestry: plain directory chain is fine' (-not (Test-UncertainPathAncestry "$root\sample\a"))
$link = "$root\link-to-sample"
if (Test-Path $link) { cmd /c rmdir "$link" 2>$null | Out-Null }
New-Item -ItemType Junction -Path $link -Target "$root\sample" | Out-Null
try {
    Record 'ancestry: junction in the chain is uncertain' (Test-UncertainPathAncestry "$link\a")
    Record 'ancestry: junction target itself is uncertain at the link point' (Test-UncertainPathAncestry $link)
} finally {
    if (Test-Path $link) { cmd /c rmdir "$link" 2>$null | Out-Null }
}

# ---- T3 entry call-site parameters (item 7) ----
$runner = Join-Path $fixtureDir '..\debug\run-debug-tests.ps1'
$runnerText = Get-Content $runner -Raw
Record 'entry: ACC-001 passes -SettingsFile from the manifest' ($runnerText -match '-SettingsFile \(\[Environment\]::ExpandEnvironmentVariables\(\$m\.env\.settings_xml\)\)')
Record 'entry: ACC-001 passes -FixtureDll from the sample root' ($runnerText -match "-FixtureDll \(Join-Path \$m\.env\.sample_root 'TestIL\.dll'\)")
Record 'entry: handler stages TestIL into the sample root' ($runnerText -match "Copy-Item \$m\.env\.testil_dll -Destination \(Join-Path \$m\.env\.sample_root 'TestIL\.dll'\)")

# ---- T4 deterministic fake session matrix (item 6): init ok -> initialized 204 -> one stale write 404 ----
if ($FakeServicePy -and (Test-Path $FakeServicePy)) {
    $log = Join-Path $fixtureDir 'tests-fake.log'
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $fake = Start-Process C:\Python313\python.exe -ArgumentList @($FakeServicePy, "$FakePort", $log, 'd4v2') -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    try {
        $script:Port = $FakePort
        $initThrew = $false
        try { Initialize-McpSession } catch { $initThrew = $true }
        Record 'session: initialize succeeds (asserted before any write)' (-not $initThrew -and $script:McpSessionId -and $script:McpProtocolVersion)
        if (-not $initThrew -and $script:McpSessionId) {
            $wrote = $false
            try { Rpc 'patch_method_il' @{ assembly_name = 'X' } | Out-Null; $wrote = $true } catch { }
            Record 'session: stale-session write REJECTED' (-not $wrote)
        } else { Record 'session: stale-session write REJECTED' $false 'init failed - write intentionally not attempted' }
        Start-Sleep -Milliseconds 500
        $lines = @(Get-Content $log | Where-Object { $_ -match '^POST' })
        $inits = @($lines | Where-Object { $_ -match 'method.{0,4}initialize' }).Count
        $notifs = @($lines | Where-Object { $_ -match 'notifications/initialized' }).Count
        $writes = @($lines | Where-Object { $_ -match 'tools/call' }).Count
        Record 'counts: initialize exactly 1' ($inits -eq 1) "got $inits"
        Record 'counts: initialized exactly 1' ($notifs -eq 1) "got $notifs"
        Record 'counts: write exactly 1, no retry/re-init' ($writes -eq 1) "got $writes; total POST=$($lines.Count)"
    } finally { Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue }
}

# ---- T5 initialize-failure against a failing fake: hard throw, nothing written (item 6) ----
if ($FakeServicePy -and (Test-Path $FakeServicePy)) {
    $log2 = Join-Path $fixtureDir 'tests-fake2.log'
    Remove-Item $log2 -Force -ErrorAction SilentlyContinue
    $fake2 = Start-Process C:\Python313\python.exe -ArgumentList @($FakeServicePy, "$FakePort", $log2, 'fail-init') -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    try {
        $script:Port = $FakePort
        $script:McpSessionId = $null
        $threw = $false
        try { Initialize-McpSession } catch { $threw = $true }
        Record 'init-failure: Initialize-McpSession throws hard' $threw
        Record 'init-failure: no session retained' (-not $script:McpSessionId)
        $lines2 = @(Get-Content $log2 | Where-Object { $_ -match '^POST' })
        Record 'init-failure: exactly one initialize request, nothing after' ($lines2.Count -eq 1)
    } finally { Stop-Process -Id $fake2.Id -Force -ErrorAction SilentlyContinue }
}

# ---- T6 teardown function on a REAL throwaway process (item 6: real try/finally code) ----
$victim = Start-Process powershell.exe -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 120') -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 600
$vp = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $victim.Id)
$ident = @{ pid = $victim.Id; exe = "$($vp.ExecutablePath)".ToLowerInvariant(); ticks = [long]$vp.CreationDate.ToUniversalTime().Ticks }
Complete-StaticE2ERun -Proc $victim -Identity $ident
Start-Sleep -Milliseconds 800
Record 'teardown: verified own process stopped' ($null -eq (Get-Process -Id $victim.Id -ErrorAction SilentlyContinue))
# a MISMATCHED identity must NOT stop a live process
$alive = Start-Process powershell.exe -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 120') -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 600
$badIdent = @{ pid = $alive.Id; exe = 'c:\definitely\not\this\exe.exe'; ticks = 1 }
Complete-StaticE2ERun -Proc $alive -Identity $badIdent
Start-Sleep -Milliseconds 800
Record 'teardown: mismatched identity leaves the process alive' ($null -ne (Get-Process -Id $alive.Id -ErrorAction SilentlyContinue))
Stop-Process -Id $alive.Id -Force -ErrorAction SilentlyContinue

if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
$failCount = @($results | Where-Object { -not $_.pass }).Count
if ($OutJson) { $results | ConvertTo-Json -Depth 3 | Set-Content $OutJson -Encoding UTF8 }
Write-Host "===== BOUNDARY/TEARDOWN TESTS: $(@($results).Count - $failCount) pass / $failCount fail ====="
if ($failCount -ne 0) { exit 1 } else { exit 0 }
