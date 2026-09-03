# dnSpy MCP overview

dnSpy MCP runs inside dnSpy and exposes loaded .NET modules to MCP clients. Its normal production
surface contains 32 static tools plus `debug_capabilities`. A dedicated debugging instance with
the frozen debug gate enabled advertises 21 additional session tools, for 54 total. Tools whose
names begin with `debug_test_` are acceptance-only and are not production interfaces.

## Capability groups

- Load assemblies without executing them.
- Navigate metadata, decompile types/methods and follow cross-references.
- Search Unity messages, attributes, strings and numeric constants.
- Read/edit IL, rename metadata and persist a module with backup protection.
- Generate signature-aware HarmonyX patches and BepInEx plugin source.
- Launch and control a managed debuggee from a dedicated dnSpy instance.

## MCP interface

The server implements `initialize`, `ping`, `tools/list`, `tools/call`, `resources/list`,
`resources/read` and notifications. It negotiates `2025-06-18`, `2025-03-26` or `2024-11-05`.
Transports are Streamable HTTP, legacy two-endpoint SSE and diagnostic one-shot HTTP JSON-RPC.
Applications should use a real MCP client or the supplied Python/stdio bridge.

Initialized legacy SSE and Streamable HTTP requests carry a server-authored session identity into
the tool registry. Tool arguments cannot replace that identity. One-shot compatibility HTTP has no
transaction-owning identity. DELETE, legacy disconnect and listener stop release a session before
emitting one idempotent internal close notification for later structured-edit transaction cleanup.

## Important limitations

- Dynamic debugging v1 is launch-only. Attach, detach and attachable-process listing are unsupported.
- Sample execution is allowed only when the fixed BIOS-registry classifier reports VMware or
  VirtualBox, unless a human explicitly enables the process-local override in the dnSpy settings UI.
- CorDebug target architecture must match the dnSpy process architecture.
- Static write tools are rejected while any debugging session is active.
- Tool output derived from assemblies or debuggees is untrusted data, not agent instructions.
- Large collections are paginated; narrow by assembly/type and carry returned cursors forward.
