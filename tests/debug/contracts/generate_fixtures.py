#!/usr/bin/env python3
"""Generate canonical request/expected-response fixtures for dnspy.debug.v1.

Emits tests/debug/contracts/fixtures/*.json from the frozen contract schema
(dnspy.debug.v1.schema.json) and the 3.6 state matrix. Deterministic output:
running this script twice produces byte-identical files (sorted case ids,
fixed key order, no timestamps). ACC-028 regenerates and diffs on Windows.

Fixture file format (dnspy.debug.fixtures.v1):
  { "schema_version", "api", "protocol_version", "cases": [ { "id", "kind",
    "request", "expected_response", "stage"? , "utf8_bytes"? } ] }

Kinds: valid | invalid-state | invalid-fields | disabled-gate |
       fixed-capability-unavailable | control-failure | byte-input | byte-output-rule
"""
import json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "fixtures")
SCHEMA_PATH = os.path.join(HERE, "dnspy.debug.v1.schema.json")
LIMITS_PATH = os.path.join(HERE, "dnspy.debug.utf8-limits.json")

# ---------- deterministic canonical values (all base64url, fixed) ----------
SESSION = "c2Vzc2lvbi0wMDAx"
GEN = 1
EPOCH = 2
CURSOR = 10
H_MODULE = "bW9kdWxlLTAwMDE"
H_RUNTIME = "cnVudGltZS0wMDAx"
H_THREAD = "dGhyZWFkLTAwMDE"
H_FRAME = "ZnJhbWUtMDAwMQ"
H_VALUE = "dmFsdWUtMDAwMQ"
H_PARENT = "dmFsdWUtMDAwMA"
H_BREAKPOINT = "YnBrcC0wMDAx"
H_STEP = "c3RlcC0wMDAx"
REQUEST_ID = "01234567-89ab-cdef-0123-456789abcdef"
MVID = "0a1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d"
SHA = "a" * 64
TS = "2026-08-27T08:00:00.000Z"
CLAIM_DEADLINE = "2026-08-27T08:00:30.000Z"
PV_NEW, PV_2025_03, PV_2024_11 = "2025-06-18", "2025-03-26", "2024-11-05"

# 3.4 fixed domain-error mapping
ERROR_MAP = {
    "DEBUG_DISABLED": ("Debug tools are disabled.", "enable_debug_tools"),
    "CAPABILITY_UNAVAILABLE": ("The requested capability is unavailable.", "choose_supported_workflow"),
    "INVALID_STATE": ("The operation is invalid in the current state.", "query_status"),
    "STALE_HANDLE": ("The referenced handle is stale.", "reacquire_handles"),
    "TARGET_MISMATCH": ("The target identity no longer matches.", "reacquire_target"),
    "NOT_FOUND": ("The requested resource was not found.", "requery_resource"),
    "ALREADY_EXISTS": ("The requested name already exists.", "choose_new_name"),
    "LIMIT_EXCEEDED": ("A fixed resource limit was exceeded.", "reduce_request_or_wait"),
    "TIMEOUT": ("The operation timed out.", "wait_for_state"),
    "OWNERSHIP_LOST": ("Exclusive target ownership could not be established.", "manual_resolve_then_wait_idle"),
    "REQUEST_ID_REUSE": ("The request_id was reused with different arguments.", "use_new_request_id"),
    "INTERNAL_ERROR": ("An internal error occurred.", "inspect_server_log"),
}
STATE_ORDER = ["idle", "starting", "running", "paused", "restarting", "stopping", "faulted"]

# 3.6 state matrix: API -> allowed non-read states (ordered subset of STATE_ORDER)
ALLOWED = {
    "debug_launch": ["idle"],
    "debug_pause": ["running"],
    "debug_continue": ["paused"],
    "debug_step": ["paused"],
    "debug_restart": ["running", "paused"],
    "debug_terminate": ["running", "paused", "faulted"],
    "debug_set_breakpoint": ["paused"],
    "debug_list_breakpoints": ["running", "paused"],
    "debug_set_breakpoint_enabled": ["paused"],
    "debug_remove_breakpoint": ["paused"],
    "debug_list_threads": ["paused"],
    "debug_get_stack": ["paused"],
    "debug_get_locals": ["paused"],
    "debug_expand_value": ["paused"],
    "debug_read_memory": ["paused"],
    "debug_dump_module": ["paused"],
    "debug_list_modules": ["running", "paused"],
    "debug_set_exception_policy": ["starting", "running", "paused"],
}
# 3.6 全状态允许/只读的 API:invalid-state 槽位以各自实际状态相邻错误呈现(status/read/wait
# 对非 active 且非保留 terminal 的 session_id 固定 NOT_FOUND),不虚构 INVALID_STATE。
NO_STATE_MATRIX = {"debug_status": "NOT_FOUND", "debug_read_events": "NOT_FOUND", "debug_wait_event": "NOT_FOUND"}

