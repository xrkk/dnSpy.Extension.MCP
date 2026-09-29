# 最终统一测试清单（P09 汇总·独立核验 CHK-005 整改重建版）

- 重建时间：2026-09-29（独立核验 `PLAN/2026.09.28/2026.09.29-01-独立核验-dnSpy-MCP结构化程序集编辑.md` CHK-005 整改：修复 `p09_final_regression.py build_manifest()` 键映射——summary 实际键为 `case_id`；本清单主表数据取自终验证据 zip 真实 summary，非占位）
- 终验 run-id 前缀：`p09-final-20260929-010430`（证据 `.tmp/dnspy-sol-remaining-20260922-01/T095-P09-final/evidence/p09-final-evidence.zip`，SHA256 前 16 `98c56876…`，53 成员；运行日期 2026-09-29）
- formal pair：插件 `ce13450a…`（net48，与 `dist/dnSpy.Extension.MCP-net48.x.dll` 字节一致）+ 私有 dnlib `ca4b55b7…`；dnSpyEx 官方最新稳定 `v6.6.0` == VM 安装（2026-09-29 复核仍为 latest）
- 工具计数事实来源：tools/list 实测（`p09-tool-registry-snapshot.json`：78 通告 = static 32 + debug 28 + edit 18；另有 9 个 `edit_test_*` 测试缝不通告）
- 既有套件复跑（ACC-028 面，formal pair，2026-09-29 本轮补跑）：见 §2

## 1. P09 终验 13 案例 × 双架构（26/26 pass）

| 案例 | 阶段 | x64 | x86 | 证据 run-id |
| --- | --- | --- | --- | --- |
| EDIT-ACC-004 | P03/P04 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-005 | P05/P06 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-031 | P06 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-006 | P07 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-015 | P07 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-032 | P07 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-007 | P08 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-008 | P08 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-016 | P08 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-033 | P08 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-018 | P09 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-021 | P09 | pass | pass | p09-final-20260929-010430-x64/x86 |
| EDIT-ACC-023 | P09 | pass | pass | p09-final-20260929-010430-x64/x86 |

ACC-018 为真实 UIA Explorer 断言（36+ 检查点树/属主取消/无旁路控件），非截图代替；EDIT-ACC-016 明示 "deferred negative boundary; positive branch unfinished (S02 user-approved deferral)"。

## 2. 既有套件复跑（ACC-028 面，formal pair 补跑，2026-09-29）

| 套件 | 结果 | 证据 |
| --- | --- | --- |
| P01 VM 套件（EDIT-ACC-017/027/030 双架构） | **pass 6/6** | run_id `20260929-073304/073525/073531`（x64）、`20260929-073819/074119/074126`（x86）；VM 摘要 `C:\dnspy-mcp-artifacts\edit-tests\<run_id>\summary.json`；宿主汇总 `tests/edit/p01-vm-summary.json` |
| 既有静态套件（tests\fixtures\run-tests.ps1，双架构） | **pass 2/2** | VM state `regression-fixtures-x64-b5de3fdd` / `regression-fixtures-x86-c72850ae`（result.json exit=0） |
| 既有调试套件（tests\debug\run-debug-tests.ps1 ACC-001..036） | **pass 36/36**（002/003/023 为整改后定向重跑：vm_ip 同步 .240、补 Editing/Contracts 与 Debugger 源、urlacl .240:15100） | 结果树 `tests\debug\results\1572b4dc…`（33 案例）+ `e35c7d0f…`（ACC-002）+ `e883ca42…`（ACC-003/023） |
| live 调试输出契约（tests.python.test_live_debug_output_contract，VM 实例 + AccFixture 调试目标） | **15 pass + 1 skip**（skip=expand fixture 未配置，设计内门控） | 2026-09-29 运行于 loopback 实例（settings `live-debug-contract3`，运行后已清理）；22 调试工具输出对 published schema 逐项校验 |
| P02 VM 套件（事务族含故障矩阵） | **fail（阻断）** | x64 operations 阶段 `EDIT_VALIDATION_FAILED/live_private_fingerprint`（row 3 `Microsoft.CodeAnalysis.EmbeddedAttribute`）；产品守卫按契约拒绝、零副作用。根因线索：P02DynamicFixture（2026-09-04 构建）与 fault-golden 基线（f1ca384，2026-09-14）均早于私有 dnlib 部署（fa43769，2026-09-28）；P02 测试资产需对私有 dnlib 再基线后重跑 |

## 3. ACC 汇总（30 行）

