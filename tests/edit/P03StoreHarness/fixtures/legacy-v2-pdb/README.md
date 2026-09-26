# T076 old PDB v2 regression fixture

`a5-checkpoints.zip` is an unchanged copy of the T057 historical net48 public package (`SHA256 04bb8130101903757972ee50761fe2011bd5ddd41af6b1ee88da5af915cb6ede`). Its manifest format is `dnspy.edit.checkpoints.v2`; its original graph has a root, resource child, combined child, and PDB method child.

`legacy-head.dll` is the old producer's public `edit_export` of method checkpoint `checkpoint-66fbfb78816b8ebc971a5f3b029c14fe` (`SHA256 ca132ac150fcb0a0977fbc058d7d1c61f6d872ff962ba25b443ef3fd891da086`). It is an exported live input, not a rewritten copy of the package. The checkpoint's recorded image SHA is identical. The original T057 package remains in its task work directory.

`legacy-image-mismatch.dll` (`SHA256 1b78bd779b3ea33b699d50f9541970b084481d1307dc02fc12ab7ebc3b76eb27`) is a derived negative input. `generator/GenVariant.csproj` uses the already pinned dnlib 4.5.0 to set the old image's entry point to `TestIL.Simple.AddOne`, then writes an embedded portable PDB with preserved RIDs and deterministic PDB options. This retains the v2 strong semantic digest and MVID while changing the complete image; the harness verifies both claims. Regenerate with `dotnet run --project generator/GenVariant.csproj -- legacy-head.dll legacy-image-mismatch.dll` from this directory.

Run `P03StoreHarness.exe legacy-head.dll --pdb-compat` with these files in one fixture directory. The test checks exact ancestor image, PDB document cleanup, compensation on the same loaded module, image-only guard input, and unchanged package bytes. `tests/edit/p03_vm_t076_legacy.py` drives the public coordinator migration contract.
