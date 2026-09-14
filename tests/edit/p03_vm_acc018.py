#!/usr/bin/env python3
"""ACC-018 real Windows UIA acceptance on a dedicated deployed dnSpy instance.
Set DNMCP_UI_DEPLOYMENT_ROOT, DNMCP_UI_ARCH and DNMCP_UI_ARTIFACT_SUBDIR.
The directory holds deployed-identity.json, per-architecture pid.txt, fixtures,
and the configured artifact subdirectory. Open MCP Edit Explorer before running.
DNMCP_UI_CLIENT_ROOT optionally selects a staged dnspy_mcp client package.
Records raw MCP/UIA/package facts and screenshots; any failed check fails the run.
"""
import sys,os,json,time,uuid,subprocess,threading,hashlib,zipfile
from pathlib import Path
sys.path.insert(0,os.environ.get('DNMCP_UI_CLIENT_ROOT',str(Path(__file__).resolve().parents[2])))
from dnspy_mcp import DnSpyClient
def check(n,v,d=None):
 checks.append(dict(name=n,passed=bool(v),detail=d));print(('PASS ' if v else 'FAIL ')+n,flush=True)
def call(c,n,a):
 try:v=c.call_tool_json(n,a)
 except Exception as e:
  s=str(e);i=s.find('{')
  try:v=json.loads(s[i:]) if i>=0 else {'ok':False,'error':{'code':'DRIVER','message':s}}
  except:v={'ok':False,'error':{'code':'DRIVER','message':s}}
 calls.append(dict(tool=n,args=a,response=v));return v
def payload(v):return v.get('result',{})
def rid():return str(uuid.uuid4())
def ps(s):
 p=subprocess.run(['powershell','-NoProfile','-NonInteractive','-Command',s],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=45)
 if p.returncode:raise RuntimeError(p.stderr[-1800:])
 return p.stdout.strip()
def ui(label):
 s=base+"""foreach($item in $exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem))){try{$pattern=$item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern);if($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed){$pattern.Expand()}}catch{}}; $items=@($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem))|ForEach-Object {$_.Current.Name}); $paths=@($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem))|ForEach-Object {$names=@($_.Current.Name);$parent=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($_);while($null -ne $parent -and $parent.Current.ControlType -eq [System.Windows.Automation.ControlType]::TreeItem){$names=@($parent.Current.Name)+$names;$parent=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)};[ordered]@{name=$_.Current.Name;ancestors=@($names)}}); $buttons=@($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button))|ForEach-Object {$_.Current.Name}); [ordered]@{state=(ById 'McpEditStateLine').Current.Name;cancel=(ById 'McpEditCancelLine').Current.Name;enabled=(ById 'McpEditCancelButton').Current.IsEnabled;detail=(ById 'McpEditDetailLine').Current.Name;items=$items;paths=$paths;buttons=$buttons}|ConvertTo-Json -Depth 12 -Compress"""
 v=json.loads(ps(s));(out/(label+'.ui.json')).write_text(json.dumps(v,ensure_ascii=False,indent=2));return v
def shot(label):
 path=str(out/(label+'.png')).replace("'","''")
 ps(base+"Add-Type -AssemblyName System.Drawing; $b=$exp.Current.BoundingRectangle; $img=[Drawing.Bitmap]::new([int]$b.Width,[int]$b.Height); $g=[Drawing.Graphics]::FromImage($img); $g.CopyFromScreen([int]$b.X,[int]$b.Y,0,0,$img.Size); $img.Save('"+path+"',[Drawing.Imaging.ImageFormat]::Png); $g.Dispose();$img.Dispose();")
def waitui(label,predicate):
 v={}
 for i in range(8):
  v=ui(label)
  if predicate(v):return v
  time.sleep(.5)
 return v
def begin(c):
 v=call(c,'edit_begin',dict(assembly_name='TestIL',request_id=rid()));check('begin',v.get('ok'),v);t=payload(v).get('transaction',{});return t.get('transaction_id',''),t.get('work_revision',0)
def apply(c,tx,rev,name):return call(c,'edit_apply',dict(request_id=rid(),transaction_id=tx,expected_revision=rev,operation=dict(kind='module_update',name=name)))
def assert_capacity(c,v,label):
 st=call(c,'edit_status',{});meters=payload(st).get('capacity',{})
 expected=[str(k)+': '+str(m['current'])+'/'+str(m['maximum']) for k,m in meters.items() if isinstance(m,dict) and 'current' in m and 'maximum' in m]
 check(label+' capacity matches all MCP meters',bool(expected) and all(x in v['items'] for x in expected),dict(expected=expected,ui=v['items']))

