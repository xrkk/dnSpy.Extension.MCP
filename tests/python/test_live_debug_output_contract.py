"""Opt-in strict input/output schema checks against a deployed debugger session.

This test exists because frozen example fixtures can conform while the live DTO serializer
emits a different JSON type.  It validates every request against the published inputSchema
and actual structuredContent against outputSchema across all 22 debug tools.
"""

from __future__ import annotations

import json
import os
import time
import unittest
import uuid
from typing import Any

from jsonschema import Draft202012Validator

from dnspy_mcp import DnSpyClient


LIVE_URL = os.getenv("DNSPY_MCP_LIVE_URL")
DEBUG_TARGET = os.getenv("DNSPY_MCP_DEBUG_TARGET")
DEBUG_TARGET_SHA256 = os.getenv("DNSPY_MCP_DEBUG_TARGET_SHA256")
DEBUG_ARCH = os.getenv("DNSPY_MCP_DEBUG_ARCH", "x64")
EXPAND_TARGET = os.getenv("DNSPY_MCP_EXPAND_TARGET")
EXPAND_TARGET_SHA256 = os.getenv("DNSPY_MCP_EXPAND_TARGET_SHA256")
EXPAND_MANIFEST = os.getenv("DNSPY_MCP_EXPAND_MANIFEST")


METHOD_TOKEN_CHARS = set("0123456789abcdefABCDEF")