def first_disallowed(api):
    allowed = ALLOWED[api]
    return next(s for s in STATE_ORDER if s not in allowed)

def ctx(state, session=True):
    c = {}
    if session:
        c["session_id"] = SESSION
    c.update({"generation": GEN, "pause_epoch": EPOCH, "event_cursor": CURSOR, "state": state})
    return c

def env_ok(result, state, session=True, warnings=None, untrusted=False):
    return {"schema_version": "dnspy.debug.v1", "ok": True, "debug_context": ctx(state, session),
            "result": result, "warnings": warnings or [], "untrusted_sample_data": untrusted}

def err_obj(code, current_state, required_states=None):
    msg, rec = ERROR_MAP[code]
    e = {"code": code, "message": msg, "recovery": rec, "current_state": current_state,
         "required_states": required_states if required_states is not None else []}
    if code == "LIMIT_EXCEEDED":
        e["retry_after_ms"] = 1000
    elif code == "TIMEOUT":
        e["retry_after_ms"] = 0
    return e

def env_err(code, current_state, required_states=None, session=True):
    return {"schema_version": "dnspy.debug.v1", "ok": False, "debug_context": ctx(current_state, session),
            "error": err_obj(code, current_state, required_states), "warnings": [],
            "untrusted_sample_data": False}

def req(api, arguments, case_id):
    return {"jsonrpc": "2.0", "id": case_id, "method": "tools/call",
            "params": {"name": api, "arguments": arguments}}

def canon(env):
    return json.dumps(env, ensure_ascii=False, separators=(",", ":"))

def resp_tool(env, pv, case_id, is_error):
    r = {"jsonrpc": "2.0", "id": case_id, "result": {"content": [{"type": "text", "text": canon(env)}]}}
    if pv == PV_NEW:
        r["result"]["structuredContent"] = env
    if is_error:
        r["result"]["isError"] = True
    return r

def resp_rpc_error(code, message, case_id, data=None):
    e = {"code": code, "message": message}
    if data is not None:
        e["data"] = data
    return {"jsonrpc": "2.0", "id": case_id, "error": e}

def case(cid, kind, api, arguments, expected, pv=PV_NEW, **extra):
    c = {"id": cid, "kind": kind, "request": req(api, arguments, cid), "expected_response": expected}
    c.update(extra)
    return c

# ---------- per-API argument / result templates ----------
MODULE_IDENTITY = {
    "module_handle": H_MODULE, "runtime_handle": H_RUNTIME, "name": "Sample.dll",
    "mvid": MVID, "base_address": "0x7ff000000000", "size": 1048576,
    "layout": "file", "identity_strength": "disk_strong",
}
LOCATION = {"module_handle": H_MODULE, "method_token": "0x06000001", "il_offset": 0,
            "native_ip": "0x7ff000001234"}
BREAKPOINT = {
    "breakpoint_id": H_BREAKPOINT, "owned": True, "enabled": True, "bound": True,
    "module_identity": MODULE_IDENTITY, "method_token": "0x06000001", "il_offset": 0,
}
FILE_ID = {"role": "target", "object_kind": "file", "final_path": "C:\\samples\\Sample.exe",
           "volume_serial": "0x" + "1a" * 8, "file_id": "2b" * 16, "sha256": SHA}
def file_id(role, kind="file"):
    f = dict(FILE_ID); f["role"] = role
    if kind == "directory":
        f["object_kind"] = "directory"; f.pop("sha256")
    return f
VALUE_NODE = {
    "value_handle": H_VALUE, "depth": 0, "name": "args", "kind": "parameter",
    "type": "System.String[]", "display": "string[2]", "has_children": True,
    "is_null": False, "truncated": False,
}
BUDGETS = {
    "depth_limit": 4, "node_limit": 1024, "value_handle_limit": 4096,
    "string_utf8_limit": 65536, "response_utf8_limit": 8388608,
    "depth_used": 0, "nodes_used": 1, "value_handles_used": 1, "truncated": False,
}
ARTIFACT = {
    "artifact_id": "art-0001", "path": "C:\\Users\\x\\AppData\\Local\\Temp\\dnspy-mcp\\" + SESSION + "\\0a1b.bin",
    "kind": "raw", "layout": "file", "size": 1048576, "sha256": SHA,
    "source_module": MODULE_IDENTITY, "manifest_path": "C:\\Temp\\dnspy-mcp\\" + SESSION + "\\0a1b.bin.manifest.json",
}
def page(items_ref, truncated=False, nxt=None, extra=None):
    r = {"items": [items_ref], "truncated": truncated}
    if nxt is not None:
        r["next_page_cursor"] = "cGFnZS0wMDAx"
    if extra:
        r.update(extra)
    return r

