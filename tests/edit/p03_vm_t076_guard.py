"""Public old-PDB divergence guard for package-baseline or derived image-only input."""
import hashlib,json,sys,uuid
from pathlib import Path
from dnspy_mcp import DnSpyClient
root=Path(sys.argv[1]);url=sys.argv[2];fixture=sys.argv[3];case=sys.argv[4]
lineage='lineage-c8650beb46dd4e2678cff8f2164db531';method='checkpoint-66fbfb78816b8ebc971a5f3b029c14fe'
artifact=root/(case+'-artifact');package=artifact/'edit-checkpoints'/(lineage+'.dnspy-mcp-checkpoints');evidence=root/'runs'/(case+'-evidence.json')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest() if p.is_file() else None
def state():return {'package':sha(package),'outputs':{str(p):sha(p) for p in (artifact/'edit-output').rglob('*') if p.is_file()}}
d={'calls':[],'fixture':fixture,'url':url}
def save():evidence.write_text(json.dumps(d,ensure_ascii=False,indent=2)+'\n')
c=DnSpyClient(url,client_name='t076-r02-semantic-guard',timeout=120);c.initialize()
def call(name,args):
 rpc=str(uuid.uuid4());request=json.loads(json.dumps({'jsonrpc':'2.0','id':rpc,'method':'tools/call','params':{'name':name,'arguments':args}},ensure_ascii=False))
 before=state();wire=c.request('tools/call',request['params'],request_id=rpc);env=wire.get('structuredContent') or json.loads(wire['content'][0]['text'])
 d['calls'].append({'request':request,'wire':wire,'envelope':env,'before':before,'after':state()});save();return env
def rid():return str(uuid.uuid4())
def expect(value,name):
 d.setdefault('assertions',[]).append({'name':name,'pass':bool(value)});save()
 if not value:raise AssertionError(name)
try:
 d['session_id']=c.session_id;d['initial']=state();save()
 call('open_files',{'paths':[fixture]})
 begin=call('edit_begin',{'request_id':rid(),'assembly_name':'TestIL'})
 d['begin']=begin;save()
 expect(begin.get('ok') is True,'negative live image binds for fingerprint observation')
 if begin.get('ok'):
  fp=begin['result']['fingerprints']['baseline_live']
  expect(call('edit_rollback',{'request_id':rid(),'transaction_id':begin['result']['transaction']['transaction_id']}).get('ok') is True,'negative binding rollback')
  assessment=call('edit_restore',{'request_id':rid(),'lineage_id':lineage,'checkpoint_id':method,'action':'assess'})
  replay=assessment.get('result',{}).get('replay',{})
  expect(replay.get('classification')=='validated_drift','old v2 target remains validated drift')
  if replay.get('replay_id'):
   d['denied']=call('edit_restore',{'request_id':rid(),'lineage_id':lineage,'checkpoint_id':method,'action':'apply','replay_id':replay['replay_id'],'expected_live_fingerprint':fp,'confirm_validated_drift':True});save()
   expect(d['denied'].get('error',{}).get('code')=='EDIT_LINEAGE_DIVERGED','confirmed migration refuses mismatched live content')
   expect(d['calls'][-1]['before']==d['calls'][-1]['after'],'rejected migration leaves package and outputs unchanged')
 history=call('edit_history',{'lineage_id':lineage,'page_size':100})
 expect(next(node['checkpoint_id'] for node in history['result']['checkpoints'] if node['is_head'])==method,'rejected migration preserves original head')
 status=call('edit_status',{})
 expect(status.get('result',{}).get('state')=='idle','rejected migration leaves coordinator idle')
 d['final']=state();save()
 expect(d['initial']==d['final'],'negative run leaves package and outputs unchanged')
finally:c.close();save()
print(json.dumps({'begin':begin.get('ok'),'begin_error':begin.get('error',{}).get('code'),'denied':d.get('denied',{}).get('error',{}).get('code'),'calls':len(d['calls'])}))
