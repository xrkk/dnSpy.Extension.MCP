#!/usr/bin/env python3
"""Check current legacy-write registry prose against the one-file manual and docs.

This is a source/document test, not proof that a particular deployed DLL uses
these words. The VM wire probe supplies that independent observation.
"""
import hashlib
import json
import re
import argparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
NAMES = ("patch_method_il", "force_return", "nop_method", "revert_method_il",
         "rename_symbol_by_token", "save_assembly")
CS_STRING = r'"(?:\\.|[^"\\])*"'
OLD_PROMISE = re.compile(
    r"snapshot.on.first.patch|first patch .*snapshot|pending snapshot|snapshot mechanism|"
    r"timestamped backup|overwrite original|overwritten after|write .*back to disk|"
    r"首次补丁.*快照|覆盖原文件前.*备份|保存后.*快照.*清理",
    re.I)


def tool_block(source: str, name: str) -> str:
    match = re.search(r'Name\s*=\s*"' + re.escape(name) + r'"\s*,', source)
    assert match, name
    end = source.find("new ToolInfo {", match.end())
    return source[match.end():end if end != -1 else None]


def tool_description(block: str) -> str:
    match = re.search(r"Description\s*=\s*(" + CS_STRING + r")", block)
    assert match, block[:120]
    return json.loads(match.group(1))


def output_path_description(block: str) -> str:
    match = re.search(r'\["output_path"\][^\n]*\{[\s\S]*?\["description"\]\s*=\s*(' + CS_STRING + r')', block)
    assert match, "save_assembly.output_path description absent"
    return json.loads(match.group(1))


def verify_descriptions(descriptions: dict[str, str], output_path: str) -> None:
    assert set(descriptions) == set(NAMES)
    for name, description in descriptions.items():
        assert not OLD_PROMISE.search(description), (name, description)
        assert "checkpoint" in description.lower(), name
    for name in ("patch_method_il", "force_return", "nop_method", "rename_symbol_by_token"):
        assert "structured edit transaction" in descriptions[name], name
        assert "ArtifactRoot" in descriptions[name], name
    assert "current history head" in descriptions["revert_method_il"]
    assert "EDIT_HISTORY_CONFLICT" in descriptions["revert_method_il"]
    assert "without overwriting the source" in descriptions["rename_symbol_by_token"]
    assert "never the source sample" in descriptions["save_assembly"]
    assert "ArtifactRoot" in descriptions["save_assembly"]
    assert not OLD_PROMISE.search(output_path), output_path
    assert "ArtifactRoot" in output_path and "cannot be overwritten" in output_path


source = (ROOT / "McpTools.cs").read_text(encoding="utf-8")
blocks = {name: tool_block(source, name) for name in NAMES}
descriptions = {name: tool_description(block) for name, block in blocks.items()}
output_path = output_path_description(blocks["save_assembly"])
verify_descriptions(descriptions, output_path)

# In-memory negative mutation proves this checks semantics, not mere text presence.
mutated = dict(descriptions)
mutated["revert_method_il"] = "Restore the first patch snapshot. Fails with -32602 if no pending snapshot exists."
try:
    verify_descriptions(mutated, output_path)
except AssertionError:
    pass
else:
    raise AssertionError("old snapshot promise escaped validation")
try:
    verify_descriptions(descriptions, "Optional. If absent, overwrite original with a timestamped backup.")
except AssertionError:
    pass
else:
    raise AssertionError("old overwrite promise escaped validation")

reference = (ROOT / "docs/AI-TOOL-REFERENCE.zh-CN.md").read_text(encoding="utf-8")
appendix_match = re.search(r"## 附录 A：32 个静态工具的原样输入 schema[\s\S]*?```json\n([\s\S]*?)\n```", reference)
assert appendix_match, "single-file static input appendix absent"
appendix = json.loads(appendix_match.group(1))
assert appendix["save_assembly"]["properties"]["output_path"]["description"] == output_path
assert appendix["save_assembly"]["properties"]["assembly_name"]["description"] == "Name of the loaded assembly whose exact checkpoint is to be exported"
baseline = json.loads((ROOT / "tests/snapshots/static-tools.baseline.json").read_text(encoding="utf-8"))
baseline_by_name = {tool["name"]: tool for tool in baseline}
assert len(baseline) == 32 and len(baseline_by_name) == 32
for name in NAMES:
    assert baseline_by_name[name]["description"] == descriptions[name], name
    assert baseline_by_name[name]["inputSchema"] == appendix[name], name
source_sha = hashlib.sha256((ROOT / "McpTools.cs").read_bytes()).hexdigest()
assert f"`McpTools.cs` SHA256 `{source_sha}`" in reference
for name in NAMES:
    match = re.search(r"^### " + name + r"\n([\s\S]*?)(?=^### |^## )", reference, re.M)
    assert match, name
    section = match.group(1)
    assert "检查点" in section or "ArtifactRoot" in section, name
    assert not OLD_PROMISE.search(section), (name, section[:200])
assert "EDIT_HISTORY_CONFLICT" in reference.split("### revert_method_il", 1)[1].split("### ", 1)[0]

for relative in ("README.md", "README.zh-CN.md", "docs/MCP-TOOLS.md",
                 "docs/MCP-TOOLS.zh-CN.md", "docs/mcp-resources/il-editing.md"):
    body = (ROOT / relative).read_text(encoding="utf-8")
    assert "ArtifactRoot" in body, relative
    assert "save_assembly" in body, relative
    assert not OLD_PROMISE.search(body), relative

parser = argparse.ArgumentParser()
parser.add_argument("--wire-evidence", type=Path)
args = parser.parse_args()
if args.wire_evidence:
    capture = json.loads(args.wire_evidence.read_text(encoding="utf-8"))
    wire = capture["registry"]
    assert set(wire) == set(NAMES)
    for name in NAMES:
        assert wire[name]["description"] == descriptions[name], name
        assert wire[name]["inputSchema"] == appendix[name], name
        assert "outputSchema" not in wire[name], name

print(json.dumps({"status": "PASS", "registry_tools": len(descriptions),
                  "static_snapshot_compared": True,
                  "manual_appendix_output_path": "matches source", "source_sha256": source_sha,
                  "negative_mutations_rejected": 2, "other_documents_checked": 5,
                  "wire_registry_compared": bool(args.wire_evidence)}))
