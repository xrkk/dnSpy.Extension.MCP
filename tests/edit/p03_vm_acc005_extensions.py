#!/usr/bin/env python3
"""ACC-005 compile-extension boundary through the real MCP loopback.

The VM fixture contains a real Roslyn DiagnosticAnalyzer/ISourceGenerator and
a .csx file.  Their positive controls run in separate test-tool processes
before this driver.  Here every product activation surface is rejected, while
the same assembly remains safe when consumed only as a metadata reference.
"""

from __future__ import annotations

import hashlib
import json
import os
import sys
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
ARCH = os.environ.get("EDIT_ACC005_ARCH", "x64")
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\TestIL.dll"
ISOLATION_ROOT: str | None = None
ARTIFACT_ROOT: str | None = None
WORK_ROOT: str | None = None
FAILURES: list[str] = []
PASSES: list[str] = []


def configure_isolation(context) -> None:
    global URL, ARCH, FIXTURE, ISOLATION_ROOT, ARTIFACT_ROOT, WORK_ROOT
    context.validate()
    URL = context.mcp_url
    ARCH = context.architecture
    FIXTURE = context.fixture("TestIL.dll")
    ISOLATION_ROOT = context.isolation_root
    ARTIFACT_ROOT = context.artifact_root
    WORK_ROOT = context.work_root


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
        return {"ok": False, "error": {"code": "DRIVER_TRANSPORT", "message": text[:500]}}


def payload(envelope: dict) -> dict:
    value = envelope.get("result") if isinstance(envelope, dict) else None
    return value if isinstance(value, dict) else {}


def compile_core(envelope: dict) -> dict:
    value = payload(envelope).get("compile")
    return value if isinstance(value, dict) else {}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def product_markers() -> list[str]:
    root = Path(WORK_ROOT or "") / "sentinel" / "product-markers"
    return sorted(str(path) for path in root.glob("*.marker")) if root.is_dir() else []


def artifact_files() -> list[dict]:
    root = Path(ARTIFACT_ROOT or "")
    rows: list[dict] = []
    if not root.is_dir():
        return rows
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root)
        if not path.is_file() or "edit-tests" in relative.parts:
            continue
        rows.append({"path": str(relative), "length": path.stat().st_size, "sha256": sha256(path)})
    return rows


def pdb_files() -> list[dict]:
    root = Path(ISOLATION_ROOT or "")
    rows = []
    if not root.is_dir():
        return rows
    for path in sorted(root.rglob("*.pdb")):
        rows.append({"path": str(path.relative_to(root)), "length": path.stat().st_size, "sha256": sha256(path)})
    return rows


def product_temp_files() -> list[dict]:
    root = Path(os.environ.get("TEMP", ""))
    rows = []
    if not root.is_dir():
        return rows
    for path in sorted(root.glob("dnspy-mcp-*")):
        if path.is_file():
            rows.append({"path": str(path), "length": path.stat().st_size, "sha256": sha256(path)})
    return rows


def tools_list(client: DnSpyClient) -> tuple[dict, str]:
    body = json.dumps({"jsonrpc": "2.0", "id": 999, "method": "tools/list", "params": {}}).encode()
    request = urllib.request.Request(URL, data=body, headers={
        "Content-Type": "application/json", "Accept": "application/json, text/event-stream",
        "Mcp-Session-Id": client.session_id})
    with urllib.request.urlopen(request, timeout=30) as response:
        wire = response.read().decode("utf-8", "replace")
    if wire.lstrip().startswith("data:"):
        wire = "\n".join(line[5:].strip() for line in wire.splitlines() if line.startswith("data:"))
    return json.loads(wire), wire


