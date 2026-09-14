# T004 real compiler and public contract regression

Run on Windows with the .NET 10 SDK and Windows Desktop runtime. The probe
uses WPF's real dispatcher path; Linux cannot substitute for this check.
Build the extension in the dnSpy v6.6.0 checkout first, then:

```powershell
dotnet build T004ImportProbe.csproj -c Release -p:ProductDir=C:/path/to/product/ -o C:/temp/t004-probe
dotnet C:/temp/t004-probe/P03StoreHarness.dll run C:/path/to/T004ImportProbe/Fixtures C:/path/to/product
```

`ProductDir` must end in a slash. Without an override it resolves to the
extension's `bin/Release/net10.0-windows` directory. The fixtures require the
SDK on PATH. Use separate copies of the fixtures when running concurrently.
Run the same compiled probe with the x64 and x86 .NET 10 hosts.

The `contracts` mode runs the dispatcher exception and public owner identity
regressions without compiling fixtures. The full `run` also validates every
emitted operation against the embedded public schema before applying it,
executes exported async/iterator methods, navigates checkpoint history, and
injects rejection after every operation prefix to check rollback state and
reference-map cleanup. A nonzero exit code is a failure.

These fixtures target net10.0. They do not establish net48 runtime coverage
or replace the real MCP coordinator acceptance.

For true CLR 4.8 coverage, build `../T004PackageProbe` against the net48
product and run `P03StoreHarness.exe <one-commit-package> <original-module>`
on Windows. It replays the package with a live corlib resolver, checks the
head image, and navigates back to the baseline. ProductDir must end in `/`.

`../t004_coordinator_driver.py` covers the real MCP coordinator. Use an
isolated dnSpy instance and a net48 fixture containing only
`namespace T004Plain { public class Simple { public static int Baseline() { return 1; } } }`.
Pass `--url`, `--fixture`, `--evidence` and `--runtime-host` (the matching
System32 or SysWOW64 Windows PowerShell executable). Put the repository's
Python client on PYTHONPATH. The driver compiles/imports async and iterator
methods, reviews/commits, exercises Undo/Redo and branch restore, exports,
checks SHA256 and executes the result on CLR 4.8.