def load_expand_locator(manifest_path: str, expected_sha256: str) -> dict:
    """Load the T038 expand-fixture manifest and validate it against the deployed fixture.

    The manifest is produced by tests/debug/fixtures-src/build-expand-manifest from the
    exact built bytes (SHA-256-bound) and carries the semantic breakpoint (KeepLive's
    entry, where its fully-built ``expandPayload`` parameter is live) instead of a
    hard-coded method token / IL offset. Everything a bad manifest could get wrong is
    rejected here before anything reaches the debug server:

    - schema must be exactly dnspy.expand-fixture-manifest.v2;
    - sha256 must be 64 hex chars and match the deployed fixture (case-insensitive);
    - mvid must be a parseable UUID (the live test also binds it to the loaded module);
    - method_token must be ``0x`` + exactly 8 hex digits, a MethodDef table token
      (table byte 0x06) with a non-zero RID - a TypeDef token like 0x02000001 or a
      zero RID like 0x06000000 is rejected;
    - breakpoint il_offset must be a true int (bool is explicitly rejected - ``True``
      would compare equal to boundary 1 in Python), non-negative, and one of the TARGET
      method's own instruction boundaries (``method_il_boundaries``); Main's boundaries
      live in a separate reference-only field and never validate an anchor.
    """
    with open(manifest_path, "r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    if manifest.get("schema_version") != "dnspy.expand-fixture-manifest.v2":
        raise ValueError("unsupported manifest schema_version")
    sha = str(manifest.get("sha256", ""))
    if len(sha) != 64 or not set(sha) <= set("0123456789abcdefABCDEF"):
        raise ValueError(f"malformed sha256 in manifest: {sha!r}")
    if sha.casefold() != str(expected_sha256).casefold():
        raise ValueError(
            f"manifest sha256 {sha} does not match the deployed fixture {expected_sha256}"
        )
    try:
        uuid.UUID(str(manifest.get("mvid", "")))
    except (AttributeError, ValueError) as ex:
        raise ValueError(f"malformed mvid in manifest: {manifest.get('mvid')!r}") from ex
    token = str(manifest.get("method_token", ""))
    if (
        len(token) != 10
        or not token.startswith("0x")
        or not set(token[2:]) <= METHOD_TOKEN_CHARS
    ):
        raise ValueError(f"malformed method_token in manifest: {token!r}")
    if token[2:4].casefold() != "06":
        raise ValueError(f"method_token is not a MethodDef token: {token!r}")
    if int(token[2:], 16) & 0x00FFFFFF == 0:
        raise ValueError(f"method_token has a zero RID: {token!r}")
    offset = manifest.get("breakpoint", {}).get("il_offset")
    # bool is an int subclass: True would equal boundary 1, so the type check is exact.
    if type(offset) is not int or offset < 0:
        raise ValueError(f"breakpoint il_offset {offset!r} must be a non-negative int (not bool)")
    boundaries = manifest.get("method_il_boundaries") or []
    if offset not in boundaries:
        raise ValueError(
            f"breakpoint il_offset {offset!r} is not an instruction boundary of the anchor method"
        )
    return manifest


def verify_manifest_matches_module(manifest: dict, module: dict) -> None:
    """Bind the validated manifest to the live module the debugger actually loaded.

    The fixture SHA-256 has already been matched against the module by the caller's
    module selection; this closes the loop on the remaining identity: the manifest's
    recorded MVID must equal the live module's MVID, so a manifest built from a
    different build of an otherwise identical file cannot steer the breakpoint.
    """
    mvid = str(manifest.get("mvid", "")).casefold()
    live = str(module.get("mvid", "")).casefold()
    if not live or mvid != live:
        raise ValueError(f"manifest mvid {mvid!r} does not match the live module mvid {live!r}")


@unittest.skipUnless(
    LIVE_URL and DEBUG_TARGET and DEBUG_TARGET_SHA256,
    "set DNSPY_MCP_LIVE_URL, DNSPY_MCP_DEBUG_TARGET, and "
    "DNSPY_MCP_DEBUG_TARGET_SHA256 to run live debug output checks",
)
class LiveDebugOutputContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.maxDiff = None
        self.client = DnSpyClient(str(LIVE_URL), client_name="dnspy-debug-output-contract-test")
        self.client.initialize()
        self.schemas = {
            str(tool["name"]): tool.get("outputSchema") for tool in self.client.iter_tools()
        }
        self.input_schemas = {
            str(tool["name"]): tool.get("inputSchema") for tool in self.client.iter_tools()
        }
        self.failures: list[str] = []
        self.session_id: str | None = None
        self.generation: int | None = None
        self.pause_epoch: int | None = None
        self.breakpoint_id: str | None = None

    def tearDown(self) -> None:
        if self.breakpoint_id and self.session_id and self.generation is not None:
            try:
                status = self.client.call_tool_json("debug_status")
                if status["debug_context"]["state"] != "paused":
                    status = self.client.call_tool_json(
                        "debug_pause",
                        {
                            "request_id": str(uuid.uuid4()),
                            "session_id": self.session_id,
                            "generation": self.generation,
                        },
                    )
                self.client.call_tool(
                    "debug_remove_breakpoint",
                    {
                        "request_id": str(uuid.uuid4()),
                        "session_id": self.session_id,
                        "generation": self.generation,
                        "pause_epoch": status["debug_context"]["pause_epoch"],
                        "breakpoint_id": self.breakpoint_id,
                    },
                )
            except Exception:
                pass
        if self.session_id and self.generation is not None:
            try:
                self.client.call_tool(
                    "debug_terminate",
                    {
                        "request_id": str(uuid.uuid4()),
                        "session_id": self.session_id,
                        "generation": self.generation,
                    },
                )
            except Exception:
                pass
        self.client.close()

    def call(
        self,
        name: str,
        arguments: dict[str, Any] | None = None,
        *,
        validate_input: bool = True,
    ) -> dict[str, Any]:
        if validate_input:
            input_schema = self.input_schemas.get(name)
            self.assertIsInstance(input_schema, dict, f"{name}.inputSchema")
            Draft202012Validator(input_schema).validate(arguments or {})
        result = self.client.call_tool(name, arguments)
        structured = result.get("structuredContent")
        self.assertIsInstance(structured, dict, name)
        schema = self.schemas.get(name)
        self.assertIsInstance(schema, dict, f"{name}.outputSchema")
        for error in sorted(
            Draft202012Validator(schema).iter_errors(structured),
            key=lambda item: list(item.absolute_path),
        ):
            path = ".".join(str(part) for part in error.absolute_path) or "$"
            self.failures.append(f"{name}: {path}: {error.message}")
        return structured

    def pause(self) -> dict[str, Any]:
        status = self.call("debug_status")
        if status["debug_context"]["state"] != "paused":
            status = self.call(
                "debug_pause",
                {
                    "request_id": str(uuid.uuid4()),
                    "session_id": self.session_id,
                    "generation": self.generation,
                },
            )
        self.pause_epoch = int(status["debug_context"]["pause_epoch"])
        return status

    def test_live_debug_outputs_conform_to_published_schemas(self) -> None:
        self.call("debug_capabilities")
        launch = self.call(
            "debug_launch",
            {
                "request_id": str(uuid.uuid4()),
                "target_path": str(DEBUG_TARGET),
                "expected_sha256": str(DEBUG_TARGET_SHA256),
                "launch_mode": "net48-exe",
                "architecture": DEBUG_ARCH,
                "break_kind": "none",
            },
        )
        self.assertTrue(launch["ok"])
        self.session_id = str(launch["debug_context"]["session_id"])
        self.generation = int(launch["debug_context"]["generation"])

        time.sleep(1.0)
        self.pause()
        modules = self.call(
            "debug_list_modules",
            {"session_id": self.session_id, "generation": self.generation},
        )
        module = next(
            item
            for item in modules["result"]["items"]
            if str(item.get("sha256", "")).casefold()
            == str(DEBUG_TARGET_SHA256).casefold()
        )

        # Refresh after module enumeration in case the initial launch pause was transient.
        self.pause()
        self.call(
            "debug_set_exception_policy",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "policy": {"break_on": "unhandled"},
            },
        )
        memory = self.call(
            "debug_read_memory",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "module_handle": module["module_handle"],
                "address": module["base_address"],
                "length": 2,
                "encoding": "hex",
            },
        )
        self.assertEqual("4d5a", memory["result"]["data"].casefold())
        self.call(
            "debug_list_threads",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
            },
        )

        breakpoint = self.call(
            "debug_set_breakpoint",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "module_handle": module["module_handle"],
                "module_sha256": module["sha256"],
                "mvid": module["mvid"],
                # The wire parser intentionally accepts decimal tokens too; successful
                # output must still use the contract's canonical hexadecimal spelling.
                "method_token": 100663297,
                "il_offset": 0,
                "enabled": True,
            },
            validate_input=False,
        )
        self.assertEqual("0x06000001", breakpoint["result"]["breakpoint"]["method_token"])
        self.breakpoint_id = breakpoint["result"]["breakpoint"]["breakpoint_id"]
        self.call(
            "debug_list_breakpoints",
            {"session_id": self.session_id, "generation": self.generation},
        )
        self.call(
            "debug_set_breakpoint_enabled",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "breakpoint_id": self.breakpoint_id,
                "enabled": False,
            },
        )
        dumped = self.call(
            "debug_dump_module",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "module_handle": module["module_handle"],
                "relative_name": f"schema-{uuid.uuid4().hex}",
            },
        )
        self.assertEqual(
            str(DEBUG_TARGET_SHA256).casefold(),
            dumped["result"]["artifact"]["sha256"].casefold(),
        )

        first = self.call(
            "debug_read_events",
            {
                "session_id": self.session_id,
                "after_cursor": 0,
                "limit": 100,
            },
        )
        cursor = first["result"]["next_cursor"]
        empty = self.call(
            "debug_read_events",
            {
                "session_id": self.session_id,
                "after_cursor": cursor,
                "limit": 100,
            },
        )
        if empty["result"]["next_cursor"] != cursor:
            self.failures.append(
                "debug_read_events: empty page must preserve the caller's current cursor"
            )

        self.call(
            "debug_remove_breakpoint",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "breakpoint_id": self.breakpoint_id,
            },
        )
        self.breakpoint_id = None
        restart = self.call(
            "debug_restart",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
            },
        )
        self.generation = int(restart["result"]["generation"])
        self.call(
            "debug_terminate",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
            },
        )
        self.session_id = None
        self.assertEqual("idle", self.call("debug_status")["result"]["state"])

        self.assertEqual([], self.failures)

    @unittest.skipUnless(
        EXPAND_TARGET and EXPAND_TARGET_SHA256 and EXPAND_MANIFEST,
        "set DNSPY_MCP_EXPAND_TARGET, DNSPY_MCP_EXPAND_TARGET_SHA256 and "
        "DNSPY_MCP_EXPAND_MANIFEST to run value checks",
    )
    def test_live_locals_and_two_level_expansion_conform_to_published_schemas(self) -> None:
        launch = self.call(
            "debug_launch",
            {
                "request_id": str(uuid.uuid4()),
                "target_path": str(EXPAND_TARGET),
                "expected_sha256": str(EXPAND_TARGET_SHA256),
                "launch_mode": "net48-exe",
                "architecture": DEBUG_ARCH,
                "break_kind": "none",
            },
        )
        self.session_id = str(launch["debug_context"]["session_id"])
        self.generation = int(launch["debug_context"]["generation"])

        time.sleep(1.0)
        self.pause()
        modules = self.call(
            "debug_list_modules",
            {"session_id": self.session_id, "generation": self.generation},
        )
        module = next(
            item
            for item in modules["result"]["items"]
            if str(item.get("sha256", "")).casefold()
            == str(EXPAND_TARGET_SHA256).casefold()
        )
        self.pause()
        before = self.call(
            "debug_read_events",
            {
                "session_id": self.session_id,
                "after_cursor": 0,
                "limit": 100,
            },
        )["result"]["next_cursor"]
        locator = load_expand_locator(str(EXPAND_MANIFEST), str(EXPAND_TARGET_SHA256))
        verify_manifest_matches_module(locator, module)
        breakpoint = self.call(
            "debug_set_breakpoint",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "module_handle": module["module_handle"],
                "module_sha256": module["sha256"],
                "mvid": module["mvid"],
                "method_token": locator["method_token"],
                "il_offset": locator["breakpoint"]["il_offset"],
                "enabled": True,
            },
        )
        self.breakpoint_id = breakpoint["result"]["breakpoint"]["breakpoint_id"]
        self.call(
            "debug_continue",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
            },
        )
        waited = self.call(
            "debug_wait_event",
            {
                "session_id": self.session_id,
                "after_cursor": before,
                "limit": 50,
                "timeout_ms": 15000,
                "kinds": ["breakpoint_hit"],
            },
        )
        self.assertFalse(waited["result"]["timed_out"])
        breakpoint_event = next(
            event
            for event in waited["result"]["events"]
            if event["kind"] == "breakpoint_hit"
        )

        self.pause()
        threads = self.call(
            "debug_list_threads",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
            },
        )
        self.assertIn(
            breakpoint_event["payload"]["thread_handle"],
            {item["thread_handle"] for item in threads["result"]["items"]},
        )
        self.assertEqual(
            module["module_handle"],
            breakpoint_event["payload"]["location"]["module_handle"],
        )
        thread = next(
            (item for item in threads["result"]["items"] if item["is_current"]),
            threads["result"]["items"][0],
        )
        stack = self.call(
            "debug_get_stack",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "thread_handle": thread["thread_handle"],
            },
        )
        frame = next(
            (
                item
                for item in stack["result"]["items"]
                if item.get("location", {}).get("method_token") == locator["method_token"]
            ),
            stack["result"]["items"][0],
        )
        locals_result = self.call(
            "debug_get_locals",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "frame_handle": frame["frame_handle"],
            },
        )
        payload = next(
            item
            for item in locals_result["result"]["items"]
            if "expandPayload" in item["name"]
        )
        first_level = self.call(
            "debug_expand_value",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "value_handle": payload["value_handle"],
                "depth": 1,
            },
        )
        child = next(item for item in first_level["result"]["items"] if "Child" in item["name"])
        second_level = self.call(
            "debug_expand_value",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "value_handle": child["value_handle"],
                "depth": 1,
            },
        )
        first_values = {
            item["name"].rsplit(".", 1)[-1]: item["display"]
            for item in first_level["result"]["items"]
        }
        second_values = {
            item["name"].rsplit(".", 1)[-1]: item["display"]
            for item in second_level["result"]["items"]
        }
        # The loop counter can advance before the host acquires its setup pause.  Verify the
        # fixture's semantic relationship instead of hard-coding a timing-sensitive zero.
        number = int(first_values["Number"], 0)
        self.assertEqual(f'"expand-{number}"', first_values["Text"])
        self.assertEqual(number + 1, int(second_values["Number"], 0))
        self.assertEqual(f'"child-{number}"', second_values["Text"])
        self.assertEqual("null", second_values["Child"])

        self.call(
            "debug_set_breakpoint_enabled",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "breakpoint_id": self.breakpoint_id,
                "enabled": False,
            },
        )
        step_cursor = self.call(
            "debug_read_events",
            {
                "session_id": self.session_id,
                "after_cursor": 0,
                "limit": 100,
            },
        )["result"]["next_cursor"]
        self.call(
            "debug_step",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "thread_handle": thread["thread_handle"],
                "kind": "over",
            },
        )
        step_event = self.call(
            "debug_wait_event",
            {
                "session_id": self.session_id,
                "after_cursor": step_cursor,
                "limit": 50,
                "timeout_ms": 15000,
                "kinds": ["step_completed"],
            },
        )
        self.assertFalse(step_event["result"]["timed_out"])
        self.pause()
        post_step_threads = self.call(
            "debug_list_threads",
            {
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
            },
        )
        completed = next(
            event
            for event in step_event["result"]["events"]
            if event["kind"] == "step_completed"
        )
        self.assertIn(
            completed["payload"]["thread_handle"],
            {item["thread_handle"] for item in post_step_threads["result"]["items"]},
        )
        self.assertEqual(
            module["module_handle"],
            completed["payload"]["location"]["module_handle"],
        )
        self.call(
            "debug_remove_breakpoint",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
                "pause_epoch": self.pause_epoch,
                "breakpoint_id": self.breakpoint_id,
            },
        )
        self.breakpoint_id = None
        self.call(
            "debug_terminate",
            {
                "request_id": str(uuid.uuid4()),
                "session_id": self.session_id,
                "generation": self.generation,
            },
        )
        self.session_id = None
        self.assertEqual("idle", self.call("debug_status")["result"]["state"])

        self.assertEqual([], self.failures)


