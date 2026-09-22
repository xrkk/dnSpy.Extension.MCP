# Process ownership helpers for the debug acceptance runner.
#
# Cleanup authority is deliberately narrower than process discovery: only an identity captured
# by this invocation (PID + canonical executable + UTC creation ticks) can be stopped.  Names,
# directory prefixes, and a reused PID are never sufficient authority.

function Initialize-DebugProcessOwnership {
    $script:DebugOwnedProcesses = New-Object System.Collections.ArrayList
}

function ConvertTo-CanonicalExecutablePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'process executable path is empty' }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\').ToLowerInvariant()
}

function Get-DebugProcessIdentity([int]$ProcessId) {
    $row = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $ProcessId) -ErrorAction Stop
    if (-not $row -or [string]::IsNullOrWhiteSpace("$($row.ExecutablePath)")) {
        throw "process identity unavailable for PID $ProcessId"
    }
    $created = ([DateTime]$row.CreationDate).ToUniversalTime()
    return [pscustomobject]@{
        pid = $ProcessId
        exe = ConvertTo-CanonicalExecutablePath "$($row.ExecutablePath)"
        ticks = [long]$created.Ticks
        start_time_utc_ms = $created.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    }
}

function Add-DebugOwnedProcess {
    param(
        [Parameter(Mandatory = $true)]$Identity,
        [Parameter(Mandatory = $true)][string]$Kind,
        [string]$SessionId = '',
        [int]$Generation = -1
    )
    if ($null -eq $script:DebugOwnedProcesses) { Initialize-DebugProcessOwnership }
    $same = @($script:DebugOwnedProcesses | Where-Object {
        [int]$_.pid -eq [int]$Identity.pid -and [long]$_.ticks -eq [long]$Identity.ticks -and "$($_.exe)" -eq "$($Identity.exe)"
    })
    if ($same.Count -gt 0) { return $same[0] }
    $record = [pscustomobject]@{
        pid = [int]$Identity.pid; exe = "$($Identity.exe)"; ticks = [long]$Identity.ticks
        start_time_utc_ms = "$($Identity.start_time_utc_ms)"; kind = $Kind
        session_id = $SessionId; generation = $Generation; stopped = $false
    }
    [void]$script:DebugOwnedProcesses.Add($record)
    return $record
}

function Register-StartedDebugProcess {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [Parameter(Mandatory = $true)][string]$ExpectedExe,
        [Parameter(Mandatory = $true)][string]$Kind,
        [string]$SessionId = '',
        [int]$Generation = -1
    )
    $identity = Get-DebugProcessIdentity ([int]$Process.Id)
    $expected = ConvertTo-CanonicalExecutablePath $ExpectedExe
    if ($identity.exe -ne $expected) {
        throw "started process identity mismatch for PID $($Process.Id): expected $expected, observed $($identity.exe)"
    }
    return Add-DebugOwnedProcess $identity $Kind $SessionId $Generation
}

function Register-AuthoritativeDebugTarget {
    param(
        [Parameter(Mandatory = $true)]$OwnedProcess,
        [Parameter(Mandatory = $true)][string]$ExpectedExe,
        [Parameter(Mandatory = $true)][string]$SessionId,
        [Parameter(Mandatory = $true)][int]$Generation
    )
    if (-not $OwnedProcess.pid -or [string]::IsNullOrWhiteSpace("$($OwnedProcess.filename)") -or
        [string]::IsNullOrWhiteSpace("$($OwnedProcess.start_time_utc)")) {
        throw "authoritative owned_process is incomplete for session $SessionId generation $Generation"
    }
    $identity = Get-DebugProcessIdentity ([int]$OwnedProcess.pid)
    $expected = ConvertTo-CanonicalExecutablePath $ExpectedExe
    $reported = ConvertTo-CanonicalExecutablePath "$($OwnedProcess.filename)"
    $reportedStart = $null
    try {
        $reportedStart = [DateTime]::Parse("$($OwnedProcess.start_time_utc)", [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
    } catch {
        throw "authoritative target start_time_utc is invalid for session $SessionId generation $Generation"
    }
    $observedStart = [DateTime]::new([long]$identity.ticks, [DateTimeKind]::Utc)
    $startDeltaMs = ($observedStart - $reportedStart).TotalMilliseconds
    # The product's start_time_utc is the launch reservation timestamp immediately before
    # StartViaWpf, not the OS creation timestamp.  It therefore corroborates a bounded launch
    # window but cannot replace the exact Win32 creation ticks used for cleanup authorization.
    if ($identity.exe -ne $expected -or $reported -ne $expected -or
        $startDeltaMs -lt 0 -or $startDeltaMs -gt 30000) {
        throw "authoritative target identity mismatch for session $SessionId generation $Generation PID $($OwnedProcess.pid): expected=$expected reported=$reported observed=$($identity.exe) reported_start=$($OwnedProcess.start_time_utc) observed_start=$($identity.start_time_utc_ms) delta_ms=$startDeltaMs"
    }
    $record = Add-DebugOwnedProcess $identity 'target' $SessionId $Generation
    $record | Add-Member -NotePropertyName authoritative_start_time_utc -NotePropertyValue "$($OwnedProcess.start_time_utc)" -Force
    return $record
}

function Resolve-DebugToolOwnershipTuple {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('debug_launch','debug_restart')][string]$ToolName,
        [Parameter(Mandatory = $true)]$ToolArgs,
        [Parameter(Mandatory = $true)]$Domain
    )
    $resultSid = "$($Domain.result.session_id)"
    $contextSid = "$($Domain.debug_context.session_id)"
    $requestSid = "$($ToolArgs.session_id)"
    $sid = if ($ToolName -eq 'debug_launch') { $resultSid } else { $requestSid }
    $hasResultGeneration = $Domain.result -and ($Domain.result.PSObject.Properties.Name -contains 'generation')
    $hasContextGeneration = $Domain.debug_context -and ($Domain.debug_context.PSObject.Properties.Name -contains 'generation')
    $resultGeneration = if ($hasResultGeneration) { [int]$Domain.result.generation } else { -1 }
    $contextGeneration = if ($hasContextGeneration) { [int]$Domain.debug_context.generation } else { -1 }

    $sessionMismatch = [string]::IsNullOrWhiteSpace($sid) -or [string]::IsNullOrWhiteSpace($contextSid) -or $sid -ne $contextSid
    if ($ToolName -eq 'debug_launch') {
        $sessionMismatch = $sessionMismatch -or [string]::IsNullOrWhiteSpace($resultSid)
    } elseif (-not [string]::IsNullOrWhiteSpace($resultSid)) {
        $sessionMismatch = $sessionMismatch -or $resultSid -ne $sid
    }
    if ($sessionMismatch -or -not $hasResultGeneration -or -not $hasContextGeneration -or
        $resultGeneration -lt 0 -or $resultGeneration -ne $contextGeneration) {
        throw "$ToolName response/request ownership tuple mismatch: request_sid=$requestSid result_sid=$resultSid context_sid=$contextSid result_generation=$resultGeneration context_generation=$contextGeneration"
    }

    $expectedExe = ''
    if ($ToolName -eq 'debug_launch') {
        $expectedExe = switch ("$($ToolArgs.launch_mode)") {
            'coreclr-dotnet' { "$($ToolArgs.host_path)"; break }
            'harness' { "$($ToolArgs.harness_path)"; break }
            default { "$($ToolArgs.target_path)"; break }
        }
        if ([string]::IsNullOrWhiteSpace($expectedExe)) { throw 'debug_launch cannot resolve its process executable' }
    }
    return [pscustomobject]@{ session_id = $sid; generation = $resultGeneration; expected_exe = $expectedExe }
}

