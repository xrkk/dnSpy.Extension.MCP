# P03 VM evidence: isolated entrypoint

`p03_vm_edit_acc_evidence.py` now has an explicit, fail-closed isolation mode
for its 16 integration drivers and four `P03StoreHarness` probes.  This is an
acceptance entrypoint, not a VM launcher: it never starts dnSpy, changes
APPDATA settings, stops a process, or deploys a host.

Before an actual VM run, an operator must provision one dedicated deployment,
MCP listener, copied fixture tree, harness directory, dotnet host, checkpoint
store, and artifact root below one newly-created isolation root.  Configure
that dedicated host's artifact/checkpoint setting before it starts so product
writes use the selected checkpoint/artifact roots.  Do not point this command
at `15378`, `C:\Tools`, a shared dnSpy deployment, or original fixtures.

Build or obtain fixtures before the run: `tests/fixtures/build-fixture.ps1`
is the TestIL build entrypoint.  The ImportHost, ResourceHost, StrongHost, and
P03 harness inputs must likewise be copied from their verified build outputs
into the dedicated fixture/harness trees; this runner intentionally does not
build, copy, or mutate those source artifacts.

## Commands

Use the same case selection for either architecture.  These examples show the
required explicit inputs; `EDIT-ACC-018` additionally needs the dedicated UI
deployment root.

```powershell
python .\tests\edit\p03_vm_edit_acc_evidence.py --case EDIT-ACC-004 --arch x64 --run-id t006-r03-x64 --isolation-root D:\T006\p03-x64 --mcp-url http://127.0.0.1:15400/mcp --fixture-root D:\T006\p03-x64\fixtures --artifact-root D:\T006\p03-x64\artifacts --checkpoint-store D:\T006\p03-x64\checkpoints --work-root D:\T006\p03-x64\work --harness-dir D:\T006\p03-x64\harness --dotnet-host D:\T006\p03-x64\dotnet\dotnet.exe
python .\tests\edit\p03_vm_edit_acc_evidence.py --case EDIT-ACC-004 --arch x86 --run-id t006-r03-x86 --isolation-root D:\T006\p03-x86 --mcp-url http://127.0.0.1:15401/mcp --fixture-root D:\T006\p03-x86\fixtures --artifact-root D:\T006\p03-x86\artifacts --checkpoint-store D:\T006\p03-x86\checkpoints --work-root D:\T006\p03-x86\work --harness-dir D:\T006\p03-x86\harness --dotnet-host D:\T006\p03-x86\dotnet\dotnet.exe
```

For ACC-018 append `--ui-deployment-root D:\T006\p03-x64\ui` (or its x86
peer).  It passes the selected URL, fixture, artifact package root, and an
output directory to the UI driver through its documented process environment;
it does not alter global user settings.

Use `--plan` before any VM invocation.  It imports no driver and performs no
RPC, file write, subprocess, or cleanup; it prints the selected URL, read
roots, write roots, harness command, and cleanup target for every case.

```powershell
python .\tests\edit\p03_vm_edit_acc_evidence.py --plan --case EDIT-ACC-004 --case EDIT-ACC-018 --case EDIT-ACC-029 --arch x64 --run-id t006-r03-plan --isolation-root D:\T006\p03-plan --mcp-url http://127.0.0.1:15400/mcp --fixture-root D:\T006\p03-plan\fixtures --artifact-root D:\T006\p03-plan\artifacts --checkpoint-store D:\T006\p03-plan\checkpoints --work-root D:\T006\p03-plan\work --harness-dir D:\T006\p03-plan\harness --dotnet-host D:\T006\p03-plan\dotnet\dotnet.exe --ui-deployment-root D:\T006\p03-plan\ui
```

Actual evidence lands only under
`<artifact-root>\edit-tests\<run-id>\<case>`, plus the selected checkpoint
and work roots.  Exit `0` means every requested case passed, `1` means a case
failed, and `2` means at least one requested case was blocked and none failed.  A
plan's `PLAN` result is never an acceptance pass.

After a dedicated host has been stopped by its owner, cleanup may remove only
the named run's `edit-tests\<run-id>` directory and that run's dedicated
checkpoint/work directories.  Never remove the isolation root wholesale,
shared artifact trees, deployment files, or original fixtures.

## T006 execution boundary

This document adapts only P03's 16 drivers plus ACC-029/031/032/033 harness
probes.  P01, P02, debug/fixture runners, and the missing ACC-011/012 entrance
remain outside this entrypoint.  T005 independent review and the recorded v1
tension also remain pending; a P03 plan or local fake test is not their
independent verification.

Main-session follow-up: paths must be absolute, must not contain parent
traversal, and existing links must resolve inside the selected root on the
execution platform. Run IDs are single path components; existing case evidence
is rejected. A mixed pass/blocked run returns 2, never success. ACC-023 binds
both its sentinel source and architecture-specific dynamic fixture to the
selected context. Local path tests do not prove that a remote dnSpy host has
been configured with the matching ArtifactRoot; verify that before execution.
