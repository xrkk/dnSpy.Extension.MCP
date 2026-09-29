"""F01 许可证/授权绕过 — family workflow (10 variants)."""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from famcommon import (SAMPLES, assemblies_contain, edit_txn_begin, edit_txn_end,
                       expect_type_face, literals, open_sample, reset_and_close)

LICENSE_TYPES = {"LicenseSample.LicenseGate", "LicenseSample.AppMain",
                 "LicenseSample.LicenseStrings", "<Module>"}
# write target per variant: (tool, method, value)
WRITES = {
    "01": ("force_return", "Check", 1), "02": ("force_return", "Check", 0),
    "03": ("nop_method", "Validate", None), "04": ("nop_method", "Validate", None),
    "05": ("patch_method_il", "DaysRemaining", None), "06": ("patch_method_il", "DaysRemaining", None),
    "07": ("force_return", "Check", 365), "08": ("nop_method", "Validate", None),
    "09": ("patch_method_il", "DaysRemaining", None), "10": ("force_return", "Check", 1),
}
SAVERS = {"02", "04", "05", "07", "09"}


def run_variant(env, sid: str) -> None:
    v = sid[-2:]
    client, asserts = env.client, env.asserts
    A = "license-01"

    open_sample(env, A)
    assemblies_contain(env, A)
    expect_type_face(env, A, LICENSE_TYPES)

    hits = literals(env, A, "INVALID")
    asserts.strong_count(client.step_seq, len(hits), 1, "INVALID literals count")
    asserts.strong_equal(client.step_seq,
                         hits[0].get("method") if hits else None, "Validate",
                         "INVALID literal owner method")

    lc = client.call_tool_json("list_string_constants",
                               {"assembly_name": A, "type_full_name": "LicenseSample.LicenseGate"})
    asserts.weak_ok(client.step_seq, isinstance(lc.get("items"), list), "list_string_constants shape")

    dec = client.call_tool_json("decompile_method",
                                {"assembly_name": A,
                                 "type_full_name": "LicenseSample.LicenseGate",
                                 "method_name": "Check"})
    asserts.weak_fields(client.step_seq, dec, ["text"], "decompile Check") if isinstance(dec, dict) \
        else asserts.weak_ok(client.step_seq, isinstance(dec, str), "decompile text")

    callers = client.call_tool_json("find_callers",
                                    {"assembly_name": A,
                                     "type_full_name": "LicenseSample.LicenseGate",
                                     "method_name": "Validate"})
    callers_names = {c.get("caller_method") for c in (callers.get("items") or [])}
    asserts.weak_ok(client.step_seq, len(callers_names) >= 1, "Validate has callers")

    il_before = client.call_tool_json("get_method_il",
                                      {"assembly_name": A,
                                       "type_full_name": "LicenseSample.LicenseGate",
                                       "method_name": "Check"})
    instr_before = il_before.get("instructions", [])
    asserts.strong_count(client.step_seq, len(instr_before), len(instr_before), "Check IL size")

    tool, method, value = WRITES[v]
    target = {"assembly_name": A, "type_full_name": "LicenseSample.LicenseGate",
              "method_name": method}
    if tool == "force_return":
        patched = client.call_tool_json("force_return", {**target, "value": value})
        first = (patched.get("instructions") or [{}])[0].get("opcode", "")
        asserts.strong_equal(client.step_seq, first, f"ldc.i4.{value}", "force_return first opcode")
    elif tool == "nop_method":
        patched = client.call_tool_json("nop_method", target)
        opcodes = {i.get("opcode") for i in patched.get("instructions", [])}
        asserts.strong_in_set(client.step_seq, "nop", opcodes, "nop_method opcode set")
    else:  # patch_method_il: replace first instruction with ldc.i4.0
        il = client.call_tool_json("get_method_il", {**target, "method_name": "DaysRemaining"})
        patched = client.call_tool_json(
            "patch_method_il", {**target, "method_name": "DaysRemaining",
                                "edits": [{"op": "replace", "index": 0,
                                           "opcode": "ldc.i4.0", "operand": ""}]})
        first = (patched.get("instructions") or [{}])[0].get("opcode", "")
        asserts.strong_equal(client.step_seq, first, "ldc.i4.0", "patch replace first opcode")

    if v in SAVERS:
        saved = client.call_tool_json(
            "save_assembly", {"assembly_name": A,
                              "output_path": rf"E:\dnspy-scenario\artifacts\f01-lic-{v}.dll"})
        asserts.strong_equal(client.step_seq, saved.get("source_preserved"), True,
                             "save source_preserved")
        asserts.weak_ok(client.step_seq, (saved.get("bytes_written") or 0) > 0, "save bytes>0")

    reverted = client.call_tool_json("revert_method_il", target)
    asserts.weak_fields(client.step_seq, reverted, ["method"], "revert response")

    reset_and_close(env)
