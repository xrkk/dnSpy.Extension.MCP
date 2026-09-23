#!/usr/bin/env python3
"""Formal ACC-024 evidence for a true checkpoint-finalize partial."""
from __future__ import annotations
import hashlib,json,sys,uuid
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402
URL="http://127.0.0.1:15378/mcp"; FIXTURE=r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"; ARTIFACT_ROOT=Path(r"C:\Tools\dnspy-mcp-edit-tests\artifacts"); CHECKPOINTS=Path.home()/"Desktop"/"dnspy-mcp-artifacts"/"edit-checkpoints"; FAILURES=[]; PASSES=[]
def configure_isolation(c):
    global URL,FIXTURE,ARTIFACT_ROOT,CHECKPOINTS
    URL=c.mcp_url; FIXTURE=c.fixture("TestIL.dll"); ARTIFACT_ROOT=Path(c.artifact_root); CHECKPOINTS=ARTIFACT_ROOT/"edit-checkpoints"
def rid():return str(uuid.uuid4())
def call(c,t,a):
    try:return c.call_tool_json(t,a)
    except Exception as ex:
        s=str(ex);i=s.find("{")
        if i>=0:
            try:return json.loads(s[i:])
            except json.JSONDecodeError:pass
        return {"ok":False,"error":{"code":"DRIVER_TRANSPORT","message":s[:400]}}
def payload(v):
    r=v.get("result") if isinstance(v,dict) else None
    return r if isinstance(r,dict) else {}
def err_code(v):
    r=v.get("error") if isinstance(v,dict) else None
    return str(r.get("code","")) if isinstance(r,dict) else ""
def check(n,c,d=""):
    (PASSES if c else FAILURES).append(n);print(f"{'PASS' if c else 'FAIL'} {n} {d if not c else ''}".rstrip(),flush=True)
