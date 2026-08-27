#!/usr/bin/env python3
"""Validate the frozen dnspy.debug.v1 contract artifacts (CI + local).

Checks (all machine-verifiable):
  1. dnspy.debug.v1.schema.json is a valid Draft 2020-12 schema.
  2. Every UTF-8 byte-limit pointer resolves inside the schema.
  3. Definition family counts: 25 API args, 22 results, 21 event payloads.
  4. Fixture regeneration is deterministic (byte-identical output).
  5. Fixture invariants: structural expectations per kind, exact -32602 scope,
     2025-06-18 text/structuredContent deep equality, no-session zero counters,
     INVALID_STATE current_state outside required_states, event envelopes valid.
Exit code 0 = all checks passed.
"""
import json, os, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
os.environ.setdefault("PYTHONHASHSEED", "0")

def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)

def main():
    try:
        import jsonschema
    except ImportError:
        fail("jsonschema is required (pip install jsonschema)")
    schema = json.load(open(os.path.join(HERE, "dnspy.debug.v1.schema.json"), encoding="utf-8"))
    limits = json.load(open(os.path.join(HERE, "dnspy.debug.utf8-limits.json"), encoding="utf-8"))

    jsonschema.validators.validator_for(schema).check_schema(schema)
    defs = schema["$defs"]

    def resolve(pointer):
        cur = schema
        for part in pointer.lstrip("/").split("/"):
            cur = cur[part.replace("~1", "/").replace("~0", "~")]
        return cur

    for item in limits["limits"]:
        resolve(item["pointer"])
    print("byte-limit pointers resolve:", len(limits["limits"]))

    args = [d for d in defs if d.endswith("_args")]
    results = [d for d in defs if d.endswith("_result")]
    events = [d for d in defs if d.startswith("event_") and d not in ("event_envelope", "event_kind")]
    if (len(args), len(results), len(events)) != (25, 22, 21):
        fail(f"def family counts {len(args)}/{len(results)}/{len(events)} != 25/22/21")
    print("def families: 25 args / 22 results / 21 events")

    # Deterministic regeneration.
    with tempfile.TemporaryDirectory() as td:
        subprocess.run([sys.executable, os.path.join(HERE, "generate_fixtures.py")], check=True, cwd=HERE)
        first = {}
        for name in sorted(os.listdir(os.path.join(HERE, "fixtures"))):
            first[name] = open(os.path.join(HERE, "fixtures", name), "rb").read()
        subprocess.run([sys.executable, os.path.join(HERE, "generate_fixtures.py")], check=True, cwd=HERE)
        for name, blob in first.items():
            if open(os.path.join(HERE, "fixtures", name), "rb").read() != blob:
                fail(f"fixture regeneration not deterministic: {name}")
    print("fixture regeneration deterministic")

    def wrap(name):
        return {"$defs": defs, "$ref": f"#/$defs/{name}"}

    total = 0
    for fname in sorted(os.listdir(os.path.join(HERE, "fixtures"))):
        if not fname.endswith(".json"):
            continue
        doc = json.load(open(os.path.join(HERE, "fixtures", fname), encoding="utf-8"))
        if "cases" not in doc:
            continue
        pv = doc["protocol_version"]
        for case in doc["cases"]:
            total += 1
            kind = case["kind"]
            name = case["request"]["params"]["name"]
            arguments = case["request"]["params"]["arguments"]
            args_def = f"{name}_args"
            if args_def in defs:
                try:
                    jsonschema.validate(arguments, wrap(args_def))
                    structurally_valid = True
                except jsonschema.ValidationError:
                    structurally_valid = False
                if kind == "invalid-fields" and structurally_valid:
                    fail(f"{case['id']}: invalid-fields must be schema-invalid")
                if kind in ("valid", "invalid-state", "disabled-gate", "fixed-capability-unavailable") and not structurally_valid:
                    fail(f"{case['id']}: {kind} must be schema-valid")
            resp = case["expected_response"]
            if isinstance(resp.get("error"), dict) and resp["error"].get("code") == -32602:
                if kind not in ("invalid-fields", "byte-input"):
                    fail(f"{case['id']}: -32602 outside invalid-fields/byte-input")
                continue
            if "result" not in resp:
                continue
            text = resp["result"]["content"][0]["text"]
            parsed = json.loads(text)
            if pv == "2025-06-18":
                env = resp["result"].get("structuredContent")
                if env is None or env != parsed:
                    fail(f"{case['id']}: text/structuredContent deep equality")
            else:
                if "structuredContent" in resp["result"]:
                    fail(f"{case['id']}: old protocolVersion must omit structuredContent")
            jsonschema.validate(parsed, wrap("envelope_success" if parsed.get("ok") else "envelope_failure"))
            ctx = parsed.get("debug_context", {})
            if "session_id" not in ctx:
                if not (ctx.get("generation") == 0 and ctx.get("pause_epoch") == 0 and ctx.get("event_cursor") == 0):
                    fail(f"{case['id']}: no-session counters must be zero")
            if kind == "invalid-state" and "required_states" in case:
                if case.get("current_state") in case["required_states"]:
                    fail(f"{case['id']}: current_state inside required_states")
                order = ["idle", "starting", "running", "paused", "restarting", "stopping", "faulted"]
                idx = [order.index(s) for s in case["required_states"]]
                if idx != sorted(idx):
                    fail(f"{case['id']}: required_states not in fixed order")
            if "expected_event" in case:
                jsonschema.validate(case["expected_event"], wrap("event_envelope"))
    print(f"fixture invariants passed: {total} cases")
    print("ALL CONTRACT CHECKS PASSED")

if __name__ == "__main__":
    main()
