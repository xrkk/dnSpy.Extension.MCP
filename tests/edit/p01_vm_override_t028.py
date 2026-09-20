#!/usr/bin/env python3
"""Small sequential probe for T028's local-UI process override lifecycle."""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from p01_vm_t028 import call, env, inject, launch, require, terminate
from dnspy_mcp import DnSpyClient


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", required=True)
    ap.add_argument("--phase", required=True, choices=("seed", "enabled", "disabled", "pre_restart", "after_restart"))
    ap.add_argument("--arch", required=True)
    ap.add_argument("--fixture", type=Path, required=True)
    args = ap.parse_args()
    client = DnSpyClient.connect(args.url, client_name="t028-override-" + args.phase, timeout=120)
    try:
        if args.phase in ("seed", "after_restart"):
            inject(client, "physical")
        row = require(call(client, "debug_capabilities"), "capabilities")["execution_environment"]
        checks = {
            "classification_physical": row["classification"] == "physical",
            "override_expected": bool(row["local_process_override_active"]) == (args.phase in ("enabled", "pre_restart")),
            "allowed_expected": bool(row["execution_allowed"]) == (args.phase in ("enabled", "pre_restart")),
        }
        if args.phase == "enabled":
            started = require(launch(client, args.fixture, args.arch), "override physical launch")
            require(terminate(client, str(started["session_id"]), int(started["generation"])), "override terminate")
            spoof = env(client, "snapshot", local_process_override_active=False)["execution_environment"]
            checks["mcp_cannot_disable"] = bool(spoof["local_process_override_active"] and spoof["execution_allowed"])
        else:
            started = None
            spoof = None
        result = {"phase": args.phase, "status": "PASS" if all(checks.values()) else "FAIL", "checks": checks,
                  "execution_environment": row, "launch": started, "mcp_spoof": spoof}
        print(json.dumps(result, ensure_ascii=False))
        return 0 if result["status"] == "PASS" else 1
    finally:
        try:
            env(client, "clear_signals") if args.phase == "after_restart" else None
        finally:
            client.close()


if __name__ == "__main__":
    raise SystemExit(main())
