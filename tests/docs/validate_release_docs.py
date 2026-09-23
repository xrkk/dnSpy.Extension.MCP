#!/usr/bin/env python3
"""Read-only cross-check of release prose and executable prompt guardrails."""
from __future__ import annotations

import importlib.util
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXPORTER = ROOT / "tools/export_tool_registry.py"
spec = importlib.util.spec_from_file_location("tool_registry_exporter", EXPORTER)
assert spec and spec.loader
registry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(registry)

profiles = {}
for gate in ("enabled", "disabled"):
    for mode in ("production", "acceptance"):
        families, operations = registry.source_registry(mode, gate)
        assert len(operations) == 39 and len(set(operations)) == 39
        profiles[f"{mode}/{gate}"] = len(families)
assert profiles == {
    "production/enabled": 72, "acceptance/enabled": 78,
    "production/disabled": 51, "acceptance/disabled": 57,
}, profiles

test_seams = registry.quoted_array(ROOT / "Editing/EditToolProvider.cs", r"static readonly string\[\] TestTools")
assert len(test_seams) == 9 and len(set(test_seams)) == 9
docs = [ROOT / "README.md", ROOT / "README.zh-CN.md",
        ROOT / "docs/MCP-TOOLS.md", ROOT / "docs/MCP-TOOLS.zh-CN.md",
        ROOT / "docs/AI-TOOL-REFERENCE.zh-CN.md",
        ROOT / "docs/mcp-resources/overview.md",
        *sorted((ROOT / "docs").glob("ZCODE-*TEST-PROMPT.zh-CN.md"))]
assert len(docs) == 15, len(docs)
texts = {path: path.read_text(encoding="utf-8") for path in docs}
for path, body in texts.items():
    for label, target in re.findall(r"\[([^]]+)\]\(([^)]+)\)", body):
        if target.startswith(("http:", "https:", "#")):
            continue
        destination = (path.parent / target.split("#", 1)[0]).resolve()
        assert destination.is_file(), (path, label, target)

for path in (ROOT / "README.md", ROOT / "README.zh-CN.md",
             ROOT / "docs/MCP-TOOLS.md", ROOT / "docs/MCP-TOOLS.zh-CN.md",
             ROOT / "docs/mcp-resources/overview.md"):
    body = texts[path]
    assert all(str(n) in body for n in (72, 78, 51, 57, 9)), path
    assert not re.search(r"\b8 (?:callable|个可调用|个不通告).*edit_test", body), path

reference = texts[ROOT / "docs/AI-TOOL-REFERENCE.zh-CN.md"]
assert "可直接调用但**从不通告**的编辑缝 9 个" in reference
assert all(f"### {name}\n" in reference for name in registry.source_registry("production", "enabled")[0])

full = texts[ROOT / "docs/ZCODE-FULL-FUNCTION-TEST-PROMPT.zh-CN.md"]
assert "192.168.204.149" not in full
assert all(term in full for term in ("9 个可调用", "支持 resources 的宿主", "调试门关闭", "PID", "全局 kill", "未执行"))
assert full.count("```") % 2 == 0
for block in re.findall(r"^```json\n(.*?)^```", full, re.M | re.S):
    json.loads(block)
for stage in range(1, 9):
    path = ROOT / f"docs/ZCODE-P{stage:02d}-TARGETED-TEST-PROMPT.zh-CN.md"
    body = texts[path]
    assert all(term in body for term in ("PID", "ArtifactRoot", "受保护")), path
    assert any(term in body for term in ("未执行", "未运行")) or stage == 2, path
    assert "全局 kill" in body and "tools/list" in body, path

source_description = (ROOT / "Editing/EditToolProvider.cs").read_text(encoding="utf-8")
assert '"edit_apply" => "Apply one of the 37' in source_description
assert "37" in texts[ROOT / "docs/MCP-TOOLS.md"] and "误写“37”" in texts[ROOT / "docs/MCP-TOOLS.zh-CN.md"]
csproj = (ROOT / "dnSpy.Extension.MCP.csproj").read_text(encoding="utf-8")
assert 'Include="docs/mcp-resources/overview.md" LogicalName="dnspy.docs.overview.md"' in csproj

print(json.dumps({"status": "PASS", "profiles": profiles, "operations": 39,
                  "unadvertised_edit_tests": len(test_seams), "documents_checked": len(docs),
                  "links_checked": sum(len(re.findall(r"\[[^]]+\]\([^)]+\)", body)) for body in texts.values()),
                  "known_source_description_mismatch": "edit_apply 37 vs schema 39"}, ensure_ascii=False))
