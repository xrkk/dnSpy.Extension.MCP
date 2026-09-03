param(
    [Parameter(Mandatory=$true)][ValidateSet('fixtures','debug')][string]$Mode,
    [string]$Architecture = '',
    [string]$Case = '',
    [Parameter(Mandatory=$true)][string]$RepoRoot,
    [Parameter(Mandatory=$true)][string]$StateRoot
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $StateRoot | Out-Null
Set-Location $RepoRoot

if ($Mode -eq 'fixtures') {
    if ($Architecture -notin @('x64','x86')) { throw 'fixtures mode requires x64 or x86 Architecture' }
    $dnSpyExe = if ($Architecture -eq 'x64') { 'C:\Tools\dnSpy\dnSpy.exe' } else { 'C:\Tools\dnSpy\dnSpy-x86.exe' }
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File tests\fixtures\run-tests.ps1 `
        -SkipBuild -Tfm net48 -DnSpyExe $dnSpyExe 2>&1 | Out-String
}
else {
    if ($Case -notmatch '^ACC-\d{3}$') { throw 'debug mode requires ACC-xxx Case' }
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File tests\debug\run-debug-tests.ps1 `
        -Case $Case 2>&1 | Out-String
}
$code = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $StateRoot 'output.txt'), $output, (New-Object Text.UTF8Encoding($false)))
$debugResultPath = $null
$debugResult = $null
if ($Mode -eq 'debug') {
    $sha = (& git -C $RepoRoot rev-parse HEAD).Trim()
    $candidate = Join-Path $RepoRoot "tests\debug\results\$sha\$Case\result.json"
    if (Test-Path -LiteralPath $candidate) {
        $debugResultPath = $candidate
        $debugResult = Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json
    }
}
$record = [pscustomobject]@{
    mode = $Mode
    architecture = $Architecture
    case_id = $Case
    exit_code = $code
    output_path = (Join-Path $StateRoot 'output.txt')
    result_path = $debugResultPath
    result = $debugResult
}
$temporary = Join-Path $StateRoot 'result.tmp'
$final = Join-Path $StateRoot 'result.json'
[IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 10 -Compress), (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath $temporary -Destination $final -Force
exit $code
