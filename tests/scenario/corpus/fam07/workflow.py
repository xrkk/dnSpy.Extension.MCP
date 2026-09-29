"""F07 family workflow — delegates to fam05 shared runners."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from fam05.workflow import run_f07 as _run

def run_variant(env, sid):
    _run(env, sid)
