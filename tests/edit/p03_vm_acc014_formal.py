#!/usr/bin/env python3
"""Formal ACC-014 product-call evidence: real exports and exact state gates."""
from __future__ import annotations
import hashlib, json, sys, uuid
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient  # noqa: E402

URL="http://127.0.0.1:15378/mcp"; FIXTURE=r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
ARTIFACT_ROOT=Path(r"C:\Tools\dnspy-mcp-edit-tests\artifacts"); FAILURES=[]; PASSES=[]
def configure_isolation(c):
    global URL,FIXTURE,ARTIFACT_ROOT
    URL=c.mcp_url; FIXTURE=c.fixture("TestIL.dll"); ARTIFACT_ROOT=Path(c.artifact_root)
def rid(): return str(uuid.uuid4())
def call(c,t,a):
    try: return c.call_tool_json(t,a)
    except Exception as ex:
        s=str(ex); i=s.find("{")
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
def is_formal_export_block(v): return not v.get("ok") and err_code(v)=="EDIT_EXPORT_BLOCKED"
def check(n,c,d=""):
    (PASSES if c else FAILURES).append(n); print(f"{'PASS' if c else 'FAIL'} {n} {d if not c else ''}".rstrip(),flush=True)
def commit(c,name,fault=False):
    b=call(c,"edit_begin",{"assembly_name":"TestIL","request_id":rid()}); tr=payload(b).get("transaction",{}); tx=tr.get("transaction_id",""); rev=int(tr.get("work_revision",0))
    call(c,"edit_apply",{"request_id":rid(),"transaction_id":tx,"expected_revision":rev,"operation":{"kind":"type_update","target":{"token":"0x02000002"},"name":name}})
    r=call(c,"edit_review",{"request_id":rid(),"transaction_id":tx,"expected_revision":rev+1}); rr=payload(r).get("review",{})
    if fault: call(c,"edit_test_storage_fault",{"action":"arm","stage":"finalize"})
    return call(c,"edit_commit",{"request_id":rid(),"transaction_id":tx,"expected_revision":rev+1,"review_id":rr.get("review_id",""),"review_revision":rr.get("review_revision",0),"confirmed_risk_ids":[]})
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def identity(p):
    s=p.stat(); return (s.st_dev,s.st_ino)

