#!/usr/bin/env python3
"""Live dnSpy exit-code regression. Run inside the private Windows VM via its fixed MCP endpoint."""
import argparse,ctypes,hashlib,json,subprocess,sys,time,traceback,uuid
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient

p=argparse.ArgumentParser()
p.add_argument('--url',required=True);p.add_argument('--fixture',required=True)
p.add_argument('--arch',choices=('x64','x86'),required=True);p.add_argument('--expected-exit',type=int,required=True)
p.add_argument('--evidence',required=True);a=p.parse_args()
f=Path(a.fixture);out=Path(a.evidence)
d={'fixture':str(f),'sha256':hashlib.sha256(f.read_bytes()).hexdigest(),'arch':a.arch,'expected_exit':a.expected_exit,'calls':[],'verdict':'ERROR'}
def save():out.write_text(json.dumps(d,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def call(name,args=None):
    row={'name':name,'arguments':args or {},'utc':time.time()}
    try:
        wire=c.request('tools/call',{'name':name,'arguments':args or {}})
        row['wire']=wire
        body=wire.get('structuredContent')
        if body is None:body=json.loads(next(x['text'] for x in wire['content'] if x.get('type')=='text'))
        row['response']=body
        return body
    finally:d['calls'].append(row);save()
def result(x):
    if not x.get('ok'):raise RuntimeError(json.dumps(x)[:1200])
    return x['result']
def rid():return str(uuid.uuid4())
def cim():
    cmd="$p=Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -eq '"+str(f).replace("'","''")+"'};@($p|ForEach-Object{[pscustomobject]@{pid=$_.ProcessId;exe=$_.ExecutablePath;ticks=([datetime]$_.CreationDate).ToUniversalTime().Ticks.ToString()}})|ConvertTo-Json -Compress"
    x=subprocess.run(['powershell.exe','-NoProfile','-Command',cmd],capture_output=True,text=True,timeout=20)
    if x.returncode or not x.stdout.strip():raise RuntimeError('CIM identity unavailable: '+x.stderr[:200])
    return json.loads(x.stdout)
c=None;active=None;handle=None
try:
    c=DnSpyClient.connect(a.url,client_name='real-exit-code-'+a.arch,timeout=90)
    launch=result(call('debug_launch',{'request_id':rid(),'target_path':str(f),'expected_sha256':d['sha256'],'launch_mode':'net48-exe','architecture':a.arch,'break_kind':'entry'}))
    active=(launch['session_id'],int(launch['generation']));d['session_id'],d['generation']=active;save()
    for _ in range(80):
        state=call('debug_status')
        if result(state)['state']=='paused':break
        time.sleep(.1)
    else:raise RuntimeError('entry pause absent')
    d['identity_at_entry']=cim();save()
    k=ctypes.windll.kernel32
    k.OpenProcess.argtypes=[ctypes.c_ulong,ctypes.c_int,ctypes.c_ulong];k.OpenProcess.restype=ctypes.c_void_p
    k.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_ulong];k.WaitForSingleObject.restype=ctypes.c_ulong
    k.GetExitCodeProcess.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_ulong)];k.GetExitCodeProcess.restype=ctypes.c_int
    k.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=k.OpenProcess(0x00100000|0x1000,0,int(d['identity_at_entry']['pid']))
    if not handle:raise RuntimeError('cannot open owned target process handle')
    for _ in range(100):
        status=call('debug_status');state=result(status)['state'];ctx=status.get('debug_context',{})
        if state=='paused':
            continued=call('debug_continue',{'session_id':active[0],'generation':active[1],'pause_epoch':int(ctx['pause_epoch']),'request_id':rid()})
            if not continued.get('ok') and continued.get('error',{}).get('code')!='INVALID_STATE':result(continued)
        if state=='idle':break
        time.sleep(.1)
    d['final_status']=call('debug_status')
    page=result(call('debug_read_events',{'session_id':active[0],'after_cursor':0,'limit':100}))
    d['event_page']=page
    wait=k.WaitForSingleObject(ctypes.c_void_p(handle),3000)
    code=ctypes.c_ulong();queried=bool(k.GetExitCodeProcess(ctypes.c_void_p(handle),ctypes.byref(code)))
    k.CloseHandle(ctypes.c_void_p(handle));handle=None
    os_signed=code.value-(2**32 if code.value>=2**31 else 0)
    d['os_exit']={'wait_result':wait,'query_ok':queried,'unsigned':code.value,'signed':os_signed}
    events=page['events'];process=[x for x in events if x['kind']=='process_exited'];end=[x for x in events if x['kind']=='session_end']
    d['checks']={'os':wait==0 and queried and os_signed==a.expected_exit,
        'public_process':len(process)==1 and process[0]['payload']['exit_code']==a.expected_exit,
        'public_end':len(end)==1 and end[0]['payload'].get('exit_code')==a.expected_exit,
        'no_loss':page['events_lost']==0,'idle':result(d['final_status'])['state']=='idle'}
    d['verdict']='GREEN' if all(d['checks'].values()) else 'RED'
except Exception as ex:d['error']={'type':type(ex).__name__,'message':str(ex),'traceback':traceback.format_exc()}
finally:
    if handle:ctypes.windll.kernel32.CloseHandle(ctypes.c_void_p(handle))
    if c:
        if active:
            try:
                status=call('debug_status')
                if result(status)['state']!='idle':d['terminate']=call('debug_terminate',{'session_id':active[0],'generation':active[1],'request_id':rid()})
            except Exception as ex:d['cleanup_error']=str(ex)
        c.close()
    save()
print(json.dumps({'verdict':d['verdict'],'checks':d.get('checks'),'os_exit':d.get('os_exit')},ensure_ascii=False))
raise SystemExit(0 if d['verdict']=='GREEN' else 1)
