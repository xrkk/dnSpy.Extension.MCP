#!/usr/bin/env python3
"""P08 ACC-008: resource payload paths.  A small resource inlines through the
normal request body; an oversize inline attempt is rejected by the existing
transport rule (limits unchanged); a 2 MiB payload generated ON THE VM imports
and exports through VM file paths with full identity evidence and reloads
consistently."""

from __future__ import annotations

import hashlib
import json
import sys
import urllib.request
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from dnspy_mcp import DnSpyClient  # noqa: E402

URL = "http://127.0.0.1:15378/mcp"
FIXTURE = r"C:\Tools\mcp-repo\tests\fixtures\bin\ImportHost\ImportHost.exe"
BIG_PATH = r"C:\Tools\dnspy-mcp-edit-tests\p08-big-payload.bin"
FAILURES: list[str] = []
PASSES: list[str] = []


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


def main() -> int:
    client = DnSpyClient(URL, client_name="p08-acc008", timeout=180)
    client.initialize()
    call(client, "open_files", {"paths": [FIXTURE]})

    # generate the 2 MiB payload ON THE VM (this driver runs VM-local)
    import random as _random
    rng = _random.Random(42)
    big_bytes = bytes(rng.randrange(256) for _ in range(512)) * (2 * 1024 * 1024 // 512)
    Path(BIG_PATH).write_bytes(big_bytes)
    big_sha = hashlib.sha256(big_bytes).hexdigest()
    check("G1 big payload generated", len(big_sha) == 64, big_sha)

    begin = call(client, "edit_begin", {"assembly_name": "ImportHost", "request_id": rid()})
    tx_row = payload(begin).get("transaction", {})
    tx = str(tx_row.get("transaction_id", ""))
    revision = int(tx_row.get("work_revision", 0))
    check("T1 transaction began", bool(tx), json.dumps(begin)[:200])

    # S1 small inline resource (< 1 MiB request budget) succeeds
    small = bytes(64 * 1024)
    applied = call(client, "edit_apply", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "operation": {"kind": "managed_resource_add", "name": "P08.Small.resources",
                      "data_base64": small.hex() and __import__("base64").b64encode(small).decode()}})
    check("S1 small inline ok", bool(applied.get("ok")), json.dumps(applied)[:240])
    revision = int(payload(applied).get("transaction", {}).get("work_revision", revision))

    # S2 oversize inline: a 1.5 MiB base64 body exceeds the 1 MiB transport rule
    oversize = __import__("base64").b64encode(bytes(1100 * 1024)).decode()
    try:
        rejected = call(client, "edit_apply", {
            "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
            "operation": {"kind": "managed_resource_add", "name": "P08.Oversize.resources",
                          "data_base64": oversize}})
        ok_or_transport = not rejected.get("ok") or "DRIVER_TRANSPORT" in json.dumps(rejected)[:200]
        check("S2 oversize inline rejected", ok_or_transport, json.dumps(rejected)[:240])
    except Exception:  # noqa: BLE001  (transport-level rejection raises here)
        check("S2 oversize inline rejected", True, "transport exception")

    # S3 VM path import of the 2 MiB payload
    imported = call(client, "edit_resource_import", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "vm_path": BIG_PATH, "resource_name": "P08.Big.resources", "resource_type": "embedded"})
    import_row = payload(imported).get("import", {})
    check("S3 vm path import identity",
          bool(imported.get("ok")) and int(import_row.get("length", 0)) == 2 * 1024 * 1024
          and str(import_row.get("sha256", "")) == big_sha,
          json.dumps(imported)[:1000])
    revision = int(payload(imported).get("transaction", {}).get("work_revision", revision))

    reviewed = call(client, "edit_review", {"request_id": rid(), "transaction_id": tx, "expected_revision": revision})
    review_row = payload(reviewed).get("review", {}) if isinstance(payload(reviewed), dict) else {}
    review_id = str(review_row.get("review_id", ""))
    required = [str(r) for r in (review_row.get("required_confirmation_ids") or [])]
    committed = call(client, "edit_commit", {
        "request_id": rid(), "transaction_id": tx, "expected_revision": revision,
        "review_id": review_id, "review_revision": revision, "confirmed_risk_ids": required})
    commit_row = payload(committed)
    lineage_id = str(commit_row.get("history", {}).get("lineage_id", ""))
    checkpoint_id = str(commit_row.get("checkpoint", {}).get("checkpoint_id", ""))
    check("W1 commit ok", bool(committed.get("ok")) and bool(lineage_id), json.dumps(committed)[:300])

    # S4 export through the VM path with identity evidence
    exported = call(client, "edit_resource_export", {
        "request_id": rid(), "assembly_name": "ImportHost",
        "resource_name": "P08.Big.resources",
        "output_path": "edit-output\\acc008\\BigPayload.bin"})
    export_row = payload(exported).get("export", {})
    check("S4 vm path export identity",
          bool(exported.get("ok")) and int(export_row.get("length", 0)) == 2 * 1024 * 1024
          and str(export_row.get("sha256", "")) == big_sha,
          json.dumps(exported)[:300])
    export_path = str(export_row.get("path", ""))

    # S5 reload consistency: the exported file exists with the recorded SHA
    on_disk = Path(export_path).read_bytes() if Path(export_path).exists() else b""
    check("S5 exported file SHA matches",
          len(on_disk) == 2 * 1024 * 1024 and hashlib.sha256(on_disk).hexdigest() == big_sha,
          f"len={len(on_disk)}")

    print(f"ACC008 {'PASS' if not FAILURES else 'FAIL'} passes={len(PASSES)} failures={FAILURES}", flush=True)
    return 0 if not FAILURES else 1


if __name__ == "__main__":
    raise SystemExit(main())
