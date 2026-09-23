# 第三方 AI：P02 事务内核、私有副本与常用结构化编辑定向验收提示词

> 本文是 P02 阶段冻结回归入口；其中 5 个编辑工具、22 类操作和 59 工具总数只描述该阶段构建，不是当前发布注册表。当前发布面请以实时 `tools/list` 和完整验收提示词为准。

只有拿到该历史构建及其 fixture/hash、且获授权在隔离实例重放时，才把下列冻结计数当断言；在当前构建上应改按 18 个通告编辑工具、39 类操作及实际调试门 profile（生产 72/51、测试 78/57）核对，不以历史 59/22 判当前失败。当前发布回归需单列新增操作/工具，不能用旧 P02 全通过替代。

把本提示词交给同时连接 Win10VM 管理 MCP 与 dnSpy MCP 的第三方智能体。人工只准备
虚拟机、dnSpy/MCP 环境；部署、架构轮换、测试、证据和清理由 AI 完成。不得使用 curl、
手写 JSON-RPC 或读取源码结果冒充真实运行证据。

## 授权和边界

- 只验收 P02：单活动事务、权威所有权、私有副本、22 类结构化操作、统一审查、测试态
  实时应用/反向恢复、容量/并发/生命周期、动态验证和不支持目标拒绝。
- P02 没有产品 commit/export/checkpoint/Undo/Redo；不得把 test-only apply-and-restore 描述成
  产品提交。不得提前测试或声称 P03-P09 能力。
- `tests/edit/p02_vm_driver.py` 及历史宿主编排须先审查写入/清理目标，再在操作员授权的隔离宿主、独立 settings/ArtifactRoot 与已核 SHA fixture 上执行；不自动写历史 `C:\Tools` 共享现场。记录受保护与本轮进程 PID+完整 exe+创建时刻，只按三元身份停止本轮对象，禁止全局 kill、旧包清理或覆盖原样本。
- 测试进程必须以 `DNMCP_TEST=1` 启动；确认 `tools/list` 只发布五个产品 `edit_*`，任何
  `edit_test_*` 都不得出现在生产工具清单。

## 双架构执行

1. AI 部署与本地构建 SHA-256 完全一致的 DLL，启动 x64 dnSpy，打开并确认 MCP 设置后执行
   全部矩阵，最终使编辑与调试状态均为 idle。
2. AI 停止 x64 dnSpy，启动 x86 dnSpy，加载 x86 夹具并重复全部架构相关矩阵。
3. x86 结束后只清理本轮登记的测试进程、临时文件、断点和会话；复核原监听/受保护进程不变，不自动改回共享配置。

夹具由 `python3 tests/edit/build_p02_fixtures.py` 生成，不得存在单独 `.pdb`；调试符号必须
嵌入程序集。混合模式、NetModule 和多文件夹具也必须由该脚本构造，缺失不能记环境阻断。

## 必做矩阵

### 1. 契约与产品表面

- 运行 `contract_source.py`、`validate_contract.py`、`test_validator_mutations.py` 和
  `write_evidence_index.py`；必须分别满足生成物无漂移、131/131、20/20 和索引一致。
- P02 阶段 `tools/list` 基线总数 59：32 旧静态 + 5 结构化编辑 + 22 debug。五个产品工具为
  `edit_begin/edit_status/edit_apply/edit_review/edit_rollback`；无 commit/export/checkpoint/raw。
- 逐工具校验 input/output schema；向 operation 注入 `raw_metadata/pe_bytes/heap/rva/hex_patch`
  均须 `-32602` 且事务、revision、指纹不变。

### 2. 事务、所有权和生命周期

- 证明私有修改不改变实时指纹；同 request ID/同载荷逐字节重放且无第二次副作用，同 ID
  改载荷为 `REQUEST_ID_REUSE`。
- 第二正式会话 begin 为 `EDIT_TRANSACTION_BUSY`；非所有者操作为
  `EDIT_OWNER_MISMATCH`；普通兼容 HTTP 没有所有权。
- 自动执行 DELETE、同 URL stdio 重连、599999/600000ms、listener Apply restart、
  15378→15379→15378 显式 URL 更新。不得扫端口、猜 endpoint 或重放旧 mutation。
- 跑并发屏障：同 ID follower、同 ID 异载荷、异 ID apply/review、apply/rollback、close/timeout/
  Stop 与 waiter、旧 generation close。失败方、清理顺序、最终 idle 必须符合契约。

### 3. 22 类操作与硬验证

- 每类都要独立执行合法 apply→review→test apply/restore→私有写出重载→rollback；以 token
  定位现有对象，以返回 object ID 连续 add→update/link/remove。
- 每类至少一个结构/引用/未知字段负例；覆盖完整 TypeSig 向量、全部属性掩码边界与稀疏非法
  位、常量完整域、property/event accessor null 规则、固定 dnlib opcode/operand 表。
- 每轮 test apply/restore 后实时指纹必须回到原值；任何半应用、错误覆盖外部修改或名称猜测
  都是 FAIL。

### 4. fault、容量与审查失效

- 从生成的 golden 建独立 oracle。240 个 fault ID 各用一个新事务、前后 reset、唯一 JSON
  证据文件；manifest/oracle 全等，covered union 每 ID 恰好一次，actual trace 终点为 armed row。
- 正向 fault 必须恢复原指纹并回到 reviewed；反向 fault 唯一进入 `live_state_unknown`，其后
  edit/review/test apply 都被阻止，只有测试清理能恢复 idle。
- 执行 begin/apply/review/rollback、body/operation/object/diff/wire 等所有 capacity golden
  边界与超一；超限零副作用，清理后容量全部释放。
- 验证 review stale、实时外部冲突、debug not idle、遗漏 risk confirmation 的固定错误。

### 5. 动态验证与能力拒绝

- 必须真实得到 `not_requested`、DLL `not_applicable`、VM 门禁 `blocked`、EXE entry pause +
  terminate `passed`、launch/terminate/delete 三类 `failed`。逐项核对事件、临时路径/SHA、
  cleanup；最终 debug idle，测试残留只按返回的精确路径清理。
- 对完整指纹六例逐项做真实外部读回：module metadata、dnlib object graph、method body IL、
  managed resource、embedded PDB 都改变完整指纹并恢复；全局枚举逆序只改变 raw order，规范
  指纹不变。禁止用拼接字符串或伪造哈希替代真实枚举。
- 混合模式、NetModule、多文件程序集仍可加载/分析，但 `edit_begin` 必须
  `EDIT_CAPABILITY_UNAVAILABLE` 且零事务、零文件、零实时副作用。

## 回归、证据和判定

- 运行双 TFM 构建、Python 单元测试、security harness、既有静态工具、P01 六项和 debug
  ACC-001..036。范围外既有失败只有保存基线并证明未恶化时才可作为环境观察。
- 每个架构产生汇总 JSON；fault suite 必须有 240 个唯一 case 文件和 suite index。报告逐项
  映射 `RACC-001/002/003/009/010/026`，不得用单一冒烟结果代替矩阵。
- 只有外部 VM/MCP/ArtifactRoot 确实不可用且保存零副作用证据时才可 BLOCKED；实现错误、
  runner 错误、夹具缺失或测试超时均为 FAIL。
- 结束时不得有 debuggee、断点、活动编辑事务、临时验证文件或未恢复端口；原样本不得覆盖。

通过标准：两架构六个 RACC 全部 PASS，fault 240/240，契约 131/131 与 mutation 20/20，
全部回归未退化，编辑/调试最终 idle，证据可由第三方按文件 SHA-256 复核。
