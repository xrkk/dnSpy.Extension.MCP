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
