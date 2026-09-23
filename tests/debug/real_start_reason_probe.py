#!/usr/bin/env python3
"""Real dedicated-host startup/restart event-reason regression (one architecture).

Keep every wire response and every unfiltered event page for offline validation
against the frozen event_envelope schema. The fixture and host are supplied by
the caller; this probe neither provisions nor kills a process.
"""
import argparse
import hashlib
import json
import sys
import time
import traceback
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from dnspy_mcp import DnSpyClient


def main():
    parser = argparse.ArgumentParser()
    for key in ("url", "fixture", "evidence", "arch"):
        parser.add_argument("--" + key, required=True)
    parser.add_argument("--kinds", default="entry,process,module_cctor_or_entry,none")
    args = parser.parse_args()
    fixture = Path(args.fixture)
    output = Path(args.evidence)
    output.parent.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256(fixture.read_bytes()).hexdigest()
    record = {"arch": args.arch, "fixture": {"path": str(fixture), "sha256": digest},
              "calls": [], "cases": [], "verdict": "RED"}

    def save():
        output.write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n")

    client = None
    active = None

    def call(name, arguments=None):
        request = {"name": name, "arguments": arguments or {}}
        row = {"request": request}
        try:
            row["wire"] = client.request("tools/call", request)
            body = row["wire"].get("structuredContent")
            if body is None:
                body = json.loads(next(item["text"] for item in row["wire"].get("content", [])
                                       if item.get("type") == "text"))
            row["response"] = body
            return body
        finally:
            record["calls"].append(row)
            save()

    def ok(body, label):
        if body.get("ok") is not True:
            raise AssertionError(f"{label}: {str(body)[:800]}")
        return body["result"]

    def wait(state):
        for _ in range(80):
            body = call("debug_status")
            if ok(body, "status")["state"] == state:
                return body
            time.sleep(.1)
        raise AssertionError(f"did not reach {state}: {body}")

    def pages(session):
        cursor = 0
        collected = []
        for _ in range(100):
            page = ok(call("debug_read_events", {"session_id": session,
                                                 "after_cursor": cursor, "limit": 2}), "events")
            events = page.get("events", [])
            collected.extend(events)
            if not events:
                break
            next_cursor = events[-1]["cursor"]
            if next_cursor <= cursor:
                raise AssertionError("event cursor did not advance")
            cursor = next_cursor
            if len(events) < 2:
                break
        else:
            raise AssertionError("event pagination did not end")
        return collected

    try:
        client = DnSpyClient.connect(args.url, client_name="t053-start-" + args.arch, timeout=60)
        for kind in args.kinds.split(","):
            case = {"break_kind": kind, "generations": []}
            record["cases"].append(case)
            launch = ok(call("debug_launch", {"request_id": str(uuid.uuid4()),
                       "target_path": str(fixture), "expected_sha256": digest,
                       "launch_mode": "net48-exe", "architecture": args.arch,
                       "break_kind": kind}), "launch")
            session = launch["session_id"]
            generation = int(launch["generation"])
            active = (session, generation)
            expected = None if kind == "none" else kind
            for index in range(2):
                status = wait("running" if kind == "none" else "paused")
                stream = pages(session)
                matching = [event for event in stream if event.get("kind") == "paused"
                            and event.get("debug_context", {}).get("generation") == generation]
                if expected is not None and not any(event.get("payload", {}).get("reason") == expected
                                                    for event in matching):
                    raise AssertionError(f"{kind} generation {generation} pause reasons: {matching}")
                allowed = {"manual", "process", "module_cctor_or_entry", "entry", "breakpoint",
                           "exception", "step", "unknown"}
                if any(event.get("payload", {}).get("reason") not in allowed for event in matching):
                    raise AssertionError(f"noncontract pause reason: {matching}")
                case["generations"].append({"generation": generation, "state": status["result"]["state"],
                                            "pause_reasons": [e.get("payload", {}).get("reason") for e in matching],
                                            "event_count": len(stream)})
                if index == 0:
                    restarted = ok(call("debug_restart", {"session_id": session,
                              "generation": generation, "request_id": str(uuid.uuid4())}), "restart")
                    new_generation = int(restarted["generation"])
                    if new_generation <= generation:
                        raise AssertionError("restart generation did not advance")
                    stale = call("debug_continue", {"session_id": session, "generation": generation,
                                 "pause_epoch": int(status["debug_context"]["pause_epoch"]),
                                 "request_id": str(uuid.uuid4())})
                    if stale.get("ok") is not False or stale.get("error", {}).get("code") != "INVALID_STATE":
                        raise AssertionError("old generation control was not rejected")
                    case["stale_rejection"] = stale
                    generation = new_generation
                    active = (session, generation)
            if kind != "none":
                status = wait("paused")
                ok(call("debug_continue", {"session_id": session, "generation": generation,
                   "pause_epoch": int(status["debug_context"]["pause_epoch"]),
                   "request_id": str(uuid.uuid4())}), "continue")
                wait("running")
                stream = pages(session)
                if not any(event.get("kind") == "continued" and
                           event.get("payload", {}).get("reason") == "manual" for event in stream):
                    raise AssertionError("manual continued event missing")
            ok(call("debug_terminate", {"session_id": session, "generation": generation,
                                        "request_id": str(uuid.uuid4())}), "terminate")
            wait("idle")
            case["final_events"] = pages(session)
            active = None
        record["verdict"] = "GREEN"
        return 0
    except Exception as exc:
        record["error"] = {"type": type(exc).__name__, "message": str(exc),
                           "traceback": traceback.format_exc()}
        return 1
    finally:
        if client:
            if active:
                try:
                    call("debug_terminate", {"session_id": active[0], "generation": active[1],
                                             "request_id": str(uuid.uuid4())})
                    wait("idle")
                except Exception as exc:
                    record["cleanup_error"] = str(exc)
            client.close()
        save()
        print(json.dumps({"verdict": record["verdict"], "error": record.get("error")}, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