def export_variants(lineage,checkpoint,explicit):return [("default","edit_export",{"lineage_id":lineage,"checkpoint_id":checkpoint}),("explicit","edit_export",{"lineage_id":lineage,"checkpoint_id":checkpoint,"output_path":explicit}),("legacy-default","save_assembly",{"assembly_name":"TestIL"}),("legacy-explicit","save_assembly",{"assembly_name":"TestIL","output_path":explicit})]
def file_sha256(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def live_probe(c):
    b=call(c,"edit_begin",{"assembly_name":"TestIL","request_id":rid()});p=payload(b);tx=p.get("transaction",{}).get("transaction_id","");fp=p.get("fingerprints",{}).get("current_live","");bound=p.get("history",{}).get("base_checkpoint_id","")
    rolled=call(c,"edit_rollback",{"request_id":rid(),"transaction_id":tx}) if tx else {}
    return {"ok":bool(b.get("ok")) and bool(rolled.get("ok")),"live_fingerprint":fp,"bound_head":bound}
def recovery_observables(c,lineage,pkg):
    h=payload(call(c,"edit_history",{"lineage_id":lineage,"page_size":100}));rows=[x for x in h.get("checkpoints",[]) if isinstance(x,dict)];probe=live_probe(c)
    return {"nodes":tuple(sorted(str(x.get("checkpoint_id","")) for x in rows)),"head":next((str(x.get("checkpoint_id","")) for x in rows if x.get("is_head")),""),"package_sha256":file_sha256(pkg) if pkg.is_file() else "","live_fingerprint":probe["live_fingerprint"],"bound_head":probe["bound_head"],"probe_ok":probe["ok"]}
def idempotence_observables_match(first,replay,expected_head,expected_live):
    return first==replay and first.get("head")==expected_head and first.get("bound_head")==expected_head and first.get("live_fingerprint")==expected_live and first.get("probe_ok") is True and len(first.get("nodes",()))==2 and expected_head in first.get("nodes",()) and bool(first.get("package_sha256"))
def main():
    o=DnSpyClient(URL,client_name="p03-vm-acc024-owner",timeout=90);o.initialize();call(o,"open_files",{"paths":[FIXTURE]})
    b=call(o,"edit_begin",{"assembly_name":"TestIL","request_id":rid()});tr=payload(b).get("transaction",{});tx=tr.get("transaction_id","");rev=int(tr.get("work_revision",0))
    call(o,"edit_apply",{"request_id":rid(),"transaction_id":tx,"expected_revision":rev,"operation":{"kind":"type_update","target":{"token":"0x02000002"},"name":"Acc024Formal"}});rv=call(o,"edit_review",{"request_id":rid(),"transaction_id":tx,"expected_revision":rev+1});rr=payload(rv).get("review",{})
    call(o,"edit_test_storage_fault",{"action":"arm","stage":"finalize"});f=call(o,"edit_commit",{"request_id":rid(),"transaction_id":tx,"expected_revision":rev+1,"review_id":rr.get("review_id",""),"review_revision":rr.get("review_revision",0),"confirmed_risk_ids":[]})
    st=payload(call(o,"edit_status",{}));rec=st.get("recovery",{});recid=str(rec.get("recovery_id",""));temp=Path(str(rec.get("temp",{}).get("path","")));lineage=temp.name.split(".dnspy-mcp-checkpoints",1)[0];post=str(rec.get("post_head_checkpoint_id",""))
    check("024.a true finalize failure enters partial",err_code(f)=="EDIT_CHECKPOINT_COMMIT_FAILED" and st.get("state")=="committed_without_checkpoint" and rec.get("recovery_kind")=="checkpoint_finalize" and rec.get("allowed_actions")==["retry_checkpoint","undo_live"],json.dumps(st)[:320]);o.close()
    c=DnSpyClient(URL,client_name="p03-vm-acc024-reconnect",timeout=90);c.initialize();rs=payload(call(c,"edit_status",{}));r2=rs.get("recovery",{})
    check("024.b reconnect queries same recovery",rs.get("state")=="committed_without_checkpoint" and r2.get("recovery_id")==recid,json.dumps(rs)[:300])
    pending_history=call(c,"edit_history",{"lineage_id":lineage})
    check("024.b staged first-lineage history reports recovery, not internal error",
          err_code(pending_history)=="EDIT_CHECKPOINT_COMMIT_FAILED"
          and pending_history.get("state")=="committed_without_checkpoint"
          and (pending_history.get("error",{}).get("details") or {}).get("recovery_id")==recid,
          json.dumps(pending_history)[:300])
    for label,tool,args in export_variants(lineage,post,str(ARTIFACT_ROOT/"formal-acc024"/"partial.dll")):
        a=dict(args)
        if tool=="edit_export":a["request_id"]=rid()
        x=call(c,tool,a);check(f"024.d {label} export exact block",err_code(x)=="EDIT_EXPORT_BLOCKED",f"actual={err_code(x)}")
    resource_path=ARTIFACT_ROOT/"formal-acc024"/"partial-resource.bin"
    resource=call(c,"edit_resource_export",{"request_id":rid(),"assembly_name":"TestIL","resource_name":"blocked.probe","output_path":str(resource_path)})
    check("024.d partial resource export retains existing gate and no disk effect",
          err_code(resource)=="EDIT_CHECKPOINT_COMMIT_FAILED" and not resource_path.exists(),f"actual={err_code(resource)}")
    pkg=CHECKPOINTS/f"{lineage}.dnspy-mcp-checkpoints";pre_temp_sha=str(rec.get("temp",{}).get("sha256",""));pre_temp=Path(str(rec.get("temp",{}).get("path","")))
    check("024.c pre-retry owns only staged package",not pkg.exists() and pre_temp.is_file() and file_sha256(pre_temp)==pre_temp_sha,f"final_exists={pkg.exists()} temp_exists={pre_temp.is_file()}")
    ra={"request_id":rid(),"recovery_id":recid,"action":"retry_checkpoint"};one=call(c,"edit_recover",ra);first=recovery_observables(c,lineage,pkg);two=call(c,"edit_recover",ra);replay=recovery_observables(c,lineage,pkg);c1=payload(one).get("checkpoint",{});c2=payload(two).get("checkpoint",{});expected_live=str(rec.get("post_live_fingerprint",""))
    check("024.c retry/replay preserve nodes package and live fingerprint",bool(one.get("ok")) and bool(two.get("ok")) and c1.get("checkpoint_id")==c2.get("checkpoint_id")==post and idempotence_observables_match(first,replay,post,expected_live),f"first={json.dumps(first,sort_keys=True)} replay={json.dumps(replay,sort_keys=True)}")
    if pkg.is_file():print(f"INFO final_package_sha256={file_sha256(pkg)}",flush=True)
    c.close();print(f"ACC024 FORMAL {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}",flush=True);return 0 if not FAILURES else 1
if __name__=="__main__":raise SystemExit(main())