A = {}   # api -> valid arguments (gate=true state preconditioned)
R = {}   # api -> valid result object

A["debug_status"] = {}
R["debug_status"] = {"state": "running", "active_session_id": SESSION, "last_session_id": "c2Vzc2lvbi0wMDAw",
                     "owned_process": {"process_handle": "cHJvYy0wMDAx", "pid": 4242, "start_time_utc": TS,
                                        "filename": "Sample.exe", "image_identity": file_id("target"),
                                        "runtime_identity": "clr4-v4.0.30319", "runtime_family": "net48",
                                        "architecture": "x64"},
                     "observed_process_state": "running", "runtime_family": "net48", "architecture": "x64",
                     "start_kind": "launch", "launch_mode": "net48-exe"}

A["debug_launch"] = {"request_id": REQUEST_ID, "target_path": "C:\\samples\\Sample.exe",
                     "expected_sha256": SHA, "launch_mode": "net48-exe", "architecture": "x64",
                     "break_kind": "entry"}
R["debug_launch"] = {"session_id": SESSION, "generation": GEN, "state": "starting",
                     "claim_deadline_utc": CLAIM_DEADLINE, "launch_mode": "net48-exe",
                     "runtime_family": "net48", "architecture": "x64",
                     "file_identities": [file_id("target")]}

A["debug_pause"] = {"session_id": SESSION, "generation": GEN, "request_id": REQUEST_ID}
R["debug_pause"] = {"state": "paused", "pause_epoch": EPOCH, "reason": "manual", "request_effect": "state_satisfied"}

A["debug_continue"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH, "request_id": REQUEST_ID}
R["debug_continue"] = {"state": "running", "pause_epoch": EPOCH}

A["debug_restart"] = {"session_id": SESSION, "generation": GEN, "request_id": REQUEST_ID}
R["debug_restart"] = {"state": "starting", "generation": GEN + 1, "claim_deadline_utc": CLAIM_DEADLINE}

A["debug_terminate"] = {"session_id": SESSION, "generation": GEN, "request_id": REQUEST_ID}
R["debug_terminate"] = {"state": "idle", "exit_code": 0, "terminal_cursor": CURSOR + 1}

A["debug_read_events"] = {"session_id": SESSION}
R["debug_read_events"] = {"events": [], "next_cursor": 0, "earliest_cursor": 0, "events_lost": 0}

A["debug_wait_event"] = {"session_id": SESSION, "timeout_ms": 1000}
R["debug_wait_event"] = {"events": [], "next_cursor": 0, "earliest_cursor": 0, "events_lost": 0, "timed_out": True}

A["debug_set_breakpoint"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                             "request_id": REQUEST_ID, "module_handle": H_MODULE, "module_sha256": SHA,
                             "mvid": MVID, "method_token": "0x06000001", "il_offset": 0}
R["debug_set_breakpoint"] = {"breakpoint": BREAKPOINT}

A["debug_list_breakpoints"] = {"session_id": SESSION, "generation": GEN}
R["debug_list_breakpoints"] = page(BREAKPOINT)

A["debug_set_breakpoint_enabled"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                                     "request_id": REQUEST_ID, "breakpoint_id": H_BREAKPOINT, "enabled": False}
R["debug_set_breakpoint_enabled"] = {"breakpoint": {**BREAKPOINT, "enabled": False}}

A["debug_remove_breakpoint"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                                "request_id": REQUEST_ID, "breakpoint_id": H_BREAKPOINT}
R["debug_remove_breakpoint"] = {"removed": True, "breakpoint_id": H_BREAKPOINT}

A["debug_list_threads"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH}
R["debug_list_threads"] = page({"thread_handle": H_THREAD, "managed_id": 1, "os_id": 7,
                                "name": "Main", "state": "paused", "is_current": True})

A["debug_get_stack"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH, "thread_handle": H_THREAD}
R["debug_get_stack"] = page({"frame_handle": H_FRAME, "index": 0, "location": LOCATION,
                             "display_name": "Sample.Program.Main(System.String[])"})

A["debug_step"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                   "request_id": REQUEST_ID, "thread_handle": H_THREAD, "kind": "over"}
R["debug_step"] = {"step_id": H_STEP, "state": "running"}

A["debug_get_locals"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH, "frame_handle": H_FRAME}
R["debug_get_locals"] = page(VALUE_NODE, extra={"evaluation_mode": "no_func_eval_raw", "budgets": BUDGETS})

A["debug_expand_value"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                           "value_handle": H_VALUE, "depth": 1}
R["debug_expand_value"] = page({**VALUE_NODE, "depth": 1, "parent_value_handle": H_PARENT, "kind": "field"},
                               extra={"evaluation_mode": "no_func_eval_raw", "budgets": BUDGETS})

