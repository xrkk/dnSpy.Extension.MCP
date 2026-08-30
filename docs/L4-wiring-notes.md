# L4 接线工程笔记(内部工作文档)

面向 IMP-005..009 Windows 侧接线的 API 测绘与增设计划。所有 dnSpy API 均已在 VM 上运行的
dnSpyEx v6.6.0(netframework)环境核实;L3/L4-a 已实车验证的部分见实施记录第 24-26 轮。

## dnSpy 调试 API 测绘(dnSpyEx v6.6.0 检出 + 实机)

- 组合引擎是 **Microsoft.VisualStudio.Composition 17.13**(非经典 MEF),release 构建部件拒绝完全静默。
- `DbgManager`(dnSpy.Contracts.Debugger):
  - `DebugProgramOptions? Start` → 实为 `string? Start(DebugProgramOptions options)`(DbgManager.cs:167,返回错误消息或 null 成功);
  - `bool IsDebugging`(:182)、`event EventHandler IsDebuggingChanged`(:187);
  - `DbgProcess[] Processes`(:226)、`event EventHandler<DbgCollectionChangedEventArgs<DbgProcess>> ProcessesChanged`(:231);
  - `Dispatcher`(IDbgDispatcher,仅 BeginInvoke)。
- 启动 options 三级(dnSpy.Contracts.Debugger.DotNet.CorDebug,csproj 已引用):
  - `CorDebugStartDebuggingOptions : StartDebuggingOptions` — `Filename/CommandLine/WorkingDirectory/DbgEnvironment Environment`;
  - `DotNetFrameworkStartDebuggingOptions : CorDebug...` — + `DebuggeeVersion`(netfx);
  - `DotNetStartDebuggingOptions : CorDebug...` — + `UseHost(默认 true)/Host/HostArguments(默认 exec)/ConnectionTimeout(5s)`(CoreCLR host 模式);
  - `StartDebuggingOptions.BreakKind`(string)必须映射 `PredefinedBreakKinds.DontBreak/CreateProcess/ModuleCctorOrEntryPoint/EntryPoint`(EVD-API-008,禁字面量透传/null)。
- 启动命令行组装证据(EVD-API-002):host+HostArguments+quoted Filename+CommandLine,Windows command line 字符串;`WindowsArgumentEncoder`(LaunchPlanner.cs)已实现。
- `--pid <pid>` 启动参数可附加调试(值必须独立 arg;`--pid=x` 被当文件名)。L3 用它实测了六写工具门控。

## 已就绪的纯逻辑组件(全部有 harness 全绿)

DebugSessionCoordinator(七态/claim/restart reservation/control admission/观察对账)、
DebugEventBuffer.Append/LastCursor/EventsLost/Frozen(session_end 冻结;>MaxBytes 单条降级 EVT-DYN-019)、
DualLaneQueue(Control 8/General 56/Total 64)、ControlOperation/ControlOperationRecord、
DbgProcessControlAdapter(production:owned DbgProcess 的 Break/Terminate 投递+Observation 事件)、
LaunchPlanner(五模式/break_kind 映射/argv)、FileIdentityModel(Windows lease)、
DebugBreakpointStore/ValueSnapshotLedger/ArtifactStoreLedger/DebugPageAssembler/StaticWriteGate。

DebugGateService 已接线(Loaded 时经 DbgManager.Dispatcher 单次采样 IsDebugging,冻结 gate)。

## 增量 1(L4-a 核心,下一步实施)

新建 `Debugger/DebugSessionService.cs`([Export] 单例),imports DbgManager + DebugGateService + McpSettings:
1. **事件泵**:订阅 `ProcessesChanged`(新增→coordinator.ObservePaused/ObserveProcessRemoved 对账 + DebugEventBuffer 追加 EVT-DYN-010/011);`IsDebuggingChanged`(false→session_end 冻结 buffer)。订阅回调体在 DbgManager.Dispatcher 线程,只调用线程安全组件。
2. **debug_status**:coordinator.ContextSnapshot → envelope。
3. **debug_launch**:参数 DTO → LaunchPlanner.Plan(架构精确相等检查在 plan 前,CON-DYN-010)→ coordinator.BeginLaunch → lease/claim(FileIdentityModel)→ **唯一 WPF 窄回调**调 `DbgManager.Start(options)`(按 plan 填三级 options+BreakKind 映射)→ MarkLaunchClaimSucceeded/Failed → envelope。Start 返回非 null → 回收(不遗留 reservation/session)。
4. **debug_pause/continue/terminate**:DualLaneQueue.Control 入队 → coordinator.TryBeginControl → DbgManager.Dispatcher 投递 adapter.PostBreak/PostTerminate 或 Continue(DbgProcess.Continue?)→ MarkControlIssued → handler 线程 await settle TCS(观察泵触发)或 30s deadline(超时 SettleControlFailure(TIMEOUT))。
5. **debug_read_events/wait_event**:buffer 按 cursor 读取(分页)+wait_event 阻塞至新事件/超时。
6. **debug_restart**:BeginRestartRelaunch→terminate→removal 观察结算→重新 lease/claim→Start(全程不公开 idle)。