| ACC | 归属 | 案例/架构 | 结果 | 证据 run-id | 日期 |
| --- | --- | --- | --- | --- | --- |
| ACC-001 | P02 | P02 套件（x64+x86） | pass(历史)；本轮 formal pair 复跑阻断（§2 P02 行） | P02 阶段 run-id（2026-09-13 复跑全绿） | 历史 |
| ACC-002 | P01/P02 | P01+P02 套件、监听器族 | pass(历史)；本轮复跑阻断（同上） | 同上 | 历史 |
| ACC-003 | P02 | P02 套件（全成员编辑） | pass(历史)；本轮复跑阻断（同上） | 同上 | 历史 |
| ACC-004 | P03/P04 | EDIT-ACC-004 ×2 | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-005 | P05/P06 | EDIT-ACC-005/031 ×2 | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-006 | P07 | EDIT-ACC-006/032 ×2 | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-007 | P08 | EDIT-ACC-007/033 ×2 + T091/T094 + 四类 df90767 | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-008 | P08 | EDIT-ACC-008 ×2 | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-009 | P02 | P02 故障矩阵 | pass(历史)；本轮复跑阻断（§2 P02 行） | P02 阶段 run-id | 历史 |
| ACC-010 | P02 | P02 过期审查 | pass(历史)；本轮复跑阻断（同上） | 同上 | 历史 |
| ACC-011 | P03 | 检查点 Undo/Redo | pass(历史)（T095-E 部分复盖） | P03 阶段 run-id | 历史 |
| ACC-012 | P03 | 去重/分支 | pass(历史) | 同上 | 历史 |
| ACC-013 | P03 | 三态重放（v1/v2/v3） | pass(历史)（v3 闭合实测 T095-E） | 同上 + T095-E 证据 | 历史 |
| ACC-014 | P03 | 导出原子性 | pass(历史) | 同上 | 历史 |
| ACC-015 | P07 | EDIT-ACC-015 ×2 | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-016 | P08 | EDIT-ACC-016 ×2 | pass（负边界；**正向分支 S02 用户暂缓**，t083 明示） | p09-final-20260929-010430 | 2026-09-29 |
| ACC-017 | P01 | EDIT-ACC-017 ×2（VM 门禁矩阵+UI 覆盖） | pass | 20260929-073304 / 20260929-073819 | 2026-09-29 |
| ACC-018 | P09 | EDIT-ACC-018 ×2（真实 UIA） | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-019 | P03 | 会话重连/部分提交 | pass(历史) | P03 阶段 run-id | 历史 |
| ACC-020 | P03 | 旧工具迁移 | pass(历史)（契约门禁持续全绿） | 同上 | 历史 |
| ACC-021 | P09 | EDIT-ACC-021 ×2（schema 全景） | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-022 | P09 | 双架构终核+文档+注册表 | pass | p09-final-20260929-010430（78 工具 wire 快照）+ v6.6.0=latest（9-29 复核） | 2026-09-29 |
| ACC-023 | P09 | EDIT-ACC-023 ×2（静态零执行） | pass | p09-final-20260929-010430 | 2026-09-29 |
| ACC-024 | P03 | 部分提交恢复 | pass(历史) | P03 阶段 run-id | 历史 |
| ACC-025 | P03 | 谱系偏离接纳 | pass(历史) | 同上 | 历史 |
| ACC-026 | P02 | NetModule 拒绝诊断（S03 回流） | pass(历史)；本轮 P02 复跑阻断（§2 P02 行） | S03 回流 2026.09.26-01 | 2026-09-26 |
| ACC-027 | P01 | EDIT-ACC-027 ×2（dump 矩阵） | pass | 20260929-073525 / 20260929-074119 | 2026-09-29 |
| ACC-028 | P09 | 既有静态/调试/传输/配置/Python 全回归 | **部分完成**：静态 2/2、调试 36/36、P01 族 6/6、live 契约 15+1skip、Python 78/78（9-28）**通过**；P02 事务族 formal pair 复跑 **阻断**（§2） | 见 §2 各行 | 2026-09-29 |
| ACC-029 | P03 | 稳定态磁盘占用 | pass(历史) | P03 阶段 run-id | 历史 |
| ACC-030 | P01 | EDIT-ACC-030 ×2（三入口门禁矩阵） | pass | 20260929-073531 / 20260929-074126 | 2026-09-29 |

## 4. MCP 资源面（CON-027）

- `resources/list` = 14（6 个 bepinex://docs + 8 个 dnspy://docs），`resources/read` 14/14 可读：2026-09-28 live MCP 契约 5/5（`2026.09.28-09` A4，Python 客户端宿主）。
- 资源操作面双架构公开执行：T091/T094（`2026.09.28-12`，含四类标准值）。

## 5. 诚实边界

- **ACC-016 正向分支（去强名称动态授权）按 `S02-STRONGNAME-DEFER-01` 用户暂缓**：恢复条件为同次完整请求身份/所选文件/验签终因、抗伪造来源证明、新实现及正负回归与重新裁决（`PLAN/2026.09.27/2026.09.27-01`）；26/26 不含正向分支。
- **S06（non-NTFS）、S07（ZCode 客户端）维持用户暂缓**（恢复条件见任务清单 §9）。
- **ACC-028 部分完成**：P02 事务族在私有 dnlib formal pair 上复跑阻断于 `live_private_fingerprint`（fixture 2026-09-04 / fault-golden 2026-09-14 均早于私有 dnlib 2026-09-28）；产品守卫按契约拒绝且零副作用；P02 测试资产再基线后须重跑并把本行更新为最终判定。
- **net10 host 重建（18 号 §4.1 项 1）未执行**：19 号 §3 "net10 host 已重建（S10，0 错误）"无执行痕迹，经独立核验 CHK-006 指出后撤回；该项维持 13 号 §5 开放状态。
- ACC-011/012/013/014/019/020/024/025/029 为历史证据行（本轮终验与补跑未逐项重跑），标注 pass(历史)。
- live 调试输出契约的 expand 子项（T038 两级展开）因 expand fixture 未配置按设计跳过（1 skip）。
- 本清单由独立核验 CHK-005 整改重建；`p09_final_regression.py` 的清单生成器键映射缺陷（`case` vs `case_id`）已同步修复。
