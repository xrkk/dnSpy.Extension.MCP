#!/usr/bin/env python3
"""P09 ACC-023: no static path ever executes sample code.  Reuses the P08
sentinel fixture (a serialized object whose deserialization writes a marker
file) plus directed probes: malformed .resources, malicious C# source compiled
but only ever viewed statically, checkpoint browsing, and a gate-allowed
dynamic run as the positive control.  Evidence list (adjudicated AUD-008):
P08 EDIT-ACC-007/033 sentinels (run-ids p08-resource-20260912-r1-*), the P01
ACC-017 rejection state, and the directed probes below."""

from __future__ import annotations

import json
import os
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\ResourceHost\ResourceHost.dll"
MALICIOUS_SOURCE = """using System;
using System.IO;
using System.Runtime.Serialization;

namespace Malicious
{
    [Serializable]
    public class Boom
    {
        [OnDeserialized]
        private void D(StreamingContext c) { File.WriteAllText(@"C:\\Tools\\dnspy-mcp-edit-tests\\p09-sentinel.flag", "fired"); }
    }

    public static class Go
    {
        public static string Payload()
        {
            var b = new Boom();
            var ms = new MemoryStream();
            new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(ms, b);
            return Convert.ToBase64String(ms.ToArray());
        }
    }
}
"""
FAILURES: list[str] = []
PASSES: list[str] = []
SENTINEL = r"C:\Tools\dnspy-mcp-edit-tests\p09-sentinel.flag"


def rid() -> str:
    return str(uuid.uuid4())


def check(name: str, condition: bool, detail: str = "") -> None:
    if condition:
        PASSES.append(name)
        print(f"PASS {name}", flush=True)
    else:
        FAILURES.append(name)
        print(f"FAIL {name} {detail}", flush=True)


def call(client: DnSpyClient, tool: str, args: dict) -> dict:
    try:
        return client.call_tool_json(tool, args)
    except Exception as ex:  # noqa: BLE001
        text = str(ex)
        start = text.find("{")
        if start >= 0:
            try:
                return json.loads(text[start:])
            except json.JSONDecodeError:
                pass
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:300]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def sentinel_absent() -> bool:
    return not os.path.exists(SENTINEL)