A["debug_list_modules"] = {"session_id": SESSION, "generation": GEN}
R["debug_list_modules"] = page(MODULE_IDENTITY)

A["debug_read_memory"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                          "module_handle": H_MODULE, "address": "0x7ff000000000", "length": 16}
R["debug_read_memory"] = {"module_handle": H_MODULE, "address": "0x7ff000000000", "length": 16,
                          "encoding": "base64", "data": "AAAAAAAAAAAAAAAAAAAAAA==",
                          "read_semantics": "dnspy-zero-fill"}

A["debug_dump_module"] = {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                          "request_id": REQUEST_ID, "module_handle": H_MODULE}
R["debug_dump_module"] = {"artifact": ARTIFACT}

A["debug_set_exception_policy"] = {"session_id": SESSION, "generation": GEN,
                                   "request_id": REQUEST_ID, "policy": {"break_on": "unhandled"}}
R["debug_set_exception_policy"] = {"previous": {"break_on": "unhandled"}, "current": {"break_on": "unhandled"}}

# per-API invalid-fields mutations (schema-invalid; expect JSON-RPC -32602)
INVALID_FIELDS = {
    "debug_status": {"session_id": SESSION, "bogus": 1},
    "debug_launch": {"target_path": "C:\\s.exe"},  # missing required fields
    "debug_pause": {"session_id": SESSION, "generation": "one"},  # wrong type
    "debug_continue": {"session_id": SESSION, "generation": GEN},  # missing pause_epoch
    "debug_restart": {"session_id": SESSION, "generation": GEN, "request_id": "NOT-A-UUID"},
    "debug_terminate": {"session_id": SESSION, "generation": GEN, "request_id": REQUEST_ID, "extra": True},
    "debug_read_events": {"session_id": SESSION, "limit": 0},  # below minimum
    "debug_wait_event": {"session_id": SESSION, "timeout_ms": 30001},
    "debug_set_breakpoint": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                             "request_id": REQUEST_ID, "module_handle": H_MODULE, "mvid": "XX"},
    "debug_list_breakpoints": {"session_id": SESSION, "generation": GEN, "page_size": 101},
    "debug_set_breakpoint_enabled": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                                     "request_id": REQUEST_ID, "breakpoint_id": H_BREAKPOINT},
    "debug_remove_breakpoint": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                                "request_id": REQUEST_ID, "breakpoint_id": H_BREAKPOINT, "depth": 1},
    "debug_list_threads": {"session_id": SESSION, "generation": GEN, "pause_epoch": "x"},
    "debug_get_stack": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH, "thread_handle": 5},
    "debug_step": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                   "request_id": REQUEST_ID, "thread_handle": H_THREAD, "kind": "sideways"},
    "debug_get_locals": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH},
    "debug_expand_value": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                           "value_handle": H_VALUE, "depth": 5},
    "debug_list_modules": {"session_id": SESSION, "generation": GEN, "page_cursor": "has space"},
    "debug_read_memory": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                          "module_handle": H_MODULE, "address": "7ff000000000", "length": 16},  # no 0x
    "debug_dump_module": {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
                          "request_id": REQUEST_ID, "module_handle": H_MODULE, "relative_name": "a/b.bin"},
    "debug_set_exception_policy": {"session_id": SESSION, "generation": GEN, "request_id": REQUEST_ID},
}

def valid_state_for(api):
    return ALLOWED[api][0] if ALLOWED[api] != ["idle"] or api == "debug_launch" else "idle"

def gen_api_fixtures():
    files = {}
    for api in sorted(list(ALLOWED) + list(NO_STATE_MATRIX)):
        cases = []
        st = valid_state_for(api) if api in ALLOWED else "running"
        # valid
        cases.append(case(f"{api}/valid", "valid", api, A[api],
                          resp_tool(env_ok(R[api], st, untrusted=api in ("debug_get_stack", "debug_get_locals", "debug_expand_value", "debug_read_memory", "debug_dump_module", "debug_list_modules")), PV_NEW, f"{api}/valid", False)))
        if api in ALLOWED:
            bad_st = first_disallowed(api)
            cases.append(case(f"{api}/invalid-state", "invalid-state", api, A[api],
                              resp_tool(env_err("INVALID_STATE", bad_st, ALLOWED[api]), PV_NEW, f"{api}/invalid-state", True),
                              current_state=bad_st, required_states=ALLOWED[api]))
        else:
            code = NO_STATE_MATRIX[api]
            cases.append(case(f"{api}/invalid-state", "invalid-state", api, A[api],
                              resp_tool(env_err(code, "idle"), PV_NEW, f"{api}/invalid-state", True),
                              current_state="idle", note="no disallowed state in 3.6; not-found for unknown session"))
        # invalid-fields
        cases.append(case(f"{api}/invalid-fields", "invalid-fields", api, INVALID_FIELDS[api],
                          resp_rpc_error(-32602, "Invalid params", f"{api}/invalid-fields")))
        # disabled-gate
        cases.append(case(f"{api}/disabled-gate", "disabled-gate", api, A[api],
                          resp_tool(env_err("DEBUG_DISABLED", "idle"), PV_NEW, f"{api}/disabled-gate", True)))
        files[f"{api}.json"] = {"schema_version": "dnspy.debug.fixtures.v1", "api": api,
                                "protocol_version": PV_NEW, "cases": cases}
    return files

