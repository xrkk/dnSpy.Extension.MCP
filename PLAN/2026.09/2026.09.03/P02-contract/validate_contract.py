#!/usr/bin/env python3
"""Independent, fail-closed cross-artifact validator for the P02 contract."""
from __future__ import annotations
import hashlib, json, re, subprocess, sys, tempfile
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parents[2]
GEN, SOURCE = ROOT / "generated", ROOT / "contract_source.py"
DEPENDENCY = ROOT / "dependency" / "dnlib-4.5.0-facts.json"
REPORT_NAME = "validation-report.json"
TOOLS = {"edit_begin","edit_status","edit_apply","edit_review","edit_rollback","edit_test_clock","edit_test_barrier","edit_test_external_mutation","edit_test_live_mutation","edit_test_fault","edit_test_apply_and_restore"}
P02_OPS = ["type_add","type_update","type_remove","method_add","method_update","method_remove","field_add","field_update","field_remove","property_add","property_update","property_remove","event_add","event_update","event_remove","parameter_add","parameter_update","parameter_remove","generic_parameter_add","generic_parameter_update","generic_parameter_remove","method_body_replace"]
# CHK-009 migration: the P02 machine suite (first 22) is frozen; the P04/P07/P08
# advanced kinds are single-sourced by contract_source.py and lower here in the
# registered order.  Both sets are re-verifiable at every HEAD.
OPS = P02_OPS + ["attribute_add","attribute_remove","security_add","security_remove","assembly_update","module_update","assembly_ref_update","entry_point_set","managed_resource_add","managed_resource_update","managed_resource_remove","win32_resource_add","win32_resource_update","win32_resource_remove","strong_name_remove","interface_add","reference_add"]
ERRORS = {"EDIT_TRANSACTION_BUSY","EDIT_TRANSACTION_NOT_FOUND","EDIT_OWNER_REQUIRED","EDIT_OWNER_MISMATCH","EDIT_REVISION_CONFLICT","EDIT_LIVE_MODULE_CONFLICT","EDIT_REVIEW_STALE","EDIT_VALIDATION_FAILED","EDIT_RISK_CONFIRMATION_REQUIRED","EDIT_CAPABILITY_UNAVAILABLE","EDIT_CAPACITY_EXCEEDED","EDIT_DEBUG_NOT_IDLE","EDIT_LIVE_STATE_UNKNOWN","EDIT_CHECKPOINT_FAILED","EDIT_EXPORT_BLOCKED","EDIT_INTERNAL_ERROR","REQUEST_ID_REUSE"}
OPERANDS = {"InlineNone","ShortInlineI","InlineI","InlineI8","ShortInlineR","InlineR","InlineString","InlineMethod","InlineField","InlineType","InlineTok","ShortInlineBrTarget","InlineBrTarget","InlineSwitch","ShortInlineVar","InlineVar","InlineSig","Phi"}
ATTRS = {"TypeAttributes":(16219583,0),"MethodAttributes":(65535,128),"MethodImplAttributes":(6143,0),"FieldAttributes":(47095,0),"PropertyAttributes":(5632,0),"EventAttributes":(1536,0),"ParamAttributes":(12319,0),"GenericParamAttributes":(63,0)}
INVALID_BITS = {"TypeAttributes":[64],"MethodAttributes":[65536],"MethodImplAttributes":[2048],"FieldAttributes":[8],"PropertyAttributes":[1],"EventAttributes":[1],"ParamAttributes":[32],"GenericParamAttributes":[64]}
ECMA = {"Module":0,"TypeRef":1,"TypeDef":2,"FieldPtr":3,"Field":4,"MethodPtr":5,"MethodDef":6,"ParamPtr":7,"Param":8,"InterfaceImpl":9,"MemberRef":10,"Constant":11,"CustomAttribute":12,"FieldMarshal":13,"DeclSecurity":14,"ClassLayout":15,"FieldLayout":16,"StandAloneSig":17,"EventMap":18,"EventPtr":19,"Event":20,"PropertyMap":21,"PropertyPtr":22,"Property":23,"MethodSemantics":24,"MethodImpl":25,"ModuleRef":26,"TypeSpec":27,"ImplMap":28,"FieldRVA":29,"Assembly":32,"AssemblyProcessor":33,"AssemblyOS":34,"AssemblyRef":35,"AssemblyRefProcessor":36,"AssemblyRefOS":37,"File":38,"ExportedType":39,"ManifestResource":40,"NestedClass":41,"GenericParam":42,"MethodSpec":43,"GenericParamConstraint":44}
PDB = {"Document":48,"MethodDebugInformation":49,"LocalScope":50,"LocalVariable":51,"LocalConstant":52,"ImportScope":53,"StateMachineMethod":54,"CustomDebugInformation":55}
HEAPS = {"StringsHeap":"#Strings","USHeap":"#US","BlobHeap":"#Blob","GuidHeap":"#GUID"}
COMPONENTS = {"ModuleMetadata","DnlibObjectGraph","MethodBodyIl","ManagedResource","EmbeddedPdb"}
CHANNEL_TARGETS = {"ModuleMetadata":"ModuleDef.Name","DnlibObjectGraph":"TypeDef.Name",
                   "MethodBodyIl":"MethodDef.Body.Instructions[0].OpCode","ManagedResource":"ModuleDef.Resources",
                   "EmbeddedPdb":"MethodDef.Body.Instructions[first SequencePoint != null].SequencePoint.Document.Url"}
