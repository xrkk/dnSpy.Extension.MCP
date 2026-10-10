# Physical architecture directories must contain no AnyCPU assemblies.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$Backup,
    [Parameter(Mandatory=$true)][string]$Evidence
)
$ErrorActionPreference = 'Stop'
$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if (Test-Path -LiteralPath $Backup) { throw 'Backup already exists' }
$running = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($rootFull+'\',[StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count) { throw 'Both deployment instances must be stopped' }
New-Item -ItemType Directory -Path $Backup | Out-Null
New-Item -ItemType Directory -Path $Evidence -Force | Out-Null
$plans = @()
foreach ($arch in 'x86','x64') {
    $app = Join-Path $rootFull "$arch\app"
    $originalBin = Join-Path $app 'bin'
    $common = Join-Path $rootFull "common\runtime-$arch\bin"
    if (Test-Path -LiteralPath $common) { throw 'Common runtime already exists' }
    $native = @()
    $bindings = @()
    $manifest = @(Get-ChildItem -LiteralPath $originalBin -Recurse -File | ForEach-Object {
        [pscustomobject]@{Relative=$_.FullName.Substring($originalBin.Length+1);Sha256=(Get-FileHash $_.FullName).Hash}
    })
    foreach ($file in (Get-ChildItem -LiteralPath $originalBin -Recurse -File | Where-Object Extension -in '.exe','.dll')) {
        $info = Get-PeArchitecture $file.FullName
        $relative = $file.FullName.Substring($originalBin.Length+1)
        if ($info.Architecture -eq $arch) { $native += $relative }
        elseif ($info.Architecture -ne 'AnyCPU') { throw 'Opposite architecture in source' }
        else {
            $identity = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
            $token = ([BitConverter]::ToString($identity.GetPublicKeyToken()) -replace '-','').ToLowerInvariant()
            # Unsigned .x.dll extensions are discovered and loaded from the relocated bin.
            if (-not $token) {
                if ($file.Name -notlike '*.x.dll') { throw 'Unmapped unsigned dependency' }
                continue
            }
            $bindings += [pscustomobject]@{Name=$identity.Name;Version=$identity.Version.ToString();
                Token=$token;Culture=$(if($identity.CultureName){$identity.CultureName}else{'neutral'});
                Href=([Uri](Join-Path $common $relative)).AbsoluteUri}
        }
    }
    $savedApp = Join-Path $Backup $arch
    New-Item -ItemType Directory -Path $savedApp | Out-Null
    $configs = @()
    foreach ($file in (Get-ChildItem -LiteralPath $app -Filter '*.exe.config' -File)) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $savedApp $file.Name)
        [xml]$xml = Get-Content -LiteralPath $file.FullName -Raw
        $ns = 'urn:schemas-microsoft-com:asm.v1'
        $manager = New-Object Xml.XmlNamespaceManager($xml.NameTable)
        $manager.AddNamespace('a',$ns)
        $assemblyBinding = $xml.SelectSingleNode('/configuration/runtime/a:assemblyBinding',$manager)
        if (-not $assemblyBinding) { throw 'Missing runtime binding policy' }
        foreach ($binding in $bindings) {
            $dependency = $null
            foreach ($node in $xml.SelectNodes('/configuration/runtime/a:assemblyBinding/a:dependentAssembly',$manager)) {
                $identity = $node.SelectSingleNode('a:assemblyIdentity',$manager)
                if ($identity.GetAttribute('name') -ieq $binding.Name -and
                    $identity.GetAttribute('culture') -ieq $binding.Culture -and
                    $identity.GetAttribute('publicKeyToken') -ieq $binding.Token) {
                    $dependency = $node
                    break
                }
            }
            if (-not $dependency) {
                $dependency = $xml.CreateElement('dependentAssembly',$ns)
                $identity = $xml.CreateElement('assemblyIdentity',$ns)
                $identity.SetAttribute('name',$binding.Name)
                $identity.SetAttribute('publicKeyToken',$binding.Token)
                $identity.SetAttribute('culture',$binding.Culture)
                [void]$dependency.AppendChild($identity)
                [void]$assemblyBinding.AppendChild($dependency)
            }
            $codeBase = $xml.CreateElement('codeBase',$ns)
            $codeBase.SetAttribute('version',$binding.Version)
            $codeBase.SetAttribute('href',$binding.Href)
            [void]$dependency.AppendChild($codeBase)
            # dnSpy.Images and other resources can be loaded by a simple assembly name.
            # Qualify those names so the CLR applies their external codeBase policy.
            if ($binding.Culture -eq 'neutral') {
                $qualified = $xml.CreateElement('qualifyAssembly',$ns)
                $qualified.SetAttribute('partialName',$binding.Name)
                $qualified.SetAttribute('fullName',"$($binding.Name), Version=$($binding.Version), Culture=neutral, PublicKeyToken=$($binding.Token)")
                [void]$assemblyBinding.AppendChild($qualified)
            }
        }
        $candidate = Join-Path $Evidence "$arch-$($file.Name)"
        $xml.Save($candidate)
        $configs += [pscustomobject]@{Target=$file.FullName;Candidate=$candidate;Saved=(Join-Path $savedApp $file.Name)}
    }
    $plans += [pscustomobject]@{Architecture=$arch;App=$app;OriginalBin=$originalBin;Common=$common;
        SavedBin=(Join-Path $savedApp 'bin');Native=$native;Bindings=$bindings;Manifest=$manifest;Configs=$configs}
}
$plans | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $Evidence 'relocation-plan.json') -Encoding UTF8
try {
    foreach ($plan in $plans) {
        New-Item -ItemType Directory -Path (Split-Path $plan.Common) -Force | Out-Null
        Copy-Item -LiteralPath $plan.OriginalBin -Destination $plan.Common -Recurse
        foreach ($file in $plan.Manifest) {
            if ((Get-FileHash (Join-Path $plan.Common $file.Relative)).Hash -ne $file.Sha256) { throw 'Common dependency copy hash mismatch' }
        }
        Move-Item -LiteralPath $plan.OriginalBin -Destination $plan.SavedBin
        New-Item -ItemType Directory -Path $plan.OriginalBin | Out-Null
        foreach ($relative in $plan.Native) {
            $target = Join-Path $plan.OriginalBin $relative
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $plan.Common $relative) -Destination $target
        }
        foreach ($config in $plan.Configs) { Copy-Item -LiteralPath $config.Candidate -Destination $config.Target -Force }
        $actual = @(Get-ChildItem -LiteralPath (Join-Path $rootFull $plan.Architecture) -Recurse -File | Where-Object Extension -in '.exe','.dll' | ForEach-Object {Get-PeArchitecture $_.FullName})
        if (@($actual | Where-Object Architecture -ne $plan.Architecture).Count) { throw 'Architecture directory still contains AnyCPU or opposite architecture' }
    }
} catch {
    foreach ($plan in $plans) {
        if (Test-Path -LiteralPath $plan.SavedBin) {
            if (Test-Path -LiteralPath $plan.OriginalBin) { Remove-Item -LiteralPath $plan.OriginalBin -Recurse }
            Move-Item -LiteralPath $plan.SavedBin -Destination $plan.OriginalBin
        }
        foreach ($config in $plan.Configs) { Copy-Item -LiteralPath $config.Saved -Destination $config.Target -Force }
        if (Test-Path -LiteralPath $plan.Common) { Remove-Item -LiteralPath $plan.Common -Recurse }
    }
    throw
}
$results = @($plans | ForEach-Object {
    [pscustomobject]@{Architecture=$_.Architecture;CommonBin=$_.Common;MovedFiles=$_.Manifest.Count;
        ExternalBindings=$_.Bindings.Count;ArchitectureFiles=@(Get-ChildItem -LiteralPath (Join-Path $rootFull $_.Architecture) -Recurse -File | Where-Object Extension -in '.exe','.dll' | ForEach-Object {Get-PeArchitecture $_.FullName});
        PortableSettingsSha256=(Get-FileHash (Join-Path $_.Common 'dnSpy.xml')).Hash}
})
$results | ConvertTo-Json -Depth 6 | Tee-Object -FilePath (Join-Path $Evidence 'relocation-result.json')
