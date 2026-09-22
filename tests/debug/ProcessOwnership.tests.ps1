#Requires -Version 5.1
param([string]$OutJson = '')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProcessOwnership.ps1')
$results = New-Object System.Collections.ArrayList
$started = New-Object System.Collections.ArrayList

function Record([string]$Name, [bool]$Pass, [string]$Detail = '') {
    [void]$results.Add([pscustomobject]@{ name = $Name; pass = $Pass; detail = $Detail })
    if (-not $Pass) { [Console]::Error.WriteLine("FAIL $Name $Detail") }
}
function Start-Sleeper {
    $p = Start-Process powershell.exe -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 120') -WindowStyle Hidden -PassThru
    [void]$started.Add($p)
    Start-Sleep -Milliseconds 350
    return $p
}

try {
    # Real same-name control: only the registered powershell.exe is stopped.
    $owned = Start-Sleeper
    $foreign = Start-Sleeper
    $ownedId = Register-StartedDebugProcess $owned (Get-DebugProcessIdentity $owned.Id).exe 'driver'
    [void](Stop-VerifiedDebugProcess $ownedId -ErrorIfUnknown)
    Start-Sleep -Milliseconds 350
    Record 'same-name only registered process stopped' ($null -eq (Get-Process -Id $owned.Id -ErrorAction SilentlyContinue))
    Record 'same-name foreign process survives' ($null -ne (Get-Process -Id $foreign.Id -ErrorAction SilentlyContinue))

    foreach ($field in @('pid','exe','ticks')) {
        $p = Start-Sleeper
        $id = Register-StartedDebugProcess $p (Get-DebugProcessIdentity $p.Id).exe 'driver'
        $copy = [pscustomobject]@{ pid=$id.pid; exe=$id.exe; ticks=$id.ticks; stopped=$false }
        if ($field -eq 'pid') { $copy.pid = $foreign.Id }
        elseif ($field -eq 'exe') { $copy.exe = 'c:\definitely\not-the-process.exe' }
        else { $copy.ticks = [long]$copy.ticks - 1 }
        $refused = $false
        try { [void](Stop-VerifiedDebugProcess $copy -ErrorIfUnknown) } catch { $refused = $true }
        Record "$field mismatch refuses stop" ($refused -and $null -ne (Get-Process -Id $p.Id -ErrorAction SilentlyContinue))
    }

    $unknown = $false
    try { Stop-DebugOwnedProcess -SessionId 'not-owned' -Generation 1 -Kind target -RequireRegistration } catch { $unknown = $true }
    Record 'unknown ownership fails closed' $unknown

    $attached = Start-Sleeper
    $before = @(Get-DebugOwnedProcess).Count
    # Merely observing an existing process is intentionally not a registration operation.
    $null = Get-DebugProcessIdentity $attached.Id
    $after = @(Get-DebugOwnedProcess).Count
    Record 'observed external process is not adopted' (($before -eq $after) -and $null -ne (Get-Process -Id $attached.Id -ErrorAction SilentlyContinue))

    # Simulate restart bookkeeping and an exceptional case body: generation 1 is stopped,
    # generation 2 is registered separately, and the finally path removes only generation 2.
    $restart1 = Start-Sleeper
    $r1 = Register-StartedDebugProcess $restart1 (Get-DebugProcessIdentity $restart1.Id).exe 'target' 'session-restart' 1
    Stop-DebugOwnedProcess -SessionId 'session-restart' -Generation 1 -Kind target -RequireRegistration
    $restart2 = Start-Sleeper
    $r2id = Get-DebugProcessIdentity $restart2.Id
    $authoritative = [pscustomobject]@{
        pid = $r2id.pid; filename = $r2id.exe; start_time_utc = $r2id.start_time_utc_ms
    }
    $r2 = Register-AuthoritativeDebugTarget $authoritative $r2id.exe 'session-restart' 2
    try { throw 'synthetic case failure' }
    catch { }
    finally { Stop-DebugOwnedProcess -SessionId 'session-restart' -Generation 2 -Kind target -RequireRegistration }
    Start-Sleep -Milliseconds 350
    Record 'restart registers and stops each generation' (($null -eq (Get-Process -Id $restart1.Id -ErrorAction SilentlyContinue)) -and ($null -eq (Get-Process -Id $restart2.Id -ErrorAction SilentlyContinue)))
    Record 'failure finally leaves other same-name instance alive' ($null -ne (Get-Process -Id $foreign.Id -ErrorAction SilentlyContinue))
}
finally {
    # The test owns every sleeper it started, but still cleans each through a freshly captured
    # full identity rather than a name sweep.  This finally also covers assertion/launch errors.
    foreach ($p in $started) {
        if ($null -ne (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) {
            try {
                $identity = Get-DebugProcessIdentity $p.Id
                $record = Add-DebugOwnedProcess $identity 'test-finally'
                [void](Stop-VerifiedDebugProcess $record -ErrorIfUnknown)
            } catch { [Console]::Error.WriteLine("test cleanup blocked for PID $($p.Id): $($_.Exception.Message)") }
        }
    }
}

$remaining = @($started | Where-Object { $null -ne (Get-Process -Id $_.Id -ErrorAction SilentlyContinue) })
Record 'all test-owned processes cleaned after finally' ($remaining.Count -eq 0) ("remaining=" + (@($remaining | ForEach-Object Id) -join ','))

$failed = @($results | Where-Object { -not $_.pass }).Count
if ($OutJson) { $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutJson -Encoding UTF8 }
$results | Format-Table -AutoSize
if ($failed) { exit 1 }
exit 0