CHANNEL_PRIMITIVES = {"ModuleMetadata":"set-dnlib-string-property","DnlibObjectGraph":"set-dnlib-string-property",
                      "MethodBodyIl":"set-dnlib-opcode","ManagedResource":"replace-dnlib-embedded-resource",
                      "EmbeddedPdb":"set-dnlib-pdb-document-url"}
CHANNEL_READBACK = {"ModuleMetadata":"read-dnlib-string-property","DnlibObjectGraph":"read-dnlib-string-property",
                    "MethodBodyIl":"read-dnlib-opcode","ManagedResource":"sha256-dnlib-embedded-resource",
                    "EmbeddedPdb":"read-dnlib-pdb-document-url"}
CHANNEL_RESTORE = {"ModuleMetadata":"restore-captured-dnlib-property","DnlibObjectGraph":"restore-captured-dnlib-property",
                   "MethodBodyIl":"restore-captured-dnlib-opcode","ManagedResource":"restore-captured-dnlib-resource",
                   "EmbeddedPdb":"restore-captured-dnlib-pdb-document-url"}
GENERATED = {"expanded-tool-schemas.json","operation-lowering.json","fault-golden.json","capacity-golden.json","mutation-corpus.json","requirements-map.json","contract-index.json"}
EXPECTED_TYPE_VECTORS = [
    {"vector_id":"type-var-zero-valid","text":"!0","context":"field","owner_type_arity":1,"owner_method_arity":0,"expected":True},
    {"vector_id":"type-var-zero-invalid","text":"!0","context":"field","owner_type_arity":0,"owner_method_arity":0,"expected":False},
    {"vector_id":"type-var-boundary-invalid","text":"!1","context":"field","owner_type_arity":1,"owner_method_arity":0,"expected":False},
    {"vector_id":"method-var-zero-valid","text":"!!0","context":"parameter","owner_type_arity":0,"owner_method_arity":1,"expected":True},
    {"vector_id":"method-var-zero-invalid","text":"!!0","context":"parameter","owner_type_arity":0,"owner_method_arity":0,"expected":False},
    {"vector_id":"method-var-boundary-invalid","text":"!!1","context":"parameter","owner_type_arity":0,"owner_method_arity":1,"expected":False},
    {"vector_id":"void-return-valid","text":"System.Void","context":"method_return","owner_type_arity":0,"owner_method_arity":0,"expected":True},
    {"vector_id":"void-field-invalid","text":"System.Void","context":"field","owner_type_arity":0,"owner_method_arity":0,"expected":False},
    {"vector_id":"void-generic-invalid","text":"Example.Box`1<System.Void>","context":"field","owner_type_arity":0,"owner_method_arity":0,"expected":False},
    {"vector_id":"final-byref-valid","text":"Example.Widget*&","context":"parameter","owner_type_arity":0,"owner_method_arity":0,"expected":True},
    {"vector_id":"double-byref-invalid","text":"System.Int32&&","context":"parameter","owner_type_arity":0,"owner_method_arity":0,"expected":False},
    {"vector_id":"suffix-after-byref-invalid","text":"System.Int32&*","context":"parameter","owner_type_arity":0,"owner_method_arity":0,"expected":False},
    {"vector_id":"generic-arity-valid","text":"Example.Map`2<System.String,System.Int32[]>","context":"field","owner_type_arity":0,"owner_method_arity":0,"expected":True},
    {"vector_id":"generic-arity-mismatch","text":"Example.Box`1<System.String,System.Int32>","context":"field","owner_type_arity":0,"owner_method_arity":0,"expected":False},
]
EXPECTED_FAULT_SUITE = {
    "server_state":{"persistent_fields":["armed_fault"],"reset_operation":"edit_test_fault:reset"},
    "per_call":{
        "fault_manifest":{"equals":"complete-generated-golden","projection":"fault_projection","cardinality":240,"unique_by":"fault_id"},
        "oracle_faults":{"equals":"complete-generated-golden","projection":"fault_projection","cardinality":240,"unique_by":"fault_id"},
        "armed_fault":{"when_unarmed":None,"when_armed":"selected-golden-row"},
        "actual_mutation_trace":{"ordering":"entire-call-forward-then-reverse-before-after-prefix","maximum_rows":24,"when_injected":{"terminal_equals":"armed_fault"},"when_not_injected":{"terminal_equals":None}},
        "covered_faults":{"when_injected":["armed_fault"],"when_not_injected":[],"maximum_rows":1}},
    "runner":{"suite_id":"unique-per-run","transaction_count":240,"transaction_policy":"one-new-transaction-per-golden-fault","artifact_count":240,
              "artifact_path":"ArtifactRoot/fault-suite/<suite_id>/<fault_id>.json","artifact_unique_by":"fault_id",
              "covered_union":"all-golden-fault-id-exactly-once","reset":"before-and-after-every-call","cleanup":"delete-temporary-fixtures-at-suite-end"}}

