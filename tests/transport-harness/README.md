# Streamable HTTP lifecycle regressions

Run `dotnet run --project tests/transport-harness/TransportHarness.csproj -c Release`.
The harness requires permission to open ephemeral loopback sockets. It links the production
HTTP server, protocol, transport lifecycle, limits, security and debug transport lease tracker.
Only dnSpy host services and MEF attributes are stubbed; edit/debug lease providers are simulated
for HTTP retention tests. The actual edit coordinator and Windows debugger require host validation.

A monotonic virtual clock makes 30-second handshake and 600-second inactivity boundaries deterministic.
The internal constructor uses a 50 ms heartbeat/sweep in this harness; production remains 15 seconds.
Raw TCP GET probes validate HTTP 200 before forcing RST, and wait for connection slots to release.

Covered: 48 abnormal GET disconnects without initialized/DELETE; full-capacity recovery and no early
reclaim; completed idle sessions and renewed activity; same-ID GET reconnect and connected-session
retention; unfinished-handshake GET abort; blocked tools and partial request bodies; edit/debug lease
retention and release; fail-closed lease checks; real debug lease association/reconnect/generation changes
and post-DELETE completion; concurrent repeated DELETE with observer failure; concurrent allocation
and reclaim; all three HTTP 429 capacity causes; listener stop/restart and single cleanup notification.

Before the fix, the minimal production-HTTP repro allocated 16 sessions, aged their creation timestamps
past the timeout and retried initialize: `initialize HTTP 429; expected 200` (exit 1).
After the fix the same allocation/timeout/admission pattern returns HTTP 200. Expanded tests exercise
actual TCP resets and assert authoritative close reasons, bounded counts and retention protections.

Validation on 2026-10-10: all 11 transport groups and the existing security harness passed.
The complete current extension source compiled for net48 and net10.0-windows using the retained
R15 host/reference DLLs. Both builds had zero errors and 20 existing warnings (including unavailable
NuGet vulnerability metadata). An isolated project included the existing XAML and resolved the
original project's stale `PLAN/2026.09.03` resource paths to `PLAN/2026.09/2026.09.03`; the unrelated
production project paths were left unchanged. Candidates and raw logs are retained at
`.tmp/streamable-session-reclamation/`. No deployed DLL was replaced and Windows dnSpy end-to-end
lease validation remains outstanding.
