#!/usr/bin/env python3
"""Build the isolated CDI fixture and MEF test helper from explicit local inputs."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

p = argparse.ArgumentParser()
p.add_argument('--build-root', required=True, type=Path)
p.add_argument('--framework-references', required=True, type=Path)
p.add_argument('--csc', required=True, type=Path)
p.add_argument('--output', required=True, type=Path)
a = p.parse_args()
src = Path(__file__).resolve().parent
out = a.output.resolve(); (out/'fixtures').mkdir(parents=True, exist_ok=True)
product = a.build_root/'Extensions/dnSpy.Extension.MCP/bin/Release/net48'
refs = a.framework_references
common = ['dotnet', str(a.csc), '/nologo', '/target:library']
def compile_file(source, output, references, extra=()):
    subprocess.run(common + ['/out:'+str(output)] + ['/r:'+str(x) for x in references] + list(extra) + [str(source)], check=True)
compile_file(src/'CdiHost.cs', out/'fixtures/CdiHost.dll', [refs/x for x in ['mscorlib.dll','System.dll','System.Core.dll']], ['/debug:portable','/optimize-'])
compile_file(src/'FixtureExtension.cs', out/'CdiCoordinatorFixture.x.dll',
             [refs/x for x in ['mscorlib.dll','System.dll','System.Core.dll','System.ComponentModel.Composition.dll','WindowsBase.dll']]
             + [product/x for x in ['dnlib.dll','dnSpy.Contracts.DnSpy.dll']])
manifest = {str(x.relative_to(out)):hashlib.sha256(x.read_bytes()).hexdigest() for x in [out/'CdiCoordinatorFixture.x.dll',out/'fixtures/CdiHost.dll',out/'fixtures/CdiHost.pdb']}
(out/'fixture-build.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print(json.dumps(manifest))
