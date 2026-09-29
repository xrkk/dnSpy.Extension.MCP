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
    # run-tests.ps1 made -SettingsFile mandatory and no longer rewrites shared
    # user config: derive a private settings file whose committed snapshot carries
    # the loopback tuple and roots that contain the uploaded fixture (same JSON
    # shape the debug runner writes via New-SnapshotJson).
    $template = Join-Path $env:APPDATA 'dnSpy\dnSpy.xml'
    if (-not (Test-Path -LiteralPath $template)) { throw "fixtures mode: settings template missing: $template" }
    $settingsFile = Join-Path $StateRoot 'dnSpy-settings.xml'
    Copy-Item -LiteralPath $template -Destination $settingsFile -Force
    $sampleRoot = Join-Path $RepoRoot 'tests\fixtures'
    $artifactRoot = Join-Path $StateRoot 'artifact'
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    [xml]$sx = Get-Content -LiteralPath $settingsFile
    $snapNode = $sx.SelectSingleNode("//section[@_='352907a0-9df5-4b2b-b47b-95e504cac301']")
    if (-not $snapNode) { throw "fixtures mode: settings template carries no MCP section: $template" }
    $snapJson = '{"AllowedSampleRoot":"' + ($sampleRoot -replace '\\','\\') + '","ArtifactRoot":"' + ($artifactRoot -replace '\\','\\') + '","DebugToolsEnabled":true,"DedicatedDebugInstanceAcknowledged":true,"EnableServer":true,"Host":"localhost","Port":15378,"RemoteAllowedCidrs":[],"RemoteHostOnlyAcknowledged":false,"RemoteTokenVerifier":null,"SchemaVersion":"dnspy.mcp.settings.v1"}'
    $snapNode.SetAttribute('SettingsSnapshotJson', $snapJson)
    $sx.Save($settingsFile)
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File tests\fixtures\run-tests.ps1 `
        -SkipBuild -Tfm net48 -DnspyExe $dnSpyExe -SettingsFile $settingsFile 2>&1 | Out-String
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