def expected_cases():
    return {
      "RACC-001":["private-live-isolation","single-active-transaction","same-request-replay","changed-request-reuse","external-live-conflict","session-owner-enforced"],
      "RACC-002":["delete-session","timeout-599999","timeout-600000","listener-restart","stdio-same-url","port-explicit-change","no-port-scan","no-payload-replay","waiter-lock-free","rollback-cancels-lease","close-generation-isolation","capacity-release"],
      # CHK-009 migration: the P02 acceptance-case registry stays P02-pure
      # (machine-suite kinds); the advanced kinds are accepted by the later
      # phase harness gates referenced from the P09 manifest.
      "RACC-003":[*[f"operation-{x}" for x in P02_OPS],"typesig-vectors","attribute-mask-vectors","constant-full-domain","accessor-nullability","opcode-operand-table"],
      "RACC-009":["fault-suite","component-suite","review-wire-budget","review-lifecycle","cache-boundaries","reverse-failure"],
      "RACC-010":["review-stale","review-live-conflict","review-debug-not-idle","review-risk-confirmation","dynamic-not_requested","dynamic-not_applicable","dynamic-blocked","dynamic-passed","dynamic-failed"],
      "RACC-026":["unsupported-mixed-mode","unsupported-netmodule","unsupported-multi-file","raw-edit-fields"]}

def read(path): return json.loads(path.read_text(encoding="utf-8"))
def canonical(v): return (json.dumps(v,ensure_ascii=False,sort_keys=True,separators=(",",":"))+"\n").encode()
def walk(v):
    yield v
    if isinstance(v,dict):
        for x in v.values(): yield from walk(x)
    elif isinstance(v,list):
        for x in v: yield from walk(x)
def consts(v,key): return {x[key]["const"] for x in walk(v) if isinstance(x,dict) and isinstance(x.get(key),dict) and isinstance(x[key].get("const"),str)}
def bound(s):
    if "oneOf" in s:return max(map(bound,s["oneOf"]))
    if "const" in s:return len(json.dumps(s["const"],ensure_ascii=True,separators=(",",":")).encode())
    if "enum" in s:return max(len(json.dumps(x,ensure_ascii=True,separators=(",",":")).encode()) for x in s["enum"])
    k=s.get("type")
    if k=="null":return 4
    if k=="boolean":return 5
    if k=="integer":return max(len(str(s.get("minimum",-(2**63)))),len(str(s.get("maximum",2**63-1))))
    if k=="number":return 32
    if k=="string":return 2+12*s.get("maxLength",4096)
    if k=="array":
        n=s.get("maxItems",0);return 2 if not n else 2+n*bound(s["items"])+n-1
    if k=="object":
        p=s.get("properties",{});return 2 if not p else 2+len(p)-1+sum(len(json.dumps(n,ensure_ascii=True).encode())+1+bound(x) for n,x in p.items())
    raise ValueError(f"unbounded schema: {s}")

