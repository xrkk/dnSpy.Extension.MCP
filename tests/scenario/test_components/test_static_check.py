"""Component unit tests: static scenario structure check (AUD-301 deliverable)."""

from __future__ import annotations

from pathlib import Path

from dnspy_scenario.static_check import run

GOOD = '''
SCENARIO_ID = "S-FAM-01"
DECLARED_TOOLS = ["t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "t9", "t10", "t1"]

def test_flow():
    """goal and steps"""
    pass
'''

MISSING_DOC = '''
SCENARIO_ID = "S-FAM-02"
DECLARED_TOOLS = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"]

def test_flow():
    pass
'''

TOO_FEW = '''
SCENARIO_ID = "S-FAM-03"
DECLARED_TOOLS = ["a", "b"]

def test_flow():
    """doc"""
    pass
'''

NO_CONSTANTS = '''
def test_flow():
    """doc"""
    pass
'''


def write(tmp_path, name, text):
    path = Path(tmp_path) / name
    path.write_text(text, encoding="utf-8")
    return path


def test_good_file_passes(tmp_path):
    write(tmp_path, "test_good.py", GOOD)
    reports, problems = run(tmp_path, 10)
    assert not problems
    scenario = reports[0]["scenarios"][0]
    assert scenario["declared_tools_distinct"] == 10  # duplicates deduped
    assert scenario["docstring_present"]


def test_missing_docstring_flagged(tmp_path):
    write(tmp_path, "test_nodoc.py", MISSING_DOC)
    _, problems = run(tmp_path, 10)
    assert any("lacks docstring" in p for p in problems)


def test_too_few_tools_flagged(tmp_path):
    write(tmp_path, "test_few.py", TOO_FEW)
    _, problems = run(tmp_path, 10)
    assert any("distinct tools" in p for p in problems)


def test_missing_constants_flagged(tmp_path):
    write(tmp_path, "test_noconst.py", NO_CONSTANTS)
    _, problems = run(tmp_path, 10)
    assert any("module constants" in p for p in problems)
