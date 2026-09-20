#!/usr/bin/env python3
"""Live RACC-027 matrix: two generations x two pause epochs x two modules.

This driver intentionally uses the repository Python MCP client.  It records every
tool request and structured response, validates artifact bytes and manifests on the
VM, and leaves byte transfer/remote process ownership to the outer orchestrator.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import time
import traceback
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from dnspy_mcp import DnSpyClient  # noqa: E402


def sha256(path: Path) -> str:
	digest = hashlib.sha256()
	with path.open("rb") as stream:
		for chunk in iter(lambda: stream.read(1024 * 1024), b""):
			digest.update(chunk)
	return digest.hexdigest()


def utc_now() -> str:
	return datetime.now(timezone.utc).isoformat()


def main() -> int:
	parser = argparse.ArgumentParser()
	parser.add_argument("--url", required=True)
	parser.add_argument("--arch", choices=("x64", "x86"), required=True)
	parser.add_argument("--fixture", required=True)
	parser.add_argument("--evidence", required=True)
	args = parser.parse_args()

	fixture = Path(args.fixture)
	evidence_path = Path(args.evidence)
	evidence_path.parent.mkdir(parents=True, exist_ok=True)
	record: dict[str, Any] = {
		"schema_version": "dnspy.t031.racc027.v1",
		"started_utc": utc_now(),
		"arch": args.arch,
		"url": args.url,
		"fixture": {"path": str(fixture), "size": fixture.stat().st_size, "sha256": sha256(fixture)},
		"calls": [],
		"dumps": [],
		"generations": [],
		"cleanup": {},
		"status": "FAILED",
	}
	client: DnSpyClient | None = None
	session_id: str | None = None
	generation: int | None = None
	old_target_handle: str | None = None

	def save() -> None:
		record["completed_utc"] = utc_now()
		evidence_path.write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

	def call(name: str, arguments: dict[str, Any] | None = None) -> dict[str, Any]:
		assert client is not None
		request = {"name": name, "arguments": dict(arguments or {})}
		entry: dict[str, Any] = {"sequence": len(record["calls"]) + 1, "utc": utc_now(), "request": request}
		try:
			wire_result = client.request("tools/call", request)
			entry["mcp_result"] = wire_result
			payload = wire_result.get("structuredContent") if isinstance(wire_result, dict) else None
			if payload is None and isinstance(wire_result, dict):
				for item in wire_result.get("content", []):
					if isinstance(item, dict) and item.get("type") == "text":
						payload = json.loads(item["text"])
						break
			if not isinstance(payload, dict):
				raise AssertionError(f"{name}: structured payload missing")
			entry["response"] = payload
			return payload
		except Exception as ex:  # noqa: BLE001 - preserve the original failing call
			entry["exception"] = {"type": type(ex).__name__, "message": str(ex), "traceback": traceback.format_exc()}
			raise
		finally:
			record["calls"].append(entry)
			save()

	def require_ok(payload: dict[str, Any], label: str) -> dict[str, Any]:
		if payload.get("ok") is not True or not isinstance(payload.get("result"), dict):
			raise AssertionError(f"{label}: expected ok=true, got {payload}")
		return payload["result"]

	def context(payload: dict[str, Any]) -> dict[str, Any]:
		value = payload.get("debug_context")
		if not isinstance(value, dict):
			raise AssertionError(f"debug_context missing: {payload}")
		return value

	def status() -> tuple[dict[str, Any], dict[str, Any]]:
		payload = call("debug_status", {"session_id": session_id} if session_id else {})
		return require_ok(payload, "debug_status"), context(payload)

	def wait_state(expected: str, timeout: float = 30.0) -> tuple[dict[str, Any], dict[str, Any]]:
		deadline = time.monotonic() + timeout
		last: tuple[dict[str, Any], dict[str, Any]] | None = None
		while time.monotonic() < deadline:
			last = status()
			if last[0].get("state") == expected:
				return last
			time.sleep(0.15)
		raise AssertionError(f"timed out waiting for {expected}; last={last}")

	def pause_fresh(previous_epoch: int) -> tuple[dict[str, Any], dict[str, Any]]:
		assert session_id is not None and generation is not None
		wait_state("running")
		paused = call("debug_pause", {
			"session_id": session_id, "generation": generation, "request_id": str(uuid.uuid4()),
		})
		require_ok(paused, "debug_pause")
		state, ctx = wait_state("paused")
		if int(ctx["generation"]) != generation or int(ctx["pause_epoch"]) <= previous_epoch:
			raise AssertionError(f"pause did not mint a fresh epoch: previous={previous_epoch}, context={ctx}")
		return state, ctx

	def listed_modules(epoch: int) -> tuple[dict[str, Any] | None, dict[str, Any] | None, list[dict[str, Any]]]:
		assert session_id is not None and generation is not None
		payload = call("debug_list_modules", {
			"session_id": session_id, "generation": generation, "pause_epoch": epoch,
		})
		items = require_ok(payload, "debug_list_modules").get("items")
		if not isinstance(items, list):
			raise AssertionError("debug_list_modules omitted items")
		target_name = fixture.name.casefold()
		target = next((item for item in items if isinstance(item, dict) and
			(target_name in {str(item.get("name", "")).casefold(), Path(str(item.get("path", ""))).name.casefold()})), None)
		core = next((item for item in items if isinstance(item, dict) and
			"mscorlib.dll" in {str(item.get("name", "")).casefold(), Path(str(item.get("path", ""))).name.casefold()}), None)
		return target, core, items

	def dump(role: str, module: dict[str, Any], epoch_index: int, epoch: int) -> None:
		assert session_id is not None and generation is not None
		label = f"t031-{args.arch}-g{generation}-e{epoch_index}-{role}"
		payload = call("debug_dump_module", {
			"session_id": session_id, "generation": generation, "pause_epoch": epoch,
			"request_id": str(uuid.uuid4()), "module_handle": module["module_handle"],
			"relative_name": label,
		})
		artifact = require_ok(payload, label).get("artifact")
		if not isinstance(artifact, dict):
			raise AssertionError(f"{label}: artifact missing")
		artifact_path = Path(str(artifact["path"]))
		manifest_path = Path(str(artifact["manifest_path"]))
		if not artifact_path.is_file() or not manifest_path.is_file():
			raise AssertionError(f"{label}: artifact or manifest missing")
		manifest_bytes = manifest_path.read_bytes()
		manifest = json.loads(manifest_bytes.decode("utf-8"))
		actual_sha = sha256(artifact_path)
		response_sha = str(artifact.get("sha256", "")).lower()
		manifest_sha = str(manifest.get("sha256", "")).lower()
		if not (actual_sha == response_sha == manifest_sha):
			raise AssertionError(f"{label}: SHA mismatch file={actual_sha} response={response_sha} manifest={manifest_sha}")
		if artifact_path.stat().st_size != int(artifact["size"]) or int(manifest.get("size", -1)) != int(artifact["size"]):
			raise AssertionError(f"{label}: size mismatch")
		source_module = artifact.get("source_module")
		if not isinstance(source_module, dict) or source_module.get("module_handle") != module.get("module_handle"):
			raise AssertionError(f"{label}: response source module is not the requested module")
		if manifest.get("source_module") != source_module:
			raise AssertionError(f"{label}: manifest/response source_module mismatch")
		if role == "target" and actual_sha != record["fixture"]["sha256"]:
			raise AssertionError(f"{label}: target dump differs from original fixture")
		record["dumps"].append({
			"label": label, "role": role, "session_id": session_id, "generation": generation,
			"epoch_index": epoch_index, "pause_epoch": epoch, "request_module": module,
			"artifact": artifact, "artifact_actual_sha256": actual_sha,
			"artifact_actual_size": artifact_path.stat().st_size,
			"manifest_sha256": hashlib.sha256(manifest_bytes).hexdigest(), "manifest": manifest,
		})
		save()

	try:
		client = DnSpyClient.connect(args.url, client_name=f"t031-r1-{args.arch}", timeout=60)
		tool_names = sorted(str(tool.get("name")) for tool in client.iter_tools())
		record["advertised_tools"] = tool_names
		if "debug_dump_module" not in tool_names or any(name.startswith("debug_test_") for name in tool_names):
			raise AssertionError("the instance is not a production debug surface")
		cap_payload = call("debug_capabilities")
		capabilities = require_ok(cap_payload, "debug_capabilities")
		environment = capabilities.get("execution_environment", {})
		if environment.get("classification") != "vmware" or environment.get("execution_allowed") is not True:
			raise AssertionError(f"real VM gate not allowed: {environment}")
		if capabilities.get("host_architecture") != args.arch:
			raise AssertionError(f"host architecture mismatch: {capabilities.get('host_architecture')} != {args.arch}")

		launch_payload = call("debug_launch", {
			"request_id": str(uuid.uuid4()), "target_path": str(fixture),
			"expected_sha256": record["fixture"]["sha256"], "launch_mode": "net48-exe",
			"architecture": args.arch, "break_kind": "none",
		})
		launch = require_ok(launch_payload, "debug_launch")
		session_id = str(launch["session_id"])
		generation = int(launch["generation"])
		if generation != 1:
			raise AssertionError(f"new session did not start at generation 1: {generation}")
		wait_state("running")
		# Do not pause the CLR bootstrap before its module table exists.  This is a
		# bounded observation delay, not a simulated seam; the target stays alive
		# for sixty seconds and every accepted epoch below is minted by debug_pause.
		time.sleep(2.0)

		for generation_index in (1, 2):
			if generation != generation_index:
				raise AssertionError(f"unexpected generation: {generation} != {generation_index}")
			generation_row: dict[str, Any] = {"generation": generation, "epochs": []}
			record["generations"].append(generation_row)
			previous_epoch = 0
			for epoch_index in (1, 2):
				deadline = time.monotonic() + 15.0
				observed: list[str] = []
				while True:
					_, ctx = pause_fresh(previous_epoch)
					epoch = int(ctx["pause_epoch"])
					target, core, items = listed_modules(epoch)
					observed = [str(item.get("name")) for item in items if isinstance(item, dict)]
					if target is not None and core is not None:
						break
					if time.monotonic() >= deadline:
						raise AssertionError(f"required modules did not become visible: target={fixture.name}; observed={observed}")
					continued_for_load = call("debug_continue", {
						"session_id": session_id, "generation": generation, "pause_epoch": epoch,
						"request_id": str(uuid.uuid4()),
					})
					require_ok(continued_for_load, "continue while waiting for module load")
					wait_state("running")
					previous_epoch = epoch
					time.sleep(0.25)
				generation_row["epochs"].append({
					"epoch_index": epoch_index, "pause_epoch": epoch,
					"target_module_handle": target["module_handle"],
					"mscorlib_module_handle": core["module_handle"], "module_count": len(items),
				})
				if generation_index == 1 and epoch_index == 1:
					old_target_handle = str(target["module_handle"])
				dump("target", target, epoch_index, epoch)
				dump("mscorlib", core, epoch_index, epoch)
				previous_epoch = epoch
				if epoch_index == 1:
					continued = call("debug_continue", {
						"session_id": session_id, "generation": generation, "pause_epoch": epoch,
						"request_id": str(uuid.uuid4()),
					})
					require_ok(continued, "debug_continue")
					wait_state("running")
			if generation_index == 1:
				restarted_payload = call("debug_restart", {
					"session_id": session_id, "generation": generation, "request_id": str(uuid.uuid4()),
				})
				restarted = require_ok(restarted_payload, "debug_restart")
				restarted_context = context(restarted_payload)
				new_generation = int(restarted["generation"])
				if str(restarted_context.get("session_id")) != session_id or new_generation <= generation:
					raise AssertionError(f"restart identity did not advance correctly: {restarted}")
				generation = new_generation
				wait_state("running")
				time.sleep(2.0)

		assert old_target_handle is not None and generation == 2
		last_epoch = int(record["generations"][-1]["epochs"][-1]["pause_epoch"])
		stale = call("debug_dump_module", {
			"session_id": session_id, "generation": generation, "pause_epoch": last_epoch,
			"request_id": str(uuid.uuid4()), "module_handle": old_target_handle,
			"relative_name": f"t031-{args.arch}-stale-g1-handle",
		})
		stale_error = stale.get("error", {}).get("code") if isinstance(stale.get("error"), dict) else None
		if stale.get("ok") is not False or stale_error not in {"STALE_HANDLE", "NOT_FOUND", "TARGET_MISMATCH"}:
			raise AssertionError(f"old generation handle was not rejected: {stale}")
		record["stale_handle_negative"] = {"old_handle": old_target_handle, "current_generation": generation,
			"current_pause_epoch": last_epoch, "error_code": stale_error}

		terminated = call("debug_terminate", {
			"session_id": session_id, "generation": generation, "request_id": str(uuid.uuid4()),
		})
		require_ok(terminated, "debug_terminate")
		final_state, final_context = wait_state("idle")
		record["cleanup"]["final_status"] = {"result": final_state, "debug_context": final_context}
		if len(record["dumps"]) != 8:
			raise AssertionError(f"expected eight successful dumps, got {len(record['dumps'])}")
		if any(entry.get("response", {}).get("error", {}).get("code") == "TARGET_MISMATCH"
				for entry in record["calls"] if isinstance(entry.get("response"), dict)
				and entry["request"]["name"] == "debug_dump_module"
				and entry["request"]["arguments"].get("module_handle") != old_target_handle):
			raise AssertionError("a current module dump returned TARGET_MISMATCH")
		record["status"] = "PASS"
		return 0
	except Exception as ex:  # noqa: BLE001 - the evidence must preserve the first failure
		record["failure"] = {"type": type(ex).__name__, "message": str(ex), "traceback": traceback.format_exc()}
		return 1
	finally:
		if client is not None:
			if record["status"] != "PASS" and session_id is not None and generation is not None:
				try:
					cleanup_payload = call("debug_terminate", {
						"session_id": session_id, "generation": generation, "request_id": str(uuid.uuid4()),
					})
					record["cleanup"]["failure_terminate"] = cleanup_payload
				except Exception as cleanup_ex:  # noqa: BLE001
					record["cleanup"]["failure_terminate_exception"] = str(cleanup_ex)
			client.close()
		record["cleanup"]["client_closed"] = True
		save()


if __name__ == "__main__":
	raise SystemExit(main())