class TypeSig:
    NAME=re.compile(r"[A-Za-z_][A-Za-z0-9_]*(?:`([1-9][0-9]*))?")
    PRIMS={"System.Void","System.Boolean","System.Char","System.SByte","System.Byte","System.Int16","System.UInt16","System.Int32","System.UInt32","System.Int64","System.UInt64","System.Single","System.Double","System.String","System.Object","System.IntPtr","System.UIntPtr","System.TypedReference"}
    def __init__(self,text,context="field",owner_type_arity=0,owner_method_arity=0):self.s,self.ctx,self.i,self.ta,self.ma=text,context,0,owner_type_arity,owner_method_arity
    def take(self,t):
        if self.s.startswith(t,self.i):self.i+=len(t);return True
        return False
    def parse(self):
        if not self.s or any(c.isspace() for c in self.s) or any(x in self.s for x in ("modreq","modopt","fnptr","sentinel")):return False
        try:
            primary,suffix=self.typ(self.ctx=="method_return")
            return self.i==len(self.s) and not(primary=="System.Void" and suffix)
        except ValueError:return False
    def typ(self,allow_void=False):
        generic_kind=None
        if self.take("!!"):generic_kind="method"
        elif self.take("!"):generic_kind="type"
        if generic_kind:
            start=self.i
            while self.i<len(self.s) and self.s[self.i].isdigit():self.i+=1
            if start==self.i or int(self.s[start:self.i])>65535:raise ValueError
            index=int(self.s[start:self.i]);arity=self.ma if generic_kind=="method" else self.ta
            if index>=arity:raise ValueError
            primary="generic-var"
        else:
            primary,arity=self.named()
            if primary=="System.Void" and not allow_void:raise ValueError
            if self.take("<"):
                if primary in self.PRIMS or arity is None:raise ValueError
                count=1;self.typ(False)
                while self.take(","):count+=1;self.typ(False)
                if not self.take(">") or count!=arity:raise ValueError
            elif arity is not None:raise ValueError
        suffix=0;byref=False
        while self.i<len(self.s):
            if byref:raise ValueError
            if self.take("[]") or self.take("*"):suffix+=1;continue
            if self.take("&"):suffix+=1;byref=True;continue
            if self.take("["):
                if not self.take(","):raise ValueError
                while self.take(","):pass
                if not self.take("]"):raise ValueError
                suffix+=1;continue
            break
        return primary,suffix
    def named(self):
        out=[];arity=None
        while True:
            m=self.NAME.match(self.s,self.i)
            if not m:raise ValueError
            out.append(m.group(0));self.i=m.end()
            if m.group(1) is not None:arity=int(m.group(1))
            if self.i>=len(self.s) or self.s[self.i] not in "./":break
            out.append(self.s[self.i]);self.i+=1
        return "".join(out),arity

def locator(c):
    if c in ECMA:return {"container":"pe-metadata-table","table_id":ECMA[c],"selector":"first-present-row-payload"}
    if c in PDB:return {"container":"portable-pdb-table","table_id":PDB[c],"selector":"first-present-row-payload"}
    if c in HEAPS:return {"container":"metadata-heap","stream":HEAPS[c],"selector":"first-non-header-payload-byte"}
    if c=="NativeResource":return {"container":"pe-data-directory","directory_id":2,"selector":"first-leaf-payload-byte"}
    if c=="ManagedResource":return {"container":"cli-resource-section","directory_id":14,"selector":"first-resource-payload-byte"}
    if c=="FieldInitialData":return {"container":"field-rva-section","directory_id":14,"selector":"first-field-payload-byte"}
    if c in {"PortablePdb","EmbeddedPdb"}:return {"container":"symbol-image","stream":c,"selector":"first-non-header-payload-byte"}
    return {"container":"metadata-opaque-region","stream":c,"selector":"first-preserved-payload-byte"}

def dynamic(schema):
    req={"state","requested","runtime_profile","artifact","events","cleanup","failure"};seen={}
    for x in walk(schema):
        if isinstance(x,dict) and x.get("type")=="object" and set(x.get("required",[]))==req:seen[json.dumps(x,sort_keys=True,separators=(",",":"))]=x
    return list(seen.values())

