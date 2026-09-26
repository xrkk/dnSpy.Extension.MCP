"""Public v2 PDB migration regression. Provision an isolated host and pass root, URL, fixture, case."""
import hashlib,json,sys,uuid
from pathlib import Path
from dnspy_mcp import DnSpyClient
root=Path(sys.argv[1]);url=sys.argv[2];fixture=sys.argv[3];case=sys.argv[4]
lineage='lineage-c8650beb46dd4e2678cff8f2164db531'
method='checkpoint-66fbfb78816b8ebc971a5f3b029c14fe'
package=root/(case+'-artifact')/'edit-checkpoints'/(lineage+'.dnspy-mcp-checkpoints')
evidence=root/'runs'/(case+'-evidence.json')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest() if p.is_file() else None
def state():return {'package':sha(package),'outputs':{str(p):sha(p) for p in (root/(case+'-artifact')/'edit-output').rglob('*') if p.is_file()}}
records={'url':url,'fixture':fixture,'calls':[]}
def save():evidence.write_text(json.dumps(records,ensure_ascii=False,indent=2)+'\n')
c=DnSpyClient(url,client_name='t076-confirmed-public',timeout=120);c.initialize()
def call(name,args):
    rpc_id=str(uuid.uuid4())
    request=json.loads(json.dumps({'jsonrpc':'2.0','id':rpc_id,'method':'tools/call','params':{'name':name,'arguments':args}},ensure_ascii=False))
    before=state();wire=c.request('tools/call',request['params'],request_id=rpc_id);envelope=wire.get('structuredContent') or json.loads(wire['content'][0]['text'])
    records['calls'].append({'rpc_request_id':rpc_id,'request':request,'wire':wire,'envelope':envelope,'before':before,'after':state()});save();return envelope
def expect(condition, label):
    records.setdefault('assertions',[]).append({'name':label,'pass':bool(condition)});save()
    if not condition: raise AssertionError(label)
def code(response):return response.get('error',{}).get('code')
try:
    records['session_id']=c.session_id;records['initial']=state();save()
    call('open_files',{'paths':[fixture]})
    begin=call('edit_begin',{'request_id':str(uuid.uuid4()),'assembly_name':'TestIL'})
    expect(begin.get('ok') is True,'begin old producer image')
    if begin.get('ok'):
        result=begin['result'];records['live_fingerprint']=result['fingerprints']['baseline_live'];save()
        expect(call('edit_rollback',{'request_id':str(uuid.uuid4()),'transaction_id':result['transaction']['transaction_id']}).get('ok') is True,'rollback read-only binding')
        assess=call('edit_restore',{'request_id':str(uuid.uuid4()),'lineage_id':lineage,'checkpoint_id':method,'action':'assess'})
        replay=assess.get('result',{}).get('replay',{})
        expect(replay.get('classification')=='validated_drift','old v2 PDB head validated drift')
        if replay.get('replay_id'):
            args={'request_id':str(uuid.uuid4()),'lineage_id':lineage,'checkpoint_id':method,'action':'apply',
                'replay_id':replay['replay_id'],'expected_live_fingerprint':records['live_fingerprint'],'confirm_validated_drift':False}
            denied=call('edit_restore',args)
            expect(code(denied)=='EDIT_REPLAY_CONFIRMATION_REQUIRED','explicit confirmation required')
            expect(records['calls'][-1]['before']==records['calls'][-1]['after'],'no-confirm package and output unchanged')
            mismatch=dict(args);mismatch['request_id']=str(uuid.uuid4());mismatch['confirm_validated_drift']=True;mismatch['expected_live_fingerprint']='0'*64
            wrong_live=call('edit_restore',mismatch)
            expect(code(wrong_live)=='EDIT_HISTORY_CONFLICT','mismatched live fingerprint rejected')
            expect(records['calls'][-1]['before']==records['calls'][-1]['after'],'mismatched live package and output unchanged')
            args['request_id']=str(uuid.uuid4());args['confirm_validated_drift']=True
            migrated=call('edit_restore',args)
            expect(migrated.get('ok') is True,'confirmed same-head migration succeeds')
            if migrated.get('ok'):
                stale=dict(args);stale['request_id']=str(uuid.uuid4())
                expect(code(call('edit_restore',stale))=='EDIT_HISTORY_CONFLICT','old ticket rejects changed head')
                expect(code(call('edit_undo',{'request_id':str(uuid.uuid4()),'lineage_id':lineage,'expected_checkpoint_id':method}))=='EDIT_HISTORY_CONFLICT','wrong head rejects undo')
            history=call('edit_history',{'lineage_id':lineage,'page_size':100})
            records['migration']=migrated;save()
            if migrated.get('ok'):
                first=migrated['result']['migration_checkpoint']['checkpoint_id']
                expect(first!=method and migrated['result']['from_checkpoint_id']==method,'migration child retains original')
                records['first_migration']=first;save()
                call('edit_status',{})
                expect(call('edit_export',{'request_id':str(uuid.uuid4()),'lineage_id':lineage,'checkpoint_id':first,
                    'output_path':'edit-output/t076/migration-first.dll'}).get('ok') is True,'migration child export')
                call('edit_undo',{'request_id':str(uuid.uuid4()),'lineage_id':lineage,'expected_checkpoint_id':first})
                reticket=call('edit_restore',{'request_id':str(uuid.uuid4()),'lineage_id':lineage,'checkpoint_id':method,'action':'assess'})
                ret=reticket.get('result',{}).get('replay',{})
                if ret.get('replay_id'):
                    again={'request_id':str(uuid.uuid4()),'lineage_id':lineage,'checkpoint_id':method,'action':'apply',
                        'replay_id':ret['replay_id'],'expected_live_fingerprint':records['live_fingerprint'],'confirm_validated_drift':True}
                    expect(call('edit_restore',again).get('ok') is True,'confirmed original can create second child')
                    call('edit_history',{'lineage_id':lineage,'page_size':100})
    records['final']=state();save()
finally:c.close();save()
print(json.dumps({'begin':begin.get('ok'),'calls':len(records['calls']),'errors':[x['envelope'].get('error',{}).get('code') for x in records['calls'] if x['envelope'].get('ok') is False]}))