def gen_capabilities_fixtures():
    limits = {k: v["const"] for k, v in json.load(open(SCHEMA_PATH, encoding="utf-8"))
              ["$defs"]["debug_capabilities_result"]["properties"]["limits"]["properties"].items()}
    def matrix(arch):
        out = []
        for lm, fam in (("net48-exe", "net48"), ("coreclr-apphost", "coreclr"), ("coreclr-dotnet", "coreclr")):
            for a in ("x86", "x64"):
                e = {"launch_mode": lm, "runtime_family": fam, "architecture": a,
                     "product_supported": True, "launch": a == arch, "attach": False,
                     "restart": a == arch, "host_path_required": lm == "coreclr-dotnet"}
                if a != arch:
                    e["unavailable_reason"] = "host_architecture_mismatch"
                out.append(e)
        return out
    def cap_res(enabled, ack, arch):
        return {"debug_enabled": enabled, "schema_version": "dnspy.debug.v1", "extension_version": "1.0.0",
                "dnspy_api": "v6.6.0", "host_architecture": arch,
                "ownership_model": "dedicated_instance_operational_isolation",
                "dedicated_instance_required": True, "dedicated_instance_acknowledged": ack,
                "attach_supported": False,
                "tools": (["debug_capabilities"] if not enabled else
                          ["debug_capabilities", "debug_status", "debug_launch", "debug_pause", "debug_continue",
                           "debug_restart", "debug_terminate", "debug_read_events", "debug_wait_event",
                           "debug_set_breakpoint", "debug_list_breakpoints", "debug_set_breakpoint_enabled",
                           "debug_remove_breakpoint", "debug_list_threads", "debug_get_stack", "debug_step",
                           "debug_get_locals", "debug_expand_value", "debug_list_modules", "debug_read_memory",
                           "debug_dump_module", "debug_set_exception_policy"]),
                "runtime_matrix": matrix(arch),
                "security": {"bind_mode": "loopback", "auth_required": False, "cidr_required": False,
                             "sample_output_policy": "all_tool_output_is_untrusted_data"},
                "artifact_policy": {"retention_scope": "current_extension_process",
                                    "retained_integrity": "process_lifetime_no_write_delete_share_handles",
                                    "external_child_race": "current_admission_may_complete_next_admission_fail_closed",
                                    "cancel_pending": "control_proceeds_store_fail_closed_until_final_completion",
                                    "restart_existing": "stale_untrusted_fail_closed",
                                    "automatic_cleanup": False},
                "limits": limits,
                "unsupported": ["debug_list_attachable_processes", "debug_attach", "debug_detach"]}
    api = "debug_capabilities"
    cases = [
        case(f"{api}/valid-enabled", "valid", api, {},
             resp_tool(env_ok(cap_res(True, True, "x64"), "idle", session=False), PV_NEW, f"{api}/valid-enabled", False)),
        case(f"{api}/valid-disabled-gate", "valid", api, {},
             resp_tool(env_ok(cap_res(False, False, "x64"), "idle", session=False), PV_NEW, f"{api}/valid-disabled-gate", False)),
        case(f"{api}/invalid-fields", "invalid-fields", api, {"unexpected": True},
             resp_rpc_error(-32602, "Invalid params", f"{api}/invalid-fields")),
    ]
    return {f"{api}.json": {"schema_version": "dnspy.debug.fixtures.v1", "api": api,
                            "protocol_version": PV_NEW, "cases": cases}}

