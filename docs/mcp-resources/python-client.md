# Python client and stdio bridge

The `dnspy_mcp` package uses only the Python standard library and supports Python 3.10+. It handles
JSON-RPC serialization, MCP initialization, protocol negotiation, `Mcp-Session-Id`, notifications,
errors, pagination and DELETE cleanup. It ignores ambient HTTP proxies by default for reliable
loopback/LAN access.

Environment variables:

- `DNSPY_MCP_URL` — dnSpy endpoint, default `http://localhost:15378/`.
- `DNSPY_MCP_TOKEN` — optional raw bearer token; omit it for the default trusted
  `192.168.204.1/32` Host-Only peer mode.
- `DNSPY_MCP_TIMEOUT` — request timeout in seconds, default 40.

Use `DnSpyClient.connect()` for Python automation, `dnspy-mcp-client` for human diagnostics and
`python -m dnspy_mcp.stdio` (or `dnspy-mcp-stdio`) as the local stdio MCP command for an AI host.
The stdio bridge transparently forwards initialize, tools, resources and server instructions; it
does not duplicate schemas or documentation.

For a stable installation, create a dedicated virtual environment and run `pip install -e` against
the repository. Editable installation points imports at the working tree: ordinary `.py` edits take
effect in each newly started client/stdio process without reinstalling. An already running process
keeps its imported code. Changes to package metadata, console-script declarations, package layout or
the interpreter environment may require reinstalling or recreating the virtual environment.

## AI batch mode (token-efficient usage)

When an AI agent drives dnSpy through `tools/call` one request at a time, every intermediate
result (assembly lists, member tables, decompiler output) travels back through the model context.
For long chains this is expensive and slow: each round trip costs one tool call plus the full
response tokens, and the agent must re-read prior output to decide the next step.

**When to use batch mode:** chains of roughly 10 or more related calls — whole-type surveys
(info + methods + fields over many types), repeated regression sweeps (re-run the same navigation
after every edit), bulk search/xref sweeps, or any loop whose shape is known in advance. For 1-3
one-off lookups, direct `tools/call` remains the cheaper option.

**The pattern:** the AI writes one Python script that talks to the server directly through
`dnspy_mcp`, executes it once (for example via a shell tool), and reads only the distilled
result. Intermediate payloads stay inside the script process and never enter the model context.
This is the same "record a script, replay it, read the summary" shape the scenario corpus uses
for its debugging workflows; against a live server you only need the client, no recording
machinery.

Minimal example — summarize Unity `Awake` overloads per assembly in one pass:

```python
# pip install -e /path/to/dnSpy.Extension.MCP  (once per environment)
from dnspy_mcp import DnSpyClient

with DnSpyClient.connect() as client:
    rows = []
    for asm in client.call_tool_json("list_assemblies")["assemblies"]:
        name = asm["Name"]
        hits = client.call_tool_json("search_members", {
            "query": "Awake", "assembly_name": name, "names_only": True})
        rows.append(f"{name}: {hits['total_count']} Awake overloads")
    print("\n".join(rows))  # the only text the AI reads
```

**Cost comparison:** a 30-call chain via `tools/call` costs ~30 request/response round trips and
streams every intermediate JSON through the context (often tens of thousands of tokens for member
tables). The same chain as a script costs 1 tool call (the script body, written once) plus the
script's distilled stdout — intermediate responses are paid only in wall-clock time, not tokens.
Inside the script, still prefer the compact knobs (`names_only: true`, `compact: true`,
`max_lines`, `max_members`, and the combined `get_type_overview` / `debug_snapshot` tools) so the
final summary stays small too.
