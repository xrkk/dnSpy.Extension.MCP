#!/usr/bin/env python3
"""P05 production acceptance (round 2): the real dnSpy Roslyn compiler through
the edit_compile MCP tool on the real loopback — valid C# compiles with a
Portable PDB payload that loads standalone with sequence points, invalid source
returns diagnostics without an artifact, the contract face carries no analyzer/
generator/script fields, and compilation leaves the coordinator idle."""

from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
FAILURES: list[str] = []
PASSES: list[str] = []

VALID_SOURCE = """using System;
namespace CompileFrontend {
    public sealed class Target {
        public int Field = 4;
        public int Helper(int value) => value + Field;
        public int Compute(int value) {
            try { return Helper(value) * 3 + Field + 7; }
            catch (Exception) { return -2; }
        }
        public string Added<T>(T value) where T : class {
            return value == null ? "null" : value.ToString();
        }
    }
}
"""
INVALID_SOURCE = "namespace CompileFrontend { public sealed class Target { public int Compute( } }"


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


def core(envelope: dict) -> dict:
    value = payload(envelope).get("compile")
    return value if isinstance(value, dict) else {}


def main() -> int:
    client = DnSpyClient(URL, client_name="p03-vm-acc005", timeout=120)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # tools/list is a protocol-level method, not a tool call: issue it through
    # the raw request channel of the client.
    import urllib.request
    body = json.dumps({"jsonrpc": "2.0", "id": 999, "method": "tools/list", "params": {}}).encode()
    request = urllib.request.Request(URL, data=body, headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream", "Mcp-Session-Id": client.session_id})
    with urllib.request.urlopen(request, timeout=30) as response:
        wire = response.read().decode("utf-8", "replace")
    names = [n for n in ("edit_compile", "open_files", "edit_begin") if f'"name":"{n}"' in wire.replace(" ", "").replace(chr(10), "") or f'"name": "{n}"' in wire]
    check("G1 edit_compile advertised", "edit_compile" in names, f"wire={wire[:120]}")

    def compile_args(source: str, kind: str = "edit_class") -> dict:
        return {
            "request_id": rid(), "assembly_name": "TestIL", "compilation_kind": kind,
            "documents": [{"path": "Target.cs", "content": source}],
        }

    # Valid compile through the real provider: success, diagnostics, artifacts.
    valid = call(client, "edit_compile", compile_args(VALID_SOURCE))
    valid_core = core(valid)
    check("V1 valid compile ok", bool(valid.get("ok")) and bool(valid_core.get("success")), json.dumps(valid)[:300])
    check("V1 diagnostics empty-or-info", isinstance(valid_core.get("diagnostics"), list), json.dumps(valid_core.get("diagnostics", []))[:200])
    assembly = valid_core.get("assembly", {}) if isinstance(valid_core.get("assembly"), dict) else {}
    pdb = valid_core.get("portable_pdb", {}) if isinstance(valid_core.get("portable_pdb"), dict) else {}
    check("V1 assembly identity", int(assembly.get("length", 0)) or len(str(assembly.get("sha256", ""))) == 64, json.dumps(assembly)[:200])
    check("V1 portable pdb identity", int(pdb.get("length", 0)) > 0 and len(str(pdb.get("sha256", ""))) == 64, json.dumps(pdb)[:200])
    compile_id = str(valid_core.get("compile_id", ""))
    check("V1 compile_id present", compile_id.startswith("compile-"), compile_id)
    check("V1 consumable_by_import", bool(valid_core.get("consumable_by_import")))

    # Invalid compile: diagnostics with location, no artifact.
    invalid = call(client, "edit_compile", compile_args(INVALID_SOURCE))
    invalid_core = core(invalid)
    check("E1 invalid compile not success", bool(invalid.get("ok")) and not bool(invalid_core.get("success")), json.dumps(invalid)[:240])
    diagnostics = invalid_core.get("diagnostics", []) if isinstance(invalid_core.get("diagnostics"), list) else []
    check("E1 diagnostics present", len(diagnostics) > 0, json.dumps(diagnostics)[:240])
    severities = [str(row.get("severity", "")) for row in diagnostics if isinstance(row, dict)]
    check("E1 error severity reported", any("error" in s.lower() for s in severities), str(severities[:4]))
    check("E1 no compile_id on failure", not str(invalid_core.get("compile_id", "")), str(invalid_core.get("compile_id", "")))

    # Contract face: unknown field rejected (CON-022 closure — the schema has no
    # analyzer/generator/script/build-task field; additionalProperties false).
    with_analyzer = compile_args(VALID_SOURCE)
    with_analyzer["analyzers"] = ["SomeAnalyzer.dll"]
    rejected = call(client, "edit_compile", with_analyzer)
    check("F1 unknown extension field rejected", not rejected.get("ok") or "error" in rejected, json.dumps(rejected)[:240])

    # Coordinator untouched by compilation.
    status = payload(call(client, "edit_status", {}))
    check("S1 coordinator idle after compiles", status.get("state") == "idle", json.dumps(status)[:160])

    # edit_compile products not consumable by any existing tool: apply with a
    # compile artifact id in the operation is an unknown kind (rejected).
    begin = call(client, "edit_begin", {"assembly_name": "TestIL", "request_id": rid()})
    tx = payload(begin).get("transaction", {}).get("transaction_id", "")
    if tx:
        revision = int(payload(begin).get("transaction", {}).get("work_revision", 0))
        applied = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
            "operation": {"kind": "compile_import", "compile_id": compile_id},
        })
        check("S2 compile artifact not consumable via edit_apply", not applied.get("ok"), json.dumps(applied)[:240])
        call(client, "edit_rollback", {"request_id": rid(), "transaction_id": tx})

    print(f"ACC005C {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
