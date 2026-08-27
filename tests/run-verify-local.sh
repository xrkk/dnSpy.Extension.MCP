#!/usr/bin/env bash
# Single-machine replacement for the CI verify pipeline (no GitHub Actions needed).
# Equivalent of .github/workflows/verify.yml's contracts + build jobs on one Linux box with a
# dnSpyEx checkout at /path/to/dnSpy (tag v6.6.0). Usage:
#   EXT_DIR=$PWD DNSPY_DIR=/path/to/dnSpyEx tests/run-verify-local.sh
# Requires: dotnet SDK 10, python3 + jsonschema (pip), rsync.
set -euo pipefail

EXT_DIR="${EXT_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
DNSPY_DIR="${DNSPY_DIR:-$EXT_DIR/../..}"   # default: this repo cloned into dnSpyEx/Extensions
BUILD_CFG="${BUILD_CFG:-Release}"

# jsonschema lives in a persistent uv venv (python3-venv/ensurepip is unavailable on this box).
VERIFY_PY="${VERIFY_PY:-$HOME/.local/verify-venv/bin/python3}"
if [ ! -x "$VERIFY_PY" ]; then
    uv venv "$HOME/.local/verify-venv" >/dev/null 2>&1 || true
    uv pip install --python "$HOME/.local/verify-venv/bin/python3" jsonschema --quiet
fi
PY() { if [ -x "$VERIFY_PY" ]; then "$VERIFY_PY" "$@"; else python3 "$@"; fi; }

echo "== [1/4] Contract validation (verify.yml: contracts) =="
PY "$EXT_DIR/tests/debug/contracts/validate.py"

echo "== [2/4] Static tool snapshot (verify.yml: contracts) =="
PY - <<'EOF'
import json, os
snap = json.load(open(os.path.join(os.environ.get("EXT_DIR", "."), "tests/snapshots/static-tools.baseline.json")))
assert len(snap) == 32, f"expected 32 static tools, got {len(snap)}"
assert all(t.get('name') and t.get('description') and t.get('inputSchema') for t in snap)
print('static tool snapshot: 32 tools, complete entries')
EOF

echo "== [3/4] rsync sources into dnSpyEx checkout =="

WORK="$DNSPY_DIR/Extensions/dnSpy.Extension.MCP"
if [ ! -d "$DNSPY_DIR/dnSpy" ]; then
    echo "DNSPY_DIR ($DNSPY_DIR) does not look like a dnSpyEx checkout (no dnSpy/ subdir)."
    echo "Usage: DNSPY_DIR=/path/to/dnSpyEx $0"
    exit 1
fi
rsync -a --delete --exclude '.git' --exclude 'bin' --exclude 'obj' "$EXT_DIR/" "$WORK/"

echo "== [4/4] net48 dependency guard + Dual-TFM $BUILD_CFG build (verify.yml: build) =="
DNSPY_DIR="$DNSPY_DIR" EXT_BUILD_DIR="$WORK" PY "$EXT_DIR/tests/check-host-deps.py"
cd "$WORK"
dotnet build -c "$BUILD_CFG" -f net10.0-windows -p:EnableWindowsTargeting=true
dotnet build -c "$BUILD_CFG" -f net48
mkdir -p "$EXT_DIR/dist"
cp "bin/$BUILD_CFG/net10.0-windows/dnSpy.Extension.MCP.x.dll" "$EXT_DIR/dist/dnSpy.Extension.MCP-net10.0-windows.x.dll"
cp "bin/$BUILD_CFG/net48/dnSpy.Extension.MCP.x.dll" "$EXT_DIR/dist/dnSpy.Extension.MCP-net48.x.dll"
sha256sum "$EXT_DIR/dist/"*.dll

echo ""
echo "LOCAL VERIFY PASSED — artifacts in $EXT_DIR/dist/"
echo "Next: L3 in-process checks on Windows (see tests/TEST-PLAN.zh-CN.md), then L4 on the VM."
