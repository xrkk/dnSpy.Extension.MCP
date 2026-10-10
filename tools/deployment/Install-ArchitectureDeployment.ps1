# Requires a stopped net48 deployment and verified, separately signed host candidates.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$Candidate,
    [Parameter(Mandatory=$true)][string]$Backup
)
$ErrorActionPreference = 'Stop'
. (Join-Path $Candidate 'Get-PeArchitecture.ps1')
if (Test-Path -LiteralPath $Backup) { throw 'Backup destination already exists' }
$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($Backup.StartsWith((Join-Path $rootFull 'x86'),[StringComparison]::OrdinalIgnoreCase) -or
    $Backup.StartsWith((Join-Path $rootFull 'x64'),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Backup must be outside both architecture directories'
}
$running = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($rootFull + '\',[StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count) { throw 'Deployment processes must be stopped before replacement' }
$expectedExtensionHash = 'AEFA85F33D07CE5ECF7067ECFFDA809F8D98E04C4057560782C812FBEEDA52F8'
if ((Get-FileHash (Join-Path $Candidate 'dnSpy.Extension.MCP.x.dll')).Hash -ne $expectedExtensionHash) {
    throw 'Unexpected extension candidate; refresh the reviewed hash before deploying another build'
}
$operations = @()
$settings = @{}
foreach ($arch in 'x86','x64') {
    $app = Join-Path $rootFull "$arch\app"
    $settings[$arch] = (Get-FileHash (Join-Path $app 'bin\dnSpy.xml')).Hash
    foreach ($exe in 'dnSpy.exe','dnSpy.Console.exe') {
        $source = Join-Path $Candidate "$arch\$exe"
        if ((Get-PeArchitecture $source).Architecture -ne $arch) { throw 'Wrong candidate architecture' }
        $operations += [pscustomobject]@{Architecture=$arch;Relative=$exe;Source=$source;Action='replace'}
    }
    foreach ($file in 'dnSpy.Extension.MCP.x.dll','dnSpy.Extension.MCP.x.pdb') {
        $operations += [pscustomobject]@{Architecture=$arch;Relative="bin\Extensions\dnSpy.Extension.MCP\$file";Source=(Join-Path $Candidate $file);Action='replace'}
    }
    $remove = @('dnSpy.pdb','dnSpy.Console.pdb','bin\CacheRead.exe','bin\MefCheck.exe')
    $peFiles = @(Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object Extension -in '.exe','.dll')
    foreach ($file in $peFiles) {
        $info = Get-PeArchitecture $file.FullName
        if ($info.Architecture -ne 'AnyCPU' -and $info.Architecture -ne $arch) {
            $relative = $file.FullName.Substring($app.Length+1)
            $remove += $relative
            # A removed executable's config and PDB must not remain as misleading launch assets.
            if ($file.Extension -eq '.exe') {
                $remove += $relative + '.config'
                $remove += [IO.Path]::ChangeExtension($relative,'.pdb')
            }
        }
    }
    foreach ($relative in ($remove | Select-Object -Unique)) {
        if (Test-Path -LiteralPath (Join-Path $app $relative) -PathType Leaf) {
            $operations += [pscustomobject]@{Architecture=$arch;Relative=$relative;Source=$null;Action='remove'}
        }
    }
}
New-Item -ItemType Directory -Path $Backup | Out-Null
# Back up and hash every changed file before mutating either deployment.
foreach ($op in $operations) {
    $target = Join-Path $rootFull "$($op.Architecture)\app\$($op.Relative)"
    $saved = Join-Path $Backup "$($op.Architecture)\$($op.Relative)"
    if (Test-Path -LiteralPath $target) {
        New-Item -ItemType Directory -Path (Split-Path $saved) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $saved
        if ((Get-FileHash $saved).Hash -ne (Get-FileHash $target).Hash) { throw 'Backup hash mismatch' }
    }
}
$operations | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Backup 'operations.json') -Encoding UTF8
try {
    foreach ($op in $operations) {
        $target = Join-Path $rootFull "$($op.Architecture)\app\$($op.Relative)"
        if ($op.Action -eq 'remove') { Remove-Item -LiteralPath $target }
        else {
            Copy-Item -LiteralPath $op.Source -Destination $target -Force
            if ((Get-FileHash $target).Hash -ne (Get-FileHash $op.Source).Hash) { throw 'Installed hash mismatch' }
        }
    }
    foreach ($arch in 'x86','x64') {
        $app = Join-Path $rootFull "$arch\app"
        if ((Get-FileHash (Join-Path $app 'bin\dnSpy.xml')).Hash -ne $settings[$arch]) { throw 'Settings changed' }
        foreach ($file in (Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object Extension -in '.exe','.dll')) {
            $info = Get-PeArchitecture $file.FullName
            if ($info.Architecture -ne 'AnyCPU' -and $info.Architecture -ne $arch) { throw 'Opposite architecture remains' }
            if ($file.Extension -eq '.exe' -and $info.Architecture -ne $arch) { throw 'Unfixed executable architecture remains' }
        }
    }
} catch {
    foreach ($op in $operations) {
        $saved = Join-Path $Backup "$($op.Architecture)\$($op.Relative)"
        $target = Join-Path $rootFull "$($op.Architecture)\app\$($op.Relative)"
        if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $target -Force }
        elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    throw
}
[pscustomobject]@{Root=$rootFull;Backup=$Backup;ChangedFiles=$operations.Count;SettingsHashes=$settings;ExtensionSha256=$expectedExtensionHash}