def gen_disabled_api_fixtures():
    files = {}
    args_map = {
        "debug_list_attachable_processes": {"name_filter": "Sample"},
        "debug_attach": {"request_id": REQUEST_ID, "pid": 4242, "runtime_identity": "clr4",
                         "runtime_family": "net48", "architecture": "x64", "attach_nonce": "n"},
        "debug_detach": {"session_id": SESSION, "generation": GEN, "request_id": REQUEST_ID},
    }
    invalid_map = {
        "debug_list_attachable_processes": {"page_size": 0},
        "debug_attach": {"request_id": REQUEST_ID, "pid": 0},
        "debug_detach": {"session_id": SESSION},
    }
    for pv in (PV_NEW, PV_2025_03, PV_2024_11):
        for api in sorted(args_map):
            cid = f"{api}/fixed-capability-unavailable@{pv}"
            c1 = case(cid, "fixed-capability-unavailable", api, args_map[api],
                      resp_tool(env_err("CAPABILITY_UNAVAILABLE", "idle"), pv, cid, True))
            iid = f"{api}/invalid-fields@{pv}"
            c2 = case(iid, "invalid-fields", api, invalid_map[api],
                      resp_rpc_error(-32602, "Invalid params", iid))
            key = f"{api}@{pv}.json"
            files[key] = {"schema_version": "dnspy.debug.fixtures.v1", "api": api,
                          "protocol_version": pv, "cases": [c1, c2]}
    return files

def gen_launch_mode_fixtures():
    api = "debug_launch"
    cases = []
    modes_valid = {
        "auto": {"launch_mode": "auto"},
        "net48-exe": {"launch_mode": "net48-exe", "break_kind": "entry"},
        "coreclr-apphost": {"launch_mode": "coreclr-apphost", "break_kind": "none",
                            "target_argv": ["-t"]},
        "coreclr-dotnet": {"launch_mode": "coreclr-dotnet", "host_path": "C:\\dotnet\\dotnet.exe",
                           "host_sha256": "b" * 64, "host_argv": ["exec"]},
        "harness": {"launch_mode": "harness", "harness_path": "C:\\samples\\H.dll",
                    "harness_sha256": "c" * 64, "harness_argv": ["-v"], "break_kind": "none"},
    }
    base = {"request_id": REQUEST_ID, "target_path": "C:\\samples\\Sample.exe",
            "expected_sha256": SHA, "architecture": "x64"}
    def full(mode_extra):
        d = dict(base); d.update(mode_extra); return d
    res = {"session_id": SESSION, "generation": GEN, "state": "starting",
           "claim_deadline_utc": CLAIM_DEADLINE, "launch_mode": "net48-exe",
           "runtime_family": "net48", "architecture": "x64", "file_identities": [file_id("target")]}
    for mode, extra in modes_valid.items():
        r = dict(res); r["launch_mode"] = mode
        r["runtime_family"] = "net48" if mode in ("auto", "net48-exe", "harness") else "coreclr"
        fid = [file_id("target")]
        if mode == "coreclr-dotnet":
            fid = [file_id("host"), file_id("target")]
        if mode == "harness":
            fid = [file_id("harness"), file_id("target")]
        r["file_identities"] = fid
        cid = f"{api}/mode-{mode}"
        cases.append(case(cid, "valid", api, full(extra),
                          resp_tool(env_ok(r, "starting", session=True), PV_NEW, cid, False)))
    for bk in ("none", "process", "module_cctor_or_entry", "entry"):
        cid = f"{api}/break-kind-{bk}"
        r = dict(res); r["launch_mode"] = "net48-exe"
        cases.append(case(cid, "valid", api, full({"launch_mode": "net48-exe", "break_kind": bk}),
                          resp_tool(env_ok(r, "paused" if bk != "none" else "running"), PV_NEW, cid, False)))
    invalids = [
        ("dotnet-missing-host", full({"launch_mode": "coreclr-dotnet"})),
        ("dotnet-break-kind-process", full({"launch_mode": "coreclr-dotnet", "host_path": "C:\\dotnet.exe",
                                            "host_sha256": "b" * 64, "break_kind": "process"})),
        ("dotnet-harness-fields", full({"launch_mode": "coreclr-dotnet", "host_path": "C:\\dotnet.exe",
                                        "host_sha256": "b" * 64, "harness_path": "C:\\h.dll"})),
        ("harness-break-kind-entry", full({"launch_mode": "harness", "harness_path": "C:\\h.dll",
                                           "harness_sha256": "c" * 64, "break_kind": "entry"})),
        ("harness-target-argv", full({"launch_mode": "harness", "harness_path": "C:\\h.dll",
                                      "harness_sha256": "c" * 64, "target_argv": ["x"]})),
        ("net48-harness-field", full({"launch_mode": "net48-exe", "harness_path": "C:\\h.dll"})),
    ]
    for name, args in invalids:
        cid = f"{api}/invalid-{name}"
        cases.append(case(cid, "invalid-fields", api, args,
                          resp_rpc_error(-32602, "Invalid params", cid)))
    return {"debug_launch-modes.json": {"schema_version": "dnspy.debug.fixtures.v1", "api": api,
                                        "protocol_version": PV_NEW, "cases": cases}}

def evt(kind, payload, cursor=11, untrusted=False):
    return {"schema_version": "dnspy.debug.v1", "cursor": cursor, "timestamp_utc": TS, "kind": kind,
            "debug_context": ctx("running"), "payload": payload, "untrusted_sample_data": untrusted}