def main() -> int:
    client = DnSpyClient(URL, client_name="p09-acc023", timeout=120)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # S1: static viewing of the fixture holding a serialized activator object
    types = call(client, "list_types", {"assembly_name": "ResourceHost"})
    check("S1 static type listing never executes", bool(types.get("items") or types.get("Items") or "error" not in types),
          json.dumps(types)[:200])
    check("S1 sentinel absent after static view", sentinel_absent(), SENTINEL)

    # S2: malformed .resources rejected with zero side effects
    begin = call(client, "edit_begin", {"assembly_name": "ResourceHost", "request_id": rid()})
    tx = str(payload(begin).get("transaction", {}).get("transaction_id", ""))
    revision = int(payload(begin).get("transaction", {}).get("work_revision", 0))
    import base64
    # CON-019: whole-blob replacement accepts arbitrary bytes (the custom-object
    # face); the codec rejection surfaces on a subsequent ENTRY edit of that row
    bad_blob = base64.b64encode(b"\xde\xad\xbe\xef not a resources container").decode()
    malformed = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "managed_resource_update",
                      "target": {"name": "ResourceHost.Strings.resources"},
                      "data_base64": bad_blob}})
    check("S2 malformed whole-blob replacement is the custom-object face", bool(malformed.get("ok")),
          json.dumps(malformed)[:240])
    revision = int(payload(malformed).get("transaction", {}).get("work_revision", revision))
    entry_reject = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "managed_resource_update",
                      "target": {"name": "ResourceHost.Strings.resources"},
                      "entry": {"name": "greeting", "value_kind": "string", "value": "x"}}})
    check("S2 entry edit of a malformed container rejected", not entry_reject.get("ok"), json.dumps(entry_reject)[:240])
    revision = int(payload(entry_reject).get("transaction", {}).get("work_revision", revision))
    truncated = base64.b64encode(b"\xce\xca\xef\xbe\x01\x00\x00\x00\x40\x00\x00\x00trunc").decode()
    call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    begin = call(client, "edit_begin", {"assembly_name": "ResourceHost", "request_id": rid()})
    tx = str(payload(begin).get("transaction", {}).get("transaction_id", ""))
    revision = int(payload(begin).get("transaction", {}).get("work_revision", 0))
    truncated_reject = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "managed_resource_update",
                      "target": {"name": "ResourceHost.Strings.resources"},
                      "data_base64": truncated}})
    check("S2 truncated whole-blob replacement is the custom-object face", bool(truncated_reject.get("ok")),
          json.dumps(truncated_reject)[:240])
    revision = int(payload(truncated_reject).get("transaction", {}).get("work_revision", revision))
    truncated_entry = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "managed_resource_update",
                      "target": {"name": "ResourceHost.Strings.resources"},
                      "entry": {"name": "greeting", "value_kind": "string", "value": "x"}}})
    check("S2 entry edit of a truncated container rejected", not truncated_entry.get("ok"), json.dumps(truncated_entry)[:240])
    check("S2 sentinel absent after resource probes", sentinel_absent(), SENTINEL)

    # S3: malicious C# compiles (compilation is not execution) and the product
    # stays idle; the artifact is only ever metadata
    call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})
    tx = ""
    compiled = call(client, "edit_compile", {
        "request_id": rid(), "assembly_name": "ResourceHost", "compilation_kind": "edit_class",
        "documents": [{"path": "Boom.cs", "content": MALICIOUS_SOURCE}]})
    compile_core = payload(compiled).get("compile", {})
    check("S3 malicious source compiles without executing", bool(compiled.get("ok")) and bool(compile_core.get("success")),
          json.dumps(compiled)[:300])
    status = payload(call(client, "edit_status", {}))
    check("S3 coordinator idle after compile", status.get("state") == "idle", json.dumps(status)[:160])
    check("S3 sentinel absent after compile", sentinel_absent(), SENTINEL)

    # S4: checkpoint browsing of the fixture module is read-only
    history = call(client, "edit_history", {})
    check("S4 checkpoint browsing never executes", "error" not in history, json.dumps(history)[:200])
    check("S4 sentinel absent after browsing", sentinel_absent(), SENTINEL)

    # rollback any staged ops
    if tx:
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})

    # P1: gate-allowed dynamic run as the positive control — a real process
    # event is only produced by debug_launch (the VM execution gate)
    import hashlib
    exe = r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\ImportHost.exe"
    sha = ""
    try:
        sha = hashlib.sha256(Path(exe).read_bytes()).hexdigest()
    except OSError:
        sha = ""
    launch = call(client, "debug_launch", {
        "request_id": rid(), "target_path": exe, "expected_sha256": sha,
        "launch_mode": "net48-exe", "architecture": "x64", "break_kind": "entry"})
    if not launch.get("ok"):
        print("LAUNCH-DETAIL " + json.dumps(launch)[:400], flush=True)
    session_id = str(payload(launch).get("session_id", ""))
    saw_event = False
    if session_id:
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline and not saw_event:
            waited = payload(call(client, "debug_wait_event", {
                "session_id": session_id, "after_cursor": 0, "limit": 10,
                "kinds": ["paused", "process_exited"], "timeout_ms": 2500}))
            saw_event = bool(waited.get("events"))
            time.sleep(0.4)
        call(client, "debug_terminate", {"session_id": session_id, "generation": int(payload(launch).get("generation", 0)), "request_id": rid()})
    check("P1 gate-allowed dynamic run produces process events", saw_event, f"session={session_id}")

    client.close()
    print(f"ACC023 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
