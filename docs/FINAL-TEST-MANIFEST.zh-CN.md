# 最终统一测试清单（P09 汇总）

- 生成时间：2026-09-12 18:49
- 工具计数事实来源：tools/list 实测 78 通告（export_tool_registry.py）
- 最终回归 run-id 前缀：p09-final-20260912-

| ACC | 阶段 | 状态 | 证据 |
| --- | --- | --- | --- |
| ACC-001 | P01 | pass | P01 阶段（2026.09.03 既有证据） |
| ACC-002 | P01/P02 | pass | P01 阶段（2026.09.03 既有证据） |
| ACC-003 | P02 | pass | P02 阶段（2026.09.03 既有证据） |
| ACC-017 | P01 | pass | P01 阶段传输契约族（2026.09.03 既有证据） |
| ACC-021 | P01/P09 | pass | P09 p09-final-r2 双架构 9/9 |
| ACC-030 | P01 | pass | P01 传输契约族（2026.09.03 既有证据） |
| ACC-004 | P03/P04 | pass | p09-final-r1/r2 x64+x86 |
| ACC-009 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-010 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-011 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-012 | P03 | pass | P03 阶段（2026.09.04 既有证据）+ ACC-029 |
| ACC-013 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-014 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-019 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-020 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-024 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-025 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-026 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-027 | P03 | pass | P03 阶段（2026.09.04 既有证据） |
| ACC-029 | P03 | pass | p03-evidence 既有 |
| ACC-005 | P05/P06 | pass | p09-final-r1/r2 x64+x86（终判全链） |
| ACC-031 | P06 | pass | p09-final-r1/r2 x64+x86 --import-matrix |
| ACC-006 | P07 | pass | p09-final-r1/r2 x64+x86 |
| ACC-015 | P07 | pass | p09-final-r1/r2 x64+x86 |
| ACC-032 | P07 | pass | p09-final-r1/r2 x64+x86 --identity-matrix |
| ACC-007 | P08 | pass | p09-final-r1/r2 x64+x86 |
| ACC-008 | P08 | pass | p09-final-r1/r2 x64+x86 |
| ACC-016 | P08 | pass | p09-final-r1/r2 x64+x86 |
| ACC-033 | P08 | pass | p09-final-r1/r2 x64+x86 --resource-matrix |
| ACC-018 | P09 | partial | p09-final-r4 x64（功能面 W1/W4/W6/W7 通过；文本面受 UIA 工具限制）；x86 计划性跳过 |
| ACC-023 | P09 | pass | p09-final-r2 x64 12/12 + p09-final-r5 x86 14/14（修复后双架构通过） |
| ACC-022 | P09 | pending | 最终回归中 P01/P02 既有套件复跑为总纲验收方发现的遗留（AUD-003） |
| ACC-028 | P09 | pending | 同上——需在最终组合补跑既有 runner |

## 最终回归覆盖说明

本轮最终回归（p09_final_regression.py）覆盖了 13 个 EDIT-ACC 案例（P03-P09 各阶段代表性验收）
在双架构上运行。P01 调试族/P02 事务族的既有回归套件与 14 resources 证据的完整复跑
被总纲验收方识别为遗留（AUD-003），其最后通过证据分别为 2026-09-03/2026-09-04。

## 无残留检查

- 终态 dnSpy 进程数=0；ArtifactRoot edit-checkpoints/edit-output 已清空。

## dnSpyEx Release 复核

- dnSpyEx GitHub latest-stable: v6.6.0 (2026-06-20)
- VM dnSpy: ProductVersion=v6.6.0, FileVersion=6.6.0.0
- 结论：完全一致，无需更新。
