#!/usr/bin/env python3
"""Real Windows MCP compile/import/review/commit/navigation/export smoke test.
Requires an isolated dnSpy host and a net48 Plain48.dll containing only
T004Plain.Simple.Baseline(). Evidence is written even when a call fails.
"""
import argparse
import json
import subprocess
from pathlib import Path
import uuid
from dnspy_mcp import DnSpyClient

SOURCE = '''using System.Collections.Generic;
using System.Threading.Tasks;
namespace T004Plain { public class Simple {
public static int Baseline() { return 1; }
public static async Task<int> AddedAsync(int x) { await Task.Delay(1); return x + 1000; }
public static IEnumerable<int> AddedIter(int x) { yield return x; yield return x + 1; }
} }'''

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--url', required=True)
    p.add_argument('--fixture', required=True)
    p.add_argument('--evidence', required=True)
    p.add_argument('--runtime-host', required=True, help='Windows PowerShell (.NET Framework) executable')
    a = p.parse_args()
    c = DnSpyClient(a.url, timeout=60)
    records = []
    tx = None
    def call(name, **args):
        if name.startswith('edit_'):
            args.setdefault('request_id', str(uuid.uuid4()))
        value = c.call_tool_json(name, args)
        records.append({'tool': name, 'arguments': args, 'response': value})
        if name.startswith('edit_') and not value.get('ok'):
            raise AssertionError(json.dumps(value))
        print('PASS', name, flush=True)
        return value.get('result', value)
    try:
        c.initialize()
        opened = call('open_files', paths=[a.fixture])
        assert opened.get('failed_count', 0) == 0, opened
        compiled = call('edit_compile', assembly_name='Plain48', compilation_kind='edit_class',
                        documents=[{'path': 'Plain48.cs', 'content': SOURCE}])['compile']
        assert compiled['success'], compiled
        tx = call('edit_begin', assembly_name='Plain48')['transaction']['transaction_id']
        imported = call('edit_import', transaction_id=tx, expected_revision=0, compile_id=compiled['compile_id'],
             targets=[{'compiled':'T004Plain.Simple::AddedAsync(System.Int32)', 'action':'add'},
                      {'compiled':'T004Plain.Simple::AddedIter(System.Int32)', 'action':'add'}])
        revision = imported['transaction']['work_revision']
        review = call('edit_review', transaction_id=tx, expected_revision=revision)
        commit = call('edit_commit', transaction_id=tx, expected_revision=revision,
                      review_id=review['review']['review_id'], review_revision=revision,
                      confirmed_risk_ids=[r['risk_id'] for r in review['risks'] if r['confirmation_required']])
        tx = None
        lineage = commit['history']['lineage_id']
        head = commit['checkpoint']['checkpoint_id']
        parent = call('edit_undo', lineage_id=lineage, expected_checkpoint_id=head)['to_checkpoint_id']
        call('edit_redo', lineage_id=lineage, expected_checkpoint_id=parent, child_checkpoint_id=head)
        call('edit_undo', lineage_id=lineage, expected_checkpoint_id=head)
        tx = call('edit_begin', assembly_name='Plain48')['transaction']['transaction_id']
        branch_import = call('edit_import', transaction_id=tx, expected_revision=0,
                             compile_id=compiled['compile_id'], targets=[
                             {'compiled':'T004Plain.Simple::AddedAsync(System.Int32)', 'action':'add'}])
        revision = branch_import['transaction']['work_revision']
        review = call('edit_review', transaction_id=tx, expected_revision=revision)
        branch = call('edit_commit', transaction_id=tx, expected_revision=revision,
                      review_id=review['review']['review_id'], review_revision=revision,
                      confirmed_risk_ids=[r['risk_id'] for r in review['risks'] if r['confirmation_required']])
        tx = None
        assert branch['history']['lineage_id'] == lineage
        assert branch['checkpoint']['checkpoint_id'] != head
        ticket = call('edit_restore', lineage_id=lineage, checkpoint_id=head, action='assess')['replay']
        assert ticket['classification'] == 'exact', ticket
        call('edit_restore', lineage_id=lineage, checkpoint_id=head, action='apply',
             replay_id=ticket['replay_id'], expected_live_fingerprint=branch['fingerprints']['after'])
        exported = call('edit_export', lineage_id=lineage, checkpoint_id=head)
        output = exported['output']
        import hashlib
        data = Path(output['path']).read_bytes()
        assert len(data) == output['length'] and hashlib.sha256(data).hexdigest() == output['sha256']
        print('PASS export bytes and SHA256', flush=True)
        path = output['path'].replace("'", "''")
        command = ("$ErrorActionPreference='Stop'; $a=[Reflection.Assembly]::LoadFile('" + path +
                   "'); $t=$a.GetType('T004Plain.Simple'); "
                   "$task=$t.GetMethod('AddedAsync').Invoke($null,@([int]21)); "
                   "$value=$task.GetAwaiter().GetResult(); if($value -ne 1021){throw 'async value'}; "
                   "$items=$t.GetMethod('AddedIter').Invoke($null,@([int]21)); "
                   "$values=@(foreach($v in $items){$v}); if(($values -join ',') -ne '21,22'){throw 'iterator values'}; "
                   "Write-Output 'PASS CLR async=1021 iterator=21,22'")
        runtime = subprocess.run([a.runtime_host, '-NoProfile', '-NonInteractive', '-Command', command],
                                 capture_output=True, text=True, timeout=30)
        records.append({'runtime_host': a.runtime_host, 'exit': runtime.returncode,
                        'stdout': runtime.stdout, 'stderr': runtime.stderr})
        assert runtime.returncode == 0, runtime.stdout + runtime.stderr
        print(runtime.stdout.strip(), flush=True)
        print('PASS net48 real compile/import/history chain', flush=True)
    finally:
        if tx is not None:
            call('edit_rollback', transaction_id=tx)
        Path(a.evidence).write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding='utf-8')
        c.close()

if __name__ == '__main__':
    main()
