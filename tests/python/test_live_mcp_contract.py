"""Opt-in public-boundary tests against a deployed dnSpy MCP instance."""

from __future__ import annotations

import json
import os
import unittest
from typing import Any

from dnspy_mcp import DnSpyClient


LIVE_URL = os.getenv("DNSPY_MCP_LIVE_URL")


@unittest.skipUnless(LIVE_URL, "set DNSPY_MCP_LIVE_URL to run deployed MCP contract tests")
class LiveMcpContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.client = DnSpyClient(str(LIVE_URL), client_name="dnspy-contract-test")
        self.client.initialize()

    def tearDown(self) -> None:
        self.client.close()

    def test_every_published_local_schema_reference_resolves(self) -> None:
        def collect_refs(value: Any) -> list[str]:
            if isinstance(value, dict):
                refs = [value["$ref"]] if isinstance(value.get("$ref"), str) else []
                return refs + [ref for child in value.values() for ref in collect_refs(child)]
            if isinstance(value, list):
                return [ref for child in value for ref in collect_refs(child)]
            return []

        failures: list[str] = []
        for tool in self.client.iter_tools():
            for field in ("inputSchema", "outputSchema"):
                schema = tool.get(field)
                if not isinstance(schema, dict):
                    continue
                defs = schema.get("$defs")
                for ref in collect_refs(schema):
                    if not ref.startswith("#/$defs/"):
                        continue
                    name = ref.removeprefix("#/$defs/").split("/", 1)[0]
                    if not isinstance(defs, dict) or name not in defs:
                        failures.append(f"{tool.get('name')}.{field}: {ref}")

        self.assertEqual([], failures)

    def test_debug_schemas_are_flat_objects_and_outputs_describe_envelopes(self) -> None:
        def keywords(value: Any, name: str) -> list[Any]:
            if isinstance(value, dict):
                found = [value[name]] if name in value else []
                return found + [item for child in value.values() for item in keywords(child, name)]
            if isinstance(value, list):
                return [item for child in value for item in keywords(child, name)]
            return []

        debug_tools = [tool for tool in self.client.iter_tools() if str(tool.get("name", "")).startswith("debug_")]
        self.assertEqual(22, len(debug_tools))
        for tool in debug_tools:
            for field in ("inputSchema", "outputSchema"):
                schema = tool.get(field)
                self.assertIsInstance(schema, dict, f"{tool.get('name')}.{field}")
                self.assertEqual("object", schema.get("type"), f"{tool.get('name')}.{field}")
                self.assertEqual([], keywords(schema, "$ref"), f"{tool.get('name')}.{field} refs")
                self.assertEqual([], keywords(schema, "$defs"), f"{tool.get('name')}.{field} defs")
                self.assertEqual([], keywords(schema, "allOf"), f"{tool.get('name')}.{field} allOf")

            output = tool["outputSchema"]
            properties = output.get("properties")
            self.assertIsInstance(properties, dict)
            for name in ("schema_version", "ok", "debug_context", "result", "error", "warnings", "untrusted_sample_data"):
                self.assertIn(name, properties, f"{tool.get('name')}.outputSchema.properties")

    def test_list_assemblies_structured_content_is_an_object(self) -> None:
        result = self.client.call_tool("list_assemblies")
        structured = result.get("structuredContent")
        self.assertIsInstance(structured, dict)
        assemblies = structured.get("assemblies")
        self.assertIsInstance(assemblies, list)
        self.assertGreater(len(assemblies), 0)
        for assembly in assemblies:
            self.assertIsInstance(assembly, dict)
            for field in ("Name", "Version", "FullName", "Culture", "PublicKeyToken"):
                self.assertIsInstance(assembly.get(field), str)
        text = self.client._first_text(result)
        self.assertIsNotNone(text)
        self.assertEqual(structured, json.loads(str(text)))

    def test_resource_templates_list_is_supported(self) -> None:
        result = self.client.request("resources/templates/list", {})
        self.assertIsInstance(result, dict)
        self.assertEqual([], result.get("resourceTemplates"))

    def test_all_documentation_resources_are_readable(self) -> None:
        listed = self.client.list_resources()
        resources = listed.get("resources")
        self.assertIsInstance(resources, list)
        self.assertEqual(14, len(resources))
        for resource in resources:
            self.assertIsInstance(resource, dict)
            uri = resource.get("uri")
            self.assertIsInstance(uri, str)
            read = self.client.read_resource(uri)
            contents = read.get("contents")
            self.assertIsInstance(contents, list, uri)
            self.assertGreater(len(contents), 0, uri)
            for item in contents:
                self.assertIsInstance(item, dict, uri)
                self.assertTrue(item.get("text"), uri)


if __name__ == "__main__":
    unittest.main()