def main():
    c=DnSpyClient(URL,client_name="p03-vm-acc014-formal",timeout=90); c.initialize()
    o=call(c,"open_files",{"paths":[FIXTURE]}); check("014.setup fixture loaded",bool(o.get("loaded_count") or o.get("already_loaded_count")),json.dumps(o)[:200])
    source=sha(Path(FIXTURE)); seeded=commit(c,"Acc014Export"); h=payload(seeded).get("history",{}); cp=payload(seeded).get("checkpoint",{}); lineage=str(h.get("lineage_id","")); head=str(cp.get("checkpoint_id",""))
    d=call(c,"edit_export",{"request_id":rid(),"lineage_id":lineage,"checkpoint_id":head}); do=payload(d).get("output",{}); dp=Path(str(do.get("path","")))
    check("014.a default working artifact export",bool(d.get("ok")) and dp.is_file() and sha(dp)==do.get("sha256") and sha(Path(FIXTURE))==source,json.dumps(d)[:280])
    ep=ARTIFACT_ROOT/"formal-acc014"/"explicit-TestIL.dll"; e=call(c,"edit_export",{"request_id":rid(),"lineage_id":lineage,"checkpoint_id":head,"output_path":str(ep)}); eo=payload(e).get("output",{})
    check("014.b explicit path export",bool(e.get("ok")) and ep.is_file() and sha(ep)==eo.get("sha256") and sha(Path(FIXTURE))==source,json.dumps(e)[:280])
    old_sha=sha(ep); old_identity=identity(ep); before_temps=set(ep.parent.glob(ep.name+".tmp-*"))
    armed=call(c,"edit_test_storage_fault",{"action":"arm","stage":"export_reload"})
    corrupt=call(c,"edit_export",{"request_id":rid(),"lineage_id":lineage,"checkpoint_id":head,"output_path":str(ep)})
    after_temps=set(ep.parent.glob(ep.name+".tmp-*"))
    print("INFO 014.c response="+json.dumps(corrupt,sort_keys=True)[:1200],flush=True)
    check("014.c temp reload validation failure preserves old bytes and identity",
          bool(armed.get("ok")) and is_formal_export_block(corrupt) and ep.is_file() and sha(ep)==old_sha
          and identity(ep)==old_identity and after_temps==before_temps and sha(Path(FIXTURE))==source,
          f"arm={json.dumps(armed)[:180]} response={json.dumps(corrupt)[:280]} temps_before={len(before_temps)} temps_after={len(after_temps)}")
    failed=commit(c,"Acc014Partial",True); st=payload(call(c,"edit_status",{})); rec=st.get("recovery",{})
    check("014.d setup committed_without_checkpoint",err_code(failed)=="EDIT_CHECKPOINT_COMMIT_FAILED" and st.get("state")=="committed_without_checkpoint",json.dumps(st)[:240])
    for label,args in (("default",{}),("explicit",{"output_path":str(ARTIFACT_ROOT/"formal-acc014"/"partial.dll")})):
        b=call(c,"edit_export",{"request_id":rid(),"lineage_id":lineage,"checkpoint_id":head,**args}); check(f"014.d partial {label} export exact block",is_formal_export_block(b),f"actual={err_code(b)}")
    solved=call(c,"edit_recover",{"request_id":rid(),"recovery_id":rec.get("recovery_id",""),"action":"retry_checkpoint"}); check("014.d recovery resolves",bool(solved.get("ok")),json.dumps(solved)[:220])
    clean=commit(c,"Acc014UnknownSeed"); nh=payload(clean).get("history",{}).get("head_checkpoint_id","")
    call(c,"edit_test_storage_fault",{"action":"arm","stage":"navigate_inverse"}); u=call(c,"edit_undo",{"request_id":rid(),"lineage_id":lineage,"expected_checkpoint_id":nh}); ust=payload(call(c,"edit_status",{}))
    check("014.e setup live_state_unknown",err_code(u)=="EDIT_LIVE_STATE_UNKNOWN" and ust.get("state")=="live_state_unknown",json.dumps(ust)[:220])
    for label,args in (("default",{}),("explicit",{"output_path":str(ARTIFACT_ROOT/"formal-acc014"/"unknown.dll")})):
        b=call(c,"edit_export",{"request_id":rid(),"lineage_id":lineage,"checkpoint_id":nh,**args}); check(f"014.e invalid-recovery {label} export exact block",is_formal_export_block(b),f"actual={err_code(b)}")
    for label,args in (("legacy-default",{"assembly_name":"TestIL"}),
                       ("legacy-explicit",{"assembly_name":"TestIL","output_path":str(ARTIFACT_ROOT/"formal-acc014"/"unknown-legacy.dll")})):
        b=call(c,"save_assembly",args); check(f"014.e invalid-recovery {label} export exact block",is_formal_export_block(b),f"actual={err_code(b)}")
    resource_path=ARTIFACT_ROOT/"formal-acc014"/"unknown-resource.bin"
    resource=call(c,"edit_resource_export",{"request_id":rid(),"assembly_name":"TestIL","resource_name":"blocked.probe","output_path":str(resource_path)})
    check("014.e unknown resource export retains existing gate and no disk effect",
          err_code(resource)=="EDIT_LIVE_STATE_UNKNOWN" and not resource_path.exists(),f"actual={err_code(resource)}")
    c.close(); print(f"ACC014 FORMAL {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}",flush=True); return 0 if not FAILURES else 1
if __name__=="__main__": raise SystemExit(main())
