#!/usr/bin/env python3
"""Fast real-host red/green loop for the missing caught first-chance exception event."""
import argparse,hashlib,json,sys,time,traceback,uuid
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def rid():return str(uuid.uuid4())
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--url',required=True);ap.add_argument('--fixture',required=True);ap.add_argument('--evidence',required=True);ap.add_argument('--arch',required=True);ap.add_argument('--policy',choices=('none','unhandled','first_chance_and_unhandled'),default='first_chance_and_unhandled');ap.add_argument('--expect-exception',choices=('yes','no'),default='yes');a=ap.parse_args()
    fx=Path(a.fixture);out=Path(a.evidence);out.parent.mkdir(parents=True,exist_ok=True);marker=fx.with_name('ExceptionLoop.ran')
    d={'fixture':{'path':str(fx),'sha256':sha(fx)},'arch':a.arch,'policy_requested':a.policy,'expect_exception':a.expect_exception,'calls':[],'marker_before':marker.read_text() if marker.exists() else None,'verdict':'RED'}
    def save():out.write_text(json.dumps(d,ensure_ascii=False,indent=2)+'\n')
    c=None;active=None
    def call(name,args=None):
        q={'name':name,'arguments':args or {}};x={'seq':len(d['calls'])+1,'request':q}
        try:
            wire=c.request('tools/call',q);x['wire']=wire;body=wire.get('structuredContent')
            if body is None:body=json.loads(next(z['text'] for z in wire.get('content',[]) if z.get('type')=='text'))
            x['response']=body;return body
        except Exception as ex:x['exception']={'type':type(ex).__name__,'message':str(ex),'traceback':traceback.format_exc()};raise
        finally:d['calls'].append(x);save()
    def require(x,label):
        if x.get('ok') is not True:raise AssertionError(label+': '+json.dumps(x)[:800])
        return x['result']
    def wait(want):
        last=None
        for _ in range(70):
            last=call('debug_status');state=require(last,'status').get('state')
            if state==want:return last
            time.sleep(.1)
        raise AssertionError('wait '+want+': '+json.dumps(last)[:800])
    try:
        c=DnSpyClient.connect(a.url,client_name='t051-r02-event-'+a.arch,timeout=60)
        launch=call('debug_launch',{'request_id':rid(),'target_path':str(fx),'expected_sha256':sha(fx),'launch_mode':'net48-exe','architecture':a.arch,'break_kind':'entry'})
        lr=require(launch,'launch');active=(lr['session_id'],int(lr['generation']))
        paused=wait('paused')
        policy=call('debug_set_exception_policy',{'session_id':active[0],'generation':active[1],'request_id':rid(),'policy':a.policy})
        pr=require(policy,'policy');d['policy']=pr;save()
        if pr.get('current',{}).get('break_on')!=a.policy:raise AssertionError('policy not accepted')
        event=None;events=[];entry_resumes=0;last_status=None;marker_seen=0
        for _ in range(65):
            last_status=call('debug_status');ctx=last_status.get('debug_context',{})
            got=call('debug_read_events',{'session_id':active[0],'after_cursor':0,'limit':100})
            events=require(got,'events').get('events',[])
            event=next((e for e in events if e.get('kind')=='exception'),None)
            if event and ctx.get('state')=='paused' and 'thread_check' not in d:
                th=event.get('payload',{}).get('thread_handle')
                if th:
                    threads=require(call('debug_list_threads',{'session_id':active[0],'generation':active[1],'pause_epoch':int(ctx['pause_epoch'])}),'threads')
                    d['thread_check']={'handle':th,'listed':any(x.get('thread_handle')==th for x in threads.get('items',[])),'items':threads.get('items',[])}
                    save()
            if marker.exists() and marker.read_text()!=d['marker_before']:
                marker_seen+=1
                if a.expect_exception=='yes' and event:break
                if a.expect_exception=='no' and marker_seen>=15:break
            if ctx.get('state')=='paused' and entry_resumes<3:
                entry_resumes+=1
                cont=call('debug_continue',{'session_id':active[0],'generation':active[1],'pause_epoch':int(ctx['pause_epoch']),'request_id':rid()})
                if cont.get('ok') is not True and not (cont.get('error',{}).get('code')=='INVALID_STATE' and cont.get('debug_context',{}).get('state')=='running'):
                    require(cont,'continue entry phase')
            time.sleep(.15)
        d['marker_after']=marker.read_text() if marker.exists() else None;d['entry_resumes']=entry_resumes;d['final_observed_status']=last_status
        d['last_event_kinds']=[e.get('kind') for e in events];d['exception_event']=event;d['fixture_executed']=d['marker_after'] is not None and d['marker_after']!=d['marker_before']
        expected=a.expect_exception=='yes';observed=isinstance(event,dict)
        d['verdict']='GREEN' if d['fixture_executed'] and observed==expected and (not expected or d.get('thread_check',{}).get('listed') is True) else 'RED';save()
        return 0 if d['verdict']=='GREEN' else 1
    except Exception as ex:
        d['error']={'type':type(ex).__name__,'message':str(ex),'traceback':traceback.format_exc()};return 2
    finally:
        if c:
            if active:
                try:
                    d['terminate']=call('debug_terminate',{'session_id':active[0],'generation':active[1],'request_id':rid()})
                    d['idle']=wait('idle')
                except Exception as ex:d['cleanup_error']=str(ex)
            c.close()
        save();print(json.dumps({'verdict':d['verdict'],'fixture_executed':d.get('fixture_executed'),'event':bool(d.get('exception_event')),'error':d.get('error')},ensure_ascii=False))
if __name__=='__main__':raise SystemExit(main())