def gen_control_failure_fixtures():
    files = {}
    # pause: scheduled/issued deadline -> TIMEOUT (current running); issued explicit failure -> INTERNAL_ERROR
    pause_cases = [
        ("scheduled-deadline", "scheduled", "TIMEOUT", "running"),
        ("issued-deadline", "issued", "TIMEOUT", "running"),
        ("issued-explicit-failure", "issued", "INTERNAL_ERROR", "running"),
    ]
    api = "debug_pause"
    cases = []
    for name, phase, code, st in pause_cases:
        cid = f"{api}/{name}"
        e = err_obj(code, st)
        ev = evt("control_failed", {"operation": "pause", "request_id": REQUEST_ID, "control_epoch": 3,
                                    "phase": phase, "error": e,
                                    "late_completion_policy": "reconcile_owned_pause"})
        cases.append(case(cid, "control-failure", api, A[api],
                          resp_tool(env_err(code, st), PV_NEW, cid, True), expected_event=ev))
    files[f"{api}-control-failures.json"] = {"schema_version": "dnspy.debug.fixtures.v1", "api": api,
                                             "protocol_version": PV_NEW, "cases": cases}
    # terminate: policies finish_owned_termination_only
    api = "debug_terminate"
    cases = []
    for name, phase, code in (("scheduled-deadline", "scheduled", "TIMEOUT"),
                              ("issued-deadline", "issued", "TIMEOUT"),
                              ("issued-explicit-failure", "issued", "INTERNAL_ERROR")):
        cid = f"{api}/{name}"
        st = "running"
        e = err_obj(code, st)
        ev = evt("control_failed", {"operation": "terminate", "request_id": REQUEST_ID, "control_epoch": 4,
                                    "phase": phase, "error": e,
                                    "late_completion_policy": "finish_owned_termination_only"})
        cases.append(case(cid, "control-failure", api, A[api],
                          resp_tool(env_err(code, st), PV_NEW, cid, True), expected_event=ev))
    files[f"{api}-control-failures.json"] = {"schema_version": "dnspy.debug.fixtures.v1", "api": api,
                                             "protocol_version": PV_NEW, "cases": cases}
    # restart: finish_restart_as_failed; issued failure keeps session (state faulted)
    api = "debug_restart"
    cases = []
    for name, phase, code, st in (("scheduled-deadline", "scheduled", "TIMEOUT", "running"),
                                  ("issued-deadline", "issued", "TIMEOUT", "faulted"),
                                  ("issued-explicit-failure", "issued", "INTERNAL_ERROR", "faulted")):
        cid = f"{api}/{name}"
        e = err_obj(code, st)
        ev = evt("control_failed", {"operation": "restart", "request_id": REQUEST_ID, "control_epoch": 5,
                                    "phase": phase, "error": e,
                                    "late_completion_policy": "finish_restart_as_failed"})
        cases.append(case(cid, "control-failure", api, A[api],
                          resp_tool(env_err(code, st), PV_NEW, cid, True), expected_event=ev))
    files[f"{api}-control-failures.json"] = {"schema_version": "dnspy.debug.fixtures.v1", "api": api,
                                             "protocol_version": PV_NEW, "cases": cases}
    return files

# ---------- byte-boundary fixtures ----------
def u(s):  # strict UTF-8 byte count (== .NET Encoding.UTF8.GetByteCount for valid strings)
    return len(s.encode("utf-8"))

def of_bytes(n, mode):
    """String of exactly n UTF-8 bytes: ASCII=1B, BMP=2B(+pad to parity via ASCII tail), non-BMP=4B."""
    if mode == "ascii":
        return "a" * n
    if mode == "bmp":
        q, r = divmod(n, 2)
        s = "é" * q
        return s + ("a" if r else "")
    q, r = divmod(n, 4)
    s = "😀" * q
    return s + "a" * r

