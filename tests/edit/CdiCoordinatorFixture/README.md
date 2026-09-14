# CDI coordinator fixture

For an isolated dnSpy test copy only; never install the helper into a user profile.
`build.py` accepts the existing dnSpy build root, .NET Framework reference directory,
Roslyn `csc.dll`, and a new output directory. It produces `CdiHost.dll` plus its
portable PDB and `CdiCoordinatorFixture.x.dll`, with SHA-256 identities.

Copy the helper into the isolated app's `bin/Extensions` and the fixture into
`<run-root>/fixtures`. Set `DNMCP_TEST=1` and `DNMCP_CDI_FIXTURE_ROOT=<run-root>`
only for that app process. Configure its allowed-sample root as `fixtures` and
artifact root as the sibling `artifacts`, with a private loopback listener.
Start with a fresh run directory; preserve failed-run evidence separately.

The helper waits for the exact fixture path in the document tree. dnSpy normally
loads its module without PDB, so the helper independently reads the fixture PDB
and binds its hoisted scopes to the live method instruction indices. It also seeds
65 flags before the first edit transaction. It never rewrites coordinator baseline
fingerprints. A UI dispatcher timer applies explicit `flags`, `hoisted`, or `restore`
commands from the run directory and records actual product guard hashes. The timer
can model an external edit while commit is paused before dispatch.

Run `tests/edit/cdi_guard_coordinator_driver.py --url <loopback-mcp> --fixture
<run-root>/fixtures/CdiHost.dll --fixture-control <run-root> --case all` on the VM.
The driver uses a second HTTP connection with the same owned session for barriers,
so the commit request cannot block its own release. Checks cover both CDI shapes
at review, commit entry, and the second gate; idle Undo/Redo with restored-success
controls; and partial-commit retry/undo rejection followed by successful recovery.
Evidence includes exact checkpoint file hashes and raw MCP responses. These are
Codex takeover self-checks, not a substitute for independent plan review.

After the run, stop only the recorded isolated app PID and the temporary transfer
server. Preserve the fixture, logs, and manifests; do not modify the user's dnSpy.
