# dnSpy MCP documentation index

This index is the authoritative starting point for an AI using dnSpy MCP. Read the documents
relevant to the task before invoking write or dynamic-debugging tools.

- `dnspy://docs/overview` — capabilities, tool counts, transports and limitations.
- `dnspy://docs/static-analysis` — loading, navigation, decompilation, xrefs and searches.
- `dnspy://docs/il-editing` — IL edits, metadata renaming, rollback and safe persistence.
- `dnspy://docs/dynamic-debugging` — dedicated-instance launch debugging and all debug tool families.
- `dnspy://docs/security` — remote access, bearer token lifecycle, CIDR and untrusted data.
- `dnspy://docs/python-client` — Python API, CLI and transparent stdio bridge.
- `dnspy://docs/tool-workflows` — recommended task-oriented tool sequences.

The six `bepinex://docs/*` resources are separate offline references for BepInEx and HarmonyX
plugin development. Use `resources/list` to discover their exact URIs.

Always inspect the live `tools/list` result for the exact input/output schemas available in the
running instance. Call `debug_capabilities` before any dynamic-debugging plan because enablement,
host architecture, security posture and limits are runtime-specific.