DebugToolProvider.ExecuteTool 路由到 service;**广告按已实现 handler 过滤**(增量 1 只上 status/launch/pause/continue/terminate/restart/read_events/wait_event + capabilities,其余保持不广告直至落地)。

## 验证循环

Linux `build-check.sh`(net48)→ http.server → VM 拉 DLL 部署 `bin\Extensions\dnSpy.Extension.MCP\` → 杀 dnSpy(设置已含 committed JSON,debug 门已开)→ 重启 → curl.exe 逐工具实测(fixture:net48 控制台 exe,同机 x64)。wire 验证一律 curl.exe(Invoke-WebRequest 对 `Accept: text/event-stream` 返回空 Content)。

## VM 环境现状(2026-08-28)

- dnSpy PID 见现场;设置 committed JSON:debug_enabled=true、ack=true、Port 15378、ArtifactRoot=C:\dnspy-mcp-artifacts(空目录);
- 54 工具广告中,21 个 debug_* 会话工具 handler 未接(返回 Unknown tool);
- MefCheck/CacheRead 法医工具在 C:\Tools\MefCheck(收尾阶段清理);诊断扩展 DiagExtension.x.dll 在 bin\(排障保留)。

## 增量 2(API 测绘,IMP-006 断点,已核实待实现)

- `DbgCodeBreakpointsService`(Contracts.Debugger/Breakpoints/Code,MEF 导出):
  `DbgCodeBreakpoint? Add(DbgCodeBreakpointInfo)`、`Remove(DbgCodeBreakpoint[])`、`DbgCodeBreakpoint[] Breakpoints`、
  `Modify(DbgCodeBreakpointAndSettings[])`(enabled=false 由此改)、`BreakpointsChanged/Modified` 事件;
- `DbgDotNetCodeLocationFactory`(Contracts.Debugger.DotNet,MEF):`Create(ModuleId module, uint token, uint offset[, DbgILOffsetMapping])` → `DbgDotNetCodeLocation{Module,Token,Offset,DbgModule}`;
- `ModuleId`(Contracts.Logic/Metadata):`(string asmFullName, string moduleName, bool isDynamic, bool isInMemory, bool nameOnly)`,隐式 `string moduleFilename` → 磁盘强身份按文件名;`DbgModule` 有 Runtime/进程内模块映射;
- 暂停真实明细:`DbgRuntime.BreakInfos : ReadOnlyCollection<DbgBreakInfo{Kind,Data}>`,Kind ∈ {Unknown, Connected, Message},Message.Data = `DbgMessageEventArgs`(步进完成/断点命中等 reason 明细;实现时读其 Message/Kind 字段映射 arbiter 的 exception/breakpoint/step/entry/process/break);
- 断点绑定事件:bound = `DbgCodeBreakpoint` 的 bound 状态/`DbgBoundCodeBreakpoint`(Modules 命中时)→ bound=true + EVT-DYN-013;
- 线程/栈(IMP-007):`DbgProcess.Threads : DbgThread[]`(有 UIHashName/ManagedId 等),栈走 `DbgThread.StackTrace`(实现时核字段);步进 `DbgThread`/runtime stepper(Contracts.Debugger.DotNet/Steppers)。

## 已实车验证的增量 1 补充(第 27 轮后)

wait_event 超时路径(timed_out=true)与投递路径(pause→wait 返回 cursor 4 paused 事件)均通过;诊断堆栈 catch 已收敛为常规 INTERNAL_ERROR 消息(构建 d98f3055,已实测部署)。VM 现状:dnSpy 运行中,会话已 terminate(干净 idle)。
