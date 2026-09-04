param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('EDIT-ACC-017','EDIT-ACC-027','EDIT-ACC-030')]
    [string]$Case,
    [string]$BaseUrl = 'http://localhost:15378/',
    [string]$ArtifactRoot = 'C:\dnspy-mcp-artifacts',
    [string]$UiSignalDir = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runner = Join-Path $PSScriptRoot 'run_edit_tests.py'
$python = (Get-Command python -ErrorAction Stop).Source

Push-Location $repo
try {
    $runnerArgs = @($runner, '--case', $Case, '--base-url', $BaseUrl, '--artifact-root', $ArtifactRoot)
    if($UiSignalDir){ $runnerArgs += @('--ui-signal-dir', $UiSignalDir) }
    & $python @runnerArgs
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
