"""Opt-in strict input/output schema checks against a deployed debugger session.

This test exists because frozen example fixtures can conform while the live DTO serializer
emits a different JSON type.  It validates every request against the published inputSchema
and actual structuredContent against outputSchema across all 22 debug tools.
"""

from __future__ import annotations

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
        EXPAND_TARGET and EXPAND_TARGET_SHA256,
        "set DNSPY_MCP_EXPAND_TARGET and DNSPY_MCP_EXPAND_TARGET_SHA256 to run value checks",
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
                "method_token": "0x06000002",
                "il_offset": 82,
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
                if item.get("location", {}).get("method_token") == "0x06000002"
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


if __name__ == "__main__":
    unittest.main()
