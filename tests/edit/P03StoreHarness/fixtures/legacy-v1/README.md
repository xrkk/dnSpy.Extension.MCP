# Legacy v1 checkpoint package fixtures

These are **real v1 packages produced by the pre-T003 product** (the historical
algorithm era). They are committed binaries because a v1 package can no longer
be produced by the current product: new lineages are always
`dnspy.edit.checkpoints.v2`. The formal probe asserts their recorded SHA-256, so
the fixtures cannot drift silently.

## Files

| File | SHA-256 | Role |
|---|---|---|
| `v1-fixed-package.dnspy-mcp-checkpoints` | `3db186f57b86b5b6f9f4774529333483853cadc5b080bff0e1dc811e57a9da26` | 3-node v1 lineage: baseline plus two `module_update` commits (`MethodOwnerProbeG1` → `MethodOwnerProbeG2`). Used for exact load, append, branch, navigation and export assertions. |
| `v1-swapped-baseline-package.dnspy-mcp-checkpoints` | `ac4d4b48a9014da768ebbb9e11bdcf7ddf0ab4acee54389fe85d3198ef5cc7e6` | Same lineage with `T.One`/`T.Two` bodies swapped in the baseline image. Historical semantics are unchanged (`ComputeRoundtrip` is blind to the swap) while the baseline image differs. Used for the v1 `unverified_drift` / migration-refusal / `accept_live` supersession assertions. |

The fixture module is deterministic in shape (`MethodOwnerProbeG.dll`, MVID
`11111111-2222-3333-4444-555555555555`, type `N.T` with `One()=>1` and
`Two()=>2`) but its lineage/checkpoint IDs, family ID and ZIP timestamps are
random, which is why the package bytes are committed rather than regenerated.

Recorded values:

- baseline historical semantic: `806506595d46ffcbb130cfec54c3f61b3533491adad79a8a3688f0e3a2884227`
- format: `dnspy.edit.checkpoints.v1`

## Regenerating

Regeneration needs the pre-T003 product (commit `c71d276106cd14340f7308870c78e10d4fdb68f4`
is the first commit whose product writes v2; the packages were generated with
the build of its parent `115b09a9ea856432eb2fb7ec24a2df9652261329`):

1. Build that product revision (`dnSpy.Extension.MCP.csproj`, `net10.0-windows`).
2. Point `generator/GenV1.csproj` `ProductDir` at that build directory.
3. `dotnet build generator/GenV1.csproj -c Release` then
   `dotnet bin/Release/net10.0/P03StoreHarness.dll <output-dir>`.

The generator writes `v1-fixed-package.zip`, `v1-swapped-baseline.zip` and
`v1-writer-image.zip` plus a `V1 baseline_semantic=` line. New bytes will differ
(random IDs/timestamps); the baseline semantic must stay
`806506595d46ffcbb130cfec54c3f61b3533491adad79a8a3688f0e3a2884227` because the
frozen historical algorithm must not change. Copy the two packages over the
committed fixtures only together with a new recorded SHA in the probe and this
table.

Re-generation evidence from T003-R03 (pre-T003 product build
`/tmp/dnspy-t003-r01-build`, net10 product DLL SHA-256
`869123f327b16457ada79251f03620a53ea0a709600c096dd348df5ba16dbe37`):
rebuilt packages produced the same baseline semantic
`806506595d46ffcbb130cfec54c3f61b3533491adad79a8a3688f0e3a2884227` with
different package SHAs (`ead8fedf…`, `801a1441…`).
