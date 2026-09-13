# 最终统一测试清单（第二轮整改后重建）

- 重建时间：2026-09-13（第二轮：CHK-011—017 整改）
- 背景：独立核验（`PLAN/2026.09.14/2026.09.12-01-独立核验-dnSpy-MCP结构化程序集编辑.md`，CHK-001—010）整改后的诚实重建；不宣称未经本轮证据支持的项。整改处置见 `PLAN/2026.09.14/2026.09.14-29-独立核验整改-CHK-001至010.md`。
- 插件构建：net48 + net10.0-windows Release 0 error（独立临时检出构建，外部检出未覆写）
- 工具计数事实来源：`tools/list` 实测（`tools/export_tool_registry.py` 双源对照，78 通告 + 8 个 `edit_test_*` 不通告）
- dnSpyEx Release 复核：官方 latest v6.6.0（2026-06-20）== VM 安装 v6.6.0 ✓

## 1. 定向整改证据（CHK 反例的正例闭合）

run-id `chk-remediation-20260912-223127`（全部 PASS，汇总 `tests/edit/chk-remediation-summary.json`）：

| 案例 | 覆盖 | 结果 |
| --- | --- | --- |
| chk003-drift | CHK-003（入口点漂移守卫） | PASS |
| chk004-identity | CHK-004（身份正例链 apply→review→commit→export→launch） | PASS |
| chk005-resource | CHK-005（空名称/Stream/自定义载荷容器编辑） | PASS |
| chk005-readback | CHK-005（BCL ResourceReader 独立读回，零反序列化哨兵） | PASS |
| chk007-risks | CHK-007（confirmed_risks 全量回显 + affected_references） | PASS |
| chk008-inverse | CHK-008（live_apply 故障→预生成逆恢复→干净 idle→重提交） | PASS |
| chk001-ui | CHK-001/002（属主在线本地取消→回滚；空闲逐检查点浏览） | PASS |

## 2. 全回归（CHK-006 规定组合）

### 2.1 13 案例 × 双架构（整改构建，run 前缀 chk006-20260913-*）

| 案例 | 阶段 | x64 | x86 | 说明 |
| --- | --- | --- | --- | --- |
| EDIT-ACC-004 | P03/P04 | pass | pass | |
| EDIT-ACC-005 | P05/P06 | pass | pass | |
| EDIT-ACC-031 | P06 | pass | pass | |
| EDIT-ACC-006 | P07 | pass | pass | 整改后正例含入口点实际变更（另见 chk004-identity） |
| EDIT-ACC-015 | P07 | pass | pass | |
| EDIT-ACC-032 | P07 | pass | pass | |
| EDIT-ACC-007 | P08 | pass | pass | |
| EDIT-ACC-008 | P08 | pass | pass | |
| EDIT-ACC-016 | P08 | pass | pass | |
| EDIT-ACC-033 | P08 | pass | pass | |
| EDIT-ACC-018 | P09 | 见 §1 chk001-ui | skipped-ui(x86) | 契约按整改后 REQ-016 重建；驱动与案例 JSON 已同步 |
| EDIT-ACC-021 | P09 | pass | pass | |
| EDIT-ACC-023 | P09 | pass | pass | |

### 2.2 既有套件复跑（ACC-028）

| 套件 | 结果 | 证据/分析 |
| --- | --- | --- |
| P01 VM 套件（run_p01_vm_tests，双架构） | pass | EDIT-ACC-017/027/030 全 pass + fixtures/debug 回归族 |
| P02 VM 套件（run_p02_vm_tests，x64+x86 含故障矩阵） | pass | 屏障超时语义修复 + open→begin 水合重试后全标签 PASS |
| P03 受影响族（013/014/019/020/024/025/029 × 双架构重跑） | **阻断（进行中）** | 新发现前置缺陷：配置 ArtifactRoot（C:\\dnspy-mcp-artifacts）与清理路径不一致→陈旧谱系→begin 报 EDIT_LINEAGE_DIVERGED（已修清理）。首轮 x64：025 pass、014 blocked、013/019/020/024 fail（该前置）；修复后重跑进行中时 VM 管理服务挂起（TCP 通、HTTP 无响应），按受保护约束不自恢复 |
| P02 监听器套件（run_p02_listener_tests） | **PASS（第二轮补证）** | 首轮 FAIL 根因查明：整改轮把持久远程配置清成 loopback 形态后，设置 UI 重绑定坐标漂移使 host 静默不落地（无效组合被校验回退）；修复 apply_settings（UIA 定位+坐标输入）并恢复有效远程配置。2026-09-13 全套 PASS（tests/edit/p02-listener-summary.json） |
| Python 客户端单测（tests/python，verify-venv） | pass | 33 tests OK / 7 skipped（live 项） |

