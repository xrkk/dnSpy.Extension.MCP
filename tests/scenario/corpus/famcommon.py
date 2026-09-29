"""Shared family-workflow stages for the scenario corpus (P03 IMP-304b).

All stages operate on the scenario_env from the P02 conftest: every MCP call
goes through env.client (RecordingClient), every assertion through env.asserts
(AssertionApi). Expected values are frozen from the P03 probe records
(corpus/manifests/*.json).
"""

from __future__ import annotations

SAMPLES = {
    "TestIL": r"E:\dnspy-scenario\samples\TestIL.dll",
    "license-01": r"E:\dnspy-scenario\samples\scenario\license-01\license-01.dll",
    "hooktarget-01": r"E:\dnspy-scenario\samples\scenario\hooktarget-01\hooktarget-01.dll",
    "malfeat-01": r"E:\dnspy-scenario\samples\scenario\malfeat-01\malfeat-01.dll",
    "obfuscated-01": r"E:\dnspy-scenario\samples\scenario\obfuscated-01\obfuscated-01.dll",
    "renametree-01": r"E:\dnspy-scenario\samples\scenario\renametree-01\renametree-01.dll",
    "constmatrix-01": r"E:\dnspy-scenario\samples\scenario\constmatrix-01\constmatrix-01.dll",
    "unitymsgs-01": r"E:\dnspy-scenario\samples\scenario\unitymsgs-01\unitymsgs-01.dll",
    "resource-01": r"E:\dnspy-scenario\samples\scenario\resource-01\resource-01.dll",
    "xref-a": r"E:\dnspy-scenario\samples\scenario\xref-a\xref-a.dll",
    "xref-b": r"E:\dnspy-scenario\samples\scenario\xref-b\xref-b.dll",
    "runtarget-01": r"E:\dnspy-scenario\samples\scenario\runtarget-01\runtarget-01.exe",
}

RUNTARGET_SHA = "8aac59523304a3ccf45c99dd6b2d3f5887593fb74eacf53966c61c819d38bced"


def open_sample(env, *names: str, expect_types: dict[str, set[str]] | None = None):
    """open_files for the named samples; optionally strong-assert each loaded
    assembly name appears in list_assemblies (checked separately by caller)."""
    paths = [SAMPLES[n] for n in names]
    opened = env.client.call_tool_json("open_files", {"paths": paths})
    total = opened.get("loaded_count", 0) + opened.get("already_loaded_count", 0)
    env.asserts.strong_count(env.client.step_seq, total, len(paths), "open_files loaded+already")
    env.asserts.strong_count(env.client.step_seq, opened.get("failed_count"), 0, "open_files failed")
    return opened


def assemblies_contain(env, *names: str) -> list[str]:
    asm = env.client.call_tool_json("list_assemblies", {})
    got = {a.get("Name") for a in asm.get("assemblies", [])}
    for n in names:
        env.asserts.strong_in_set(env.client.step_seq, n, got, f"list_assemblies has {n}")
    return sorted(got)


def type_face(env, assembly: str, query: str = "", page: int = 200) -> set[str]:
    r = env.client.call_tool_json("search_types",
                                  {"query": query, "assembly_name": assembly, "page_size": page})
    return {i["FullName"] for i in r.get("items", [])}


def expect_type_face(env, assembly: str, expected: set[str]) -> set[str]:
    face = type_face(env, assembly)
    env.asserts.strong_set(env.client.step_seq, face, expected, f"{assembly} type face")
    return face


def literals(env, assembly: str, query: str) -> list[dict]:
    r = env.client.call_tool_json("search_string_literals",
                                  {"query": query, "assembly_name": assembly})
    return r.get("items", [])


def edit_txn_begin(env, request_id: str, assembly: str) -> tuple[str, int]:
    b = env.client.call_tool_json("edit_begin", {"request_id": request_id,
                                                 "assembly_name": assembly})
    inner = b.get("result", b)
    tx = inner.get("transaction", inner)
    env.asserts.weak_fields(env.client.step_seq, tx,
                            ["transaction_id", "work_revision"], "edit_begin txn fields")
    return tx["transaction_id"], tx["work_revision"]


def edit_txn_end(env, request_id: str, tx: str, commit: bool, review_id=None,
                 expected_revision=None):
    if commit:
        if review_id is None:
            review_id = f"{request_id}-rv"
        env.client.call_tool_json("edit_review",
                                  {"request_id": request_id, "transaction_id": tx,
                                   "expected_revision": expected_revision or 1})
        return env.client.call_tool_json(
            "edit_commit", {"request_id": request_id, "transaction_id": tx,
                            "expected_revision": expected_revision or 1,
                            "review_id": review_id, "review_revision": 0,
                            "confirmed_risk_ids": []})
    return env.client.call_tool_json("edit_rollback", {"request_id": request_id,
                                                       "transaction_id": tx})


def recovery_probe(env, tool: str, args: dict, expect_code: str):
    """恢复面探针: expect the domain error envelope, strong via expect_error."""
    with env.client.expect_error(tool, expect_code):
        env.client.call_tool_json(tool, args)


def reset_and_close(env):
    """复位段收尾: 状态核对 + 关闭会话(五检查由 conftest teardown 兜底)."""
    status = env.client.call_tool_json("edit_status", {})
    env.asserts.weak_ok(env.client.step_seq, status.get("ok"), "edit_status.ok")
    env.client.close()
