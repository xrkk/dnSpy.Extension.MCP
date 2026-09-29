"""F10 family workflow — delegates to the shared read-only corpus runners."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from fam03.workflow import run_f10 as _run


def run_variant(env, sid):
    _run(env, sid)