def gen_byte_fixtures():
    api_bk = {"pointer_kind": None}
    cases = []
    # input-direction pointers -> requests; over-limit always -32602, at-limit reaches next gate
    INPUTS = [
        ("/$defs/opaque_handle", 1024, "debug_step", "thread_handle",
         {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH, "request_id": REQUEST_ID,
          "kind": "over"}, "invalid-state", "paused"),
        ("/$defs/session_id", 1024, "debug_status", "session_id", {}, "valid", "idle"),
        ("/$defs/page_cursor", 1024, "debug_list_breakpoints", "page_cursor",
         {"session_id": SESSION, "generation": GEN}, "valid", "running"),
        ("/$defs/debug_list_attachable_processes_args/properties/name_filter", 256,
         "debug_list_attachable_processes", "name_filter", {}, "fixed-capability-unavailable", "idle"),
        ("/$defs/debug_dump_module_args/properties/relative_name", 128, "debug_dump_module", "relative_name",
         {"session_id": SESSION, "generation": GEN, "pause_epoch": EPOCH,
          "request_id": REQUEST_ID, "module_handle": H_MODULE}, "invalid-state", "idle"),
    ]
    for pointer, limit, api, field, base_args, at_kind, at_state in INPUTS:
        for mode in ("ascii", "bmp", "non-bmp"):
            for bound, delta in (("at-limit", 0), ("over-limit", 1)):
                val = of_bytes(limit + delta, mode)
                args = dict(base_args)
                args[field] = val
                cid = f"byte/{pointer.rsplit('/', 1)[-1]}@{mode}@{bound}"
                if bound == "over-limit" or (mode != "ascii" and pointer in (
                        "/$defs/opaque_handle", "/$defs/session_id", "/$defs/page_cursor")):
                    # pattern 化句柄指针:charset+{1,1024} 使任何非 ASCII 或超限值先违反结构,
                    # 仅 ASCII at-limit 真正到达字节阶段;其余指针 Unicode 合法,超限才被字节阶段拒绝
                    stage = "structure" if pointer in (
                        "/$defs/opaque_handle", "/$defs/session_id", "/$defs/page_cursor") else "byte"
                    expected = resp_rpc_error(-32602, "Invalid params", cid)
                    cases.append(case(cid, "byte-input", api, args, expected, stage=stage, utf8_bytes=u(val),
                                      pointer=pointer, max_utf8_bytes=limit))
                else:
                    # at-limit: pointer is byte-valid; downstream expectation per api/kind
                    if at_kind == "valid":
                        expected = resp_tool(env_ok(R[api], at_state), PV_NEW, cid, False)
                    elif at_kind == "fixed-capability-unavailable":
                        expected = resp_tool(env_err("CAPABILITY_UNAVAILABLE", "idle"), PV_NEW, cid, True)
                    else:
                        expected = resp_tool(env_err("INVALID_STATE", at_state, ALLOWED[api]), PV_NEW, cid, True)
                    cases.append(case(cid, "byte-input", api, args, expected, stage="byte", utf8_bytes=u(val),
                                      pointer=pointer, max_utf8_bytes=limit))
    # output-direction pointers -> truncation / small-envelope rules
    OUTPUTS = [
        ("/$defs/warning", 1024, "warning", "internal_error_or_shrink"),
        ("/$defs/value_node/properties/name", 65536, "value_node", "scalar_prefix_truncate"),
        ("/$defs/value_node/properties/type", 65536, "value_node", "scalar_prefix_truncate"),
        ("/$defs/value_node/properties/display", 65536, "value_node", "scalar_prefix_truncate"),
        ("/$defs/unsupported_target_evidence/properties/value", 1024, "evidence_value", "internal_error_or_shrink"),
    ]
    for pointer, limit, target, rule in OUTPUTS:
        for mode in ("ascii", "bmp", "non-bmp"):
            for bound, delta in (("at-limit", 0), ("over-limit", 1)):
                val = of_bytes(limit + delta, mode)
                cid = f"byte/{target}.{pointer.rsplit('/', 1)[-1]}@{mode}@{bound}"
                expected = {"rule": ("pass" if bound == "at-limit" else
                                     ("truncated_true_scalar_prefix" if rule == "scalar_prefix_truncate"
                                      else "small_internal_error_envelope"))}
                cases.append(case(cid, "byte-output-rule", "debug_get_locals", {},
                                  expected, stage="byte", utf8_bytes=u(val), pointer=pointer,
                                  max_utf8_bytes=limit, output_target=target))
    return {"byte-limits.json": {"schema_version": "dnspy.debug.fixtures.v1", "api": "byte-limits",
                                 "protocol_version": PV_NEW, "cases": cases}}

def main():
    os.makedirs(OUT, exist_ok=True)
    files = {}
    files.update(gen_api_fixtures())
    files.update(gen_capabilities_fixtures())
    files.update(gen_disabled_api_fixtures())
    files.update(gen_launch_mode_fixtures())
    files.update(gen_control_failure_fixtures())
    files.update(gen_byte_fixtures())
    manifest = {"schema_version": "dnspy.debug.fixtures.v1", "files": sorted(files.keys()),
                "case_count": sum(len(f["cases"]) for f in files.values())}
    for name, doc in files.items():
        with open(os.path.join(OUT, name), "w", encoding="utf-8") as fh:
            json.dump(doc, fh, ensure_ascii=False, indent=1, sort_keys=False)
            fh.write("\n")
    with open(os.path.join(OUT, "MANIFEST.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, ensure_ascii=False, indent=1)
        fh.write("\n")
    print(f"wrote {len(files)} fixture files, {manifest['case_count']} cases -> {OUT}")

if __name__ == "__main__":
    main()
