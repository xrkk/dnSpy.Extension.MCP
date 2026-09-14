#!/usr/bin/env python3
"""Isolated real-coordinator CDI checks; requires CdiCoordinatorFixture.x.dll.
The fixture is prepared before begin. Its UI timer acts as an external editor,
including when the edit operation gate is held. No baseline hash is rewritten.
"""
import argparse
import json
from pathlib import Path
import sys
import threading
import time
import uuid
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--url', default='http://localhost:15548/mcp')
    p.add_argument('--assembly', default='CdiHost')
    p.add_argument('--fixture', required=True)
    p.add_argument('--fixture-control', required=True)
    p.add_argument('--case', choices=['all', 'review', 'commit', 'gate', 'navigation', 'recovery'], default='all')
    a = p.parse_args(); root = Path(a.fixture_control); records = []; checks = []; pending_mutation = [False]; thread = None
    c = DnSpyClient(a.url, timeout=60); c.initialize()
    control_client = DnSpyClient(a.url, timeout=20)
    control_client.session_id = c.session_id
    control_client.protocol_version = c.protocol_version
    def uid(): return str(uuid.uuid4())
    def call(name, args=None):
        try: v = (control_client if name == "edit_test_barrier" else c).call_tool_json(name, args or {})
        except Exception as e:
            raw = str(e); start = raw.find('{')
            try: v = json.loads(raw[start:]) if start >= 0 else {'error': {'code': 'TRANSPORT', 'message': raw}}
            except ValueError: v = {'error': {'code': 'TRANSPORT', 'message': raw}}
        records.append({'tool': name, 'args': args or {}, 'response': v})
        return v
    def check(name, ok):
        checks.append({'name': name, 'pass': bool(ok)}); print(('PASS ' if ok else 'FAIL ') + name, flush=True)
        if not ok: raise AssertionError(name)
    def result(v): return v.get('result', {})
    def error(v): return v.get('error', {}).get('code')
    def control(command):
        out = root/'fixture-result.txt'
        if command != 'restore': pending_mutation[0] = True
        if out.exists(): out.unlink()
        tmp = root/'fixture-command.tmp'; tmp.write_text(command, encoding='utf-8'); tmp.replace(root/'fixture-command.txt')
        deadline = time.monotonic() + 10
        while not out.exists():
            if (root/'fixture-error.txt').exists(): raise RuntimeError((root/'fixture-error.txt').read_text(encoding='utf-8-sig'))
            if time.monotonic() > deadline: raise TimeoutError('fixture command')
            time.sleep(.05)
        lines = out.read_text(encoding='utf-8-sig').splitlines(); check('fixture '+command, len(lines)==3 and lines[0]=='OK')
        records.append({'fixture': command, 'before': lines[1], 'after': lines[2]})
        if command == 'restore': pending_mutation[0] = False
        return lines[1:]
    def begin():
        v = call('edit_begin', {'request_id':uid(), 'assembly_name':a.assembly}); check('begin', v.get('ok'))
        return result(v)['transaction']['transaction_id']
    def apply(tx, tag):
        v=call('edit_apply', {'request_id':uid(),'transaction_id':tx,'expected_revision':0,'operation':{'kind':'module_update','name':tag}})
        check('apply',v.get('ok'))
    def review(tx): return call('edit_review', {'request_id':uid(),'transaction_id':tx,'expected_revision':1})
    def commit(tx, rv):
        r=result(rv)
        return call('edit_commit',{'request_id':uid(),'transaction_id':tx,'expected_revision':1,'review_id':r['review']['review_id'],'review_revision':1,'confirmed_risk_ids':[x['risk_id'] for x in r['risks'] if x['confirmation_required']]})
    def rollback(tx): check('rollback',call('edit_rollback',{'request_id':uid(),'transaction_id':tx}).get('ok'))
    def files():
        import hashlib
        return {str(x.relative_to(root)):hashlib.sha256(x.read_bytes()).hexdigest() for x in (root/'artifacts'/'edit-checkpoints').rglob('*') if x.is_file()}
    try:
        opened=call('open_files',{'paths':[a.fixture]});check('open',opened.get('failed_count')==0)
        deadline=time.monotonic()+15
        while not (root/'fixture-ready.txt').exists():
            if (root/'fixture-error.txt').exists():raise RuntimeError((root/'fixture-error.txt').read_text(encoding='utf-8-sig'))
            if time.monotonic()>deadline:raise TimeoutError('fixture initialization')
            time.sleep(.05)
        for mode in ['review','commit','gate']:
            if a.case not in ['all',mode]:continue
            for shape in ['flags','hoisted']:
                tx=begin();apply(tx,'CdiHost-'+mode+'-'+shape);rv=None
                if mode!='review': rv=review(tx);check('clean review',rv.get('ok'))
                before_files=files();res={};thread=None
                if mode=='gate':
                    check('barrier reset',call('edit_test_barrier',{'action':'reset'}).get('ok'))
                    check('barrier arm',call('edit_test_barrier',{'action':'arm','name':'commit_dispatcher_queued'}).get('ok'))
                    thread=threading.Thread(target=lambda:res.update(value=commit(tx,rv)));thread.start()
                    deadline=time.monotonic()+15
                    while not result(call('edit_test_barrier',{'action':'snapshot'})).get('entered'):
                        if time.monotonic()>deadline:raise TimeoutError('barrier enter')
                        time.sleep(.1)
                before,changed=control(shape);check('content-only drift '+shape,before!=changed)
                if mode=='gate':
                    call('edit_test_barrier',{'action':'release'});thread.join(20);check('commit ended',not thread.is_alive());v=res['value']
                else:v=review(tx) if mode=='review' else commit(tx,rv)
                check(mode+' rejects '+shape,error(v)=='EDIT_LIVE_MODULE_CONFLICT')
                check('checkpoint bytes unchanged',files()==before_files)
                old,new=control('restore');check('exact guard restoration',old==changed and new==before)
                if mode=='gate':
                    check('second gate ended transaction',v.get('state')=='idle')
                    call('edit_test_barrier',{'action':'reset'})
                    tx=begin();apply(tx,'CdiHost-gate-restored')
                check('fresh review after restore',review(tx).get('ok'));rollback(tx)
        if a.case in ['all','navigation']:
            heads=[]
            for tag in ['one','two']:
                tx=begin();apply(tx,'CdiHost-'+tag);rv=review(tx);check('navigation clean review',rv.get('ok'));cm=commit(tx,rv);check('commit',cm.get('ok'));heads.append(result(cm))
            lid=heads[-1]['history']['lineage_id'];head=heads[-1]['checkpoint']['checkpoint_id']
            before_files=files();before,changed=control('flags')
            v=call('edit_undo',{'request_id':uid(),'lineage_id':lid,'expected_checkpoint_id':head})
            check('idle undo rejects CDI drift',error(v) in ['EDIT_LINEAGE_DIVERGED','EDIT_LIVE_MODULE_CONFLICT','EDIT_HISTORY_CONFLICT'])
            check('undo failure has no store write',files()==before_files)
            old,new=control('restore');check('undo exact restore',old==changed and new==before)
            clean=call('edit_undo',{'request_id':uid(),'lineage_id':lid,'expected_checkpoint_id':head})
            check('undo succeeds after restore',clean.get('ok'))
            parent=result(clean)['to_checkpoint_id']; before_files=files();before,changed=control('hoisted')
            v=call('edit_redo',{'request_id':uid(),'lineage_id':lid,'expected_checkpoint_id':parent,'child_checkpoint_id':head})
            check('idle redo rejects CDI drift',error(v) in ['EDIT_LINEAGE_DIVERGED','EDIT_LIVE_MODULE_CONFLICT','EDIT_HISTORY_CONFLICT'])
            check('redo failure has no store write',files()==before_files)
            old,new=control('restore');check('redo exact restore',old==changed and new==before)
            check('redo succeeds after restore',call('edit_redo',{'request_id':uid(),'lineage_id':lid,'expected_checkpoint_id':parent,'child_checkpoint_id':head}).get('ok'))
        if a.case in ['all','recovery']:
            tx=begin();apply(tx,'CdiHost-partial');rv=review(tx);check('recovery clean review',rv.get('ok'))
            check('arm finalize fault',call('edit_test_storage_fault',{'action':'arm','stage':'finalize'}).get('ok'))
            cm=commit(tx,rv);check('partial commit reached',cm.get('state')=='committed_without_checkpoint')
            recovery_id=cm['error']['details']['recovery_id'];before_files=files();before,changed=control('flags')
            for action in ['retry_checkpoint','undo_live']:
                v=call('edit_recover',{'request_id':uid(),'recovery_id':recovery_id,'action':action})
                check(action+' rejects CDI drift',error(v)=='EDIT_HISTORY_CONFLICT')
                check(action+' no store write',files()==before_files)
            old,new=control('restore');check('recovery exact restore',old==changed and new==before)
            check('recovery undo succeeds after restore',call('edit_recover',{'request_id':uid(),'recovery_id':recovery_id,'action':'undo_live'}).get('ok'))
    except Exception as e:
        checks.append({'name':'exception','pass':False,'detail':repr(e)});print('ERROR '+repr(e),flush=True)
    finally:
        try:
            if thread is not None and thread.is_alive():
                call('edit_test_barrier', {'action':'release'}); thread.join(20)
            if pending_mutation[0]: control('restore')
        except Exception as e: checks.append({'name':'fixture cleanup','pass':False,'detail':repr(e)})
        (root/'evidence'/('coordinator-'+a.case+'-actions.json')).write_text(json.dumps(records,indent=2),encoding='utf-8')
        (root/'evidence'/('coordinator-'+a.case+'-checks.json')).write_text(json.dumps(checks,indent=2),encoding='utf-8');control_client.session_id=None;control_client.close();c.close()
    return 0 if checks and all(x['pass'] for x in checks) else 1

if __name__=='__main__':sys.exit(main())
