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
# 2026-09-29 token-optimization release: +get_type_overview (static) and
# +debug_snapshot (debug); the previously frozen debug advertisement count 22
# was revised to 23 by the audited plan (IMP-503/IMP-504, D-03/D-08).
assert profiles == {
    "production/enabled": 74, "acceptance/enabled": 80,
    "production/disabled": 52, "acceptance/disabled": 58,
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
    assert all(str(n) in body for n in (74, 80, 52, 58, 9)), path
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
source_operations = registry.source_registry("production", "enabled")[1]
schema = json.loads((ROOT / "Editing/Contracts/p03-tool-schemas.json").read_text(encoding="utf-8"))
# The appendix projection for the new edit_begin branch comes from the embedded
# authoritative schema, including its strict mutually exclusive selectors.
appendix = reference.split("## 附录 C：", 1)[1].split("```json\n", 1)[1].split("\n```", 1)[0]
assert json.loads(appendix)["edit_begin"]["inputSchema"] == schema["edit_begin"]["inputSchema"]
# IMP-502: the operation schema is the compact discriminating union; the 39
# kind names come from its enum in the frozen OperationKinds order.
operation_schema = schema["edit_apply"]["inputSchema"]["properties"]["operation"]
schema_operations = operation_schema["properties"]["kind"]["enum"]
assert len(schema_operations) == 39
assert schema_operations == source_operations, "edit_apply schema differs from OperationKinds"

def description_count(description: str) -> int:
    match = re.search(r'"edit_apply"\s*=>\s*"Apply one of the (\d+) structured metadata/body operations atomically to the transaction private copy\."', description)
    assert match, "edit_apply registry description missing or changed"
    count = int(match.group(1))
    assert count == len(source_operations) == len(schema_operations), (
        "edit_apply description/schema/OperationKinds count mismatch", count,
        len(schema_operations), len(source_operations))
    return count

assert description_count(source_description) == 39
# In-memory negative mutation: catches the old 37 claim without changing shared source.
try:
    description_count(source_description.replace("Apply one of the 39 structured", "Apply one of the 37 structured", 1))
except AssertionError:
    pass
else:
    raise AssertionError("stale 37 description escaped registry validation")
assert "still says “37”" not in texts[ROOT / "docs/MCP-TOOLS.md"]
assert "误写“37”" not in texts[ROOT / "docs/MCP-TOOLS.zh-CN.md"]
csproj = (ROOT / "dnSpy.Extension.MCP.csproj").read_text(encoding="utf-8")
assert 'Include="docs/mcp-resources/overview.md" LogicalName="dnspy.docs.overview.md"' in csproj

# initialize.instructions is published before tools/list. Keep its editing workflow
# consistent with the live registry/schema and the embedded editing document.
documentation_source = (ROOT / "McpDocumentationResources.cs").read_text(encoding="utf-8")
instructions_block = documentation_source.split("public const string Instructions =", 1)[1].split(
    "sealed class Definition", 1)[0]
instructions_chunks = re.findall(r'"([^"\\]*(?:\\.[^"\\]*)*)"', instructions_block)
instructions = "".join(instructions_chunks)
server_source = (ROOT / "McpServer.cs").read_text(encoding="utf-8")
resource_source = (ROOT / "BepInExResources.cs").read_text(encoding="utf-8")
editing_resource = (ROOT / "docs/mcp-resources/il-editing.md").read_text(encoding="utf-8")
assert "Instructions = McpDocumentationResources.Instructions" in server_source
assert "McpDocumentationResources.AddTo(resources)" in resource_source
assert len(re.findall(r'new Definition\("dnspy://docs/', documentation_source)) == 9
assert len(re.findall(r'resources\["bepinex://docs/', resource_source)) == 6
assert all(name in source_description and name in schema for name in
           ("edit_begin", "edit_apply", "edit_review", "edit_rollback", "edit_commit",
            "edit_history", "edit_export"))
assert "confirmed_risk_ids" in schema["edit_commit"]["inputSchema"]["required"]
assert all(name in editing_resource for name in ("edit_commit", "edit_rollback", "edit_history", "edit_export"))
assert "New `strong_name_remove` edits are deferred" in editing_resource

def validate_initialize_guidance(guidance: str) -> None:
    assert not re.search(r"no product commit/export|must end with edit_rollback|remain private in P02",
                         guidance, re.I), "obsolete P02-only initialize guidance"
    assert all(term in guidance for term in ("dnspy://docs/index", "dnspy://docs/il-editing",
            "verify target/output paths", "initialized owner", "debugger idle",
            "edit_begin/edit_apply/edit_review", "edit_commit", "confirmed_risk_ids",
            "edit_rollback", "edit_history", "edit_export", "ArtifactRoot",
            "strong_name_remove", "EDIT_CAPABILITY_UNAVAILABLE"))
    assert re.search(r"edit_commit needs a reviewed revision and explicit confirmed_risk_ids"
                     r" and persists a checkpoint", guidance)
    assert re.search(r"edit_rollback to discard uncommitted changes", guidance)
    assert re.search(r"edit_export to write only below ArtifactRoot without overwriting the source", guidance)

validate_initialize_guidance(instructions)
assert len(instructions) > 512 and instructions[:512].endswith(". ")
stale_instructions = instructions.replace(
    "edit_commit needs a reviewed revision and explicit confirmed_risk_ids and persists a checkpoint for audits.",
    "changes must end with edit_rollback; no product commit/export exists yet.", 1)
assert stale_instructions != instructions
try:
    validate_initialize_guidance(stale_instructions)
except AssertionError:
    pass
else:
    raise AssertionError("obsolete P02 initialize guidance escaped release validation")

print(json.dumps({"status": "PASS", "profiles": profiles, "operations": 39,
                  "unadvertised_edit_tests": len(test_seams), "documents_checked": len(docs),
                  "links_checked": sum(len(re.findall(r"\[[^]]+\]\([^)]+\)", body)) for body in texts.values()),
                  "registry_description_operations": 39,
                  "negative_mutation_37_rejected": True,
                  "negative_mutation_old_initialize_rejected": True,
                  "initialize_resources_checked": 15}, ensure_ascii=False))
