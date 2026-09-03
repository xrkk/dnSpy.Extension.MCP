param(
    [Parameter(Mandatory=$true)][string]$Case,
    [Parameter(Mandatory=$true)][string]$RepoRoot,
    [Parameter(Mandatory=$true)][string]$StateRoot
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $StateRoot | Out-Null
Set-Location $RepoRoot
$output = & powershell -NoProfile -ExecutionPolicy Bypass -File tests\edit\run-edit-tests.ps1 -Case $Case 2>&1 | Out-String
$code = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $StateRoot 'output.txt'), $output, (New-Object Text.UTF8Encoding($false)))
$summary = Get-ChildItem -LiteralPath C:\dnspy-mcp-artifacts\edit-tests -Filter summary.json -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$record = [pscustomobject]@{
    exit_code = $code
    summary_path = if($summary){ $summary.FullName } else { $null }
    summary = if($summary){ Get-Content -LiteralPath $summary.FullName -Raw | ConvertFrom-Json } else { $null }
}
$temporary = Join-Path $StateRoot 'result.tmp'
$final = Join-Path $StateRoot 'result.json'
[IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 20 -Compress), (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath $temporary -Destination $final -Force
exit $code