def main(gendir=GEN):
    checks={};errors=[]
    def ck(v,n):
        checks[n]=bool(v)
        if not v:errors.append(n)
    stored={n:(gendir/n).read_bytes() for n in GENERATED}
    with tempfile.TemporaryDirectory(prefix="p02-contract-") as td:
        subprocess.run([sys.executable,str(SOURCE),"--output-dir",td],cwd=ROOT,check=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
        for n in sorted(GENERATED):ck(stored[n]==(Path(td)/n).read_bytes(),f"regeneration:{n}")
    schemas=read(gendir/"expanded-tool-schemas.json");lower=read(gendir/"operation-lowering.json");fault=read(gendir/"fault-golden.json");capacity=read(gendir/"capacity-golden.json");corpus=read(gendir/"mutation-corpus.json")["cases"];reqdoc=read(gendir/"requirements-map.json");index=read(gendir/"contract-index.json");dep=read(DEPENDENCY)
    ck(set(schemas)==TOOLS,"tool-contract:nine-tools")
    for tool,pair in sorted(schemas.items()):
        ck(set(pair)=={"inputSchema","outputSchema"},f"tool-contract:{tool}:pair")
        ck(all(not(isinstance(x,dict) and "$ref" in x) for x in walk(pair)),f"tool-contract:{tool}:no-ref")
        ck(all(x.get("additionalProperties") is False for x in walk(pair) if isinstance(x,dict) and x.get("type")=="object"),f"tool-contract:{tool}:closed")
        ck(consts(pair["outputSchema"],"code")==ERRORS,f"error-contract:{tool}")
    ops=lower["operations"];by={x["kind"]:x for x in ops}
    ck([x["kind"] for x in ops]==OPS,"operation-set:order");ck([x["kind"] for x in ops[:len(P02_OPS)]]==P02_OPS,"operation-set:p02-prefix");ck(set(lower["operand_kinds"])==OPERANDS,"operation-set:operands")
    ck(lower["operand_kinds"]["ShortInlineI"]=={"kind":"i32","minimum":-128,"maximum":127},"operation-set:short-i")
    ck(lower["operand_kinds"]["ShortInlineVar"]["maximum"]==255 and lower["operand_kinds"]["InlineVar"]["maximum"]==65535,"operation-set:vars")
    ck(all(x["private"] and x["live_forward"] and x["live_reverse"] for x in ops),"operation-set:models")
    ck(by["parameter_update"]["live_forward"][0]=="insert:paramdef_collection:if_missing" and by["parameter_update"]["live_reverse"][-1]=="remove:paramdef_collection:if_materialized","operation-set:paramdef")
    ck(all("hard_fail_if" in x.get("delete_policy","") for x in ops if x["kind"].endswith("_remove")),"operation-set:delete")
    sem=lower["operation_semantics"];ts=sem["type_sig"]
    ck(ts["version"]=="p02-typesig-v1" and any("argument count" in x for x in ts["ebnf"]),"type-sig:grammar")
    ck(all(TypeSig(x["text"],x.get("context","field"),x.get("owner_type_arity",0),x.get("owner_method_arity",0)).parse() for x in ts["valid_examples"]),"type-sig:valid")
    ck(all(not TypeSig(x["text"],x.get("context","field"),x.get("owner_type_arity",0),x.get("owner_method_arity",0)).parse() for x in ts["invalid_examples"]),"type-sig:invalid")
    ck(ts.get("required_vectors")==EXPECTED_TYPE_VECTORS,"type-sig:required-vectors-exact")
    ck(all(TypeSig(x["text"],x["context"],x["owner_type_arity"],x["owner_method_arity"]).parse()==x["expected"] for x in EXPECTED_TYPE_VECTORS),"type-sig:required-vectors-outcomes")
    ck(ts["forbidden"]==["modreq","modopt","fnptr","sentinel"] and ts["forbidden_error"]=="EDIT_CAPABILITY_UNAVAILABLE","type-sig:unsupported")
    ck(sem["attribute_domains"]=={n:{"mask":m,"add_default":d} for n,(m,d) in ATTRS.items()},"attribute-domains:exact")
    ck(sem["attribute_invalid_vectors"]==INVALID_BITS and all(v&~ATTRS[n][0] for n,vs in INVALID_BITS.items() for v in vs),"attribute-domains:invalid-bits")
    branches={x["properties"]["kind"]["const"]:x for x in schemas["edit_apply"]["inputSchema"]["properties"]["operation"]["oneOf"]}
    domains={"type":"TypeAttributes","method":"MethodAttributes","field":"FieldAttributes","property":"PropertyAttributes","event":"EventAttributes","parameter":"ParamAttributes","generic_parameter":"GenericParamAttributes"}
    for k,b in sorted(branches.items()):
        for p in ("attributes","impl_attributes"):
            if p not in b["properties"]:continue
            n="MethodImplAttributes" if p=="impl_attributes" else domains[k.rsplit("_",1)[0]];m,d=ATTRS[n]
            ck(b["properties"][p].get("x-dnspy-defined-bit-mask")==m and b["properties"][p].get("default")== (d if k.endswith("_add") else None),f"attribute-domains:{k}:{p}")
    pattern=sem["constants"]["char_pattern"]
    ck(re.fullmatch(pattern,"a") is not None and re.fullmatch(pattern,"😀") is not None and re.fullmatch(pattern,"ab") is None,"constant-rules:char")
    constants=branches["field_add"]["properties"]["constant"]["oneOf"];u8=next(x for x in constants if x["properties"]["kind"]["const"]=="u8")
    ck(u8["properties"]["value"]["maximum"]==2**64-1,"constant-rules:u8")
    ck("null" not in json.dumps(branches["property_add"]["properties"]["getter"]) and any(x.get("type")=="null" for x in walk(branches["property_update"]["properties"]["getter"]) if isinstance(x,dict)),"constant-rules:nullability")
    ck(dep["format"]=="dnspy.p02.dnlib-facts.v1" and len(dep["opcodes"])==229,"opcode-table:shape")
    ck(dep.get("public_api_paths")=={"sequence_point_document_url":CHANNEL_TARGETS["EmbeddedPdb"]},"dependency-api:sequence-point-document-url")
    ck(sem["opcode_operand_table"]==dep["opcodes"],"opcode-table:exact")
    ck(sem["dependency"]=={"name":"dnlib","version":"4.5.0","assembly_version":dep["assembly_version"],"assembly_file_sha256":dep["assembly_file_sha256"]},"opcode-table:identity")
    projection=["fault_id","operation_index","step_index","operation_kind","direction","boundary","primitive_kind","target","step"];expected=[]
    for oi,row in enumerate(ops[:len(P02_OPS)]):
        for direction,steps in (("forward",row["live_forward"]),("reverse",row["live_reverse"])):
            for si,step in enumerate(steps):
                primitive,target=step.split(":",1)
                for boundary in ("before","after"):expected.append({"fault_id":f"fp-{oi}-{direction}-{si}-{primitive}-{boundary}","operation_index":oi,"step_index":si,"operation_kind":row["kind"],"direction":direction,"boundary":boundary,"primitive_kind":primitive,"target":target,"step":step})
    ck(fault["projection"]==projection,"fault-contract:projection");ck(fault["faults"]==expected and len(expected)==240,"fault-contract:rows");ck(all(x["operation_kind"] in P02_OPS for x in fault["faults"]),"fault-contract:machine-suite-scope")
    suite=fault["suite_contract"];ck(suite==EXPECTED_FAULT_SUITE,"fault-contract:machine-suite")
    nodes=[x["properties"]["execution_evidence"] for x in walk(schemas["edit_test_apply_and_restore"]["outputSchema"]) if isinstance(x,dict) and isinstance(x.get("properties"),dict) and "execution_evidence" in x["properties"]];ck(bool(nodes),"fault-contract:evidence")
    ev=nodes[0]["properties"];ck(set(ev)=={"armed_fault","fault_manifest","oracle_faults","actual_mutation_trace","covered_faults"},"fault-contract:fields")
    max_trace=2*max(len(x["live_forward"])+len(x["live_reverse"]) for x in ops[:len(P02_OPS)])
    ck(max_trace==24 and [ev[x]["maxItems"] for x in ("fault_manifest","oracle_faults","actual_mutation_trace","covered_faults")]==[240,240,max_trace,1] and
       all(ev[x].get("minItems")==240 and ev[x].get("uniqueItems") is True for x in ("fault_manifest","oracle_faults")) and ev["covered_faults"].get("uniqueItems") is True,
       "fault-contract:cardinality")
    items=[ev[x]["items"] for x in ("fault_manifest","oracle_faults","actual_mutation_trace","covered_faults")];ck(len({json.dumps(x,sort_keys=True) for x in items})==1 and set(items[0]["required"])==set(projection),"fault-contract:row-schema")
    limits=capacity["limits"];payload={n:bound(x["outputSchema"]) for n,x in schemas.items()};wire={n:limits["transport_fixed_envelope_bytes"]+limits["transport_payload_expansion_factor"]*v for n,v in payload.items()}
    ck(capacity["tool_response_bounds"]=={n:{"payload_max_bytes":payload[n],"full_jsonrpc_max_bytes":wire[n]} for n in sorted(schemas)},"capacity:all-tools")
    ck(all(x<=limits["transport_response_bytes"] for x in wire.values()),"capacity:transport")
    budget=capacity["review_budget"];review=payload["edit_review"];ck(review==budget["calculated_max_bytes"] and review<=limits["review_slot_bytes"] and budget["headroom_bytes"]==limits["review_slot_bytes"]-review,"capacity:review-slot")
    expected_cache={"edit_begin":{"payload_max_bytes":payload["edit_begin"],"per_response_limit_bytes":limits["begin_response_bytes"],"entry_limit":limits["begin_cache_entries"],"total_limit_bytes":limits["begin_cache_bytes"]},
                    "edit_apply":{"payload_max_bytes":payload["edit_apply"],"per_response_limit_bytes":limits["apply_response_bytes"],"entry_limit":limits["apply_cache_entries"],"total_limit_bytes":limits["apply_cache_bytes"]}}
    ck(capacity["cache_response_bounds"]==expected_cache and all(x["payload_max_bytes"]<=x["per_response_limit_bytes"] for x in expected_cache.values()),"capacity:cache-response-limits")
    tomb_schema={"type":"object","properties":{"request_id_sha256":{"type":"string","minLength":1,"maxLength":64,"pattern":"^[0-9a-f]{64}$"},"request_payload_sha256":{"type":"string","minLength":1,"maxLength":64,"pattern":"^[0-9a-f]{64}$"},"review_id":{"type":"string","minLength":1,"maxLength":128},"review_revision":{"type":"integer","minimum":0,"maximum":2**32-1}},"required":["request_id_sha256","request_payload_sha256","review_id","review_revision"],"additionalProperties":False}
    tomb_item=bound(tomb_schema);expected_tomb={"schema":tomb_schema,"item_max_bytes":tomb_item,"entry_limit":limits["review_tombstone_entries"],"calculated_total_max_bytes":tomb_item*limits["review_tombstone_entries"],"configured_total_limit_bytes":limits["review_tombstone_bytes"]}
    ck(limits["review_tombstone_entries"]==64,"capacity:review-tombstone-entries")
    ck(capacity.get("review_tombstone_bound")==expected_tomb and expected_tomb["calculated_total_max_bytes"]<=expected_tomb["configured_total_limit_bytes"],"capacity:review-tombstones")
    expected_rollback={"payload_max_bytes":payload["edit_rollback"],"configured_slot_bytes":limits["rollback_slot_bytes"]}
    ck(capacity.get("rollback_response_bound")==expected_rollback and expected_rollback["payload_max_bytes"]<=expected_rollback["configured_slot_bytes"],"capacity:rollback-slot")
    text=(REPO/"Transport/TransportLimits.cs").read_text(encoding="utf-8");m=re.search(r"MaxResponseBytes\s*=\s*([0-9_]+)",text)
    ck(m is not None and int(m.group(1).replace("_",""))==limits["transport_response_bytes"]==8388608,"capacity:p01-limit")
    lifecycle=capacity["review_lifecycle"];by_transition={x.get("transition_id"):x for x in lifecycle}
    ck(len(lifecycle)==6 and len(by_transition)==6 and
       by_transition.get("review-first",{}).get("guard",{}).get("tombstones_less_than")==limits["review_tombstone_entries"] and
       by_transition.get("review-new",{}).get("guard",{}).get("tombstones_less_than")==limits["review_tombstone_entries"] and
       by_transition.get("review-capacity",{}).get("guard",{}).get("tombstones_equal")==limits["review_tombstone_entries"] and
       by_transition.get("review-capacity",{}).get("tombstones_after")==limits["review_tombstone_entries"] and
       by_transition.get("review-capacity",{}).get("result")=="EDIT_CAPACITY_EXCEEDED" and by_transition.get("review-capacity",{}).get("side_effect")=="none" and
       by_transition.get("review-apply-clear",{}).get("tombstones_after")==0 and by_transition.get("review-terminal-clear",{}).get("tombstones_after")==0,
       "review-lifecycle:bounded-closure")
    ck(any(x.get("action")=="cancel_lease_then_wait_outside_monitor" for x in capacity["cache_transitions"]),"cache-transitions:lock-free")
    ck(len(corpus)==6,"component-recipes:count");mutations=[x for x in corpus if x["mode"]=="mutate"];orders=[x for x in corpus if x["mode"]=="reorder"]
    ck({x["component"] for x in mutations}==COMPONENTS and len(orders)==1 and orders[0]["component"]=="GlobalCanonicalOrder","component-recipes:set")
    recipes=True
    for row in mutations:
        recipe={k:row[k] for k in ("recipe_id","component","category","fixture_precondition","live_target","mutate_action","readback","restore_action")}
        recipes &= row["live_target"]==CHANNEL_TARGETS[row["component"]] and row["mutate_action"]["primitive"]==CHANNEL_PRIMITIVES[row["component"]]
        recipes &= row["readback"]["primitive"]==CHANNEL_READBACK[row["component"]] and row["restore_action"]["primitive"]==CHANNEL_RESTORE[row["component"]]
        recipes &= row["fixture_precondition"]["live_target_resolves"] is True and row["action"]==row["mutate_action"] and row["expected_fingerprint_change"] is True
        recipes &= row["restore_action"]["verify_full_fingerprint"] is True and row["recipe_sha256"]==hashlib.sha256(canonical(recipe)).hexdigest()
    order=orders[0];order_identity={k:order[k] for k in ("recipe_id","component","category","fixture_precondition","live_target","mutate_action","readback","restore_action","reorder_action")}
    recipes &= order["action"]=={"primitive":"reverse-global-fingerprint-channel-enumerator","minimum_channels":5} and order["expected_fingerprint_change"] is False
    recipes &= order["fixture_precondition"]["channel_count_minimum"]==5 and order["recipe_sha256"]==hashlib.sha256(canonical(order_identity)).hexdigest()
    ck(recipes,"component-recipes:exact-live-actions")
    ck(set(schemas["edit_test_external_mutation"]["inputSchema"]["properties"]["case_id"]["enum"])=={x["case_id"] for x in corpus},"component-recipes:schema")
    output=schemas["edit_review"]["outputSchema"]["oneOf"];success=dynamic(output[0]);failure=dynamic(output[2]);ss=[x["properties"]["state"]["const"] for x in success];fs=[x["properties"]["state"]["const"] for x in failure]
    ck(ss==["not_requested","not_applicable","passed"],"dynamic:success-only");ck(fs==["blocked","failed","failed","failed"],"dynamic:error-only")
    failed=[x for x in failure if x["properties"]["state"]["const"]=="failed"];phases={json.dumps(x["properties"]["failure"]["properties"]["phase"],sort_keys=True) for x in failed}
    ck(failure[0]["properties"]["failure"]["properties"]["phase"]=={"const":"precheck"} and phases=={json.dumps({"enum":["precheck","write"]},sort_keys=True),json.dumps({"enum":["launch","run","terminate"]},sort_keys=True),json.dumps({"const":"delete"},sort_keys=True)},"dynamic:phases")
    ck(all(x["properties"]["cleanup"]["properties"]["debug_idle"]["const"] is True for x in success+failure),"dynamic:idle")
    expected_map=expected_cases();mapped=reqdoc["requirements"];rows=reqdoc["cases"]
    ck(mapped==expected_map,"fidelity:exact-map");ck(set(rows)==set(expected_map) and all([x["case_id"] for x in rows[r]]==ids for r,ids in expected_map.items()),"fidelity:ownership")
    ck(all(set(x)=={"case_id","action","expected","evidence"} and x["action"] and x["expected"] and x["evidence"] for rs in rows.values() for x in rs),"fidelity:contracts")
    sh=hashlib.sha256(SOURCE.read_bytes()).hexdigest();dh=hashlib.sha256(DEPENDENCY.read_bytes()).hexdigest();ck(index["source_sha256"]==sh,"integrity:source");ck(index["dependency_sha256"]==dh,"integrity:dependency");ck(set(index["generated_sha256"])==GENERATED-{"contract-index.json"},"integrity:files")
    for n,h in sorted(index["generated_sha256"].items()):ck(hashlib.sha256((gendir/n).read_bytes()).hexdigest()==h,f"integrity:{n}")
    ck(limits["review_slot_bytes"]>2*1024*1024,"negative:old-review-slot");ck(limits["transport_response_bytes"]==8*1024*1024,"negative:invented-transport");ck(u8["properties"]["value"]["maximum"]!=2**63-1,"negative:u8");ck(re.fullmatch(pattern,"ab") is None,"negative:char")
    subs={"RACC-001":["tool-contract:","operation-set:"],"RACC-002":["cache-transitions:","review-lifecycle:"],"RACC-003":["operation-set:","type-sig:","attribute-domains:","constant-rules:","opcode-table:"],"RACC-009":["fault-contract:","capacity:","component-recipes:","dynamic:"],"RACC-010":["dynamic:","error-contract:"],"RACC-026":["tool-contract:"]}
    results={r:mapped.get(r)==ids and all(rows.get(r)) and all(any(n.startswith(p) for n in checks) and all(v for n,v in checks.items() if n.startswith(p)) for p in subs[r]) for r,ids in expected_map.items()}
    missing=sorted(set(expected_map)-set(mapped));weak=sorted(r for r,v in results.items() if not v and r not in missing);untestable=sorted(r for r,ids in expected_map.items() if not ids or r not in subs);complete=sorted(r for r,v in results.items() if v)
    report={"format":"dnspy.p02.contract.validation.v3","validator":"independent-cross-artifact-validator","source_sha256":sh,"dependency_sha256":dh,"checks_total":len(checks),"checks_passed":sum(checks.values()),"errors":errors,"requirement_results":results,"requirements_fidelity":{"expected":len(expected_map),"complete":len(complete),"complete_ids":complete,"missing":len(missing),"missing_ids":missing,"weakened":len(weak),"weakened_ids":weak,"untestable":len(untestable),"untestable_ids":untestable},"result":"PASS" if not errors and len(complete)==len(expected_map) else "FAIL"}
    (gendir/REPORT_NAME).write_text(json.dumps(report,ensure_ascii=False,sort_keys=True,separators=(",",":"))+"\n",encoding="utf-8");print(json.dumps(report,ensure_ascii=False,indent=2));return 0 if report["result"]=="PASS" else 1

if __name__=="__main__":
    selected=GEN
    if len(sys.argv)==3 and sys.argv[1]=="--generated-dir":selected=Path(sys.argv[2]).resolve()
    elif len(sys.argv)!=1:raise SystemExit("usage: validate_contract.py [--generated-dir PATH]")
    raise SystemExit(main(selected))