def main() -> int:
    if not all((ISOLATION_ROOT, ARTIFACT_ROOT, WORK_ROOT)):
        check("C0 isolation configured", False, "configure_isolation(context) is required")
        return 1
    sentinel_root = Path(WORK_ROOT) / "sentinel"
    extension = sentinel_root / "T024.ExtensionSentinel.dll"
    script = sentinel_root / "T024.ScriptSentinel.csx"
    control_root = sentinel_root / "control-markers"
    product_root = sentinel_root / "product-markers"
    check("C0 sentinel inputs present", extension.is_file() and script.is_file(), str(sentinel_root))
    check("C0 analyzer positive control", (control_root / "analyzer.marker").is_file())
    check("C0 generator positive control", (control_root / "generator.marker").is_file())
    check("C0 script positive control", (control_root / "script.marker").is_file())
    check("C0 product markers initially absent", product_markers() == [], str(product_markers()))

    before_artifacts = artifact_files()
    before_pdb = pdb_files()
    before_temp = product_temp_files()
    client = DnSpyClient(URL, client_name=f"t024-acc005-extensions-{ARCH}", timeout=120)
    client.initialize()
    opened = call(client, "open_files", {"paths": [FIXTURE]})
    check("L1 fixture opened", int(opened.get("loaded_count", 0)) + int(opened.get("already_loaded_count", 0)) == 1,
          json.dumps(opened)[:300])

    listed, raw_wire = tools_list(client)
    tools = listed.get("result", {}).get("tools", [])
    compile_tool = next((row for row in tools if row.get("name") == "edit_compile"), {})
    schema = compile_tool.get("inputSchema", {})
    properties = schema.get("properties", {})
    expected = {"request_id", "assembly_name", "compilation_kind", "documents", "target_platform", "references_override"}
    document_schema = properties.get("documents", {}).get("items", {})
    check("G1 exact compile surface", schema.get("additionalProperties") is False
          and set(properties) == expected
          and set(properties.get("compilation_kind", {}).get("enum", [])) == {"edit_method", "edit_class"}, raw_wire[:800])
    check("G1 exact document surface", document_schema.get("additionalProperties") is False
          and set(document_schema.get("properties", {})) == {"path", "content"}, json.dumps(document_schema)[:500])

    def base(source: str = "namespace T024 { public sealed class Safe { public object Value; } }") -> dict:
        return {"request_id": rid(), "assembly_name": "TestIL", "compilation_kind": "edit_class",
                "documents": [{"path": "Safe.cs", "content": source}]}

    extension_attempts = {
        "analyzer field": ("analyzers", [str(extension)]),
        "generator field": ("source_generators", [str(extension)]),
        "build-task field": ("build_tasks", [str(extension)]),
        "script-path field": ("script_path", str(script)),
    }
    rejection_rows = []
    for label, (field, value) in extension_attempts.items():
        args = base()
        args[field] = value
        response = call(client, "edit_compile", args)
        rejection_rows.append({"label": label, "field": field, "response": response})
        check(f"F1 {label} rejected", not bool(response.get("ok")), json.dumps(response)[:500])
        check(f"F1 {label} did not execute", product_markers() == [], str(product_markers()))

    script_kind = base()
    script_kind["compilation_kind"] = "script"
    response = call(client, "edit_compile", script_kind)
    rejection_rows.append({"label": "script compilation kind", "response": response})
    check("F2 script compilation kind rejected", not bool(response.get("ok")), json.dumps(response)[:500])

    nested_attempts = []
    nested = base()
    nested["documents"][0]["source_kind"] = "script"
    nested_attempts.append(("document script kind", nested))
    arbitrary = base()
    arbitrary["documents"][0]["future_extension"] = {"enabled": True}
    nested_attempts.append(("document arbitrary field", arbitrary))
    later = base()
    later["documents"].append({"path": "Second.cs", "content": "namespace T024 { public class Second { } }",
                                "future_extension": "generator"})
    nested_attempts.append(("second document arbitrary field", later))
    for label, args in nested_attempts:
        response = call(client, "edit_compile", args)
        rejection_rows.append({"label": label, "response": response})
        check(f"F2 {label} rejected", not bool(response.get("ok")) and not compile_core(response).get("compile_id"),
              json.dumps(response)[:800])
        check(f"F2 {label} did not execute", product_markers() == [], str(product_markers()))

    directive_sources = {
        "load": f'#load "{str(script).replace(chr(92), chr(92) * 2)}"\nnamespace T024 {{ public class LoadProbe {{ }} }}',
        "reference": f'#r "{str(extension).replace(chr(92), chr(92) * 2)}"\nnamespace T024 {{ public class RefProbe {{ }} }}',
    }
    for label, source in directive_sources.items():
        response = call(client, "edit_compile", base(source))
        row = compile_core(response)
        diagnostics = row.get("diagnostics", []) if isinstance(row.get("diagnostics"), list) else []
        rejection_rows.append({"label": f"#{label}", "response": response})
        check(f"F3 #{label} rejected by regular compilation", bool(response.get("ok"))
              and row.get("success") is False and bool(diagnostics), json.dumps(response)[:800])
        check(f"F3 #{label} did not execute", product_markers() == [], str(product_markers()))

    csx_document = base(directive_sources["load"])
    csx_document["documents"][0]["path"] = "Probe.csx"
    response = call(client, "edit_compile", csx_document)
    row = compile_core(response)
    diagnostics = row.get("diagnostics", []) if isinstance(row.get("diagnostics"), list) else []
    rejection_rows.append({"label": ".csx document", "response": response})
    check("F3 .csx path stays regular compilation", bool(response.get("ok"))
          and row.get("success") is False and bool(diagnostics), json.dumps(response)[:800])
    check("F3 .csx path did not execute", product_markers() == [], str(product_markers()))

    framework = "Framework64" if ARCH == "x64" else "Framework"
    mscorlib = Path(os.environ["SystemRoot"]) / "Microsoft.NET" / framework / "v4.0.30319" / "mscorlib.dll"
    metadata = base()
    metadata["references_override"] = [FIXTURE, str(extension), str(mscorlib)]
    response = call(client, "edit_compile", metadata)
    row = compile_core(response)
    check("M1 analyzer assembly accepted only as metadata", bool(response.get("ok")) and bool(row.get("success"))
          and str(row.get("compile_id", "")).startswith("compile-"), json.dumps(response)[:900])
    check("M1 metadata read did not execute extensions", product_markers() == [], str(product_markers()))

    status = payload(call(client, "edit_status", {}))
    after_artifacts = artifact_files()
    after_pdb = pdb_files()
    after_temp = product_temp_files()
    check("S1 coordinator remains idle", status.get("state") == "idle", json.dumps(status)[:300])
    check("S2 compile artifact remains memory-only", after_artifacts == before_artifacts,
          json.dumps({"before": before_artifacts, "after": after_artifacts})[:1200])
    check("S3 no new PDB anywhere in isolation root", after_pdb == before_pdb,
          json.dumps({"before": before_pdb, "after": after_pdb})[:1200])
    check("S4 no dnspy-mcp temp artifact", after_temp == before_temp,
          json.dumps({"before": before_temp, "after": after_temp})[:1200])
    check("S5 product sentinel remains absent", product_markers() == [], str(product_markers()))
    print("INFO T024_BOUNDARY " + json.dumps({
        "arch": ARCH, "extension": {"path": str(extension), "length": extension.stat().st_size,
        "sha256": sha256(extension)}, "script": {"path": str(script), "length": script.stat().st_size,
        "sha256": sha256(script)}, "control_markers": sorted(str(p) for p in control_root.glob("*.marker")),
        "product_markers": product_markers(), "artifact_before": before_artifacts,
        "artifact_after": after_artifacts, "pdb_before": before_pdb, "pdb_after": after_pdb,
        "temp_before": before_temp, "temp_after": after_temp,
        "rejections": rejection_rows}, sort_keys=True), flush=True)
    print(f"ACC005X {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
