#!/usr/bin/env python3
"""Validate every distinct event observed in unfiltered real-probe pages."""
import argparse
import json
from pathlib import Path
import jsonschema

SCHEMA = json.loads((Path(__file__).resolve().parent / "contracts/dnspy.debug.v1.schema.json").read_text())
VALIDATOR = jsonschema.Draft202012Validator({"$defs": SCHEMA["$defs"],
                                             "$ref": "#/$defs/event_envelope"})

def validate(path):
    doc = json.loads(Path(path).read_text())
    pages = []
    for entry in doc["calls"]:
        if entry.get("request", {}).get("name") != "debug_read_events":
            continue
        response = entry.get("response", {})
        if response.get("ok") is True:
            assert "kinds" not in entry["request"]["arguments"], "filtered event page"
            pages.append(response["result"].get("events", []))
    events = {(event["debug_context"]["session_id"], event["cursor"]): event
              for page in pages for event in page}
    errors = []
    for key, event in sorted(events.items()):
        for error in VALIDATOR.iter_errors(event):
            errors.append({"session_id": key[0], "cursor": key[1], "kind": event["kind"],
                           "path": "/" + "/".join(map(str, error.absolute_path)),
                           "schema_path": "/" + "/".join(map(str, error.absolute_schema_path)),
                           "value": error.instance, "message": error.message})
    return {"file": str(path), "pages": len(pages), "events": len(events), "errors": errors}

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("paths", nargs="+")
    args = parser.parse_args()
    results = [validate(path) for path in args.paths]
    print(json.dumps(results, ensure_ascii=False, indent=2))
    return int(any(result["errors"] for result in results))

if __name__ == "__main__":
    raise SystemExit(main())
