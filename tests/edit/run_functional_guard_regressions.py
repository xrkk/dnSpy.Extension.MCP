#!/usr/bin/env python3
"""Run production fingerprint and extracted dispatcher-apply control flow without WPF.

Only the surrounding host/dispatcher/operation adapters are substitutes. The real
ApplyTransactionToLive body is extracted verbatim on each run; the adapter counts
all attempted forward and inverse writes. This is not a Windows integration test.
All generated source/build output is confined to a fresh temporary directory.
"""
import os
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape

root = Path(__file__).resolve().parents[2]
harness = root / 'tests/edit/FunctionalGuardHarness'
source = (root / 'Editing/EditTransactionCoordinator.cs').read_text()
start = source.index('\tstring ApplyTransactionToLive(')
end = source.index('\n\tDictionary<string, object?> TestStorageFault', start)
method = source[start:end]
dnlib = Path(os.environ.get('DNSPY_GUARD_DNLIB', str(Path.home() / '.nuget/packages/dnlib/4.5.0/lib/net6.0/dnlib.dll')))
if not dnlib.is_file():
    raise SystemExit('dnlib unavailable; set DNSPY_GUARD_DNLIB to the local dnlib 4.5.0 assembly')
with tempfile.TemporaryDirectory(prefix='dnspy-functional-guard-') as temporary:
    dest = Path(temporary)
    (dest / 'GateProbe.cs').write_text((harness / 'GateProbe.cs.in').read_text().replace('@@METHOD@@', method))
    expiry_start = source.index('\tvoid ExpireLocked()')
    expiry_end = source.index('\n\tvoid EndLocked', expiry_start)
    (dest / 'ExpiryProbe.cs').write_text((harness / 'ExpiryProbe.cs.in').read_text().replace('@@METHOD@@', source[expiry_start:expiry_end]))
    includes = [harness / 'ResourceProbe.cs', root / 'Editing/EditResourceCodec.cs', root / 'Editing/EditSourceFileIdentity.cs', root / 'Debugger/FileIdentityModel.cs', harness / 'Program.cs', harness / 'Support.cs', root / 'Editing/EditFingerprint.cs', dest / 'GateProbe.cs', dest / 'ExpiryProbe.cs']
    project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
    project += ''.join('<Compile Include="' + escape(str(path)) + '" />' for path in includes)
    project += '<Reference Include="dnlib"><HintPath>' + escape(str(dnlib)) + '</HintPath></Reference></ItemGroup></Project>'
    (dest / 'GuardRegression.csproj').write_text(project)
    raise SystemExit(subprocess.run(['dotnet', 'run', '--project', str(dest / 'GuardRegression.csproj')], cwd=root).returncode)
