# Live edit guard regression harness

Run from the repository root:

```sh
python3 tests/edit/run_functional_guard_regressions.py
```

Requires .NET SDK 10 and the restored dnlib 4.5.0 package. Set
`DNSPY_GUARD_DNLIB` to an explicit local dnlib assembly if the package is elsewhere.
The runner creates and removes a temporary project; it never generates files in
production directories or modifies a dnSpy checkout.

The fingerprint tests compile the production `EditFingerprint.cs` directly and
construct dnlib modules. Attribute changes are also written and reloaded to prove
that the input changes real metadata. They check that the non-persistent conflict
guard detects payload changes while the frozen checkpoint fingerprint is unchanged.

The coordinator tests extract `ApplyTransactionToLive` and `ExpireLocked` verbatim
from current source on each run. Their surrounding dispatcher, registry, and
coordinator containers are substitutes: the dispatcher injects a queued external
edit, and the registry counts forward/inverse calls. Cases cover conflict/debug/
cancellation rejection without writes, successful application, post-write failure
recovery, and timeout before versus after live linearization.

These tests exercise production decision logic but do not load WPF, the checkpoint
store, or an MCP listener. They supplement the required Windows end-to-end and
cross-plan acceptance tests; they do not establish that those tests passed.