function Get-DebugOwnedProcess {
    param([string]$SessionId = '', [int]$Generation = -1, [string]$Kind = '')
    if ($null -eq $script:DebugOwnedProcesses) { return @() }
    return @($script:DebugOwnedProcesses | Where-Object {
        (-not $SessionId -or $_.session_id -eq $SessionId) -and
        ($Generation -lt 0 -or [int]$_.generation -eq $Generation) -and
        (-not $Kind -or $_.kind -eq $Kind)
    })
}

function Test-DebugOwnedProcessAlive {
    param([Parameter(Mandatory = $true)][string]$SessionId, [Parameter(Mandatory = $true)][int]$Generation)
    $records = @(Get-DebugOwnedProcess -SessionId $SessionId -Generation $Generation -Kind 'target')
    if ($records.Count -ne 1) { return $false }
    try {
        $now = Get-DebugProcessIdentity ([int]$records[0].pid)
        return ($now.exe -eq $records[0].exe -and [long]$now.ticks -eq [long]$records[0].ticks)
    } catch { return $false }
}

function Stop-VerifiedDebugProcess {
    param([Parameter(Mandatory = $true)]$Identity, [switch]$ErrorIfUnknown)
    $current = $null
    try { $current = Get-DebugProcessIdentity ([int]$Identity.pid) }
    catch {
        if ($null -eq (Get-Process -Id ([int]$Identity.pid) -ErrorAction SilentlyContinue)) {
            $Identity.stopped = $true
            return $true
        }
        if ($ErrorIfUnknown) { throw "cannot verify owned PID $($Identity.pid); refusing to stop it" }
        return $false
    }
    if ($current.exe -ne "$($Identity.exe)" -or [long]$current.ticks -ne [long]$Identity.ticks) {
        if ($ErrorIfUnknown) { throw "PID $($Identity.pid) no longer matches its registered executable/creation time; refusing to stop it" }
        return $false
    }
    Stop-Process -Id ([int]$Identity.pid) -Force -ErrorAction Stop
    # Stop-Process can return before the process disappears from the Windows process table.
    # Poll the full identity rather than trusting a cached Process.HasExited or a bare PID: a
    # reused PID means our process is gone and must never grant authority over the replacement.
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if ($null -eq (Get-Process -Id ([int]$Identity.pid) -ErrorAction SilentlyContinue)) {
            $Identity.stopped = $true
            return $true
        }
        try {
            $afterStop = Get-DebugProcessIdentity ([int]$Identity.pid)
            if ($afterStop.exe -ne "$($Identity.exe)" -or [long]$afterStop.ticks -ne [long]$Identity.ticks) {
                $Identity.stopped = $true
                return $true
            }
        } catch {
            if ($null -eq (Get-Process -Id ([int]$Identity.pid) -ErrorAction SilentlyContinue)) {
                $Identity.stopped = $true
                return $true
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "owned PID $($Identity.pid) did not exit after Stop-Process"
}

function Stop-DebugOwnedProcess {
    param(
        [string]$SessionId = '', [int]$Generation = -1, [string]$Kind = '',
        [switch]$RequireRegistration
    )
    $records = @(Get-DebugOwnedProcess -SessionId $SessionId -Generation $Generation -Kind $Kind |
        Where-Object { -not $_.stopped })
    if ($records.Count -eq 0 -and $RequireRegistration) {
        throw "no cleanup authority is registered for session='$SessionId' generation=$Generation kind='$Kind'"
    }
    foreach ($record in $records) { [void](Stop-VerifiedDebugProcess $record -ErrorIfUnknown) }
}

Initialize-DebugProcessOwnership
