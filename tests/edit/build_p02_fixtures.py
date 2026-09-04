#!/usr/bin/env python3
"""Build deterministic P02 fixtures without creating a standalone PDB."""

from __future__ import annotations

import argparse
import shutil
import struct
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "tests" / "edit" / "fixtures"


def run(*args: str, cwd: Path | None = None) -> None:
    subprocess.run(args, cwd=cwd or ROOT, check=True)


def clear_il_only(source: Path, destination: Path) -> None:
    data = bytearray(source.read_bytes())
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    directory = optional + (112 if magic == 0x20B else 96)
    cli_rva = struct.unpack_from("<I", data, directory + 14 * 8)[0]
    section_count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    sections = optional + optional_size
    cli_offset = None
    for index in range(section_count):
        row = sections + index * 40
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from("<IIII", data, row + 8)
        if virtual_address <= cli_rva < virtual_address + max(virtual_size, raw_size):
            cli_offset = raw_offset + cli_rva - virtual_address
            break
    if cli_offset is None:
        raise RuntimeError("CLI header RVA is not mapped")
    flags_offset = cli_offset + 16
    flags = struct.unpack_from("<I", data, flags_offset)[0]
    struct.pack_into("<I", data, flags_offset, flags & ~1)
    destination.write_bytes(data)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "tests" / "edit" / "fixtures" / "bin")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    for architecture in ("x64", "x86"):
        out = output / architecture
        obj = output / "obj" / architecture
        run("dotnet", "build", str(SOURCE / "P02DynamicFixture.csproj"), "-c", "Release",
            "-p:PlatformTarget=" + architecture, "-p:OutputPath=" + str(out),
            "-p:BaseIntermediateOutputPath=" + str(obj) + "/")
        run("dotnet", "build", str(SOURCE / "P02AttachmentFixture.csproj"), "-c", "Release",
            "-p:PlatformTarget=" + architecture, "-p:OutputPath=" + str(out),
            "-p:BaseIntermediateOutputPath=" + str(output / "obj" / "attachment" / architecture) + "/")
        exe = out / "P02DynamicFixture.exe"
        if not exe.is_file() or (out / "P02DynamicFixture.pdb").exists():
            raise RuntimeError("embedded-PDB fixture output is invalid")
        clear_il_only(exe, out / "P02MixedModeFixture.exe")
        run("dotnet", "run", "--project", str(SOURCE / "P02FixtureMutator.csproj"),
            "-c", "Release", "--", str(exe), str(out / "P02DuplicateParamFixture.exe"))

    dotnet_root = Path(shutil.which("dotnet") or "dotnet").resolve().parent
    sdk = sorted(dotnet_root.glob("sdk/*/Roslyn/bincore/csc.dll"))[-1]
    ref_root = sorted(dotnet_root.glob("packs/Microsoft.NETCore.App.Ref/*/ref/net*"))[-1]
    refs = ["/reference:" + str(path) for path in sorted(ref_root.glob("*.dll"))]
    module_source = output / "P02ModuleFixture.cs"
    module_source.write_text("public static class P02ModuleFixture { public static int Value = 7; }\n", encoding="utf-8")
    netmodule = output / "P02ModuleFixture.netmodule"
    run("dotnet", str(sdk), "/nologo", "/target:module", "/out:" + str(netmodule), *refs, str(module_source))
    manifest_source = output / "P02MultiFileFixture.cs"
    manifest_source.write_text("public static class P02MultiFileFixture { public static int MainValue = 1; }\n", encoding="utf-8")
    run("dotnet", str(sdk), "/nologo", "/target:library", "/out:" + str(output / "P02MultiFileFixture.dll"),
        "/addmodule:" + str(netmodule), *refs, str(manifest_source))
    module_source.unlink()
    manifest_source.unlink()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
