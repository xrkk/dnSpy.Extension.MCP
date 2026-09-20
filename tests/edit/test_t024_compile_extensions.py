#!/usr/bin/env python3
"""Local structural guards for the T024 extension-disable evidence."""

from __future__ import annotations

import ast
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
FRONTEND = ROOT / "Editing" / "EditCompileFrontend.cs"
DRIVER = ROOT / "tests" / "edit" / "p03_vm_acc005_extensions.py"


class CompileExtensionBoundaryTests(unittest.TestCase):
    def test_provider_whitelist_and_schema_have_only_ordinary_compile_inputs(self):
        source = FRONTEND.read_text(encoding="utf-8")
        top_level_guard = next(line for line in source.splitlines()
                               if 'key is not "request_id"' in line)
        whitelist = set(re.findall(r'(?:key is not|and not) "([^"]+)"', top_level_guard))
        self.assertEqual({"request_id", "assembly_name", "compilation_kind", "documents",
                          "target_platform", "references_override"}, whitelist)
        self.assertIn('property.Name is not "path" and not "content"', source)
        self.assertIn('["additionalProperties"] = false', source)
        self.assertIn('new[] { "edit_method", "edit_class" }', source)

    def test_frontend_has_no_extension_activation_or_process_surface(self):
        source = FRONTEND.read_text(encoding="utf-8")
        for forbidden in ("AnalyzerReference", "GeneratorDriver", "CSharpScript", "ScriptOptions",
                          "MSBuildWorkspace", "Process.Start(", "Assembly.Load(", "Assembly.LoadFrom("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, source)
        for required in ("provider.Create(kind)", "InitializeProject(", "AddDocuments(", "CompileAsync("):
            self.assertIn(required, source)

    def test_real_driver_covers_every_activation_alias_and_script_directive(self):
        tree = ast.parse(DRIVER.read_text(encoding="utf-8"))
        strings = {node.value for node in ast.walk(tree)
                   if isinstance(node, ast.Constant) and isinstance(node.value, str)}
        for value in ("analyzers", "source_generators", "build_tasks", "script_path",
                      "script", "source_kind", "future_extension", "Second.cs", "load", "reference",
                      "Probe.csx", "references_override"):
            with self.subTest(value=value):
                self.assertIn(value, strings)


if __name__ == "__main__":
    unittest.main()
