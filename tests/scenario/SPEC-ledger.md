# 情景执行账本规范（SPEC-ledger, 冻结）

- 版本: v1.1（P02 ACC-041 交付物, 经子方案 v1 审核闭环冻结; v1.1 增补: §5 ArtifactRoot 清单对产品管理区的例外, 依据 P03 核验记录 20 §4.5 与 reset.py 实现, P04 AUD-501 收口）
- schema 标识: `dnspy.scenario.ledger.v1`（实现: `tests/scenario/dnspy_scenario/ledger.py`; 机读 schema 语义内嵌于行级校验 `validate_row`, 本文档为其人读权威）
- 变更约束: 冻结后字段变更需走总纲重裁决记录, 不允许静默扩展

### v1.1 变更记录

| 版本 | 变更 |
| --- | --- |
| v1.1 | §5 ArtifactRoot 清单核对: `edit-checkpoints/` 前缀为产品管理区（commit 持久化并可能改写检查点文件）, 复位例程允许该区文件改动/更替并记录计数; 直属 ArtifactRoot 的测试产物仍不可变（改动/删除 → BLOCKED）。与 reset.py 实现对齐, 消除规范-实现漂移（P04 AUD-501）。 |

## 1. 文件与编码

- 每次情景执行一本 JSONL 账本（UTF-8, 每行一个 JSON 对象, 追加写, 不改写不截断）。
- 文件命名: `<batch>/<scenario_id>.jsonl`（批次目录由执行驱动建立）。
- 每行共同字段: `schema_version`、`kind`、`scenario_id`、`batch`、`step_seq`（≥0 整数）、`timestamp`（ISO8601 毫秒, 写入时自动补）。

## 2. 行类型

### meta（每情景首行, step_seq=0）
`declared_tools`: 有序接口序列（与用例 `DECLARED_TOOLS` 常量一致）; `tfm`: `net48|net10`; `docstring`: 用例 docstring。

### call（每次 MCP 工具调用一行）
- `tool`、`request`（参数对象）、`duration_ms`。
- `response_summary`: `{"is_error": bool, "error_code": str|null, "content_head": ≤200 字符摘要}`——响应全文不入账本（服务端 tool_result 上限 8MiB; `Transport/TransportLimits.cs`）; 断言在情景内即时判定。
- `outcome`: `ok` | `expected_error` | `unexpected_error` | `transport_error`。

### assert（每次断言一行）
- `step_seq`: 关联的 call 行步骤序。
- `grade`: `strong`（精确匹配/集合相等/计数, 仅结构化字段）| `weak`（无 isError + 结构/类型正确）。
- `verdict`: `pass|fail`; `expected_error`: 布尔（预期错误验证断言为 true 且 grade=strong）; `detail`: ≤200 字符。

### reset（标准复位例程每次检查一行）
- `check`: `edit_idle|debug_idle|session_closed|samples_hash|artifacts_inventory`。
- `verdict`: `pass|blocked`; `detail`: ≤200 字符。

### result（每情景终态行, finalizer 必然落账）
- `outcome`: `pass|fail|blocked`——判定语义 = F-03: **强断言全过且无未预期 error 才 pass**。
- `strong_asserts`/`weak_asserts`/`unexpected_errors`: 计数; `failure_class`（非 pass 必填）: `strong_assertion|unexpected_error|reset_blocked|env_blocked|collection_error`。
- RACC-002 三类处置映射: 规格缺陷 ← `strong_assertion`/`unexpected_error`（用例侧）; 产品缺陷 ← `unexpected_error`（产品侧）; 环境问题 ← `env_blocked`; 归 P04 处置循环判定。

## 3. 异常路径记账时序契约（冻结五点）

1. `expect_error(tool, code)` 上下文管理器**先于**调用进入登记; 调用时无预告而 isError → `unexpected_error`, 事后预告无效。
2. `error_code` 提取: `ToolCallError.text` → `json.loads` → `error.code`（回退顶层 `code`, 再回退 `null`）。
3. 传输层错误（协议/HTTP/连接）记 `transport_error`, 与工具级 isError 分离, 处置学 `env_blocked`。
4. 预告命中的验证断言 grade=strong（预期错误码匹配是关键数据传递点判定）。
5. result 行由 fixture teardown 的 finally 块必然写入（含未捕获异常/复位 BLOCKED 场景）。

## 4. 覆盖矩阵口径（F-01）

- 矩阵单元 = 工具 × 出现过该工具 call 行的**情景 ID 去重集合**。
- `batch` 前缀 `net10-sample` / `net10-rerun-` 的 call 行不进矩阵（抽样批与升级复跑不重复计入）。
- 聚合器（`aggregator.py`）逐行校验账本, 任何违规行使整体退出非 0。

## 5. 基线与清单文件（§4b 机制）

- 样本基线: 权威件 `E:\dnspy-scenario\evidence\samples-baseline.json`（P01 产出）; 仓库副本 `tests/scenario/baselines/samples-baseline.json`（P02 同步建立）。P03 按 ACC-047 增补后两处同步更新。复位例程对基线外新样本判 BLOCKED（解除责任在 P03 补基线）。
- ArtifactRoot 清单: `tests/scenario/baselines/artifacts-inventory.json`, 追加式（仅允许新增条目: path+sha256+首见情景）; **直属 ArtifactRoot 的已记录文件被改动/删除 → BLOCKED; `edit-checkpoints/` 前缀为产品管理区, 允许改写并记录（v1.1）**。
- 两文件缺失 → `env_blocked`, 不允许无基线运行。
