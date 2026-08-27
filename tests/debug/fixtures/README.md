# Dynamic launch fixtures (IMP-011)

Six launch fixtures form the ACC-029 product matrix; each must run in a dnSpy OS process of the
SAME bitness as the target/host (EVD-API-010: the CorDebug engine only debugs same-bitness
targets and requires switching between dnSpy/dnSpy-x86 otherwise):

| fixture | runtime | architecture | launch_mode | notes |
|---|---|---|---|---|
| net48-x86 | .NET Framework 4.8 | x86 | net48-exe | classic managed EXE |
| net48-x64 | .NET Framework 4.8 | x64 | net48-exe | |
| coreclr-apphost-x86 | CoreCLR | x86 | coreclr-apphost | apphost EXE |
| coreclr-apphost-x64 | CoreCLR | x64 | coreclr-apphost | |
| coreclr-dotnethost-x86 | CoreCLR | x86 | coreclr-dotnet | DLL + dotnet host (`exec`) |
| coreclr-dotnethost-x64 | CoreCLR | x64 | coreclr-dotnet | |

`build-launch-fixtures.ps1` generates all six from embedded project templates into
`tests/debug/fixtures/bin/` (deterministic; no network). The dotnet-host legs install an
isolated x86/x64 .NET 10 runtime via `dotnet-install.ps1 -Channel 10.0 -Quality GA
-Architecture <x86|x64>` and record the resolved version. The E2E driver
(`tests/debug/run-debug-tests.ps1`) consumes the fixture paths; x86 legs must drive an x86
dnSpy OS process, x64 legs an x64 one, and cross-bitness requests must be rejected before Start.

The fixture payloads exercise: launch lifecycle, all break kinds allowed per mode (and the
forbidden values), strong-identity breakpoints, enable/disable, the three step kinds, locals
(no-func-eval raw reads), module listing and dumps, plus the harness-mode contract (first
argument receives the absolute target path).
