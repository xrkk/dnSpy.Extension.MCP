#!/usr/bin/env python3
"""Validate real Registry outcomes in complete resource-import envelopes.

Argument: directory containing built product DLL and dnlib.dll. Windows I/O and
transaction bookkeeping are fixtures; operation/diff/risk values come from product.
Requires jsonschema. Builds only inside a temporary directory.
"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape
import jsonschema

root = Path(__file__).resolve().parents[2]
binary = Path(sys.argv[1]).resolve()
contract = json.loads((root / 'Editing/Contracts/p03-tool-schemas.json').read_text())
with tempfile.TemporaryDirectory(prefix='dnspy-resource-response-') as tmp:
    dest = Path(tmp)
    project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><AssemblyName>P03StoreHarness</AssemblyName></PropertyGroup><ItemGroup>'
    for name in ('dnSpy.Extension.MCP.x', 'dnlib'):
        project += '<Reference Include="' + name + '"><HintPath>' + escape(str(binary / (name + '.dll'))) + '</HintPath></Reference>'
    (dest / 'Probe.csproj').write_text(project + '</ItemGroup></Project>')
    (dest / 'Program.cs').write_text(r'''using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnSpy.Extension.MCP.Editing;
foreach (var kind in new[] { "managed_resource_add", "win32_resource_add" }) {
    using var module = new ModuleDefUser("probe.exe");
    module.Win32Resources = new dnlib.W32Resources.Win32ResourcesUser();
    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { kind, name="payload", type_name="RCDATA", name_string="payload", lang_id=0, data_base64="AQIDBA==" }));
    var o = EditOperationRegistry.Apply(module, doc.RootElement, new Dictionary<string, IMDTokenProvider>(), 0);
    Console.WriteLine("OUTCOME:" + JsonSerializer.Serialize(new {
        kind=o.Kind, created_object_ids=o.CreatedObjectIds, risks=o.Risks,
        diffs=new[] { new { operation_index=0, kind=o.Kind, target=o.Target, path="metadata/"+o.Kind,
            before=o.Before, after=o.After, risk_ids=o.Risks.Select(r=>r["risk_id"]).ToArray() } }
    }));
}
using var riskModule = new ModuleDefUser("probe.exe");
Console.WriteLine("OPERATION_KINDS:" + JsonSerializer.Serialize(EditWire.OperationKinds));
var type = new TypeDefUser("Probe", "T", riskModule.CorLibTypes.Object.TypeDefOrRef);
riskModule.Types.Add(type);
var method = new MethodDefUser("M", MethodSig.CreateStatic(riskModule.CorLibTypes.Void), MethodAttributes.Private | MethodAttributes.Static);
type.Methods.Add(method);
var objects = new Dictionary<string, IMDTokenProvider> { ["m"] = method };
using var document = JsonDocument.Parse("""{"kind":"method_update","target":{"object_id":"m"},"return_type":"System.Int32","attributes":22}""");
var outcome = EditOperationRegistry.Apply(riskModule, document.RootElement, objects, 0);
Console.WriteLine("OUTCOME:" + JsonSerializer.Serialize(new {
    created_object_ids = outcome.CreatedObjectIds, kind = outcome.Kind,
    risks = outcome.Risks,
    diffs = new[] { new { operation_index = 0, kind = outcome.Kind, target = outcome.Target, path = "metadata/method_update", before = outcome.Before, after = outcome.After, risk_ids = outcome.Risks.Select(r => (string)r["risk_id"]!).ToArray() } }
}));

''')
    run = subprocess.run(['dotnet', 'run', '--project', str(dest / 'Probe.csproj')], text=True, capture_output=True)
    if run.returncode:
        raise SystemExit(run.stdout + run.stderr)
    outcomes = [json.loads(line.removeprefix('OUTCOME:')) for line in run.stdout.splitlines() if line.startswith('OUTCOME:')]
    assert len(outcomes) == 3, run.stdout
    operations = json.loads(next(line.removeprefix('OPERATION_KINDS:') for line in run.stdout.splitlines() if line.startswith('OPERATION_KINDS:')))
    capability = contract['edit_begin']['outputSchema']['oneOf'][0]['properties']['result']['properties']['capabilities']['properties']['operation_kinds']
    jsonschema.Draft202012Validator(capability).validate(operations)
    print('PASS actual capability operation kinds:', len(operations))
    for outcome in outcomes:
        for resource_type in (('embedded', 'linked') if outcome['kind'] == 'managed_resource_add' else (('win32',) if outcome['kind'] == 'win32_resource_add' else ('method',))):
            sha = 'a' * 64
            result = dict(outcome, transaction=dict(transaction_id='edit-1', work_revision=1, review_revision=None,
                operation_count=1, started_at_monotonic_ms=0, last_activity_monotonic_ms=0), operation_index=0,
                fingerprints=dict(baseline_live=sha, current_live=sha, private=sha), review_cleared=True,
                capacity={name:dict(current=0, maximum=1) for name in ('operations','object_ids','normalized_operation_bytes','diff_bytes','apply_cache_entries','apply_cache_bytes')})
            result['capacity'].update(review_tombstone_entries=dict(current=0, maximum=64), review_tombstone_bytes=dict(current=0, maximum=262144))
            result['import'] = dict(vm_path=r'C:\samples\payload.bin', resource_name='payload', resource_type=resource_type,
                file_id='0'*32, length=4, sha256=sha)
            envelope = dict(schema_version='dnspy.edit.v1', ok=True, state='editing', result=result, warnings=[], untrusted_sample_data=True)
            tool = 'edit_resource_import'
            if resource_type == 'method':
                del result['import']
                tool = 'edit_apply'
                assert len(outcome['risks']) == 2
            errors = list(jsonschema.Draft202012Validator(contract[tool]['outputSchema']['oneOf'][0]).iter_errors(envelope))
            assert not errors, '\n'.join(str(list(e.absolute_path)) + ': ' + e.message for e in errors)
            print('PASS complete envelope:', tool, resource_type, outcome['kind'])
