#!/usr/bin/env python3
"""Linux-runnable equivalent of tests/check-host-deps.ps1 (no pwsh needed).

Same contract: fail when a NuGet package this extension pins for net48 drifts from the version
dnSpy's own net48 restore graph resolves. Compares project.assets.json of both projects, plus
the DNSPY_REF pin agreement between the two workflows and the local dnSpyEx checkout tag.
"""
import json, os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
EXT = os.path.dirname(HERE)
GUARDED = ["System.Text.Json"]

def fail(msg):
    print("FAIL:", msg)
    sys.exit(1)

def net48_versions(project_dir, label):
    assets = os.path.join(project_dir, "obj", "project.assets.json")
    if not os.path.exists(assets):
        fail(f"{label} restore graph not found: {assets}\nRun 'dotnet restore' in that project first.")
    doc = json.load(open(assets, encoding="utf-8"))
    versions = {}
    for target, libs in (doc.get("targets") or {}).items():
        if not re.match(r"^(net48|\.NETFramework,Version=v4\.8)$", target):
            continue
        for lib in libs:
            parts = lib.split("/", 1)
            if len(parts) == 2:
                versions[parts[0]] = parts[1]
    if not versions:
        fail(f"{label} has no .NETFramework,Version=v4.8 target in {assets}")
    return versions

def pinned_ref(workflow):
    path = os.path.join(EXT, ".github", "workflows", workflow)
    if not os.path.exists(path):
        return None
    m = re.search(r"^\s*DNSPY_REF:\s*(\S+)", open(path, encoding="utf-8").read(), re.M)
    return m.group(1) if m else None

def main():
    do_restore = "--restore" in sys.argv
    # When this repo is cloned standalone (not inside dnSpyEx/Extensions/), pass DNSPY_DIR.
    dnspy_dir = os.environ.get("DNSPY_DIR") or os.path.normpath(os.path.join(EXT, "..", ".."))
    dnspy_project = os.path.join(dnspy_dir, "dnSpy", "dnSpy")
    if not os.path.isdir(dnspy_project):
        fail(f"dnSpy app project not found at {dnspy_project}; set DNSPY_DIR=/path/to/dnSpyEx")
    # Standalone checkout: the extension builds inside the dnSpyEx tree, so use that copy's
    # restore graph (run-verify-local.sh rsyncs the sources there before building).
    ext_dir = os.environ.get("EXT_BUILD_DIR") or os.path.join(dnspy_dir, "Extensions", "dnSpy.Extension.MCP")
    if not os.path.isdir(ext_dir):
        fail(f"extension build copy not found at {ext_dir}; set EXT_BUILD_DIR")

    if do_restore:
        for proj in (dnspy_project, EXT):
            r = subprocess.run(["dotnet", "restore", proj, "--nologo", "-v", "q"])
            if r.returncode != 0:
                fail(f"dotnet restore failed for {proj}")

    build_ref, release_ref = pinned_ref("build.yml"), pinned_ref("release.yml")
    if build_ref and release_ref and build_ref != release_ref:
        fail(f"DNSPY_REF mismatch between workflows: build.yml={build_ref} release.yml={release_ref}")
    pinned = build_ref or release_ref

    # Warn when the local dnSpyEx checkout tag drifts from the pin (CI's checkout IS the pin).
    tag = subprocess.run(["git", "describe", "--tags", "--exact-match"],
                         cwd=dnspy_dir, capture_output=True, text=True).stdout.strip() or None
    if pinned and tag and tag != pinned:
        print(f"WARN: local dnSpyEx checkout is {tag} but DNSPY_REF pins {pinned}; "
              "version comparison describes this checkout, not the pin.")

    dnspy = net48_versions(dnspy_project, "dnSpy app")
    ext = net48_versions(ext_dir, "extension")
    drift = False
    for pkg in GUARDED:
        want, got = dnspy.get(pkg), ext.get(pkg)
        if want and got and want != got:
            print(f"  {pkg}: dnSpy resolves {want}, extension pins {got}")
            drift = True
    if drift:
        fail("net48 dependency drift — dnSpy ships no binding redirects; fix the PackageReference")
    for pkg in GUARDED:
        print(f"  {pkg}: {ext.get(pkg)} (matches dnSpy {dnspy.get(pkg)})")
    print("net48 dependency guard PASSED")

if __name__ == "__main__":
    main()