def main():
 global root,arch,r,pid,url,out,checks,calls,base,artifact_subdir
 deployment=os.environ.get('DNMCP_UI_DEPLOYMENT_ROOT')
 if not deployment:
  raise RuntimeError('Set DNMCP_UI_DEPLOYMENT_ROOT to the dedicated deployed instance directory; no shared-instance fallback')
 root=Path(deployment);arch=os.environ.get('DNMCP_UI_ARCH','x64')
 if arch not in ('x64','x86'):raise ValueError('DNMCP_UI_ARCH must be x64 or x86')
 artifact_subdir=os.environ.get('DNMCP_UI_ARTIFACT_SUBDIR','artifacts-final')
 r=root/arch;pid=int((r/'pid.txt').read_text());url='http://localhost:'+str(15540 if arch=='x64' else 15541)+'/mcp'
 out=r/('run-'+uuid.uuid4().hex);out.mkdir();checks=[];calls=[]
 base="""$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new(); Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes; $root=[System.Windows.Automation.AutomationElement]::RootElement; $condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,PIDVALUE),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'McpEditExplorer')); $exp=$root.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition); if($null -eq $exp){throw 'dedicated explorer missing'}; function ById($id){return $exp.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))};
 """.replace('PIDVALUE',str(pid))
 c=DnSpyClient(url,client_name='real-ui-'+arch,timeout=140);c.initialize()
 try:
  check('open_fixture',call(c,'open_files',{'paths':[str(r/'fixtures/TestIL.dll')]}).get('ok',True))
  v=ui('idle');assert_capacity(c,v,'idle');check('U2 real explorer window',bool(v));shot('idle')
  tx,rev=begin(c);a=apply(c,tx,rev,'UiStaged');check('stage operation',a.get('ok'),a)
  rev=payload(a).get('transaction',{}).get('work_revision',rev+1)
  v=waitui('editing',lambda v:tx in v['state'] and any('module_update' in s for s in v['items']))
  st=call(c,'edit_status',{});check('W2 exact state and transaction',payload(st).get('state',st.get('state'))=='editing' and tx in v['state'],dict(mcp=st,ui=v))
  assert_capacity(c,v,'editing')
  check('W2 kind and target',any('module_update|UiStaged' in s for s in v['items']),v)
  check('W3 connected-owner cancel enabled',v['enabled'] and 'canceled locally' in v['cancel'],v)
  check('W4 no commit restore export UI',len(v['buttons'])>=1 and not any(any(w in b.lower() for w in ['commit','restore','export']) for b in v['buttons']),v['buttons']);shot('editing')
  ps(base+"(ById 'McpEditCancelButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();")
  v=waitui('canceled',lambda v:'idle' in v['state']);st=call(c,'edit_status',{});gone=apply(c,tx,rev,'MustFail')
  check('W5 UI rollback same owner', 'idle' in v['state'] and payload(st).get('state',st.get('state'))=='idle' and gone.get('error',{}).get('code')=='EDIT_TRANSACTION_NOT_FOUND',dict(ui=v,mcp=st,gone=gone));shot('canceled')
  tx,rev=begin(c);call(c,'edit_test_barrier',{'action':'arm','name':'apply_before_mutation'})
  response=[];thread=threading.Thread(target=lambda:response.append(apply(c,tx,rev,'BusyUi')),daemon=True);thread.start()
  observer=DnSpyClient(url,client_name='ui-observer',timeout=140);observer.initialize()
  for _ in range(30):
   b=call(observer,'edit_test_barrier',{'action':'snapshot'})
   if payload(b).get('entered'):break
   time.sleep(.1)
  v=waitui('busy',lambda v:not v['enabled'] and tx in v['state']);check('busy actual UI disables cancel',not v['enabled'] and tx in v['state'] and payload(b).get('entered'),dict(ui=v,barrier=b));shot('busy')
  call(observer,'edit_test_barrier',{'action':'release'});thread.join(30);check('busy apply completed',bool(response and response[0].get('ok')),response);call(observer,'edit_test_barrier',{'action':'reset'})
  c.close();v=waitui('owner-closed',lambda v:'idle' in v['state']);check('owner-close safely terminates private transaction','idle' in v['state'],v);observer.close()
  c=DnSpyClient(url,client_name='ui-history-'+arch,timeout=140);c.initialize();tx,rev=begin(c);a=apply(c,tx,rev,'UiCommitted');rev=payload(a).get('transaction',{}).get('work_revision',rev+1)
  review=call(c,'edit_review',dict(request_id=rid(),transaction_id=tx,expected_revision=rev));rr=payload(review).get('review',{});check('review',review.get('ok'),review)
  v=waitui('reviewed',lambda v:any(rr.get('review_id','?') in x for x in v['items']))
  check('review id shown before commit',any(rr.get('review_id','?') in x for x in v['items']),v)
  check('risks match MCP review',all(any(risk['risk_id'] in x for x in v['items']) for risk in payload(review).get('risks',[])),v)
  check('diffs match MCP review',all(any(d['kind']+'/'+d['target'] in x for x in v['items']) for d in payload(review).get('diffs',[])),v);shot('reviewed')
  commit=call(c,'edit_commit' ,dict(request_id=rid(),transaction_id=tx,expected_revision=rev,review_id=rr.get('review_id',''),review_revision=rr.get('review_revision',rev),confirmed_risk_ids=rr.get('required_confirmation_ids',[])));check('real commit',commit.get('ok'),commit)
  h=call(c,'edit_history',{});check('history MCP query',h.get('ok'),h);(out/'history.json').write_text(json.dumps(h,indent=2));v=waitui('history',lambda v:sum(s.startswith('checkpoint checkpoint-') and ' image ' in s for s in v['items'])>=2)
  assert_capacity(c,v,'history')
  rows=[s for s in v['items'] if s.startswith('checkpoint checkpoint-') and ' image ' in s];check('idle checkpoint parent-child rows',len(rows)>=2 and 'idle' in v['state'],v)
  packages={}
  for package in (r/artifact_subdir).rglob('*.dnspy-mcp-checkpoints'):
   with zipfile.ZipFile(package) as z:
    manifest=json.loads(z.read('manifest.json'))
    for node in manifest['checkpoints']:
     node['zip_time']='%04d-%02d-%02dT%02d:%02d:%02d'%z.getinfo(node['operation_entry']).date_time
     packages[node['checkpoint_id']]=node
  check('UI lineage IDs and heads match MCP',all(any(l['lineage_id'] in row and l['head_checkpoint_id'] in row for row in v['items']) for l in payload(h).get('lineages',[])),v)
  check('history contains a real family',bool(payload(h).get('lineages')),h)
  for lineage in payload(h).get('lineages',[]):
   check('family lineage UIA hierarchy '+lineage['lineage_id'],any(p['name'].startswith(lineage['lineage_id']+' ') and 'family '+lineage['family_id'] in p['ancestors'][:-1] for p in v['paths']),v['paths'])
  for row in rows:
   checkpoint_id=row.split()[1];node=packages[checkpoint_id];parent_id=node.get('parent_checkpoint_id')
   check('checkpoint UIA parent '+checkpoint_id,any(p['name']==row and len(p['ancestors'])>=3 and (p['ancestors'][-2].startswith('checkpoint '+parent_id+' ') if parent_id else p['ancestors'][-2].startswith('lineage-')) for p in v['paths']),v['paths'])
  # Selecting each real TreeViewItem invokes the actual selected-detail binding.
  for i in range(len(rows)):
   result=ps(base+"$nodes=@($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::TreeItem))|Where-Object {$_.Current.Name -like 'checkpoint checkpoint-* kind * image * semantic *'}); $nodes["+str(i)+"].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); (ById 'McpEditDetailLine').Current.Name")
   (out/('checkpoint-'+str(i)+'.txt')).write_text(result)
   checkpoint_id=rows[i].split()[1];node=packages[checkpoint_id]
   mh=call(c,'edit_history',{'checkpoint_id':checkpoint_id});actual=payload(mh).get('checkpoint',{})
   check('checkpoint UI vs MCP vs package '+str(i),mh.get('ok') and actual.get('result_image_sha256')==node['result_image_sha256'] and all(value in result for value in [checkpoint_id,node['result_image_sha256'],node['result_semantic_fingerprint'],node['review']['review_id'],'structural '+node['review']['structural'],'roundtrip '+node['review']['roundtrip'],node['zip_time']]),dict(ui=result,mcp=actual,package=node))
   check('selected checkpoint detail '+str(i),all(k in result for k in ['image_sha256','semantic','parent','package_entry_time','timezone unknown']),result);shot('checkpoint-'+str(i))
  check('actual checkpoint package exists',len(list((r/artifact_subdir).rglob('*.dnspy-mcp-checkpoints')))>0)
 except Exception as e:
  check('driver completed',False,str(e));raise
 finally:
  try:c.close()
  except:pass
  (out/'calls.json').write_text(json.dumps(calls,ensure_ascii=False,indent=2))
  (out/'summary.json').write_text(json.dumps(dict(head=json.loads((root/'deployed-identity.json').read_text())['head'],arch=arch,pid=pid,checks=checks,passed=sum(x['passed'] for x in checks),failed=sum(not x['passed'] for x in checks)),ensure_ascii=False,indent=2))
  print(str(out),flush=True)
 return 1 if any(not x['passed'] for x in checks) else 0

if __name__=='__main__':
 raise SystemExit(main())
