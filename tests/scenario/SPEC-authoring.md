# 情景编写规范（SPEC-authoring, 冻结）

- 版本: v1（P02 交付, ACC-041/ACC-045 的编写侧依据; 对应 REQ-006 编写规范强制项）
- 适用: `tests/scenario/` 下全部情景用例（P03 语料必须遵守; P02 冒烟为首个范例）

## 强制项（违反 = 静态核对/评审不通过）

1. **接口序列声明**: 每情景模块级常量 `SCENARIO_ID: str`（`S-<族缩写>-<NN>`）与 `DECLARED_TOOLS: list[str]`（该情景按业务顺序调用的接口序列; 重复调用保留重复项以反映序列, 计数按不同名去重）。静态核对工具（`static_check.py`）据此检查每情景 ≥10 个**不同**接口（CON-001）。
2. **docstring**: 每个测试函数必须有 docstring（目标与步骤概要, REQ-007）。
3. **公共 helper 构造客户端**: 情景**只能**通过 `scenario_env.client`（conftest fixture 提供的 `RecordingClient`）发起 MCP 调用; 禁止自建 `DnSpyClient`、禁止使用 `dnspy-mcp-*` CLI 旁路。
4. **统一断言 API**: 只使用 `scenario_env.asserts`（`AssertionApi`）与 `client.expect_error`; 禁止裸 `assert`（不入账本的断言视为未发生）。
5. **禁止旁路录制代理**: 禁止直接操作 `client._ledger`/原始 `request*` 方法; 录制代理已覆盖 `call_tool`/`call_tool_json`/`request_object(tools/call)` 全部路径。
6. **复位段**: 情景体内完成业务后必须执行复位段——释放编辑租约（`edit_rollback`/`edit_recover`）、结束调试（`debug_terminate`）、撤销静态改写（`revert_method_il`）、关闭会话（`client.close()`）——然后才结束用例; 标准复位例程（teardown 五检查）兜底核对, 任一不过 → BLOCKED。

## 形态约定

- 情景 = pytest 用例, 直接编排调用序列与断言; 后一步输入取自前一步输出（REQ-001 数据链语义）。
- 强断言仅对结构化字段（名称集合、计数、token、错误码）, 不对反编译全文逐字节断言（F-03）。
- 预期错误路径必须用 `expect_error` 预告（先于调用）, 错误码精确匹配。
- 每情景至少 ≥10 个不同接口、其中关键数据传递点全为强断言（P03 语料层执行）。

## 示例（冒烟, P02）

见 `tests/scenario/smoke/test_smoke_chain.py`。