class ExpandManifestLoaderTests(unittest.TestCase):
    """Negative/positive checks for the T038 expand-fixture locator (runs without a VM).

    The key counterexamples below were first demonstrated RED against the R1 loader
    (see fixture-t038-r02/red/red-evidence.txt in the acceptance archive): a non-hex
    token 0xZZZZZZZZ, a TypeDef token 0x02000001, a zero-RID 0x06000000 and a
    Main-only boundary were all wrongly accepted. The v2 loader rejects each class.
    """

    @staticmethod
    def _write_manifest(tmpdir: str, **overrides):
        manifest = {
            "schema_version": "dnspy.expand-fixture-manifest.v2",
            "fixture": "ExpandValuesFixture-x64.exe",
            "sha256": "a" * 64,
            "mvid": "39a1ac13-eb69-4e84-9769-31c3024e4b5b",
            "entry_type": "ExpandValuesFixtureNs.ExpandValuesFixture",
            "entry_type_token": "0x02000003",
            "method": "KeepLive",
            "method_token": "0x06000002",
            "breakpoint": {"il_offset": 0, "rationale": "KeepLive entry", "is_instruction_boundary": True},
            "method_il_boundaries": [0, 1, 2, 4],
            "reference_main_il_boundaries": [0, 5, 10, 107, 110],
            "main_keep_call_il_offset": 107,
            "node_type": "ExpandValuesFixtureNs.ExpandNode",
            "local_name": "expandPayload",
            "anchor_frame_kind": "parameter",
        }
        manifest.update(overrides)
        path = os.path.join(tmpdir, "m.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(manifest, handle)
        return path

    def setUp(self) -> None:
        import tempfile

        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def test_valid_manifest_loads(self) -> None:
        path = self._write_manifest(self.tmp.name)
        manifest = load_expand_locator(path, "A" * 64)
        self.assertEqual("0x06000002", manifest["method_token"])
        self.assertEqual(0, manifest["breakpoint"]["il_offset"])

    def test_wrong_sha_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name)
        with self.assertRaisesRegex(ValueError, "match the deployed fixture"):
            load_expand_locator(path, "b" * 64)

    def test_malformed_sha_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name, sha256="xyz")
        with self.assertRaisesRegex(ValueError, "malformed sha256"):
            load_expand_locator(path, "a" * 64)

    def test_non_hex_token_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name, method_token="0xZZZZZZZZ")
        with self.assertRaisesRegex(ValueError, "malformed method_token"):
            load_expand_locator(path, "a" * 64)

    def test_wrong_table_token_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name, method_token="0x02000001")
        with self.assertRaisesRegex(ValueError, "MethodDef"):
            load_expand_locator(path, "a" * 64)

    def test_zero_rid_token_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name, method_token="0x06000000")
        with self.assertRaisesRegex(ValueError, "zero RID"):
            load_expand_locator(path, "a" * 64)

    def test_bool_offset_rejected_even_when_equal_to_boundary(self) -> None:
        # True == 1 and 1 IS a boundary here; only the exact type check rejects it.
        path = self._write_manifest(self.tmp.name, breakpoint={"il_offset": True})
        with self.assertRaisesRegex(ValueError, "non-negative int"):
            load_expand_locator(path, "a" * 64)

    def test_negative_offset_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name, breakpoint={"il_offset": -1})
        with self.assertRaisesRegex(ValueError, "non-negative int"):
            load_expand_locator(path, "a" * 64)

    def test_main_only_boundary_rejected_for_anchor(self) -> None:
        # 107 exists only in reference_main_il_boundaries (a Main offset); the anchor
        # is KeepLive, so it must not validate.
        path = self._write_manifest(self.tmp.name, breakpoint={"il_offset": 107})
        with self.assertRaisesRegex(ValueError, "not an instruction boundary of the anchor method"):
            load_expand_locator(path, "a" * 64)

    def test_malformed_mvid_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name, mvid="not-a-uuid")
        with self.assertRaisesRegex(ValueError, "malformed mvid"):
            load_expand_locator(path, "a" * 64)

    def test_v1_schema_rejected(self) -> None:
        path = self._write_manifest(self.tmp.name, schema_version="dnspy.expand-fixture-manifest.v1")
        with self.assertRaisesRegex(ValueError, "schema_version"):
            load_expand_locator(path, "a" * 64)


class ExpandManifestModuleBindingTests(unittest.TestCase):
    """verify_manifest_matches_module: the manifest's MVID must equal the live module's."""

    def test_matching_module_accepted(self) -> None:
        manifest = {"mvid": "39A1AC13-EB69-4E84-9769-31C3024E4B5B"}
        module = {"mvid": "39a1ac13-eb69-4e84-9769-31c3024e4b5b"}
        verify_manifest_matches_module(manifest, module)  # must not raise

    def test_mismatching_module_mvid_rejected(self) -> None:
        manifest = {"mvid": "39a1ac13-eb69-4e84-9769-31c3024e4b5b"}
        module = {"mvid": "52d6ef29-e684-4ea9-bf69-b35d39c57c93"}
        with self.assertRaisesRegex(ValueError, "does not match the live module"):
            verify_manifest_matches_module(manifest, module)

    def test_missing_module_mvid_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "does not match the live module"):
            verify_manifest_matches_module({"mvid": "39a1ac13-eb69-4e84-9769-31c3024e4b5b"}, {})


if __name__ == "__main__":
    unittest.main()
