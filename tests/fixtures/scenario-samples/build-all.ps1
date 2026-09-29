# Scenario sample builder (P03 IMP-302): compiles the corpus sample set on the
# .240 VM with the .NET Framework csc. Idempotent: rebuilds into fresh output
# dirs. Determinism note (AUD-403): classic csc is not reproducible (MVID/
# timestamps differ per build); acceptance uses the semantic-equivalence arm
# of ACC-047 (key decompiled surface compared, metadata drift tolerated).
param(
    [string]$SrcDir = "E:\dnspy-scenario\stage\scenario-samples\src",
    [string]$OutRoot = "E:\dnspy-scenario\samples\scenario",
    [string]$Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Csc)) { throw "csc not found: $Csc" }

$libs = @('license-01','hooktarget-01','malfeat-01','obfuscated-01','renametree-01','constmatrix-01','unitymsgs-01','resource-01')

foreach ($name in $libs) {
    $dir = Join-Path $OutRoot $name
    New-Item -ItemType Directory -Force $dir | Out-Null
    & $Csc /nologo /target:library /optimize+ "/out:$(Join-Path $dir "$name.dll")" (Join-Path $SrcDir "$name.cs") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "csc failed for $name" }
}

# resource-01: build the embedded .resources via ResourceWriter, then recompile with /res
$resDir = Join-Path $OutRoot 'resource-01'
$resFile = Join-Path $SrcDir 'strings.resources'
$rw = New-Object System.Resources.ResourceWriter($resFile)
$rw.AddResource('greeting', 'hello-resource-01')
$rw.AddResource('payload_hint', 'artifact-write-target')
$rw.AddResource('count', 'three-entries')
$rw.Generate(); $rw.Close()
& $Csc /nologo /target:library /optimize+ "/res:$resFile,ResourceSample.strings" "/out:$(Join-Path $resDir 'resource-01.dll')" (Join-Path $SrcDir 'resource-01.cs') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'csc failed for resource-01' }

# xref pair: b first, then a with reference
$dirB = Join-Path $OutRoot 'xref-b'; New-Item -ItemType Directory -Force $dirB | Out-Null
& $Csc /nologo /target:library /optimize+ "/out:$(Join-Path $dirB 'xref-b.dll')" (Join-Path $SrcDir 'xref-b.cs') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'csc failed for xref-b' }
$dirA = Join-Path $OutRoot 'xref-a'; New-Item -ItemType Directory -Force $dirA | Out-Null
& $Csc /nologo /target:library /optimize+ "/r:$(Join-Path $dirB 'xref-b.dll')" "/out:$(Join-Path $dirA 'xref-a.dll')" (Join-Path $SrcDir 'xref-a.cs') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'csc failed for xref-a' }

# runtarget-01: console exe
$dirR = Join-Path $OutRoot 'runtarget-01'; New-Item -ItemType Directory -Force $dirR | Out-Null
& $Csc /nologo /target:exe /optimize+ "/out:$(Join-Path $dirR 'runtarget-01.exe')" (Join-Path $SrcDir 'runtarget-01.cs') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'csc failed for runtarget-01' }

Get-ChildItem $OutRoot -Recurse -File | ForEach-Object {
    [pscustomobject]@{ path = $_.FullName.Substring($OutRoot.Length + 1); size = $_.Length;
                       sha256 = (Get-FileHash $_.FullName).Hash.ToLowerInvariant() }
} | ConvertTo-Json | Set-Content (Join-Path $OutRoot 'build-manifest.json') -Encoding UTF8
Write-Host "SAMPLES BUILT:" (Get-ChildItem $OutRoot -Recurse -File -Filter *.dll | Measure-Object | Select-Object -ExpandProperty Count) "dll"
