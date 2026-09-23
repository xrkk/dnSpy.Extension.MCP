# 第三方 AI：P01 公共契约、VM 门禁与调试基线定向验收提示词

把本提示词交给同时连接 Win10VM 管理 MCP 与 dnSpy MCP 的第三方智能体。人工只准备虚拟机、两个 dnSpy 可执行文件和 MCP 环境；部署、位数切换、真实 UI 操作、测试、证据和清理由 AI 完成。不得使用 curl、手写 JSON-RPC 或读取源码来冒充运行结果。

## 授权和边界

- 只验收 P01：权威调用上下文、传输会话关闭、VMware/VirtualBox 执行门禁、进程期本地覆盖、三个执行入口、模块 dump 回归。
- 正式 initialize 后读取实时 `tools/list` 与 `debug_capabilities`；调试门关闭即停止动态案例并记未执行/阻断，不能把静态宿主结果视为调试通过。
- 不测试或声称结构化编辑事务、检查点、签名/资源编辑等 P02-P09 能力。
- `tests/edit/run_p01_vm_tests.py` 仅是可复用编排入口；先核其写入/清理目标与本轮授权范围，再由 VM 管理能力执行，不得把脚本输出或 mock 代替真实 UI。未获部署/位数切换授权时将对应腿标未执行。
- 使用操作员提供的独立宿主、settings、fixture 与 ArtifactRoot，核插件/样本 SHA；记录受保护及本轮自启进程的 PID、完整 exe 路径和创建时刻。只可按三元身份停本轮对象，禁止按名全局 kill、覆盖原样本、清理共享配置或旧产物。历史 `C:\Tools\dnSpy` 不是默认写入授权。

## 执行动作

1. 记录本次插件 DLL SHA-256，部署后再次读取**本轮隔离根内**目标 DLL SHA-256，二者必须一致。只停止本轮登记的 dnSpy 后才替换其 DLL；只可清理该根内可重建的 `dnSpy-mef-info.bin`，不得触及其他实例。
2. 自动启动 x64 dnSpy，打开真实 **视图 → 选项 → MCP 服务器** 页面并应用回环配置。依次执行：
   - `EDIT-ACC-017`：真实 VMware 分类；测试 seam 注入 VMware、VirtualBox、physical、unknown；验证前两者允许、后两者 fail-closed；验证 `Oracle Corporation` 单独出现不算 VirtualBox；通过本地页面 Apply 开关进程覆盖，Cancel/远程参数不得改变；重启 dnSpy 后覆盖必须清零。
   - `EDIT-ACC-027`：对当前架构 fixture launch/pause；分别 dump 目标模块与 mscorlib；改变 pause epoch 后重复；restart 后重新取得 generation/module handle 再重复；响应、artifact、manifest 的长度和 SHA-256 必须一致；旧 handle 必须拒绝；最终 idle。
   - `EDIT-ACC-030`：执行 `OUT-001-H01`、`OUT-001-H02` 和 `ACC-030`。H01 必须用真实 initialized legacy SSE、initialized Streamable HTTP、无正式会话兼容调用证明服务端身份不可由 args 伪造；H02 必须用真实 DELETE、legacy disconnect、listener Stop 证明先释放名额、每会话仅通知一次且观察者异常被隔离；ACC-030 必须对 `debug_launch`、`debug_restart`、`edit_dynamic_validation` 跑四分类矩阵，并证明拒绝发生在 Start/terminate/状态或文件副作用前。
3. x64 三项全部结束并确认 idle 后，由 AI 停止 x64 dnSpy、启动 x86 dnSpy，重复相同三项。不要删除旧动态产物来规避账本校验。
4. x86 完成后仅清理本轮登记的测试 debuggee、断点和传输会话，验证原配置/受保护进程未改；不自动重写共享远程监听。

## 证据和判定

- 每个架构必须有三个 `summary.json`，`EDIT-ACC-030` 还必须包含 `out-001-h01.json`、`out-001-h02.json`，并在 `outcome_checks` 中分别记录 `pass|fail|blocked`。
- 总退出码同时聚合案例与两个 OUT 检查：任何 fail 为 1；无 fail 但有 blocked 为 2；全部通过为 0。
- 只有外部 Win10VM/MCP/ArtifactRoot 不可用且保存原始错误与零副作用证据时才可写 BLOCKED。实现错误、分类错误、dump 失败或测试驱动缺陷均为 FAIL。
- 最终报告逐项列出 x64/x86 的 ACC-017、ACC-027、ACC-030、OUT-001-H01/H02，插件部署哈希、环境分类、UI Apply 与重启清零、dump 路径/长度/SHA、关闭事件、三个入口副作用计数、清理结果和最终 idle。

通过标准：x64/x86 共 6 个案例全部退出码 0，4 个 OUT 子检查全部 pass，没有遗留 debuggee/断点/会话、没有覆盖原样本，远程监听已恢复。