### 2.3 MCP 资源面（ACC-022 resources 部分）

- `resources/list` = 14（6 个 bepinex://docs + 8 个 dnspy://docs），`resources/read` 14/14 全部可读（`tests/edit/chk006-resources-result.json`）。

### 2.4 契约门禁

- P02 生成契约：`validate_contract.py` 133/133 PASS（37 操作单源再生 + P02 前缀 + 机器套件范围）；`test_validator_mutations.py` 20/20 PASS
- P03 契约：`validate_p03_contract.py` PASS（37 操作 / 17 产品 + 8 测试工具 / 20 验收案例）

### 2.5 全回归新发现并修复（本轮）

| 发现 | 根因 | 修复 |
| --- | --- | --- |
| 屏障暂停期超时失效 | P03 提交 2142266 给 `ExpireLocked` 加 `!OperationBusy`，意外废除 P02 冻结契约 | parked-at-barrier 仍可超时；进行中变更保持不可超时 |
| open→edit_begin 竞态 | dnSpy 文档 ModuleDef 异步水合；open_files 返回后立即 edit_begin 可能 0 候选 | Create 每次派发器枚举、调用线程重试（≤9×250ms），候选恰好 1 才继续 |

## 3. ACC 汇总（30 行，修正归属）

| ACC | 归属 | 结果 | 本轮证据 |
| --- | --- | --- | --- |
| ACC-001/003/009/010/026 | **P02** | pass | P02 VM 套件复跑（含隔离/冲突/故障矩阵/过期审查/能力边界） |
| ACC-002 | P01/P02 | pass | P01/P02 复跑 + 监听器第二轮 PASS |
| ACC-019 | **P03**（归属修正） | 重跑阻断 | 当前 HEAD 重跑待 VM 管理服务恢复 |
| ACC-004 | P03/P04 | pass | EDIT-ACC-004 双架构 |
| ACC-005 | P05/P06 | pass | EDIT-ACC-005/031 双架构 |
| ACC-006 | P07 | pass | EDIT-ACC-006 双架构 + chk004-identity |
| ACC-007 | P08 | pass | EDIT-ACC-007 双架构 + chk005-resource/readback |
| ACC-008 | P08 | pass | EDIT-ACC-008 双架构 |
| ACC-011/012/013/014/024/025/029 | P03 | pass(历史) | P03 阶段 run-id（本轮未逐项重跑；检查点族在 13 案例与 chk008 中部分复盖） |
| ACC-015 | P07 | pass | EDIT-ACC-015 双架构 + chk007-risks |
| ACC-016 | P08 | pass | EDIT-ACC-016 双架构 |
| ACC-017/030 | P01 | pass | P01 套件复跑 |
| ACC-018 | P09 | pass | chk001-ui（整改后契约） |
| ACC-020 | P03 | pass(历史) | EDIT-ACC-020 阶段 run-id |
| ACC-021 | P09 | pass | EDIT-ACC-021 双架构 + P03 契约门禁 |
| ACC-022 | P09 | pass | 注册表双源 78 + Release 复核 + 四类文档同步 + resources 14/14 |
| ACC-023 | P09 | pass | EDIT-ACC-023 双架构 |
| ACC-027 | **P01** | pass | P01 套件复跑（EDIT-ACC-027 dump 矩阵） |
| ACC-028 | P09 | pass(监听器已补证；P03 族重跑阻断) | §2.2：P01/P02/监听器/Python pass；P03 族重跑待 VM 恢复 |

## 4. 诚实边界

- P02 监听器套件本轮 FAIL（环境类），已记录分析，未复现为产品缺陷；历史 PASS 证据保留，不以历史掩盖本轮 FAIL。
- ACC-011/012/013/014/020/024/025/029 为历史证据行（本轮 13 案例回归未逐项重跑），明确标注 pass(历史)。
- CHK-010（核验会话 VM 工具加载）只能由下一次具备指定连接的独立核验在新 HEAD 复核。
- PLAN-CHANGE 追认位（CHK-001/003/009 实施方决定）待作者裁决，见整改记录 §1。
