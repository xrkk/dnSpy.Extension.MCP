# 私有 dnlib 依赖（4.5.0-r15.private.1）

按 T095-C 批准（2026-09-28，PLAN/2026.09.28/2026.09.28-05 候选 + 同日批准回流记录）钉住的
dnlib 私有发行。它只在 v3 检查点编码（`dnspy.edit.checkpoints.v3`）所需的最小 writer 扩展上
偏离官方源码；不启用其新选项时行为与上游一致。

## 身份与完整性

| 项 | 值 |
| --- | --- |
| 官方源 commit | `c78d296c522aae0520df2afd825d48266321cf36`（tarball SHA256 `c904aa96e920bd84ae401e1cb2ea617c131fc51f91f740dd2eed915bdf5e3ee1`） |
| 官方签名键 | 仓库内 `dnlib.snk`，SHA256 `32cee4ede450a63c3a18a9ecacc51131199b3a65aac2d9376ff17653834018ac` |
| 私有 patch | `dnlib-c78d296-r15-private.patch`（SHA256 `28a2f6bac2596912495a7a1a5cf34f1cd215cba6bb425b9b6c86d71bad790b98`，13,109 字节）：五表 writer-local `ReferenceSourceRows` 前缀、完整 MethodDef RID 映射、双 TFM 构建适配 |
| 本包 | `packages/dnlib.4.5.0-r15.private.1.nupkg`（SHA256 `174095c10dc92eceb3704635b18a656d6face014b6c1bd655ab7ef0891ae5d44`，862,511 字节） |
| 程序集身份 | net48 与 net10.0 均为 `dnlib, Version=4.5.0.0, Culture=neutral, PublicKeyToken=50e96378b6e77999` |
| 许可 | dnlib 为 MIT；本 patch 以同等许可随仓库分发 |

**身份事实（部署必须遵守）**：dnlib 的签名密钥在上游仓库公开，本 fork 沿用官方强名称身份。
同身份意味着无法与官方 dnlib 4.5.0.0 并存，绑定层视为同一程序集；**验收与部署必须按文件
SHA256 钉住实际二进制**（本目录 nupkg 及其解包 DLL 的 SHA），不得以强名称或包名宣称身份。
版本号 `4.5.0-r15.private.1` 是 4.5.0 的 prerelease，语义版本低于官方 4.5.0——构建必须以
`-p:DnlibVersion=4.5.0-r15.private.1` 全局属性显式覆盖（`tests/run-verify-local.sh` 已集成），
否则 NuGet 统一会回落到官方 4.5.0 并在编译期失败。

## 构建/复建

```bash
# 从官方 tarball + 本 patch 独立重建（产出与本包方法集/IL/强名称全等）：
tar -xzf dnlib-upstream-c78d296.tar.gz && cd dnlib-c78d296*/ && git apply ../dnlib-c78d296-r15-private.patch
dotnet restore src/dnlib.csproj -p:RestorePackagesPath=<isolated> -p:BaseIntermediateOutputPath=<isolated>
dotnet build src/dnlib.csproj -c Release -f net48    --no-restore ...
dotnet build src/dnlib.csproj -c Release -f net10.0  --no-restore ...
```

完整可复现交接（固定输入、命令、派生样本与已知坑——含"带 PDB 比较 IL 会得假差异"）见
`.tmp/dnspy-sol-remaining-20260922-01/T095-R15-work/manifest/REBUILD.md`（协作证据目录）。
对照证据：主验收记录 `PLAN/2026.09.28/2026.09.28-02-*`（独立源码重建 IL 全等）与
`2026.09.28-06-*`（真实 Windows x64/x86 公开链验证）。

## 维护与回退

- 上游升级时按 patch 重放；patch 面仅 writer 选项与构建适配，预期冲突面小。
- 若放弃本 fork，v3 检查点编码必须同时撤销（两者互为前提）；回退属规范变更，须按 PLAN-CHANGE
  流程另行裁决，不得静默降级。
