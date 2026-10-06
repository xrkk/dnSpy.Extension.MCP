#!/usr/bin/env python3
"""Mutation tests proving that the independent validator fails closed."""
from __future__ import annotations
import hashlib, json, shutil, subprocess, sys, tempfile
from pathlib import Path
from typing import Any, Callable

ROOT=Path(__file__).resolve().parent
SOURCE=ROOT/"generated";VALIDATOR=ROOT/"validate_contract.py";OUTPUT=SOURCE/"mutation-validation-report.json"
def write(path,v):path.write_text(json.dumps(v,ensure_ascii=False,sort_keys=True,separators=(",",":"))+"\n",encoding="utf-8")
def mutate(directory,name,fn):
    path=directory/name;value=json.loads(path.read_text(encoding="utf-8"));fn(value);write(path,value)
    index_path=directory/"contract-index.json";index=json.loads(index_path.read_text(encoding="utf-8"));index["generated_sha256"][name]=hashlib.sha256(path.read_bytes()).hexdigest();write(index_path,index)
def dynamic_success_branch(doc):
    dynamic=doc["edit_review"]["outputSchema"]["oneOf"][0]["properties"]["result"]["properties"]["dynamic_validation"]["oneOf"]
    dynamic[0]["properties"]["state"]={"const":"blocked"}
def fault_trace_cardinality(doc):
    for node in walk(doc["edit_test_apply_and_restore"]["outputSchema"]):
        if isinstance(node,dict) and isinstance(node.get("properties"),dict) and "execution_evidence" in node["properties"]:
            node["properties"]["execution_evidence"]["properties"]["actual_mutation_trace"]["maxItems"]=240
def walk(v):
    yield v
    if isinstance(v,dict):
        for x in v.values():yield from walk(x)
    elif isinstance(v,list):
        for x in v:yield from walk(x)
def u8_truncation(doc):
    rows=doc["edit_apply"]["inputSchema"]["properties"]["operation"]["oneOf"]
    field=next(x for x in rows if x["properties"]["kind"]["const"]=="field_add")
    u8=next(x for x in field["properties"]["constant"]["oneOf"] if x["properties"]["kind"]["const"]=="u8")
    u8["properties"]["value"]["maximum"]=2**63-1
def delete_interface_add(doc):
    doc["operations"][:]=[row for row in doc["operations"] if row["kind"]!="interface_add"]
def swap_new_operation_order(doc):
    rows=doc["operations"]
    interface=next(i for i,row in enumerate(rows) if row["kind"]=="interface_add")
    reference=next(i for i,row in enumerate(rows) if row["kind"]=="reference_add")
    rows[interface],rows[reference]=rows[reference],rows[interface]
def main():
    cases=[
      ("old-review-slot","capacity-golden.json","capacity:review-slot",lambda d:d["limits"].update({"review_slot_bytes":2*1024*1024})),
      ("invented-transport-limit","capacity-golden.json","capacity:p01-limit",lambda d:d["limits"].update({"transport_response_bytes":16*1024*1024})),
      ("fault-missing-target","fault-golden.json","fault-contract:rows",lambda d:d["faults"][0].pop("target")),
      ("fault-unbounded-prefix","expanded-tool-schemas.json","fault-contract:cardinality",fault_trace_cardinality),
      ("fault-manifest-empty-allowed","expanded-tool-schemas.json","fault-contract:cardinality",lambda d:next(n for n in walk(d["edit_test_apply_and_restore"]["outputSchema"]) if isinstance(n,dict) and isinstance(n.get("properties"),dict) and "execution_evidence" in n["properties"])["properties"]["execution_evidence"]["properties"]["fault_manifest"].update({"minItems":0})),
      ("wrong-live-component-target","mutation-corpus.json","component-recipes:exact-live-actions",lambda d:d["cases"][0].update({"live_target":"byte-buffer"})),
      ("typesig-arity-rule-removed","operation-lowering.json","type-sig:grammar",lambda d:d["operation_semantics"]["type_sig"].update({"ebnf":["type = primary, { suffix } ;"]})),
      ("typesig-owner-arity-removed","operation-lowering.json","type-sig:valid",lambda d:next(x for x in d["operation_semantics"]["type_sig"]["valid_examples"] if x["text"]=="!0").update({"owner_type_arity":0})),
      ("typesig-required-vector-deleted","operation-lowering.json","type-sig:required-vectors-exact",lambda d:d["operation_semantics"]["type_sig"]["required_vectors"].pop()),
      ("sparse-mask-forgotten","operation-lowering.json","attribute-domains:invalid-bits",lambda d:d["operation_semantics"]["attribute_invalid_vectors"].update({"FieldAttributes":[]})),
      ("uint64-truncated","expanded-tool-schemas.json","constant-rules:u8",u8_truncation),
      ("opcode-map-drift","operation-lowering.json","opcode-table:exact",lambda d:d["operation_semantics"]["opcode_operand_table"].update({"UNKNOWN1":"InlineI"})),
      ("success-envelope-blocked","expanded-tool-schemas.json","dynamic:success-only",dynamic_success_branch),
      ("missing-acceptance-case","requirements-map.json","fidelity:exact-map",lambda d:d["requirements"]["RACC-009"].remove("review-wire-budget")),
      ("apply-cache-response-limit-one","capacity-golden.json","capacity:cache-response-limits",lambda d:d["limits"].update({"apply_response_bytes":1})),
      ("review-tombstone-limits-one","capacity-golden.json","capacity:review-tombstones",lambda d:d["limits"].update({"review_tombstone_entries":1,"review_tombstone_bytes":1})),
      ("review-tombstone-entries-one","capacity-golden.json","capacity:review-tombstone-entries",lambda d:d["limits"].update({"review_tombstone_entries":1})),
      ("rollback-slot-one","capacity-golden.json","capacity:rollback-slot",lambda d:d["limits"].update({"rollback_slot_bytes":1})),
      ("fault-trace-terminal-weakened","fault-golden.json","fault-contract:machine-suite",lambda d:d["suite_contract"]["per_call"]["actual_mutation_trace"]["when_injected"].update({"terminal_equals":None})),
      ("fault-runner-artifact-reuse","fault-golden.json","fault-contract:machine-suite",lambda d:d["suite_contract"]["runner"].update({"artifact_count":1,"artifact_unique_by":"none"})),
      ("interface-add-deleted","operation-lowering.json","operation-set:order",delete_interface_add),
      ("new-operation-order-swapped","operation-lowering.json","operation-set:order",swap_new_operation_order),
    ]
    results=[]
    with tempfile.TemporaryDirectory(prefix="p02-validator-mutations-") as td:
        for cid,name,expected,fn in cases:
            target=Path(td)/cid;shutil.copytree(SOURCE,target);mutate(target,name,fn)
            done=subprocess.run([sys.executable,str(VALIDATOR),"--generated-dir",str(target)],cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
            report=json.loads((target/"validation-report.json").read_text(encoding="utf-8"))
            passed=done.returncode!=0 and report["result"]=="FAIL" and expected in report["errors"]
            results.append({"case_id":cid,"expected_error":expected,"validator_exit_code":done.returncode,"observed_errors":report["errors"],"passed":passed})
    out={"format":"dnspy.p02.validator-mutation-tests.v2","cases":results,"passed":sum(x["passed"] for x in results),"total":len(results),"result":"PASS" if all(x["passed"] for x in results) else "FAIL"}
    write(OUTPUT,out);print(json.dumps(out,ensure_ascii=False,indent=2));return 0 if out["result"]=="PASS" else 1
if __name__=="__main__":raise SystemExit(main())
