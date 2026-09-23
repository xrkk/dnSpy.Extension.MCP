# dnSpy MCP：AI 单文件工具与接口手册

接口结构基线：`fca80b399a98446a551202b34163e8db88d3c39a`（2026-09-23，`feature/p01-public-contract-vm-gate`）；以下状态说明更新至异常修复提交 `03f84607bf67a77fa9bb8850edb85143abc93a31`。**静态规格不是实机验收证明**。运行中以当前 `tools/list` 为可通告事实，冷门条件分支与错误仍须按实际响应处理。

本文独立给出工具选择、参数/结果、状态关联和冻结结构，英文代码字段保持原样。文末附源 schema 的自包含 JSON：编辑工具完整 `inputSchema`/`outputSchema` 与全部 39 种操作；调试 `$defs` 与 `$ref` 均在同一文件内；静态工具的 `inputSchema` 亦在此。表格是快速入口，附录是精确嵌套语法，不必另开源码。

已知状态：T051-R02 曾记录真实首机会异常事件缺失的 x64 红态；T051-R03 随后在隔离 .240 上完成 x86/x64 定向复验，实际异常 payload 符合冻结 schema。T053 在新隔离根复验了 x86/x64 四种 `break_kind` 的启动、重启、完整分页事件流和真实首机会异常；这不代表全调试套件或所有异常策略已验收。`strong_name_remove` 有可信事件门控分支，但真实可信来源的成功路径尚未完成 ACC016 验收，不能当作已验收的成功能力。其他动态/编辑能力也受下述运行门限限制。

## 1. MCP 接入、信封与能力门

- 服务由 dnSpy 扩展进程提供，典型 Streamable HTTP 端点 `http://127.0.0.1:<实际端口>/mcp`；旧版 HTTP+SSE 也受支持。实际端口看 dnSpy 选项/监听快照，不能把默认值当已绑定端口。远程模式需按实例配置的令牌/CIDR/隔离要求访问。支持协议 `2025-06-18`、`2025-03-26`、`2024-11-05`，不支持的版本协商到最新。
- 先发送 JSON-RPC `initialize`，读取响应头 `Mcp-Session-Id` 并在后续 Streamable HTTP 请求中携带；随后 `notifications/initialized`，再 `tools/list`。旧 SSE 流在 `/sse` 建立、消息送其 endpoint；无会话的 plain POST 只属兼容路径，不可据此取得编辑事务所有权。
- `tools/list` 的工具对象包含 `name`、`description`、`inputSchema`；只有协商 `2025-06-18` 才带可用的 `outputSchema`。`tools/call` 参数为 `{"name":"工具名","arguments":{...}}`。顶层 JSON-RPC 错误 `-32602` 为无效参数、`-32603` 为内部异常；未知工具是 `isError:true` 的文本内容。
- 工具响应外层为 `result:{content:[{type:"text",text:"..."}],isError?:boolean,structuredContent?:object}`。在 `2025-06-18` 下若首个文本为合法 JSON，`structuredContent` 与之同值；旧协议省略。静态读工具多是直接 JSON 投影而非 `schema_version` 信封；静态工具没有统一错误码，异常变成 `Error executing tool ...` 文本。调试工具内部是 `dnspy.debug.v1` 成功/失败信封，编辑工具内部是 `dnspy.edit.v1`；`isError` 对应域失败。
- `resources/list` 另返回 `{resources:[{uri,name,description,mimeType}]}`；`resources/read` 输入 `{uri}` 返回 `{contents:[{uri,mimeType:"text/markdown",text}]}`，未知 URI 是 `-32602`。`resources/templates/list` 当前返回空模板页。资源是文档，不计为 tools/list 工具。
- 资源 URI 固定为 `dnspy://docs/{index,overview,static-analysis,il-editing,dynamic-debugging,security,python-client,tool-workflows}` 与 `bepinex://docs/{plugin-structure,harmony-patching,configuration,common-scenarios,il2cpp-guide,mono-vs-il2cpp}` 共 14 个；`resources/read` 要传完整具体 URI，不能传上述花括号缩写。初始化返回的 `instructions` 是嵌入说明，不是额外工具；若其阶段性文字与当前实际 provider 能力冲突，以本基线源码和 live `tools/list` 为准。
- 静态加载/反编译只解析 .NET 元数据和 IL，不执行目标程序集；`debug_launch` 与调试控制才跨入目标执行门。调试工具受冻结的专用实例确认、启动时调试器空闲采样、设置/执行环境门控制；`debug_capabilities` 常通告，其余会话工具只在门生效且 handler 存在时通告。`debug_attach`/`debug_detach`/`debug_list_attachable_processes` 固定不通告且调用返回 `CAPABILITY_UNAVAILABLE`。编辑工具的事务/编译有各自可用性、所有权和容量约束。

- 传输 raw body 上限 1,048,576 字节；短请求并发门 16、长连接门 8、transport session 最多 16。调试事件缓存默认 8,388,608 字节，ArtifactRoot 账本上限：128 保留会话/根子项、4096 每会话子项及全库子项、单文件 536,870,912 字节、单会话 1,073,741,824 字节、全库 8,589,934,592 字节；UTF-8 字节限制另见附录 D，不能把 JSON Schema 的字符 `maxLength` 误当字节限制。编辑上限见附录 C 中输出 `limits` 与正文事务规则，单次 HTTP body 限制仍先于业务解析。

### 可复制线协议骨架

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"ai-client","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_assemblies","arguments":{}}}
{"jsonrpc":"2.0","id":4,"method":"resources/list","params":{}}
```

上列每行是**独立请求**，不是一个 JSON 数组；例中 JSON-RPC `id` 只关联传输响应，编辑/调试 `request_id` 则是工具内副作用幂等键。示意占位符如 `<assembly_name>` 必须替换为真实前序返回值或目标值。

静态响应示例（演示 `2025-06-18` 的 MCP 外层；数组内容为说明用样本，并非本机已加载程序集）：

```json
{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"{\"assemblies\":[{\"Name\":\"Demo\",\"Version\":\"1.0.0.0\",\"FullName\":\"Demo, Version=1.0.0.0\",\"Culture\":\"neutral\",\"PublicKeyToken\":\"null\"}]}"}],"structuredContent":{"assemblies":[{"Name":"Demo","Version":"1.0.0.0","FullName":"Demo, Version=1.0.0.0","Culture":"neutral","PublicKeyToken":"null"}]}}}
```

分页静态响应示例（`nextCursor` 不存在即末页；实际 cursor 必须原样传回，不自行解码/编造）：

```json
{"items":[{"FullName":"Demo.Widget","Token":33554433}],"total_count":1,"returned_count":1}
```

## 2. 全工具索引

基线源码生产工具 72 个：静态 32、调试 22、编辑/编译 18。验收环境 `DNMCP_TEST=1` 另外通告 6 个调试探针；文末另列只可直接测试调用但不通告的缝，均非正常业务入口。

| 精确工具名 | 类别/作用 | 影响与可用条件 | 详情 |
| --- | --- | --- | --- |
| `open_files` | 静态：Load .NET assemblies/modules into dnSpy from disk so the other tools can analyze them — like dnSpy's File → Open, but driven by the AI. | 加载本地文件/目录；无需预先加载目标 | [open_files](#open_files) |
| `list_assemblies` | 静态：List all loaded assemblies in dnSpy. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [list_assemblies](#list_assemblies) |
| `get_assembly_info` | 静态：Get detailed information about a specific assembly. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [get_assembly_info](#get_assembly_info) |
| `list_types` | 静态：List all types in an assembly or namespace. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [list_types](#list_types) |
| `get_type_info` | 静态：Get detailed information about a specific type including its TypeDef token, generic-parameter tokens, and members. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [get_type_info](#get_type_info) |
| `decompile_method` | 静态：Decompile a specific method to C# code. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [decompile_method](#decompile_method) |
| `search_types` | 静态：Search for types by name. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [search_types](#search_types) |
| `search_members` | 静态：Search for MEMBERS (methods / fields / properties / events) by name across all loaded assemblies (or one via assembly_name) — the member counterpart of search_types, together covering dnSpy's Ctrl+Shift+K 'Search Assemblies'. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [search_members](#search_members) |
| `decompile_type` | 静态：Decompile a whole TYPE to C# (all members) — the dnSpy 'click the class and read its source' view. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [decompile_type](#decompile_type) |
| `decompile_by_token` | 静态：Decompile a method (or type) to C# by MDToken alone — no type name needed. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [decompile_by_token](#decompile_by_token) |
| `find_callers` | 静态：Cross-reference: find every method that CALLS a given method (call / callvirt / newobj / ldftn / ldvirtftn), across ALL loaded assemblies — callers routinely live in a different assembly than the target. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [find_callers](#find_callers) |
| `find_callees` | 静态：Cross-reference (inverse of find_callers): list what a single method USES — the methods it calls, the fields it reads/writes, and the types it touches in its own body (dnSpy Analyze's 'Uses' node). | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [find_callees](#find_callees) |
| `find_references` | 静态：Cross-reference: find every IL site that references a target across ALL loaded assemblies. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [find_references](#find_references) |
| `find_overrides` | 静态：Cross-reference for virtual / interface methods (dnSpy Analyze 'Overridden By' / 'Overrides'). | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [find_overrides](#find_overrides) |
| `find_unity_messages` | 静态：List the Unity lifecycle / message methods (Awake / Start / Update / FixedUpdate / OnEnable / OnTriggerEnter / OnCollisionEnter / OnGUI / OnDestroy / …) declared on a type, or across a whole assembly. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [find_unity_messages](#find_unity_messages) |
| `find_by_attribute` | 静态：Find types and/or members decorated with a given custom attribute, across all assemblies (or one). | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [find_by_attribute](#find_by_attribute) |
| `search_string_literals` | 静态：Reverse-lookup: find every method that emits a given string literal (ldstr) across loaded assemblies. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [search_string_literals](#search_string_literals) |
| `list_string_constants` | 静态：List all string literals (ldstr) in a type, or in a single method. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [list_string_constants](#list_string_constants) |
| `search_constants` | 静态：Find where a NUMERIC constant is used in code (ldc.i4* / ldc.i8 / ldc.r4 / ldc.r8) — the number counterpart of search_string_literals, completing dnSpy's Search Assemblies set (types / members / strings / numbers). | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [search_constants](#search_constants) |
| `generate_bepinex_plugin` | 静态：Generate a complete BepInEx plugin: the BaseUnityPlugin shell (Awake wiring Harmony.PatchAll, OnDestroy unpatch) plus a [HarmonyPatch] class per hook. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [generate_bepinex_plugin](#generate_bepinex_plugin) |
| `generate_harmony_patch` | 静态：Generate a compile-ready HarmonyX patch class for a REAL method, with the correct injected parameters read from its actual signature — unlike the empty stubs from generate_bepinex_plugin. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [generate_harmony_patch](#generate_harmony_patch) |
| `get_type_fields` | 静态：Get fields from a type matching a name pattern (supports wildcards like *Bonus*). | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [get_type_fields](#get_type_fields) |
| `get_type_property` | 静态：Get detailed information about a specific property from a type | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [get_type_property](#get_type_property) |
| `find_path_to_type` | 静态：Find property/field chains connecting two types through their members. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [find_path_to_type](#find_path_to_type) |
| `list_methods` | 静态：List methods of a type with unambiguous identifiers. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [list_methods](#list_methods) |
| `get_method_il` | 静态：Return the IL body of a method: instructions (index, offset, opcode, operand), locals, exception handlers, and body flags. | 只读/加载；需 dnSpy 实例；目标类工具需已加载目标 | [get_method_il](#get_method_il) |
| `patch_method_il` | 静态：按原始 IL 指令索引执行 replace/insert/delete/set_init_locals 兼容批次，并经结构化事务提交检查点；不要按旧通告描述假设仍只在内存暂存。 | 写/导出；需初始化会话、静态写门和编辑前提 | [patch_method_il](#patch_method_il) |
| `force_return` | 静态：生成固定返回体并经结构化事务提交检查点；返回类型和值组合受实现限制。 | 写/导出；需初始化会话、静态写门和编辑前提 | [force_return](#force_return) |
| `nop_method` | 静态：生成立即 ret 的空方法体并经结构化事务提交检查点。 | 写/导出；需初始化会话、静态写门和编辑前提 | [nop_method](#nop_method) |
| `revert_method_il` | 静态：仅对当前兼容检查点执行受限 Undo，不跨越其他历史 head；返回当前 IL 投影。 | 写/导出；需初始化会话、静态写门和编辑前提 | [revert_method_il](#revert_method_il) |
| `rename_symbol_by_token` | 静态：以 token 定位符号并经结构化事务提交；`enum_members` 批量值映射有额外 members 约束。 | 写/导出；需初始化会话、静态写门和编辑前提 | [rename_symbol_by_token](#rename_symbol_by_token) |
| `save_assembly` | 静态：从当前精确检查点导出到 ArtifactRoot，不覆盖源样本、也不创建原地备份。 | 写/导出；需初始化会话、静态写门和编辑前提 | [save_assembly](#save_assembly) |
| `debug_capabilities` | 调试：动态调试：capabilities | 只读；始终通告 | [debug_capabilities](#debug_capabilities) |
| `debug_status` | 调试：动态调试：status | 读/控制目标；需冻结调试门 | [debug_status](#debug_status) |
| `debug_launch` | 调试：动态调试：launch | 读/控制目标；需冻结调试门 | [debug_launch](#debug_launch) |
| `debug_pause` | 调试：动态调试：pause | 读/控制目标；需冻结调试门 | [debug_pause](#debug_pause) |
| `debug_continue` | 调试：动态调试：continue | 读/控制目标；需冻结调试门 | [debug_continue](#debug_continue) |
| `debug_restart` | 调试：动态调试：restart | 读/控制目标；需冻结调试门 | [debug_restart](#debug_restart) |
| `debug_terminate` | 调试：动态调试：terminate | 读/控制目标；需冻结调试门 | [debug_terminate](#debug_terminate) |
| `debug_read_events` | 调试：动态调试：read events | 读/控制目标；需冻结调试门 | [debug_read_events](#debug_read_events) |
| `debug_wait_event` | 调试：动态调试：wait event | 读/控制目标；需冻结调试门 | [debug_wait_event](#debug_wait_event) |
| `debug_set_breakpoint` | 调试：动态调试：set breakpoint | 读/控制目标；需冻结调试门 | [debug_set_breakpoint](#debug_set_breakpoint) |
| `debug_list_breakpoints` | 调试：动态调试：list breakpoints | 读/控制目标；需冻结调试门 | [debug_list_breakpoints](#debug_list_breakpoints) |
| `debug_set_breakpoint_enabled` | 调试：动态调试：set breakpoint enabled | 读/控制目标；需冻结调试门 | [debug_set_breakpoint_enabled](#debug_set_breakpoint_enabled) |
| `debug_remove_breakpoint` | 调试：动态调试：remove breakpoint | 读/控制目标；需冻结调试门 | [debug_remove_breakpoint](#debug_remove_breakpoint) |
| `debug_list_threads` | 调试：动态调试：list threads | 读/控制目标；需冻结调试门 | [debug_list_threads](#debug_list_threads) |
| `debug_get_stack` | 调试：动态调试：get stack | 读/控制目标；需冻结调试门 | [debug_get_stack](#debug_get_stack) |
| `debug_step` | 调试：动态调试：step | 读/控制目标；需冻结调试门 | [debug_step](#debug_step) |
| `debug_get_locals` | 调试：动态调试：get locals | 读/控制目标；需冻结调试门 | [debug_get_locals](#debug_get_locals) |
| `debug_expand_value` | 调试：动态调试：expand value | 读/控制目标；需冻结调试门 | [debug_expand_value](#debug_expand_value) |
| `debug_list_modules` | 调试：动态调试：list modules | 读/控制目标；需冻结调试门 | [debug_list_modules](#debug_list_modules) |
| `debug_read_memory` | 调试：动态调试：read memory | 读/控制目标；需冻结调试门 | [debug_read_memory](#debug_read_memory) |
| `debug_dump_module` | 调试：动态调试：dump module | 读/控制目标；需冻结调试门 | [debug_dump_module](#debug_dump_module) |
| `debug_set_exception_policy` | 调试：动态调试：set exception policy | 读/控制目标；需冻结调试门 | [debug_set_exception_policy](#debug_set_exception_policy) |
| `edit_begin` | 编辑：事务编辑/编译：begin | 读/事务写/导出；按状态/所有权及 schema | [edit_begin](#edit_begin) |
| `edit_status` | 编辑：事务编辑/编译：status | 只读；非属主返回受限视图 | [edit_status](#edit_status) |
| `edit_apply` | 编辑：事务编辑/编译：apply | 读/事务写/导出；按状态/所有权及 schema | [edit_apply](#edit_apply) |
| `edit_import` | 编辑：事务编辑/编译：import | 读/事务写/导出；按状态/所有权及 schema | [edit_import](#edit_import) |
| `edit_impact_scan` | 编辑：事务编辑/编译：impact scan | 读/事务写/导出；按状态/所有权及 schema | [edit_impact_scan](#edit_impact_scan) |
| `edit_resource_import` | 编辑：事务编辑/编译：resource import | 读/事务写/导出；按状态/所有权及 schema | [edit_resource_import](#edit_resource_import) |
| `edit_resource_export` | 编辑：事务编辑/编译：resource export | 读/事务写/导出；按状态/所有权及 schema | [edit_resource_export](#edit_resource_export) |
| `edit_review` | 编辑：事务编辑/编译：review | 读/事务写/导出；按状态/所有权及 schema | [edit_review](#edit_review) |
| `edit_rollback` | 编辑：事务编辑/编译：rollback | 读/事务写/导出；按状态/所有权及 schema | [edit_rollback](#edit_rollback) |
| `edit_commit` | 编辑：事务编辑/编译：commit | 读/事务写/导出；按状态/所有权及 schema | [edit_commit](#edit_commit) |
| `edit_history` | 编辑：事务编辑/编译：history | 只读；非属主返回受限视图 | [edit_history](#edit_history) |
| `edit_undo` | 编辑：事务编辑/编译：undo | 读/事务写/导出；按状态/所有权及 schema | [edit_undo](#edit_undo) |
| `edit_redo` | 编辑：事务编辑/编译：redo | 读/事务写/导出；按状态/所有权及 schema | [edit_redo](#edit_redo) |
| `edit_restore` | 编辑：事务编辑/编译：restore | 读/事务写/导出；按状态/所有权及 schema | [edit_restore](#edit_restore) |
| `edit_export` | 编辑：事务编辑/编译：export | 读/事务写/导出；按状态/所有权及 schema | [edit_export](#edit_export) |
| `edit_recover` | 编辑：事务编辑/编译：recover | 读/事务写/导出；按状态/所有权及 schema | [edit_recover](#edit_recover) |
| `edit_accept_live` | 编辑：事务编辑/编译：accept live | 读/事务写/导出；按状态/所有权及 schema | [edit_accept_live](#edit_accept_live) |
| `edit_compile` | 编辑：事务编辑/编译：compile | 编译执行编译器但不写模块；需初始化会话 | [edit_compile](#edit_compile) |

## 3. 静态分析、生成与旧写工具

本类由 `StaticToolProvider` 通过 WPF Dispatcher 调用 `McpTools`；六个旧写工具经 `LegacyEditAdapter` 走结构化事务/检查点兼容路径，不直接绕开编辑门。这里的输入来自通告的静态 `inputSchema`；多数无正式 `outputSchema`，下列输出解释按执行实现整理，不是伪造的冻结 schema。源输入 schema 全量见附录 A。旧输入对象没有统一 `additionalProperties:false` 声明，但实现逐字段读取；多余字段不能作为有效功能。每项反引号调用是满足声明 required 字段的**语法最小形状**，`<...>` 和示例数值不代表现有程序集/文件；`open_files.paths` 等运行时要求非空时须填真实值。

通用分页投影是 `{items,total_count,returned_count,nextCursor?}`；首请求默认每页 10，支持 `page_size` 的工具上限 1000，后续页游标内固定原页大小。`get_assembly_info`、`get_type_fields` 等使用自己的字段名；对某些仅 `cursor` 的工具 `page_size` 不起作用。空/缺失 cursor 从第一页开始，非法 base64/结构报参数错误。搜索可按 `assembly_name` 缩小已加载元数据范围，返回的 token 与 assembly 配对使用。

### open_files

Load .NET assemblies/modules into dnSpy from disk so the other tools can analyze them — like dnSpy's File → Open, but driven by the AI. Use this when the assembly you need isn't loaded yet. Each entry in 'paths' may be a FILE (loaded directly) or a DIRECTORY (every file matching 'pattern', default '*.dll', optionally 'recursive') — so you can open several DLLs at once or a whole folder (e.g. a Unity game's 'Managed' directory). Only reads metadata; does not execute the assembly. Returns loaded_count / already_loaded_count / failed_count, a 'loaded' list ({name, path, already_loaded}) and a 'failed' list ({path, error}). Then use list_assemblies / search_types to work with them.

`{"name":"open_files","arguments":{"paths":["C:\\Samples\\Demo.dll"]}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `paths` | 是 | array; items=string |
| `recursive` | 否 | boolean |
| `pattern` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：JSON `{loaded_count,already_loaded_count,failed_count,loaded:[{name,path,already_loaded}],failed:[{path,error}],note?}`；列表超过 100 条仅截取展示，计数不截断。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### list_assemblies

List all loaded assemblies in dnSpy. Unity titles load hundreds of framework modules; pass name_filter (substring or '*' wildcard) to narrow, e.g. 'Assembly-CSharp'.

`{"name":"list_assemblies","arguments":{}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `name_filter` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：正式 outputSchema：`{assemblies:[{Name,Version,FullName,Culture,PublicKeyToken}]}`，全为字符串字段。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### get_assembly_info

Get detailed information about a specific assembly. Supports pagination of namespaces with default page size of 10.

`{"name":"get_assembly_info","arguments":{"assembly_name":"sample_assembly_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：JSON `{Name,Version,FullName,Culture,PublicKeyToken,Modules:[{Name,Kind,Architecture,RuntimeVersion}],Namespaces:[string],NamespacesTotalCount,NamespacesReturnedCount,TypeCount,nextCursor?}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### list_types

List all types in an assembly or namespace. Metadata rows include each TypeDef token so they can feed rename_symbol_by_token / decompile_by_token. Paginated (default page size 10; override with page_size). Use names_only for a compact list of FullName strings, and base_type to list only (transitive) subclasses of a base — e.g. base_type='MonoBehaviour' lists all Unity components.

`{"name":"list_types","arguments":{"assembly_name":"sample_assembly_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `namespace` | 否 | string |
| `base_type` | 否 | string |
| `names_only` | 否 | boolean |
| `page_size` | 否 | integer |
| `include_nested` | 否 | boolean |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON `items:[{Token,FullName,Namespace,Name,IsPublic,IsNested,IsCompilerGenerated,DeclaringType,IsClass,IsInterface,IsEnum,IsValueType,IsAbstract,IsSealed,BaseType}]`；`names_only:true` 时 items 是 FullName 字符串。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### get_type_info

Get detailed information about a specific type including its TypeDef token, generic-parameter tokens, and members. Fields/properties/events carry their metadata token; full method rows include MethodDef, parameter, and method-generic-parameter tokens. First request returns fields/properties/events and paginated methods; cursor requests return methods only. Use compact=true and members_filter to reduce output. Field rows also carry Offset/OffsetSource/Il2CppToken when discoverable.

`{"name":"get_type_info","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `compact` | 否 | boolean |
| `members_filter` | 否 | string |
| `page_size` | 否 | integer |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：JSON `Token,FullName,Namespace,Name,IsPublic,IsClass,IsInterface,IsEnum,IsValueType,IsAbstract,IsSealed,BaseType,Interfaces,GenericParameters,Methods,MethodsTotalCount,MethodsReturnedCount,FieldsCount,PropertiesCount,EventsCount,nextCursor?`；首请求另有 `Fields/Properties/Events`，后续页只给三类计数；`compact` 缩减成员字段。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### decompile_method

Decompile a specific method to C# code. For overloaded methods, pass parameter_types (array of fully-qualified type names from list_methods) or method_token (uint MDToken) to disambiguate.

`{"name":"decompile_method","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |
| `include_state_machine` | 否 | boolean |

返回（从执行实现整理，非正式 outputSchema）：原样 C# 文本，不是 JSON；可附加状态机 `MoveNext` 救援文本，取决于 `include_state_machine`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### search_types

Search for types by name. Metadata rows include each TypeDef token so they can feed rename_symbol_by_token / decompile_by_token. Defaults to all loaded assemblies — pass assembly_name to scope to one (e.g. 'Assembly-CSharp') so framework types don't drown game types. Matches nested compiler-generated types too. Paginated (default page size 10; override with page_size). Use names_only for a compact FullName-string list.

`{"name":"search_types","arguments":{"query":"sample_query"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `query` | 是 | string |
| `assembly_name` | 否 | string |
| `names_only` | 否 | boolean |
| `page_size` | 否 | integer |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON；`names_only:true` 为 FullName 字符串；否则行 `{AssemblyName,Token,FullName,Namespace,Name,IsPublic,IsNested,IsCompilerGenerated,DeclaringType}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### search_members

Search for MEMBERS (methods / fields / properties / events) by name across all loaded assemblies (or one via assembly_name) — the member counterpart of search_types, together covering dnSpy's Ctrl+Shift+K 'Search Assemblies'. Use this when you have a bare member name (e.g. from decompiled code) but DON'T know its declaring type: each hit returns assembly, declaring_type, member_kind, name, full signature, the MDToken (uint), is_static and is_public. Field hits additionally carry offset/offset_source/il2cpp_token when discoverable (real FieldLayout or Il2CppDumper synthetic [FieldOffset]/[Token] attributes on Unity IL2CPP dumps); omitted otherwise. Feed a renameable token straight into rename_symbol_by_token, or a method/type token into decompile_by_token. Paginated (default page size 10; override with page_size); names_only returns a compact list of signatures.

`{"name":"search_members","arguments":{"query":"sample_query"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `query` | 是 | string |
| `kinds` | 否 | array; items=string |
| `assembly_name` | 否 | string |
| `names_only` | 否 | boolean |
| `page_size` | 否 | integer |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON；`names_only:true` 为 signature 字符串；否则行 `{assembly,declaring_type,member_kind,name,signature,token,is_static,is_public,offset?,offset_source?,il2cpp_token?}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### decompile_type

Decompile a whole TYPE to C# (all members) — the dnSpy 'click the class and read its source' view. Use it to understand a class in one shot, instead of decompiling members one by one. Nested / compiler-generated types are addressable (separator-tolerant: '.', '+', '/'). For very large types the output can be big — prefer get_type_info (compact=true) for an overview, or decompile_method for a single member.

`{"name":"decompile_type","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |

返回（从执行实现整理，非正式 outputSchema）：原样整个类型的 C# 文本，不是 JSON。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### decompile_by_token

Decompile a method (or type) to C# by MDToken alone — no type name needed. Ideal when you have a token from find_callers / find_references / search_string_literals / list_methods but not the declaring type (also the simplest way to reach nested compiler-generated state machines). Tokens are per-module: pass assembly_name to be exact; without it the token must be unambiguous across loaded assemblies. Method tokens get the same async/iterator state-machine rescue as decompile_method.

`{"name":"decompile_by_token","arguments":{"token":0}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `token` | 是 | integer/string |
| `assembly_name` | 否 | string |
| `include_state_machine` | 否 | boolean |

返回（从执行实现整理，非正式 outputSchema）：原样方法或类型的 C# 文本，不是 JSON；跨模块 token 歧义要求 `assembly_name`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### find_callers

Cross-reference: find every method that CALLS a given method (call / callvirt / newobj / ldftn / ldvirtftn), across ALL loaded assemblies — callers routinely live in a different assembly than the target. Identify the target by assembly_name + type_full_name + method_name (with parameter_types or method_token to disambiguate overloads). Each hit returns the caller's type, method, MDToken (uint), signature, the call opcode, the resolved reference, and IL index/offset. Paginated (default page size 10).

`{"name":"find_callers","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{caller_assembly,caller_type,caller_method,caller_token,signature,opcode,reference,il_index,il_offset}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### find_callees

Cross-reference (inverse of find_callers): list what a single method USES — the methods it calls, the fields it reads/writes, and the types it touches in its own body (dnSpy Analyze's 'Uses' node). Identify the method by assembly_name + type_full_name + method_name (with parameter_types or method_token for overloads). Results are deduplicated per referenced member: each row has ref_kind (method/field/type), the full signature, the resolved MDToken (uint) paired with target_assembly (pass both to decompile_by_token — the callee may live in a different assembly than the caller), the set of opcodes, an occurrences count, and first_il_index. token/target_assembly are null for references into assemblies that aren't loaded. Paginated (default page size 10).

`{"name":"find_callees","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{ref_kind,signature,token?,target_assembly?,opcodes,occurrences,first_il_index}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### find_references

Cross-reference: find every IL site that references a target across ALL loaded assemblies. target_kind selects what to look for: 'method' (any call / ldftn / ldtoken of the method), 'field' (any ldfld/stfld/ldsfld/stsfld/ldflda + ldtoken), 'type' (newarr / castclass / isinst / box / ldtoken / etc.), or 'string' (ldstr matching a query). Provide the identity fields for the chosen kind (see properties). Each hit returns the referencing type, method, MDToken (uint), signature, opcode, the resolved reference, and IL index/offset. Paginated (default page size 10). For methods, find_callers is the call-only specialization.

`{"name":"find_references","arguments":{"target_kind":"method"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `target_kind` | 是 | string; enum=["method","field","type","string"] |
| `assembly_name` | 否 | string |
| `type_full_name` | 否 | string |
| `method_name` | 否 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |
| `field_name` | 否 | string |
| `query` | 否 | string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行同 `find_callers`；`target_kind` 决定 method/field/type/string 匹配。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### find_overrides

Cross-reference for virtual / interface methods (dnSpy Analyze 'Overridden By' / 'Overrides'). direction='overridden_by' (default): list every type across ALL loaded assemblies that overrides or implements the target — i.e. the concrete bodies that actually run on a callvirt (find_callers only finds the literal call site to the base/interface slot, never these implementations). If the target is a CLASS virtual/abstract method, this lists overriding subclasses; if it's an INTERFACE method, it lists implementing types (implicit or explicit impls), with is_interface_impl=true on each hit. direction='overrides': given a method, list the base-class virtual(s) it overrides, walking up the base chain (plus explicit interface overrides). Identify the method by assembly_name + type_full_name + method_name (parameter_types / method_token for overloads). Each hit returns type, method, signature, MDToken (uint), assembly, is_abstract, is_interface_impl. Paginated (default page size 10).

`{"name":"find_overrides","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `direction` | 否 | string; enum=["overridden_by","overrides"] |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{type,method,signature,token,assembly,is_abstract,is_interface_impl?}`；后者只在 `overridden_by` 分支。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### find_unity_messages

List the Unity lifecycle / message methods (Awake / Start / Update / FixedUpdate / OnEnable / OnTriggerEnter / OnCollisionEnter / OnGUI / OnDestroy / …) declared on a type, or across a whole assembly. Unity invokes these by name via reflection, so they have NO IL call site — find_callers / find_references can't surface them, yet they're the entry points you hook in a MonoBehaviour. Pass type_full_name for one type, or omit it to sweep the assembly (e.g. 'which classes have an Update()?'). Each hit returns type, message (method name), parameter_types (so you see e.g. OnTriggerEnter(UnityEngine.Collider)), signature, MDToken, is_static — feed token into generate_harmony_patch / decompile_by_token. These only actually fire on MonoBehaviour subclasses. Paginated (default page size 10).

`{"name":"find_unity_messages","arguments":{"assembly_name":"sample_assembly_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 否 | string |
| `page_size` | 否 | integer |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{assembly,type,message,signature,token,parameter_types,is_static}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### find_by_attribute

Find types and/or members decorated with a given custom attribute, across all assemblies (or one). The 'locate by convention' tool: '[SerializeField]' fields (Unity-serialized private state), '[BepInPlugin]' classes (plugin entry points), '[Serializable]' types, '[CompilerGenerated]' members, etc. attribute_name is a case-insensitive substring (or '*' wildcard) matched against the attribute type's short name and FullName, so 'SerializeField' / 'BepInPlugin' / 'Serializable' all work without the 'Attribute' suffix. 'targets' restricts which declaration kinds to scan (type/method/field/property/event; default all). Each hit returns target_kind, declaring_type, name, signature, MDToken, and the matched attribute's FullName. Note: a few 'pseudo' attributes ([Serializable], [NonSerialized], [DllImport], [StructLayout]) compile to metadata flags rather than real custom attributes and are therefore NOT findable here. Paginated (default page size 10).

`{"name":"find_by_attribute","arguments":{"attribute_name":"sample_attribute_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `attribute_name` | 是 | string |
| `targets` | 否 | array; items=string |
| `assembly_name` | 否 | string |
| `page_size` | 否 | integer |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{assembly,target_kind,declaring_type,name,signature,token,attribute}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### search_string_literals

Reverse-lookup: find every method that emits a given string literal (ldstr) across loaded assemblies. Answers "which method uses this string?" — the #1 question in game/Unity RE where logic is wired by string keys (PlayerPrefs keys, scene names, save tokens). Query is a case-insensitive substring by default; use * for wildcards anchored to the whole string (e.g. 'SAVE*'). Optionally restrict to one assembly. Each hit returns the string value, declaring type, method name + MDToken (uint), full signature, and IL index/offset. Paginated (default page size 10).

`{"name":"search_string_literals","arguments":{"query":"sample_query"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `query` | 是 | string |
| `assembly_name` | 否 | string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{value,assembly,type,method,method_token,signature,il_index,il_offset}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### list_string_constants

List all string literals (ldstr) in a type, or in a single method. Type scope includes nested types (compiler-generated closures often hold the interesting keys). Pass method_name (with parameter_types or method_token to disambiguate overloads) to narrow to one method. Each entry returns the string value, declaring type, method name + MDToken (uint), full signature, and IL index/offset. Paginated (default page size 10).

`{"name":"list_string_constants","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 否 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{value,type,method,method_token,signature,il_index,il_offset}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### search_constants

Find where a NUMERIC constant is used in code (ldc.i4* / ldc.i8 / ldc.r4 / ldc.r8) — the number counterpart of search_string_literals, completing dnSpy's Search Assemblies set (types / members / strings / numbers). For magic numbers, item IDs, damage values, thresholds. An integer query (e.g. 1337) matches integer constants; a query with a decimal point (e.g. 0.5) matches floating-point constants (r4 compared at float precision). Pass assembly_name to scope — common values like 0 or 1 are everywhere, so scoping to e.g. 'Assembly-CSharp' is strongly recommended. Each hit returns the matched value, opcode, declaring type, method + MDToken, signature, and IL index/offset. Paginated (default page size 10).

`{"name":"search_constants","arguments":{"value":{}}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `value` | 是 | 结构对象 |
| `assembly_name` | 否 | string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{value,opcode,assembly,type,method,method_token,signature,il_index,il_offset}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### generate_bepinex_plugin

Generate a complete BepInEx plugin: the BaseUnityPlugin shell (Awake wiring Harmony.PatchAll, OnDestroy unpatch) plus a [HarmonyPatch] class per hook. Each hook is resolved against target_assembly so its patch carries the method's REAL signature (__instance, ref <ret> __result, original params by name) — the same signature-aware output as generate_harmony_patch, not an empty stub. An unresolved/overloaded hook degrades to a commented stub (pin it with generate_harmony_patch). For a single patch, prefer generate_harmony_patch; use this to scaffold the whole plugin at once.

`{"name":"generate_bepinex_plugin","arguments":{"plugin_name":"sample_plugin_name","plugin_guid":"sample_plugin_guid","target_assembly":"sample_target_assembly"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `plugin_name` | 是 | string |
| `plugin_guid` | 是 | string |
| `target_assembly` | 是 | string |
| `hooks` | 否 | array; items=object |

返回（从执行实现整理，非正式 outputSchema）：原样 C# 插件源码文本，不是 JSON；无法解析的 hook 会退化为注释/模板，不是保证可编译补丁。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### generate_harmony_patch

Generate a compile-ready HarmonyX patch class for a REAL method, with the correct injected parameters read from its actual signature — unlike the empty stubs from generate_bepinex_plugin. Resolves the method (assembly + type + name, with parameter_types / method_token for overloads) and emits: the [HarmonyPatch(typeof(T), "M")] attribute (adding a new Type[]{...} array when the name is overloaded), `__instance` for instance methods, `ref <ReturnType> __result` for a non-void postfix, and the original parameters by name (positional __0/__1 if names were stripped). patch_type = postfix (default, for reading/altering the return value), prefix (returns bool — return false to skip the original), or transpiler (IL-rewrite skeleton). Non-primitive types are fully qualified so it compiles as-is.

`{"name":"generate_harmony_patch","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `patch_type` | 否 | string; enum=["postfix","prefix","transpiler"] |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |

返回（从执行实现整理，非正式 outputSchema）：原样 C# Harmony patch 源码文本，不是 JSON。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### get_type_fields

Get fields from a type matching a name pattern (supports wildcards like *Bonus*). Supports pagination with default page size of 10 fields. Each field row also carries an Offset when one is discoverable — either a real [StructLayout(Explicit)] FieldLayout row (OffsetSource='field-layout') or an Il2CppDumper 'dummy DLL' synthetic [FieldOffset(Offset="0x330")] attribute (OffsetSource='il2cpp', typical of Unity IL2CPP dumps). The paired Il2CppDumper [Token(Token="0x4000B02")] surfaces as Il2CppToken. All three keys are omitted on normal managed fields with no offset info.

`{"name":"get_type_fields","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","pattern":"sample_pattern"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `pattern` | 是 | string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：JSON `{Type,Pattern,MatchCount,ReturnedCount,Fields:[{Name,Type,IsPublic,IsStatic,IsLiteral,IsReadOnly,Attributes,Offset?,OffsetSource?,Il2CppToken?}],nextCursor?}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### get_type_property

Get detailed information about a specific property from a type

`{"name":"get_type_property","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","property_name":"sample_property_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `property_name` | 是 | string |

返回（从执行实现整理，非正式 outputSchema）：JSON `{Name,Type,CanRead,CanWrite,GetMethod?,SetMethod?,Attributes,CustomAttributes}`；访问器对象有 `{Name,IsPublic,IsStatic}`，缺失时为 null。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### find_path_to_type

Find property/field chains connecting two types through their members. (e.g., PlayerState -> RpBonus)

`{"name":"find_path_to_type","arguments":{"assembly_name":"sample_assembly_name","from_type":"sample_from_type","to_type":"sample_to_type"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `from_type` | 是 | string |
| `to_type` | 是 | string |
| `max_depth` | 否 | number |

返回（从执行实现整理，非正式 outputSchema）：找到时 JSON `{FromType,ToType,PathsFound,Paths}`；未找到时返回普通文本 `No path found ...`，不设置 `isError`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### list_methods

List methods of a type with unambiguous identifiers. Each entry includes the MethodDef token, parameter rows with Param tokens, method generic-parameter rows with GenericParam tokens, and parameter_types for overload disambiguation. Feed any renameable token to rename_symbol_by_token. Paginated (default page size 10).

`{"name":"list_methods","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `cursor` | 否 | string |

返回（从执行实现整理，非正式 outputSchema）：分页 JSON，行 `{name,token,signature,return_type,parameter_types,parameters,generic_parameters,is_static,is_virtual,is_abstract,has_body}`。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### get_method_il

Return the IL body of a method: instructions (index, offset, opcode, operand), locals, exception handlers, and body flags. Operand format is tagged and round-trips with patch_method_il (e.g. 'int:42', 'str:"hello"', 'method:Ns.T::M(System.Int32):System.Void', 'label:7'). For overloaded methods, pass parameter_types or method_token to disambiguate.

`{"name":"get_method_il","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |

返回（从执行实现整理，非正式 outputSchema）：JSON `{method:{name,token,signature},instructions:[{index,offset,opcode,operand}],max_stack,init_locals,keep_old_max_stack,local_var_sig_tok,locals,exception_handlers,has_pending_patch}`；`has_pending_patch` 经适配器按当前检查点状态补正。 `2025-06-18` 只在文本可解析为 JSON 时镜像到 `structuredContent`；纯代码/普通文本没有此字段。失败通常是 `isError:true` 的执行错误文本。

### patch_method_il

按原始 IL 指令索引执行 replace/insert/delete/set_init_locals 兼容批次，并经结构化事务提交检查点；不要按旧通告描述假设仍只在内存暂存。

`{"name":"patch_method_il","arguments":{"assembly_name":"<已加载程序集>","type_full_name":"<前序类型全名>","method_name":"<前序方法名>","edits":[{"op":"set_init_locals","value":true}]}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |
| `edits` | 是 | array; items=object |
| `optimize_macros` | 否 | boolean |

返回：本工具的完整成功投影和条件分支见附录 A2；旧兼容修改通过编辑协调器生成检查点，导出仅限 ArtifactRoot。失败可返回编辑域信封或执行错误文本；调试/事务门会先拒绝不允许的写入。此处调用只是 schema 形状示意，实际需用前序读取取得目标 token/名称、当前检查点与事务状态，不能将占位值直接用于真实目标。

### force_return

生成固定返回体并经结构化事务提交检查点；返回类型和值组合受实现限制。

`{"name":"force_return","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `value` | 否 | 结构对象 |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |

返回：本工具的完整成功投影和条件分支见附录 A2；旧兼容修改通过编辑协调器生成检查点，导出仅限 ArtifactRoot。失败可返回编辑域信封或执行错误文本；调试/事务门会先拒绝不允许的写入。此处调用只是 schema 形状示意，实际需用前序读取取得目标 token/名称、当前检查点与事务状态，不能将占位值直接用于真实目标。

### nop_method

生成立即 ret 的空方法体并经结构化事务提交检查点。

`{"name":"nop_method","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |

返回：本工具的完整成功投影和条件分支见附录 A2；旧兼容修改通过编辑协调器生成检查点，导出仅限 ArtifactRoot。失败可返回编辑域信封或执行错误文本；调试/事务门会先拒绝不允许的写入。此处调用只是 schema 形状示意，实际需用前序读取取得目标 token/名称、当前检查点与事务状态，不能将占位值直接用于真实目标。

### revert_method_il

仅对当前兼容检查点执行受限 Undo，不跨越其他历史 head；返回当前 IL 投影。

`{"name":"revert_method_il","arguments":{"assembly_name":"sample_assembly_name","type_full_name":"sample_type_full_name","method_name":"sample_method_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `type_full_name` | 是 | string |
| `method_name` | 是 | string |
| `parameter_types` | 否 | array; items=string |
| `method_token` | 否 | integer/string |

返回：本工具的完整成功投影和条件分支见附录 A2；旧兼容修改通过编辑协调器生成检查点，导出仅限 ArtifactRoot。失败可返回编辑域信封或执行错误文本；调试/事务门会先拒绝不允许的写入。此处调用只是 schema 形状示意，实际需用前序读取取得目标 token/名称、当前检查点与事务状态，不能将占位值直接用于真实目标。

### rename_symbol_by_token

以 token 定位符号并经结构化事务提交；`enum_members` 批量值映射有额外 members 约束。

`{"name":"rename_symbol_by_token","arguments":{"target_kind":"type","token":"0x02000001","new_name":"RenamedType","assembly_name":"<已加载程序集>"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `target_kind` | 是 | string; enum=["type","class","enum","interface","struct","delegate","method","field","enum_member","enum_members","property","event","parameter","generic_parameter"] |
| `token` | 是 | integer/string |
| `new_name` | 否 | string |
| `members` | 否 | array; items=object |
| `assembly_name` | 否 | string |

返回：本工具的完整成功投影和条件分支见附录 A2；旧兼容修改通过编辑协调器生成检查点，导出仅限 ArtifactRoot。失败可返回编辑域信封或执行错误文本；调试/事务门会先拒绝不允许的写入。此处调用只是 schema 形状示意，实际需用前序读取取得目标 token/名称、当前检查点与事务状态，不能将占位值直接用于真实目标。

### save_assembly

从当前精确检查点导出到 ArtifactRoot，不覆盖源样本、也不创建原地备份。

`{"name":"save_assembly","arguments":{"assembly_name":"sample_assembly_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string |
| `output_path` | 否 | string |

返回：本工具的完整成功投影和条件分支见附录 A2；旧兼容修改通过编辑协调器生成检查点，导出仅限 ArtifactRoot。失败可返回编辑域信封或执行错误文本；调试/事务门会先拒绝不允许的写入。此处调用只是 schema 形状示意，实际需用前序读取取得目标 token/名称、当前检查点与事务状态，不能将占位值直接用于真实目标。

### 旧 `patch_method_il` 子操作与 tagged operand

`edits` 每项恰为 `{op:"replace",index,opcode,operand}`、`{op:"insert",index,opcode,operand}`、`{op:"delete",index}` 或 `{op:"set_init_locals",value:boolean}`；每项禁止额外字段。`insert` 在原索引前插入，批次后续索引都指**批次前**指令序列；`optimize_macros` 默认 false。合法 opcode 还必须与 operand 类型匹配，静态 schema 的 `opcode:string` 不能保证这一点。

operand 是带标签字符串：无操作数用空串；`int:<Int32>`、`int8:<SByte>`/`uint8:<Byte>`、`long:<Int64>`、`float:<Single>`、`double:<Double>`、`str:<JSON 字符串字面量>`、`method:<完整方法名>`、`field:<完整字段名>`、`type:<完整类型名>`、`token:method|field|type:<完整名>`、`label:<原始指令索引>`、`switch:[<原始索引>,...]`、`local:<局部索引>`、`arg:<参数索引>`。名称须能从已加载模块唯一解析，索引须在范围内；`calli`/InlineSig 及其他未列 operand 类型当前拒绝。`get_method_il` 返回同一 tagged 投影（部分只读/未知 operand 可能不可反向提交），请先用它取得原索引。

`rename_symbol_by_token.target_kind` 的 singular 与 `enum_members` 分支、token 字符串/整数、成员 `{name,value}` 的完整形状在附录 A；`force_return.value` 的布尔/数值/null/default 运行时类型规则由返回类型决定。六旧写工具都受当前 debug/事务 idle 门与兼容事务 owner 限制，不是独立的原地文件修改通道。

## 4. 动态调试工具

内部通用成功形状：`{schema_version:"dnspy.debug.v1",ok:true,debug_context:{session_id?,generation,pause_epoch,event_cursor,state},result:{...},warnings:[],untrusted_sample_data:false}`；失败用 `ok:false,error:{code,message,recovery,current_state,required_states,retry_after_ms?,details?}`，没有 `result`。域错误码：`DEBUG_DISABLED`、`CAPABILITY_UNAVAILABLE`、`INVALID_STATE`、`STALE_HANDLE`、`TARGET_MISMATCH`、`NOT_FOUND`、`ALREADY_EXISTS`、`LIMIT_EXCEEDED`、`TIMEOUT`、`OWNERSHIP_LOST`、`REQUEST_ID_REUSE`、`INTERNAL_ERROR`。`LIMIT_EXCEEDED` 的 retry_after_ms=1000，`TIMEOUT` 为 0；`UNAUTHORIZED` 不属于此域。

调试 `session_id` 属于一次目标会话，`generation` 在重启/代际变化后更新；`pause_epoch` 随暂停/继续变化。线程、帧、值、模块等 opaque handle 只可在其会话/代次/暂停上下文中使用；看到 `STALE_HANDLE` 应重新查询，不能自行构造。事件通过单调 `event_cursor` 读取/等待；被淘汰的旧游标依 schema 返回缺失/省略信息，不要把某次 `events=[]` 当事件从未发生。副作用调用携带 `request_id`，相同请求重试可命中幂等记录，异参同 ID 为 `REQUEST_ID_REUSE`。

### 调试调用与状态关联（示意，非异常事件绿态证明）

```json
{"name":"debug_capabilities","arguments":{}}
{"name":"debug_launch","arguments":{"request_id":"00000000-0000-0000-0000-000000000001","target_path":"C:\\Samples\\Demo.exe","expected_sha256":"0000000000000000000000000000000000000000000000000000000000000000","launch_mode":"net48-exe","architecture":"x64","break_kind":"entry"}}
{"name":"debug_read_events","arguments":{"session_id":"sample_session_1","after_cursor":0}}
{"name":"debug_status","arguments":{}}
{"name":"debug_list_threads","arguments":{"session_id":"sample_session_1","generation":1,"pause_epoch":1}}
{"name":"debug_get_stack","arguments":{"session_id":"sample_session_1","generation":1,"pause_epoch":1,"thread_handle":"sample_thread_1"}}
{"name":"debug_continue","arguments":{"request_id":"00000000-0000-0000-0000-000000000002","session_id":"sample_session_1","generation":1,"pause_epoch":1}}
{"name":"debug_terminate","arguments":{"request_id":"00000000-0000-0000-0000-000000000003","session_id":"sample_session_1","generation":1}}
```

以上给出 schema 合法的示意值，**零 SHA 和 sample_session_1 不是真实目标/会话**；实际调用时必须把 SHA 替换为目标文件哈希，把 `session_id`、`generation`、`pause_epoch` 替换为前序响应值。`debug_launch` 是执行门，未验证环境不要照示例直接启动。

调试响应示例（取冻结契约 fixture 的 `expected_response`，**不是本轮真实 VM 成功事件**；展示 launch 与事件页的 content/structuredContent 镜像）：

```json
{"jsonrpc":"2.0","id":"debug_launch/valid","result":{"content":[{"type":"text","text":"{\"schema_version\":\"dnspy.debug.v1\",\"ok\":true,\"debug_context\":{\"session_id\":\"c2Vzc2lvbi0wMDAx\",\"generation\":1,\"pause_epoch\":2,\"event_cursor\":10,\"state\":\"idle\"},\"result\":{\"session_id\":\"c2Vzc2lvbi0wMDAx\",\"generation\":1,\"state\":\"starting\",\"claim_deadline_utc\":\"2026-08-27T08:00:30.000Z\",\"launch_mode\":\"net48-exe\",\"runtime_family\":\"net48\",\"architecture\":\"x64\",\"file_identities\":[{\"role\":\"target\",\"object_kind\":\"file\",\"final_path\":\"C:\\\\samples\\\\Sample.exe\",\"volume_serial\":\"0x1a1a1a1a1a1a1a1a\",\"file_id\":\"2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]},\"warnings\":[],\"untrusted_sample_data\":false}"}],"structuredContent":{"schema_version":"dnspy.debug.v1","ok":true,"debug_context":{"session_id":"c2Vzc2lvbi0wMDAx","generation":1,"pause_epoch":2,"event_cursor":10,"state":"idle"},"result":{"session_id":"c2Vzc2lvbi0wMDAx","generation":1,"state":"starting","claim_deadline_utc":"2026-08-27T08:00:30.000Z","launch_mode":"net48-exe","runtime_family":"net48","architecture":"x64","file_identities":[{"role":"target","object_kind":"file","final_path":"C:\\samples\\Sample.exe","volume_serial":"0x1a1a1a1a1a1a1a1a","file_id":"2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]},"warnings":[],"untrusted_sample_data":false}}}
```

```json
{"jsonrpc":"2.0","id":"debug_read_events/valid","result":{"content":[{"type":"text","text":"{\"schema_version\":\"dnspy.debug.v1\",\"ok\":true,\"debug_context\":{\"session_id\":\"c2Vzc2lvbi0wMDAx\",\"generation\":1,\"pause_epoch\":2,\"event_cursor\":10,\"state\":\"running\"},\"result\":{\"events\":[],\"next_cursor\":0,\"earliest_cursor\":0,\"events_lost\":0},\"warnings\":[],\"untrusted_sample_data\":false}"}],"structuredContent":{"schema_version":"dnspy.debug.v1","ok":true,"debug_context":{"session_id":"c2Vzc2lvbi0wMDAx","generation":1,"pause_epoch":2,"event_cursor":10,"state":"running"},"result":{"events":[],"next_cursor":0,"earliest_cursor":0,"events_lost":0},"warnings":[],"untrusted_sample_data":false}}}
```

### debug_capabilities

`{"name":"debug_capabilities","arguments":{}}`

无顶层参数；传 `{}`。

成功 `result` 字段：`debug_enabled`、`schema_version`、`extension_version`、`dnspy_api`、`host_architecture`、`ownership_model`、`dedicated_instance_required`、`dedicated_instance_acknowledged`、`attach_supported`、`tools`、`runtime_matrix`、`execution_environment`、`security`、`artifact_policy`、`limits`、`unsupported`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_capabilities_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_status

`{"name":"debug_status","arguments":{}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 否 | 定义 `session_id` |

成功 `result` 字段：`state`、`active_session_id`、`last_session_id`、`owned_process`、`observed_process_state`、`runtime_family`、`architecture`、`start_kind`、`launch_mode`、`fault`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_status_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_launch

`{"name":"debug_launch","arguments":{"request_id":"00000000-0000-0000-0000-000000000001","target_path":"sample_target_path","expected_sha256":"0000000000000000000000000000000000000000000000000000000000000000","launch_mode":"auto","architecture":"x86"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | 定义 `uuid` |
| `target_path` | 是 | string; minLength=1 |
| `expected_sha256` | 是 | 定义 `sha256_hex` |
| `launch_mode` | 是 | 定义 `launch_mode` |
| `architecture` | 是 | 定义 `architecture` |
| `target_argv` | 否 | array; items=string |
| `working_directory` | 否 | string; minLength=1 |
| `break_kind` | 否 | 定义 `break_kind` |
| `host_path` | 否 | string; minLength=1 |
| `host_sha256` | 否 | 定义 `sha256_hex` |
| `host_argv` | 否 | array; items=string |
| `harness_path` | 否 | string; minLength=1 |
| `harness_sha256` | 否 | 定义 `sha256_hex` |
| `harness_argv` | 否 | array; items=string |
| `exception_policy` | 否 | 定义 `exception_policy` |

成功 `result` 字段：`session_id`、`generation`、`state`、`claim_deadline_utc`、`launch_mode`、`runtime_family`、`architecture`、`file_identities`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_launch_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

启动及重启首个暂停的 `paused.payload.reason` 是事件域枚举：`break_kind=process` 对应 `process`，`module_cctor_or_entry` 对应同名值，`entry` 对应 `entry`；`none` 不要求首个暂停，若后续出现无可归因暂停则可为 `unknown`。它不同于 dnSpy 内部的 `process_break`/`entry_point_break` 名称，也不同于 `debug_pause` 控制结果的 `reason` 枚举。显式继续产生的 `continued.payload.reason` 为 `manual`，不是内部动作名 `continue`；`process_exited.payload.process_handle` 使用本会话的 `proc-<pid>` 句柄。

### debug_pause

`{"name":"debug_pause","arguments":{"session_id":"sample_session_id","generation":0,"request_id":"00000000-0000-0000-0000-000000000001"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |

成功 `result` 字段：`state`、`pause_epoch`、`reason`、`request_effect`、`thread_handle`、`location`、`breakpoint_id`、`step_id`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_pause_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_continue

`{"name":"debug_continue","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"request_id":"00000000-0000-0000-0000-000000000001"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |

成功 `result` 字段：`state`、`pause_epoch`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_continue_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_restart

`{"name":"debug_restart","arguments":{"session_id":"sample_session_id","generation":0,"request_id":"00000000-0000-0000-0000-000000000001"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |

成功 `result` 字段：`state`、`generation`、`claim_deadline_utc`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_restart_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_terminate

`{"name":"debug_terminate","arguments":{"session_id":"sample_session_id","generation":0,"request_id":"00000000-0000-0000-0000-000000000001"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |

成功 `result` 字段：`state`、`exit_code`、`terminal_cursor`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_terminate_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_read_events

`{"name":"debug_read_events","arguments":{"session_id":"sample_session_id"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `after_cursor` | 否 | 定义 `non_negative_int` |
| `limit` | 否 | integer; minimum=1; maximum=100 |
| `kinds` | 否 | array; minItems=0; maxItems=21; items=定义 `event_kind` |

成功 `result` 字段：`events`、`next_cursor`、`earliest_cursor`、`events_lost`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_read_events_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_wait_event

`{"name":"debug_wait_event","arguments":{"session_id":"sample_session_id"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `after_cursor` | 否 | 定义 `non_negative_int` |
| `limit` | 否 | integer; minimum=1; maximum=50 |
| `kinds` | 否 | array; minItems=0; maxItems=21; items=定义 `event_kind` |
| `timeout_ms` | 否 | integer; minimum=0; maximum=30000 |

成功 `result` 字段：`events`、`next_cursor`、`earliest_cursor`、`events_lost`、`timed_out`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_wait_event_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_set_breakpoint

`{"name":"debug_set_breakpoint","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"request_id":"00000000-0000-0000-0000-000000000001","module_handle":"sample_module_handle","mvid":"00000000-0000-0000-0000-000000000001","method_token":"0x06000001","il_offset":0}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |
| `module_handle` | 是 | 定义 `opaque_handle` |
| `module_sha256` | 否 | 定义 `sha256_hex` |
| `mvid` | 是 | 定义 `mvid` |
| `method_token` | 是 | 定义 `method_token` |
| `il_offset` | 是 | 定义 `non_negative_int` |
| `enabled` | 否 | boolean |

成功 `result` 字段：`breakpoint`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_set_breakpoint_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_list_breakpoints

`{"name":"debug_list_breakpoints","arguments":{"session_id":"sample_session_id","generation":0}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `page_size` | 否 | integer; minimum=1; maximum=100 |
| `page_cursor` | 否 | 定义 `page_cursor` |
| `enabled` | 否 | boolean |

成功 `result` 字段：`items`、`next_page_cursor`、`truncated`、`total_known`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_list_breakpoints_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_set_breakpoint_enabled

`{"name":"debug_set_breakpoint_enabled","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"request_id":"00000000-0000-0000-0000-000000000001","breakpoint_id":"sample_breakpoint_id","enabled":false}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |
| `breakpoint_id` | 是 | 定义 `opaque_handle` |
| `enabled` | 是 | boolean |

成功 `result` 字段：`breakpoint`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_set_breakpoint_enabled_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_remove_breakpoint

`{"name":"debug_remove_breakpoint","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"request_id":"00000000-0000-0000-0000-000000000001","breakpoint_id":"sample_breakpoint_id"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |
| `breakpoint_id` | 是 | 定义 `opaque_handle` |

成功 `result` 字段：`removed`、`breakpoint_id`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_remove_breakpoint_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_list_threads

`{"name":"debug_list_threads","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `page_size` | 否 | integer; minimum=1; maximum=100 |
| `page_cursor` | 否 | 定义 `page_cursor` |

成功 `result` 字段：`items`、`next_page_cursor`、`truncated`、`total_known`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_list_threads_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_get_stack

`{"name":"debug_get_stack","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"thread_handle":"sample_thread_handle"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `thread_handle` | 是 | 定义 `opaque_handle` |
| `page_size` | 否 | integer; minimum=1; maximum=100 |
| `page_cursor` | 否 | 定义 `page_cursor` |

成功 `result` 字段：`items`、`next_page_cursor`、`truncated`、`total_known`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_get_stack_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_step

`{"name":"debug_step","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"request_id":"00000000-0000-0000-0000-000000000001","thread_handle":"sample_thread_handle","kind":"into"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |
| `thread_handle` | 是 | 定义 `opaque_handle` |
| `kind` | 是 | 结构对象; enum=["into","over","out"] |

成功 `result` 字段：`step_id`、`state`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_step_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_get_locals

`{"name":"debug_get_locals","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"frame_handle":"sample_frame_handle"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `frame_handle` | 是 | 定义 `opaque_handle` |
| `page_size` | 否 | integer; minimum=1; maximum=100 |
| `page_cursor` | 否 | 定义 `page_cursor` |

成功 `result` 字段：`items`、`next_page_cursor`、`truncated`、`total_known`、`evaluation_mode`、`budgets`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_get_locals_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_expand_value

`{"name":"debug_expand_value","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"value_handle":"sample_value_handle"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `value_handle` | 是 | 定义 `opaque_handle` |
| `depth` | 否 | integer; minimum=1; maximum=4 |
| `page_size` | 否 | integer; minimum=1; maximum=100 |
| `page_cursor` | 否 | 定义 `page_cursor` |

成功 `result` 字段：`items`、`next_page_cursor`、`truncated`、`total_known`、`evaluation_mode`、`budgets`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_expand_value_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_list_modules

`{"name":"debug_list_modules","arguments":{"session_id":"sample_session_id","generation":0}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `page_size` | 否 | integer; minimum=1; maximum=100 |
| `page_cursor` | 否 | 定义 `page_cursor` |

成功 `result` 字段：`items`、`next_page_cursor`、`truncated`、`total_known`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_list_modules_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_read_memory

`{"name":"debug_read_memory","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"module_handle":"sample_module_handle","address":"0x0","length":1}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `module_handle` | 是 | 定义 `opaque_handle` |
| `address` | 是 | 定义 `address` |
| `length` | 是 | integer; minimum=1; maximum=65536 |
| `encoding` | 否 | 结构对象; enum=["base64","hex"] |

成功 `result` 字段：`module_handle`、`address`、`length`、`encoding`、`data`、`read_semantics`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_read_memory_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_dump_module

`{"name":"debug_dump_module","arguments":{"session_id":"sample_session_id","generation":0,"pause_epoch":0,"request_id":"00000000-0000-0000-0000-000000000001","module_handle":"sample_module_handle"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `pause_epoch` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |
| `module_handle` | 是 | 定义 `opaque_handle` |
| `relative_name` | 否 | string; minLength=1; maxLength=128; pattern="^(?!\\.{1,2}$)(?!.*[. ]$)[^/\\\\:\\s]+$" |

成功 `result` 字段：`artifact`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_dump_module_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

### debug_set_exception_policy

`{"name":"debug_set_exception_policy","arguments":{"session_id":"sample_session_id","generation":0,"request_id":"00000000-0000-0000-0000-000000000001","policy":{"break_on":"unhandled"}}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `session_id` | 是 | 定义 `session_id` |
| `generation` | 是 | 定义 `non_negative_int` |
| `request_id` | 是 | 定义 `uuid` |
| `policy` | 是 | 定义 `exception_policy` |

成功 `result` 字段：`previous`、`current`。完整字段类型、条件分支及所有嵌套结构见附录 B 的 `debug_set_exception_policy_result`、相关 `$defs`；这里的 schema 是冻结调试契约，不是运行成功断言。

## 5. 事务编辑与编译工具

编辑变更/导出需要已初始化、可拥有事务的 MCP 会话；`edit_status` 可返回非属主受限视图，`edit_history` 对未初始化会话只给 summary。常见成功信封：`{schema_version:"dnspy.edit.v1",ok:true,state,result,warnings:[],untrusted_sample_data:true}`；失败为 `ok:false,error:{code,message,current_state,recovery,details?}`，`isError:true`。输入/输出的精确 schema、额外字段策略和每个嵌套对象在附录 C 同文件；`edit_compile` 的编译结果为执行实现整理，独立于 JSON schema 文件。

`edit_begin` 只建立单个纯托管单模块的进程级私有事务；`edit_apply` 以 `expected_revision` 严格推进私有修订；`edit_review` 固定该修订并返回 diff/风险；`edit_commit` 必须带匹配 `review_id`、`review_revision` 与全部 `confirmed_risk_ids`，通过后更改 live 模块并生成可恢复检查点。`edit_rollback` 丢弃私有事务。`edit_history` 浏览 lineage/checkpoint；`edit_undo`/`edit_redo` 明确预期 head，分支 redo 必选 child；`edit_restore` 先评估/按 exact 或漂移规则显式恢复；`edit_recover` 收尾故障；`edit_accept_live` 明确接纳 UI 已偏离模块为新基线。不要在旧 session 重用所有权或把 checkpoint ID 当事务 ID。

编辑域主要失败码：`EDIT_TRANSACTION_BUSY`、`EDIT_TRANSACTION_NOT_FOUND`、`EDIT_OWNER_REQUIRED`、`EDIT_OWNER_MISMATCH`、`EDIT_REVISION_CONFLICT`、`EDIT_LIVE_MODULE_CONFLICT`、`EDIT_REVIEW_STALE`、`EDIT_VALIDATION_FAILED`、`EDIT_RISK_CONFIRMATION_REQUIRED`、`EDIT_CAPABILITY_UNAVAILABLE`、`EDIT_CAPACITY_EXCEEDED`、`EDIT_DEBUG_NOT_IDLE`、`EDIT_LIVE_STATE_UNKNOWN`、`EDIT_CHECKPOINT_INVALID`、`EDIT_CHECKPOINT_COMMIT_FAILED`、`EDIT_CHECKPOINT_CLEANUP_FAILED`、`EDIT_EXPORT_BLOCKED`、`EDIT_REPLAY_CONFIRMATION_REQUIRED`、`EDIT_REPLAY_UNVERIFIED`、`EDIT_OPERATION_VERSION_UNSUPPORTED`、`EDIT_HISTORY_CONFLICT`、`EDIT_BRANCH_SELECTION_REQUIRED`、`EDIT_LINEAGE_DIVERGED`、`EDIT_SOURCE_IDENTITY_CONFLICT`、`EDIT_RECOVERY_NOT_FOUND`、`REQUEST_ID_REUSE`。按响应的 `recovery` 和当前状态处理，不把协议参数错误当业务拒绝。

### edit_begin

`{"name":"edit_begin","arguments":{"request_id":"sample_request_id","assembly_name":"sample_assembly_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `assembly_name` | 是 | string; minLength=1; maxLength=512 |
| `module_mvid` | 否 | string; minLength=1; maxLength=36 |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `source_family_id` | 否 | string; pattern="^family-[0-9a-f]{32}$" |

输出 schema 顶层字段：`capabilities`、`capacity`、`fingerprints`、`limits`、`source`、`transaction`、`history`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_begin.outputSchema`；参数复杂对象见 `edit_begin.inputSchema`。

### edit_status

`{"name":"edit_status","arguments":{}}`

无顶层参数；传 `{}`。

输出 schema 顶层字段：成功 `result` 分支 `busy`、`state`、`history`、`recovery`、`capacity` / `busy`、`state`、`owner_transport_kind` / `busy`、`state`、`transaction`、`fingerprints`、`review`、`history`、`recovery`、`capacity`、`risks`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_status.outputSchema`；参数复杂对象见 `edit_status.inputSchema`。

### edit_apply

`{"name":"edit_apply","arguments":{"request_id":"sample_request_id","transaction_id":"sample_transaction_id","expected_revision":0,"operation":{"kind":"type_add","name":"sample_name"}}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `expected_revision` | 是 | integer; minimum=0; maximum=4294967295 |
| `operation` | 是 | oneOf(39 分支) |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `transaction_id` | 是 | string; minLength=1; maxLength=128 |

输出 schema 顶层字段：`capacity`、`created_object_ids`、`diffs`、`fingerprints`、`kind`、`operation_index`、`review_cleared`、`risks`、`transaction`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_apply.outputSchema`；参数复杂对象见 `edit_apply.inputSchema`。

### edit_import

`{"name":"edit_import","arguments":{"request_id":"import-1","transaction_id":"<edit_begin 返回 ID>","expected_revision":0,"compile_id":"<edit_compile 返回 ID>","targets":[{"compiled":"<编译产物方法标识>","action":"add"}]}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `transaction_id` | 是 | string; minLength=1; maxLength=128 |
| `expected_revision` | 是 | integer; minimum=0; maximum=4294967295 |
| `compile_id` | 是 | string; minLength=1; maxLength=128 |
| `targets` | 是 | array; minItems=1; maxItems=256; items=object; extra=禁止 |

输出 schema 顶层字段：`transaction`、`fingerprints`、`diffs`、`risks`、`review_cleared`、`capacity`、`operation_count`、`import`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_import.outputSchema`；参数复杂对象见 `edit_import.inputSchema`。

### edit_impact_scan

`{"name":"edit_impact_scan","arguments":{"request_id":"sample_request_id","transaction_id":"sample_transaction_id","expected_revision":0}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `transaction_id` | 是 | string; minLength=1; maxLength=128 |
| `expected_revision` | 是 | integer; minimum=0; maximum=4294967295 |

输出 schema 顶层字段：`transaction`、`impact`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_impact_scan.outputSchema`；参数复杂对象见 `edit_impact_scan.inputSchema`。

### edit_resource_import

`{"name":"edit_resource_import","arguments":{"request_id":"sample_request_id","transaction_id":"sample_transaction_id","expected_revision":0,"vm_path":"sample_vm_path","resource_name":"sample_resource_name"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `transaction_id` | 是 | string; minLength=1; maxLength=128 |
| `expected_revision` | 是 | integer; minimum=0; maximum=4294967295 |
| `vm_path` | 是 | string; minLength=2; maxLength=1024 |
| `resource_name` | 是 | string; minLength=1; maxLength=512 |
| `resource_type` | 否 | string; enum=["embedded","linked","win32"] |
| `type_id` | 否 | integer; minimum=0; maximum=65535 |
| `type_name` | 否 | string; minLength=1; maxLength=512 |
| `name_id` | 否 | integer; minimum=0; maximum=65535 |
| `lang_id` | 否 | integer; minimum=0; maximum=65535 |

输出 schema 顶层字段：`capacity`、`created_object_ids`、`diffs`、`fingerprints`、`kind`、`operation_index`、`review_cleared`、`risks`、`transaction`、`import`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_resource_import.outputSchema`；参数复杂对象见 `edit_resource_import.inputSchema`。

### edit_resource_export

`{"name":"edit_resource_export","arguments":{"request_id":"sample_request_id","assembly_name":"sample_assembly_name","resource_name":"sample_resource_name","output_path":"sample_output_path"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `assembly_name` | 是 | string; minLength=1; maxLength=512 |
| `resource_name` | 是 | string; minLength=1; maxLength=512 |
| `output_path` | 是 | string; minLength=1; maxLength=512 |
| `resource_type` | 否 | string; enum=["embedded","linked","win32"] |
| `type_id` | 否 | integer; minimum=0; maximum=65535 |
| `type_name` | 否 | string; minLength=1; maxLength=512 |
| `name_id` | 否 | integer; minimum=0; maximum=65535 |
| `lang_id` | 否 | integer; minimum=0; maximum=65535 |

输出 schema 顶层字段：`export`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_resource_export.outputSchema`；参数复杂对象见 `edit_resource_export.inputSchema`。

### edit_review

`{"name":"edit_review","arguments":{"request_id":"sample_request_id","transaction_id":"sample_transaction_id","expected_revision":0}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `dynamic_validation` | 否 | object; extra=禁止 |
| `expected_revision` | 是 | integer; minimum=0; maximum=4294967295 |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `transaction_id` | 是 | string; minLength=1; maxLength=128 |

输出 schema 顶层字段：`diffs`、`dynamic_validation`、`fingerprints`、`limits`、`review`、`risks`、`roundtrip_validation`、`structural_validation`、`transaction`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_review.outputSchema`；参数复杂对象见 `edit_review.inputSchema`。

### edit_rollback

`{"name":"edit_rollback","arguments":{"request_id":"sample_request_id","transaction_id":"sample_transaction_id"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `transaction_id` | 是 | string; minLength=1; maxLength=128 |

输出 schema 顶层字段：`end_reason`、`original_live_fingerprint`、`released`、`rolled_back`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_rollback.outputSchema`；参数复杂对象见 `edit_rollback.inputSchema`。

### edit_commit

`{"name":"edit_commit","arguments":{"request_id":"sample_request_id","transaction_id":"edit-00000000000000000000000000000000","expected_revision":0,"review_id":"review-00000000000000000000000000000000","review_revision":0,"confirmed_risk_ids":[]}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `transaction_id` | 是 | string; pattern="^edit-[0-9a-f]{32}$" |
| `expected_revision` | 是 | integer; minimum=0; maximum=4294967295 |
| `review_id` | 是 | string; pattern="^review-[0-9a-f]{32}$" |
| `review_revision` | 是 | integer; minimum=0; maximum=4294967295 |
| `confirmed_risk_ids` | 是 | array; maxItems=256; items=string; minLength=1; maxLength=128 |

输出 schema 顶层字段：`checkpoint`、`history`、`fingerprints`、`confirmed_risks`、`live_recovery`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_commit.outputSchema`；参数复杂对象见 `edit_commit.inputSchema`。

### edit_history

`{"name":"edit_history","arguments":{}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `lineage_id` | 否 | string; pattern="^lineage-[0-9a-f]{32}$" |
| `checkpoint_id` | 否 | string; pattern="^checkpoint-[0-9a-f]{32}$" |
| `cursor` | 否 | string; maxLength=1024 |
| `page_size` | 否 | integer; minimum=1; maximum=100 |

输出 schema 顶层字段：`view`、`lineages`、`checkpoints`、`checkpoint`、`next_cursor`、`capacity`、`recovery`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_history.outputSchema`；参数复杂对象见 `edit_history.inputSchema`。

### edit_undo

`{"name":"edit_undo","arguments":{"request_id":"sample_request_id","lineage_id":"lineage-00000000000000000000000000000000","expected_checkpoint_id":"checkpoint-00000000000000000000000000000000"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `lineage_id` | 是 | string; pattern="^lineage-[0-9a-f]{32}$" |
| `expected_checkpoint_id` | 是 | string; pattern="^checkpoint-[0-9a-f]{32}$" |

输出 schema 顶层字段：`from_checkpoint_id`、`to_checkpoint_id`、`history`、`replay`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_undo.outputSchema`；参数复杂对象见 `edit_undo.inputSchema`。

### edit_redo

`{"name":"edit_redo","arguments":{"request_id":"sample_request_id","lineage_id":"lineage-00000000000000000000000000000000","expected_checkpoint_id":"checkpoint-00000000000000000000000000000000"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `lineage_id` | 是 | string; pattern="^lineage-[0-9a-f]{32}$" |
| `expected_checkpoint_id` | 是 | string; pattern="^checkpoint-[0-9a-f]{32}$" |
| `child_checkpoint_id` | 否 | string; pattern="^checkpoint-[0-9a-f]{32}$" |

输出 schema 顶层字段：`from_checkpoint_id`、`to_checkpoint_id`、`history`、`replay`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_redo.outputSchema`；参数复杂对象见 `edit_redo.inputSchema`。

### edit_restore

`{"name":"edit_restore","arguments":{"request_id":"sample_request_id","lineage_id":"lineage-00000000000000000000000000000000","checkpoint_id":"checkpoint-00000000000000000000000000000000","action":"assess"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `lineage_id` | 是 | string; pattern="^lineage-[0-9a-f]{32}$" |
| `checkpoint_id` | 是 | string; pattern="^checkpoint-[0-9a-f]{32}$" |
| `action` | 是 | string; enum=["assess","apply"] |
| `replay_id` | 否 | string; pattern="^replay-[0-9a-f]{32}$" |
| `expected_live_fingerprint` | 否 | string; pattern="^[0-9a-f]{64}$" |
| `confirm_validated_drift` | 否 | boolean |

输出 schema 顶层字段：`replay`、`history`、`migration_checkpoint`、`from_checkpoint_id`、`to_checkpoint_id`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_restore.outputSchema`；参数复杂对象见 `edit_restore.inputSchema`。

### edit_export

`{"name":"edit_export","arguments":{"request_id":"sample_request_id","lineage_id":"lineage-00000000000000000000000000000000","checkpoint_id":"checkpoint-00000000000000000000000000000000"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `lineage_id` | 是 | string; pattern="^lineage-[0-9a-f]{32}$" |
| `checkpoint_id` | 是 | string; pattern="^checkpoint-[0-9a-f]{32}$" |
| `output_path` | 否 | string; minLength=1; maxLength=32767 |

输出 schema 顶层字段：`checkpoint`、`output`、`replay`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_export.outputSchema`；参数复杂对象见 `edit_export.inputSchema`。

### edit_recover

`{"name":"edit_recover","arguments":{"request_id":"sample_request_id","recovery_id":"recovery-00000000000000000000000000000000","action":"retry_checkpoint"}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `recovery_id` | 是 | string; pattern="^recovery-[0-9a-f]{32}$" |
| `action` | 是 | string; enum=["retry_checkpoint","undo_live","cleanup_temp"] |

输出 schema 顶层字段：`resolved`、`action`、`checkpoint`、`restored_fingerprint`、`removed_temp`、`history`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_recover.outputSchema`；参数复杂对象见 `edit_recover.inputSchema`。

### edit_accept_live

`{"name":"edit_accept_live","arguments":{"request_id":"sample_request_id","assembly_name":"sample_assembly_name","source_family_id":"family-00000000000000000000000000000000","superseded_lineage_id":"lineage-00000000000000000000000000000000","expected_live_fingerprint":"0000000000000000000000000000000000000000000000000000000000000000","acknowledge_new_baseline":true}}`

| 字段 | 必填 | 类型、枚举与约束 |
| --- | --- | --- |
| `request_id` | 是 | string; minLength=1; maxLength=128 |
| `assembly_name` | 是 | string; minLength=1; maxLength=512 |
| `source_family_id` | 是 | string; pattern="^family-[0-9a-f]{32}$" |
| `superseded_lineage_id` | 是 | string; pattern="^lineage-[0-9a-f]{32}$" |
| `expected_live_fingerprint` | 是 | string; pattern="^[0-9a-f]{64}$" |
| `acknowledge_new_baseline` | 是 | `True` |
| `module_mvid` | 否 | string; pattern="^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$" |

输出 schema 顶层字段：`source_identity`、`lineage`、`root_checkpoint`、`superseded_lineage_id`。完整成功 `result`、失败、分支结构见附录 C 中 `edit_accept_live.outputSchema`；参数复杂对象见 `edit_accept_live.inputSchema`。

### edit_compile

经 dnSpy C# Roslyn provider 编译并把程序集 + Portable PDB 注册为内存工件；不导入目标模块。初始化会话必需。`request_id`、`assembly_name`(1..512)、`compilation_kind`(`edit_method`/`edit_class`)、`documents`(1..32，每项仅 `path` 1..512、`content` 1..524288) 必填；`target_platform` 可为 `anycpu`（默认）、`x86`、`x64`；`references_override` 最多 128 条，每条 1..1024。顶层与 document 都禁止额外字段；无 analyzer/generator/script/build-task 面。注册工件最多 8 个。

`{"name":"edit_compile","arguments":{"request_id":"compile-1","assembly_name":"Demo","compilation_kind":"edit_method","documents":[{"path":"Patch.cs","content":"public class Patch {}"}]}}`

成功 `result.compile` 含 `compile_id`、`success:true`、`assembly_name`、`compilation_kind`、`diagnostics`、`assembly:{length,sha256}`、`portable_pdb:{length,sha256}`、`target_platform`、`consumable_by_import:true`；`compile_id` 随后交给 `edit_import`。编译不成功但调用成功时 `result.compile={success:false,diagnostics,consumable_by_import:false}`，没有 `compile_id`。诊断行字段为 `severity,id,description,filename,line,column`。域失败另有错误信封；不因编译成功推断导入成功。

### edit_apply：39 种 operation 的可用参数

`edit_apply` 顶层必填 `request_id`、`transaction_id`、`expected_revision`、`operation`。`operation` 是严格 `oneOf`，`kind` 决定分支；下表逐种列**全部直接字段**与必填集，字段的嵌套语法、null/空集合、数值范围、互斥及默认值由附录 C 的 `edit_apply.inputSchema.properties.operation.oneOf` 对应 `kind` 完整给出。不存在开放的任意 kind；未知/无效字段被 schema 拒绝。`kind_version` 是检查点持久化版本判定，非调用方随意选的 edit_apply 参数。

每种成功时共用 `edit_apply.result`：`kind` 回显本行、`operation_index` 指向本次提交的私有操作、`created_object_ids` 仅对创建类有值，另返回 `transaction`（递增修订）、`fingerprints`、`diffs`、`risks`、`review_cleared:true` 与 `capacity`；失败整体原子回退，不返回部分成功种类。具体各字段类型/风险记录见附录 C 的 outputSchema。

| kind | 持久版本 | 必填字段 | 全部直接字段（精确嵌套见附录 C） |
| --- | --- | --- | --- |
| `type_add` | v1；使用新结构域时 v2 | `kind`, `name` | `attributes`, `base_type`, `kind`, `name`, `namespace`, `owner_type`, `layout` |
| `type_update` | v1；使用新结构域时 v2 | `kind`, `target` | `attributes`, `base_type`, `kind`, `name`, `namespace`, `target`, `layout` |
| `type_remove` | v1 | `kind`, `target`, `remove_mode` | `kind`, `remove_mode`, `target` |
| `method_add` | v1；使用新结构域时 v2 | `kind`, `owner_type`, `name`, `signature` | `attributes`, `body`, `impl_attributes`, `kind`, `name`, `owner_type`, `signature`, `overrides`, `pinvoke`, `custom_debug_infos` |
| `method_update` | v1；使用新结构域时 v2 | `kind`, `target` | `attributes`, `has_this`, `impl_attributes`, `kind`, `name`, `return_type`, `target`, `overrides`, `pinvoke` |
| `method_remove` | v1 | `kind`, `target`, `remove_mode` | `kind`, `remove_mode`, `target` |
| `field_add` | v1；使用新结构域时 v2 | `kind`, `owner_type`, `name`, `field_type` | `attributes`, `constant`, `field_type`, `kind`, `name`, `owner_type`, `field_offset`, `initial_data`, `marshal` |
| `field_update` | v1；使用新结构域时 v2 | `kind`, `target` | `attributes`, `clear_constant`, `constant`, `field_type`, `kind`, `name`, `target`, `field_offset`, `initial_data`, `marshal` |
| `field_remove` | v1 | `kind`, `target`, `remove_mode` | `kind`, `remove_mode`, `target` |
| `property_add` | v1；使用新结构域时 v2 | `kind`, `owner_type`, `name`, `property_type` | `attributes`, `getter`, `index_parameter_types`, `kind`, `name`, `owner_type`, `property_type`, `setter` |
| `property_update` | v1；使用新结构域时 v2 | `kind`, `target` | `attributes`, `getter`, `index_parameter_types`, `kind`, `name`, `property_type`, `setter`, `target` |
| `property_remove` | v1 | `kind`, `target`, `remove_mode` | `kind`, `remove_mode`, `target` |
| `event_add` | v1；使用新结构域时 v2 | `kind`, `owner_type`, `name`, `event_type`, `add_method`, `remove_method` | `add_method`, `attributes`, `event_type`, `kind`, `name`, `owner_type`, `raise_method`, `remove_method` |
| `event_update` | v1；使用新结构域时 v2 | `kind`, `target` | `add_method`, `attributes`, `event_type`, `kind`, `name`, `raise_method`, `remove_method`, `target` |
| `event_remove` | v1 | `kind`, `target`, `remove_mode` | `kind`, `remove_mode`, `target` |
| `parameter_add` | v1；使用新结构域时 v2 | `kind`, `owner_method`, `parameter_index`, `name`, `parameter_type` | `attributes`, `kind`, `name`, `owner_method`, `parameter_index`, `parameter_type`, `marshal` |
| `parameter_update` | v1；使用新结构域时 v2 | `kind`, `parameter_target` | `attributes`, `kind`, `name`, `parameter_target`, `parameter_type`, `marshal` |
| `parameter_remove` | v1 | `kind`, `parameter_target`, `remove_mode` | `kind`, `parameter_target`, `remove_mode` |
| `generic_parameter_add` | v1；使用新结构域时 v2 | `kind`, `owner`, `generic_index`, `name` | `attributes`, `generic_index`, `kind`, `name`, `owner`, `constraints` |
| `generic_parameter_update` | v1；使用新结构域时 v2 | `kind`, `target` | `attributes`, `kind`, `name`, `target`, `constraints` |
| `generic_parameter_remove` | v1 | `kind`, `target`, `remove_mode` | `kind`, `remove_mode`, `target` |
| `method_body_replace` | v1；使用新结构域时 v2 | `kind`, `target`, `body` | `body`, `kind`, `target`, `custom_debug_infos` |
| `attribute_add` | v1；使用新结构域时 v2 | `kind`, `target`, `constructor` | `kind`, `target`, `constructor`, `fixed_arguments`, `named_arguments` |
| `attribute_remove` | v1 | `kind`, `target`, `match` | `kind`, `target`, `match` |
| `security_add` | v1 | `kind`, `parent`, `action`, `xml` | `kind`, `parent`, `action`, `xml` |
| `security_remove` | v1 | `kind`, `parent`, `action` | `kind`, `parent`, `action`, `index` |
| `assembly_update` | v1 | `kind` | `kind`, `name`, `version`, `culture` |
| `module_update` | v1 | `kind`, `name` | `kind`, `name` |
| `assembly_ref_update` | v1 | `kind`, `target` | `kind`, `target`, `name`, `version`, `culture` |
| `entry_point_set` | v1 | `kind` | `kind`, `entry_point` |
| `managed_resource_add` | v1 | `kind`, `name`, `data_base64` | `kind`, `name`, `attributes`, `data_base64` |
| `managed_resource_update` | v1 | `kind`, `target` | `kind`, `target`, `entry`, `data_base64` |
| `managed_resource_remove` | v1 | `kind`, `target`, `remove_mode` | `kind`, `target`, `remove_mode` |
| `win32_resource_add` | v1 | `kind`, `data_base64` | `kind`, `type_id`, `type_name`, `name_id`, `name_string`, `lang_id`, `data_base64` |
| `win32_resource_update` | v1 | `kind`, `data_base64` | `kind`, `type_id`, `type_name`, `name_id`, `name_string`, `lang_id`, `data_base64` |
| `win32_resource_remove` | v1 | `kind`, `remove_mode` | `kind`, `type_id`, `type_name`, `name_id`, `name_string`, `lang_id`, `remove_mode` |
| `strong_name_remove` | v1 | `kind`, `dynamic_failure` | `kind`, `dynamic_failure` |
| `interface_add` | v1 | `kind`, `owner_type`, `interface` | `kind`, `owner_type`, `interface` |
| `reference_add` | v1 | `kind`, `reference` | `kind`, `reference` |

字符串 TypeSig 语法（1..4096）：无空白的 `System.Int32` 等全名/基元；类型或方法泛型参数 `!<index>`/`!!<index>`（必须落在 owner arity）；可用 `class:<全名>` 或 `valuetype:<全名>` 固定类型类别；泛型实例名须含正数反引号元数并跟 `<T1,T2,...>` 且实参数吻合；后缀 `[]`、`[,]` 等多维秩、`*` 指针、`&` byref（byref 后不得再有后缀）；嵌套类型以 `/` 分隔。`System.Void` 仅允许裸方法返回类型，不能作一般参数/后缀。字符串解析器拒绝 `modreq`/`modopt`/`fnptr`/`sentinel` 文本；需要这些结构时使用结构化节点。外部类型按已加载/引用解析，歧义会拒绝。

结构对象入口是 `{kind:"type",type:TypeNode}`。`TypeNode` 字段为 `Kind:string`、`Value:uint`、`Reference?:string`、`Owner?:string`、`Children?:TypeNode[]`、`Sizes?:uint[]`、`Bounds?:int[]`、`Call?:CallNode`。`Kind` 闭集为 `CorLibTypeSig,ClassSig,ValueTypeSig,GenericVar,GenericMVar,FnPtrSig,GenericInstSig,CModReqdSig,CModOptSig,ArraySig,ValueArraySig,ModuleSig,PtrSig,ByRefSig,SZArraySig,PinnedSig,SentinelSig`。CorLib/Class/Value 节点用 `Reference` 绑定类型；GenericVar/MVar 用 `Value` 序号及可选 `Owner`；FnPtr 用 `Call`；GenericInst 的 `Children[0]` 是泛型类型、余下为参数；修饰符用 `Reference+Children[0]`；Array 用 `Value` 秩、`Sizes/Bounds+Children[0]`；ValueArray/Module 用 `Value+Children[0]`；Ptr/ByRef/SZArray/Pinned 用单 child；Sentinel 无 child。`Reference/Owner` 不是任意 FullName：必须按当前图的 token/object_id 绑定。

`CallNode` 字段为 `Kind:string`、`Convention:byte`、`Arity:uint`、`Extra?:byte[]`、`Result?:TypeNode`、`Parameters?:TypeNode[]`、`Optional?:TypeNode[]`；Kind 闭集 `FieldSig,MethodSig,PropertySig,LocalSig,GenericInstMethodSig`。FieldSig 要 Result；MethodSig/PropertySig 要 Result，可带 Parameters/Optional；LocalSig、GenericInstMethodSig 以 Parameters 表示局部/泛型实参。`Convention` 与 Kind 不符会拒绝；循环或空节点拒绝。结构体的更细 oneOf/长度约束仍按附录 C 中实际使用位置检查。

定义目标可以是 token 或事务 object_id 等 schema 指定地址；不能把跨修订 token/name 当稳定身份。检查点 replay 的定义地址形式为 `t/<index>[/t/<nested-index>][/m|f|p|e/<index>][/a|g/<index>]`，绑定预期图指纹，不是永久对象 ID。`reference_add.reference.form` 闭集是 `assembly_ref,type_ref,type_spec,member_ref,method_spec`；`type_ref` 必须显式 scope，`assembly_ref` 的 public key/token 必须显式 kind，不从字节长度猜，具体字段见附录 C。IL body 指令、operand、局部变量、异常处理、CDI/资源 base64 的全量约束均在对应 operation 子对象里。

持久操作版本：`interface_add`、`reference_add` 从 v1 起；部分原有种类使用结构化 TypeSig、overrides 或 CDI `enc_state_map` 时自动要求 v2，其余保持 v1。未知 `(kind,kind_version)` 以 `EDIT_OPERATION_VERSION_UNSUPPORTED` 拒绝；旧写兼容 `legacy_symbol_rename` 仅存在历史回放表，不是公开 `edit_apply` kind。`strong_name_remove` 虽有输入和可信事件门控分支，但真实来源成功路径尚未验收，不能宣称 ACC016 已通过。

### 编辑代表流程（ID 均由前一步实际响应替换）

```json
{"name":"edit_begin","arguments":{"request_id":"begin-1","assembly_name":"Demo"}}
{"name":"edit_apply","arguments":{"request_id":"apply-1","transaction_id":"edit-00000000000000000000000000000000","expected_revision":0,"operation":{"kind":"module_update","name":"Demo.dll"}}}
{"name":"edit_review","arguments":{"request_id":"review-1","transaction_id":"edit-00000000000000000000000000000000","expected_revision":1}}
{"name":"edit_commit","arguments":{"request_id":"commit-1","transaction_id":"edit-00000000000000000000000000000000","expected_revision":1,"review_id":"review-00000000000000000000000000000000","review_revision":1,"confirmed_risk_ids":[]}}
{"name":"edit_history","arguments":{}}
{"name":"edit_export","arguments":{"request_id":"export-1","lineage_id":"lineage-00000000000000000000000000000000","checkpoint_id":"checkpoint-00000000000000000000000000000000"}}
{"name":"edit_compile","arguments":{"request_id":"compile-1","assembly_name":"Demo","compilation_kind":"edit_method","documents":[{"path":"Patch.cs","content":"public class Patch {}"}]}}
{"name":"edit_import","arguments":{"request_id":"import-1","transaction_id":"edit-00000000000000000000000000000000","expected_revision":1,"compile_id":"compile-00000000000000000000000000000000","targets":[{"compiled":"Patch.Method","action":"add"}]}}
```

以上 ID 是 schema 合法的示意值，调用前须换成 `begin.result.transaction.transaction_id`、`review.result.review.review_id`、`commit.result.history.lineage_id`/`checkpoint.checkpoint_id`、`compile.result.compile.compile_id` 等实际返回值；不表示示意值、空确认风险或示例源码会实际通过。以 review 返回风险、compile 诊断与当前 schema 分支为准。`edit_resource_import` 从 AllowedSampleRoot 内普通非 reparse 文件读取并把数据暂存为资源操作；`edit_resource_export`/`edit_export` 只能写 ArtifactRoot 下安全目标且不覆盖源样本。`edit_impact_scan` 的范围仅为已加载模块，不能声称全局无入站引用。

编辑响应关联示例（关键字段的路径映射；实际完整对象与失败分支见附录 C）：`edit_begin → result.transaction.transaction_id / work_revision`；`edit_apply → result.transaction.work_revision / created_object_ids / risks`；`edit_review → result.review.review_id / review_revision / required_confirmation_ids`；`edit_commit → result.checkpoint.checkpoint_id / result.history.lineage_id`；`edit_history → result.view / lineage/checkpoint 列表与 cursor`；`edit_export → result.output.path / length / sha256 / file_id`；`edit_compile → result.compile.compile_id`；`edit_import → result.import / operation_count`。

编译成功响应示例（执行实现整理的说明样本，非实机输出；此处展示完整 MCP 外层与内部 `compile` 结构）：

```json
{"jsonrpc":"2.0","id":9,"result":{"content":[{"type":"text","text":"{\"schema_version\":\"dnspy.edit.v1\",\"ok\":true,\"state\":\"idle\",\"result\":{\"compile\":{\"compile_id\":\"compile-00000000000000000000000000000000\",\"success\":true,\"assembly_name\":\"Demo\",\"compilation_kind\":\"edit_method\",\"diagnostics\":[],\"assembly\":{\"length\":1234,\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"},\"portable_pdb\":{\"length\":0,\"sha256\":\"\"},\"target_platform\":\"AnyCpu\",\"consumable_by_import\":true}},\"warnings\":[],\"untrusted_sample_data\":true}"}],"structuredContent":{"schema_version":"dnspy.edit.v1","ok":true,"state":"idle","result":{"compile":{"compile_id":"compile-00000000000000000000000000000000","success":true,"assembly_name":"Demo","compilation_kind":"edit_method","diagnostics":[],"assembly":{"length":1234,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"},"portable_pdb":{"length":0,"sha256":""},"target_platform":"AnyCpu","consumable_by_import":true}},"warnings":[],"untrusted_sample_data":true}}}
```

## 6. 测试缝与固定不可用 API（正常客户端勿用）

`DNMCP_TEST=1` 时另**通告** 6 个：`debug_test_adapter`, `debug_test_clock`, `debug_test_dump`, `debug_test_flood`, `debug_test_spy`, `debug_test_start`。仅用于验收注入/观测，可能伪造事件或故障，不能作真实业务结果。

可直接调用但**从不通告**的编辑缝 9 个：`edit_test_clock`, `edit_test_barrier`, `edit_test_external_mutation`, `edit_test_live_mutation`, `edit_test_fault`, `edit_test_apply_and_restore`, `edit_test_storage_fault`, `edit_test_lineage_mutation`, `edit_test_strong_name`；其精确输入/输出也在附录 C。调试缝 `debug_test_settings`, `debug_test_artifact`, `debug_test_transport`, `debug_test_environment` 同样从不通告；测试模式外返回 `CAPABILITY_UNAVAILABLE`。固定禁用 API `debug_list_attachable_processes`, `debug_attach`, `debug_detach` 从不通告，合法直呼也只返回 `CAPABILITY_UNAVAILABLE`、零副作用，输入定义可见附录 B。

条件通告调试测试工具的 `inputSchema` 顶层均为 `type:object,additionalProperties:false`、无 required 字段；下表逐项给全部字段，未列字段拒绝。响应沿用 `dnspy.debug.v1` 信封，但没有生产冻结的细化 result schema。

| 名称 | 作用 | 全部顶层输入字段 |
| --- | --- | --- |
| `debug_test_adapter` | 安装假控制适配器并发出合成观测 | `install` boolean; `fail_next` const: explicit_failure; `emit` object: kind=paused\|removed; pid,exit_code integer; break_infos array; no_pause,first_chance,unhandled boolean; exception_type string |
| `debug_test_clock` | 读取/推进虚拟控制时钟 | `advance_ms` integer; `reset` boolean |
| `debug_test_dump` | 给下一次 dump 固定原始字节分支 | `mode` enum: raw \| force_memory \| both_unavailable |
| `debug_test_flood` | 追加合成事件以测淘汰/省略 | `count` integer; `bytes_per_event` integer |
| `debug_test_spy` | 读取或重置内存探针计数 | `reset` boolean |
| `debug_test_start` | 给下一次 launch 注入启动/归属情形 | `mode` enum: fail_start \| exit_before_claim \| ui_debugging \| ui_debugging_off \| foreign_process \| manager_idle |

仅测试可调用调试缝：`debug_test_settings {}` 执行内存设置事务/恢复断言；`debug_test_artifact {}` 执行内存工件账本限额/取消断言；`debug_test_transport {hold_ms?:integer 0..10000}` 测有界 body 与 16/8 并发门（`hold_ms` 会使当前测试请求延时）；`debug_test_environment {p01_action:"inject_signals"|"clear_signals"|"snapshot"|"evaluate",manufacturer?,product_name?,bios_vendor?,read_failure?,entry_point?}` 操作虚拟执行环境信号，`evaluate` 必填有效 `entry_point`；`debug_test_transport` 的特殊 `p01_action:"snapshot"|"reset"|"arm_observer_fault"` 分支改由 provider 读取当前传输上下文。所有这些入口需要 DNMCP_TEST，非生产工具。

编辑测试缝的精确输入/输出位于附录 C 的同名键：`edit_test_clock`（虚拟时钟）、`edit_test_barrier`（同步屏障）、`edit_test_external_mutation`/`edit_test_live_mutation`/`edit_test_lineage_mutation`（冲突注入）、`edit_test_fault`/`edit_test_storage_fault`（故障注入）、`edit_test_apply_and_restore`（逆向/恢复）、`edit_test_strong_name`（隔离强名称探针）。它们从不进入 tools/list；不能用返回数据替代真实样本验收。

## 附录 A：32 个静态工具的原样输入 schema

以下键与 `McpTools.GetAvailableTools()` 一一对应。静态多数无 `outputSchema`；源码推导的返回结构见附录 A2，唯一正式 `list_assemblies.outputSchema` 已在 A2 标明。

```json
{
  "open_files": {
    "type": "object",
    "properties": {
      "paths": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "File and/or directory paths (absolute recommended). A file is loaded directly; a directory loads every file matching 'pattern'."
      },
      "recursive": {
        "type": "boolean",
        "description": "Default false. For directory entries, also descend into subdirectories."
      },
      "pattern": {
        "type": "string",
        "description": "Default '*.dll'. Glob applied to directory entries (e.g. '*.exe'). Ignored for file entries."
      }
    },
    "required": [
      "paths"
    ]
  },
  "list_assemblies": {
    "type": "object",
    "properties": {
      "name_filter": {
        "type": "string",
        "description": "Optional. Case-insensitive substring, or '*' wildcard anchored to the whole name (e.g. 'Assembly-CSharp', '*Firstpass')."
      }
    },
    "required": []
  },
  "get_assembly_info": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination of namespaces (opaque token from previous response). Default page size: 10 namespaces."
      }
    },
    "required": [
      "assembly_name"
    ]
  },
  "list_types": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "namespace": {
        "type": "string",
        "description": "Optional namespace filter"
      },
      "base_type": {
        "type": "string",
        "description": "Optional. Only include types whose base chain contains this (case-insensitive substring of a base type FullName), e.g. 'MonoBehaviour', 'UnityEngine.MonoBehaviour', 'ScriptableObject'."
      },
      "names_only": {
        "type": "boolean",
        "description": "Default false. Return a flat list of type FullName strings instead of per-type metadata — much cheaper in tokens."
      },
      "page_size": {
        "type": "integer",
        "description": "Optional page size for the first request (default 10, max 1000). Follow-up pages keep it via the cursor."
      },
      "include_nested": {
        "type": "boolean",
        "description": "Default true. Include nested types — including compiler-generated async/iterator state machines (e.g. GameOver/<Awake>d__63). Set false for top-level types only. Each entry carries is_nested / is_compiler_generated / declaring_type."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response)."
      }
    },
    "required": [
      "assembly_name"
    ]
  },
  "get_type_info": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type including namespace"
      },
      "compact": {
        "type": "boolean",
        "description": "Default false. Return only name + signature + token per method, and name + type per field/property. Much cheaper than the full per-member detail."
      },
      "members_filter": {
        "type": "string",
        "description": "Optional. Only include methods/fields/properties whose name matches (case-insensitive substring, or '*' wildcard anchored to the whole name), e.g. '*Save*'."
      },
      "page_size": {
        "type": "integer",
        "description": "Optional page size for methods on the first request (default 10, max 1000)."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination of methods (opaque token from previous response)."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name"
    ]
  },
  "decompile_method": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type"
      },
      "method_name": {
        "type": "string",
        "description": "Name of the method"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names (e.g. [\"System.Int32\",\"System.String\"]) to disambiguate overloads. Matches MethodSig.Params, not including 'this'."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw (from get_type_info or list_methods) — decimal uint or hex string ('0x06000001', as dnSpy shows). Unambiguous — takes precedence over parameter_types."
      },
      "include_state_machine": {
        "type": "boolean",
        "description": "Default true. For async / iterator methods, if the decompiler can't inline the state machine back into await/yield (common on Unity/Mono output), append the raw compiler-generated MoveNext body so the real logic isn't lost. Set false to get only the kickoff."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "search_types": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "Search query. Wildcards (*) match against FullName (namespace + type name). Recommended patterns: '*TypeName' for suffix (e.g., '*Controller' finds MyNamespace.PlayerController), '*.Keyword*' for types containing keyword, 'Full.Namespace.Path.*' for specific namespace. Without wildcards, performs case-insensitive substring matching (e.g., 'Controller' finds all types with 'Controller' in name)."
      },
      "assembly_name": {
        "type": "string",
        "description": "Optional. Restrict the search to a single assembly. Omit to search all loaded assemblies."
      },
      "names_only": {
        "type": "boolean",
        "description": "Default false. Return a flat list of type FullName strings instead of per-type metadata — much cheaper in tokens."
      },
      "page_size": {
        "type": "integer",
        "description": "Optional page size for the first request (default 10, max 1000)."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response)."
      }
    },
    "required": [
      "query"
    ]
  },
  "search_members": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "Member name to search for. Without '*', case-insensitive substring (e.g. 'Save' finds SaveGame, LoadSaveData). With '*', a wildcard anchored to the whole member name (e.g. 'get_*' finds property getters, '*Health' for a suffix)."
      },
      "kinds": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Which member kinds to include — any of: method, field, property, event. Omit for all four. (Property/event accessor methods like get_X also appear under 'method'.)"
      },
      "assembly_name": {
        "type": "string",
        "description": "Optional. Restrict the search to a single assembly (e.g. 'Assembly-CSharp'). Omit to search all loaded assemblies — recommended for common names that would otherwise match thousands of framework members."
      },
      "names_only": {
        "type": "boolean",
        "description": "Default false. Return a flat list of member signature strings instead of per-member metadata — much cheaper in tokens."
      },
      "page_size": {
        "type": "integer",
        "description": "Optional page size for the first request (default 10, max 1000)."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response)."
      }
    },
    "required": [
      "query"
    ]
  },
  "decompile_type": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type (namespace + name; nested types may use '.', '+', or '/')."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name"
    ]
  },
  "decompile_by_token": {
    "type": "object",
    "properties": {
      "token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "MDToken.Raw — decimal uint or hex string ('0x06000001', as dnSpy shows); e.g. the method_token / caller_token returned by other tools."
      },
      "assembly_name": {
        "type": "string",
        "description": "Optional. Module the token belongs to. Recommended — tokens are only unique within a module."
      },
      "include_state_machine": {
        "type": "boolean",
        "description": "Default true. For async/iterator methods, append the raw MoveNext body when the kickoff can't be reconstructed (same behavior as decompile_method)."
      }
    },
    "required": [
      "token"
    ]
  },
  "find_callers": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Assembly that declares the target method"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type that declares the target method"
      },
      "method_name": {
        "type": "string",
        "description": "Name of the target method"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names to disambiguate an overloaded target."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw of the target method — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "find_callees": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Assembly that declares the method to analyze"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type that declares the method"
      },
      "method_name": {
        "type": "string",
        "description": "Name of the method whose outgoing references to list"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names to disambiguate an overloaded method."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw of the method — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "find_references": {
    "type": "object",
    "properties": {
      "target_kind": {
        "type": "string",
        "enum": [
          "method",
          "field",
          "type",
          "string"
        ],
        "description": "What to look for: method | field | type | string."
      },
      "assembly_name": {
        "type": "string",
        "description": "Assembly declaring the target (required for method/field/type)."
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the target type (for target_kind=type) or the type declaring the target member (method/field)."
      },
      "method_name": {
        "type": "string",
        "description": "Target method name (target_kind=method)."
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Disambiguate an overloaded target method."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw of the target method — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      },
      "field_name": {
        "type": "string",
        "description": "Target field name (target_kind=field)."
      },
      "query": {
        "type": "string",
        "description": "String to match (target_kind=string). Case-insensitive substring, or '*' wildcard anchored to the whole literal."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
      }
    },
    "required": [
      "target_kind"
    ]
  },
  "find_overrides": {
    "type": "object",
    "properties": {
      "direction": {
        "type": "string",
        "enum": [
          "overridden_by",
          "overrides"
        ],
        "description": "overridden_by (default): subclasses that override this method. overrides: base-class virtuals this method overrides."
      },
      "assembly_name": {
        "type": "string",
        "description": "Assembly that declares the target method"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type that declares the target method"
      },
      "method_name": {
        "type": "string",
        "description": "Name of the target (virtual) method"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names to disambiguate an overloaded method."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw of the target method — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "find_unity_messages": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly (e.g. 'Assembly-CSharp')"
      },
      "type_full_name": {
        "type": "string",
        "description": "Optional. A single type to inspect. Omit to sweep every type in the assembly."
      },
      "page_size": {
        "type": "integer",
        "description": "Optional page size for the first request (default 10, max 1000)."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response)."
      }
    },
    "required": [
      "assembly_name"
    ]
  },
  "find_by_attribute": {
    "type": "object",
    "properties": {
      "attribute_name": {
        "type": "string",
        "description": "Attribute to match (case-insensitive substring or '*' wildcard), e.g. 'SerializeField', 'BepInPlugin', 'Serializable'. The 'Attribute' suffix is optional."
      },
      "targets": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Which declaration kinds to scan — any of: type, method, field, property, event. Omit for all."
      },
      "assembly_name": {
        "type": "string",
        "description": "Optional. Restrict to a single assembly. Omit to sweep all loaded assemblies."
      },
      "page_size": {
        "type": "integer",
        "description": "Optional page size for the first request (default 10, max 1000)."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response)."
      }
    },
    "required": [
      "attribute_name"
    ]
  },
  "search_string_literals": {
    "type": "object",
    "properties": {
      "query": {
        "type": "string",
        "description": "String to search for. Case-insensitive substring by default; '*' is a wildcard matching the whole literal (e.g. 'Player*Score', '*SAVEFILE*')."
      },
      "assembly_name": {
        "type": "string",
        "description": "Optional. Restrict the search to a single assembly. Omit to sweep all loaded assemblies."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
      }
    },
    "required": [
      "query"
    ]
  },
  "list_string_constants": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type"
      },
      "method_name": {
        "type": "string",
        "description": "Optional. Restrict to a single method. If omitted, lists ldstr across the type and its nested types."
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names to disambiguate an overloaded method_name."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw to disambiguate an overloaded method_name — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name"
    ]
  },
  "search_constants": {
    "type": "object",
    "properties": {
      "value": {
        "description": "The numeric constant to find. A whole number matches integer constants; a number with a '.' matches floating-point constants. May also be given as a string."
      },
      "assembly_name": {
        "type": "string",
        "description": "Optional. Restrict to a single assembly (strongly recommended). Omit to sweep all loaded modules."
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination (opaque token from previous response). Default page size: 10 results."
      }
    },
    "required": [
      "value"
    ]
  },
  "generate_bepinex_plugin": {
    "type": "object",
    "properties": {
      "plugin_name": {
        "type": "string",
        "description": "Name of the plugin"
      },
      "plugin_guid": {
        "type": "string",
        "description": "GUID for the plugin"
      },
      "target_assembly": {
        "type": "string",
        "description": "Assembly whose methods the hooks target (must be loaded so signatures can be read), e.g. 'Assembly-CSharp'"
      },
      "hooks": {
        "type": "array",
        "description": "Array of methods to hook. Each patch is generated from the resolved method's signature.",
        "items": {
          "type": "object",
          "properties": {
            "type_name": {
              "type": "string",
              "description": "Full name of the declaring type"
            },
            "method_name": {
              "type": "string",
              "description": "Method to hook"
            },
            "patch_type": {
              "type": "string",
              "description": "postfix (default) / prefix / transpiler"
            }
          }
        }
      }
    },
    "required": [
      "plugin_name",
      "plugin_guid",
      "target_assembly"
    ]
  },
  "generate_harmony_patch": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Assembly that declares the method to patch"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type that declares the method"
      },
      "method_name": {
        "type": "string",
        "description": "Name of the method to patch (.ctor for a constructor)"
      },
      "patch_type": {
        "type": "string",
        "enum": [
          "postfix",
          "prefix",
          "transpiler"
        ],
        "description": "postfix (default): run after, modify __result. prefix: run before, return false to skip the original. transpiler: IL-rewrite skeleton."
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names to disambiguate an overloaded method."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw of the method — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "get_type_fields": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type"
      },
      "pattern": {
        "type": "string",
        "description": "Field name pattern (supports * wildcard)"
      },
      "cursor": {
        "type": "string",
        "description": "Optional cursor for pagination of fields (opaque token from previous response). Default page size: 10 fields."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "pattern"
    ]
  },
  "get_type_property": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type"
      },
      "property_name": {
        "type": "string",
        "description": "Name of the property"
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "property_name"
    ]
  },
  "find_path_to_type": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "from_type": {
        "type": "string",
        "description": "Starting type full name"
      },
      "to_type": {
        "type": "string",
        "description": "Target type full name or partial name"
      },
      "max_depth": {
        "type": "number",
        "description": "Maximum search depth (default: 5)"
      }
    },
    "required": [
      "assembly_name",
      "from_type",
      "to_type"
    ]
  },
  "list_methods": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Fully qualified type name"
      },
      "cursor": {
        "type": "string",
        "description": "Opaque pagination cursor from a previous response."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name"
    ]
  },
  "get_method_il": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly"
      },
      "type_full_name": {
        "type": "string",
        "description": "Fully qualified type name"
      },
      "method_name": {
        "type": "string",
        "description": "Method name"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names for overload disambiguation."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw from list_methods / get_type_info — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "patch_method_il": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string"
      },
      "type_full_name": {
        "type": "string"
      },
      "method_name": {
        "type": "string"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        }
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ]
      },
      "edits": {
        "type": "array",
        "items": {
          "type": "object",
          "oneOf": [
            {
              "properties": {
                "op": {
                  "const": "replace"
                },
                "index": {
                  "type": "integer"
                },
                "opcode": {
                  "type": "string"
                },
                "operand": {
                  "type": "string"
                }
              },
              "required": [
                "op",
                "index",
                "opcode",
                "operand"
              ],
              "additionalProperties": false
            },
            {
              "properties": {
                "op": {
                  "const": "insert"
                },
                "index": {
                  "type": "integer"
                },
                "opcode": {
                  "type": "string"
                },
                "operand": {
                  "type": "string"
                }
              },
              "required": [
                "op",
                "index",
                "opcode",
                "operand"
              ],
              "additionalProperties": false
            },
            {
              "properties": {
                "op": {
                  "const": "delete"
                },
                "index": {
                  "type": "integer"
                }
              },
              "required": [
                "op",
                "index"
              ],
              "additionalProperties": false
            },
            {
              "properties": {
                "op": {
                  "const": "set_init_locals"
                },
                "value": {
                  "type": "boolean"
                }
              },
              "required": [
                "op",
                "value"
              ],
              "additionalProperties": false
            }
          ]
        },
        "description": "Ordered edit ops. See tool description for shape."
      },
      "optimize_macros": {
        "type": "boolean",
        "description": "Call body.OptimizeMacros() after edits to auto-shorten ldarg/ldloc/branches. Default false."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name",
      "edits"
    ]
  },
  "force_return": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Assembly that declares the method"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type that declares the method"
      },
      "method_name": {
        "type": "string",
        "description": "Name of the method to rewrite"
      },
      "value": {
        "description": "Value to return: a boolean, a number, null, or the string 'default'. Omit for the return type's default. Ignored/invalid for void methods."
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names to disambiguate an overloaded method."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw of the method — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "nop_method": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Assembly that declares the method"
      },
      "type_full_name": {
        "type": "string",
        "description": "Full name of the type that declares the method"
      },
      "method_name": {
        "type": "string",
        "description": "Name of the method to empty out"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        },
        "description": "Optional. Fully-qualified parameter type names to disambiguate an overloaded method."
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Optional. MDToken.Raw of the method — decimal uint or hex string ('0x06000001', as dnSpy shows). Takes precedence over parameter_types."
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "revert_method_il": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string"
      },
      "type_full_name": {
        "type": "string"
      },
      "method_name": {
        "type": "string"
      },
      "parameter_types": {
        "type": "array",
        "items": {
          "type": "string"
        }
      },
      "method_token": {
        "type": [
          "integer",
          "string"
        ]
      }
    },
    "required": [
      "assembly_name",
      "type_full_name",
      "method_name"
    ]
  },
  "rename_symbol_by_token": {
    "type": "object",
    "properties": {
      "target_kind": {
        "type": "string",
        "enum": [
          "type",
          "class",
          "enum",
          "interface",
          "struct",
          "delegate",
          "method",
          "field",
          "enum_member",
          "enum_members",
          "property",
          "event",
          "parameter",
          "generic_parameter"
        ],
        "description": "Metadata symbol kind. Use enum_members for complete value-mapped enum-member batch rename."
      },
      "token": {
        "type": [
          "integer",
          "string"
        ],
        "description": "Matching metadata token — decimal uint or hex string copied from dnSpy (for example 0x02000001, 0x04000001, or 0x06000001)."
      },
      "new_name": {
        "type": "string",
        "description": "Required for every singular target_kind. New simple metadata name."
      },
      "members": {
        "type": "array",
        "description": "Required only for target_kind=enum_members. Complete desired enum mapping by existing numeric value.",
        "items": {
          "type": "object",
          "properties": {
            "name": {
              "type": "string"
            },
            "value": {
              "type": [
                "integer",
                "string"
              ]
            }
          },
          "required": [
            "name",
            "value"
          ]
        }
      },
      "assembly_name": {
        "type": "string",
        "description": "Optional but recommended. Assembly/module the token belongs to."
      }
    },
    "required": [
      "target_kind",
      "token"
    ]
  },
  "save_assembly": {
    "type": "object",
    "properties": {
      "assembly_name": {
        "type": "string",
        "description": "Name of the assembly to save"
      },
      "output_path": {
        "type": "string",
        "description": "Optional. Target file path. If absent, overwrite original with a timestamped backup."
      }
    },
    "required": [
      "assembly_name"
    ]
  }
}
```

## 附录 A2：静态工具成功返回结构（源码推导）

以下 32 项是执行实现推导的成功文本投影，**不是** provider 声明的 outputSchema；唯一正式声明的是 `list_assemblies.outputSchema`，此处原样嵌入。表内 `required` 表示该成功分支构造时必有，未列入的字段只在所述条件出现；`null` 是实际 JSON null。`items` 的 `oneOf` 由 `names_only` 选择，`get_type_info.Methods` 的 `oneOf` 由 `compact` 选择；`get_type_info` 的 Fields/Properties/Events 仅无 cursor 的首请求出现。`nextCursor` 只在仍有下一页时出现；`get_assembly_info` 的 Namespaces 同理分页。`find_path_to_type` 无路径时是普通文本；五个反编译/生成工具直接返回文本。六个旧写工具通过编辑协调器：`checkpoint`/`history`/`confirmed_risks` 嵌套结构沿用冻结 `edit_commit` 输出定义，`revert_method_il.history` 沿用 `edit_undo` 输出定义；若事务分支没有某字段则该字段不出现。`rename_symbol_by_token` 普通分支仅出现 `updated_type_references` 或 `updated_member_references` 之一；`enum_members` 分支使用独立成员数组。错误没有统一冻结输出 schema：参数缺失、目标不存在、歧义、无 IL body、分页 cursor 无效等由执行实现抛出并映射成 `isError:true` 文本；不要将成功结构用于解析错误。`get_type_info.Fields[].Constant` 是源元数据值，可能是不同 JSON 基元；`search_constants.items[].value` 为数字。

```json
{
 "open_files": {
  "type": "object",
  "properties": {
   "loaded_count": {
    "type": "integer"
   },
   "already_loaded_count": {
    "type": "integer"
   },
   "failed_count": {
    "type": "integer"
   },
   "loaded": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "name": {
       "type": "string"
      },
      "path": {
       "type": "string"
      },
      "already_loaded": {
       "type": "boolean"
      }
     },
     "required": [
      "name",
      "path",
      "already_loaded"
     ],
     "additionalProperties": false
    }
   },
   "failed": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "path": {
       "type": "string"
      },
      "error": {
       "type": "string"
      }
     },
     "required": [
      "path",
      "error"
     ],
     "additionalProperties": false
    }
   },
   "note": {
    "type": "string"
   }
  },
  "required": [
   "loaded_count",
   "already_loaded_count",
   "failed_count",
   "loaded",
   "failed"
  ],
  "additionalProperties": false
 },
 "list_assemblies": {
  "type": "object",
  "properties": {
   "assemblies": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Name": {
       "type": "string"
      },
      "Version": {
       "type": "string"
      },
      "FullName": {
       "type": "string"
      },
      "Culture": {
       "type": "string"
      },
      "PublicKeyToken": {
       "type": "string"
      }
     },
     "required": [
      "Name",
      "Version",
      "FullName",
      "Culture",
      "PublicKeyToken"
     ],
     "additionalProperties": false
    }
   }
  },
  "required": [
   "assemblies"
  ],
  "additionalProperties": false
 },
 "get_assembly_info": {
  "type": "object",
  "properties": {
   "Name": {
    "type": "string"
   },
   "Version": {
    "type": "string"
   },
   "FullName": {
    "type": "string"
   },
   "Culture": {
    "type": "string"
   },
   "PublicKeyToken": {
    "type": "string"
   },
   "Modules": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Name": {
       "type": "string"
      },
      "Kind": {
       "type": "string"
      },
      "Architecture": {
       "type": "string"
      },
      "RuntimeVersion": {
       "type": "string"
      }
     },
     "required": [
      "Name",
      "Kind",
      "Architecture",
      "RuntimeVersion"
     ],
     "additionalProperties": false
    }
   },
   "Namespaces": {
    "type": "array",
    "items": {
     "type": "string"
    }
   },
   "NamespacesTotalCount": {
    "type": "integer"
   },
   "NamespacesReturnedCount": {
    "type": "integer"
   },
   "TypeCount": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "Name",
   "Version",
   "FullName",
   "Culture",
   "PublicKeyToken",
   "Modules",
   "Namespaces",
   "NamespacesTotalCount",
   "NamespacesReturnedCount",
   "TypeCount"
  ],
  "additionalProperties": false
 },
 "list_types": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "oneOf": [
      {
       "type": "object",
       "properties": {
        "Token": {
         "type": "integer"
        },
        "FullName": {
         "type": "string"
        },
        "Namespace": {
         "type": "string"
        },
        "Name": {
         "type": "string"
        },
        "IsPublic": {
         "type": "boolean"
        },
        "IsNested": {
         "type": "boolean"
        },
        "IsCompilerGenerated": {
         "type": "boolean"
        },
        "DeclaringType": {
         "type": [
          "string",
          "null"
         ]
        },
        "IsClass": {
         "type": "boolean"
        },
        "IsInterface": {
         "type": "boolean"
        },
        "IsEnum": {
         "type": "boolean"
        },
        "IsValueType": {
         "type": "boolean"
        },
        "IsAbstract": {
         "type": "boolean"
        },
        "IsSealed": {
         "type": "boolean"
        },
        "BaseType": {
         "type": "string"
        }
       },
       "required": [
        "Token",
        "FullName",
        "Namespace",
        "Name",
        "IsPublic",
        "IsNested",
        "IsCompilerGenerated",
        "DeclaringType",
        "IsClass",
        "IsInterface",
        "IsEnum",
        "IsValueType",
        "IsAbstract",
        "IsSealed",
        "BaseType"
       ],
       "additionalProperties": false
      },
      {
       "type": "string"
      }
     ]
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "get_type_info": {
  "type": "object",
  "properties": {
   "Token": {
    "type": "integer"
   },
   "FullName": {
    "type": "string"
   },
   "Namespace": {
    "type": "string"
   },
   "Name": {
    "type": "string"
   },
   "IsPublic": {
    "type": "boolean"
   },
   "IsClass": {
    "type": "boolean"
   },
   "IsInterface": {
    "type": "boolean"
   },
   "IsEnum": {
    "type": "boolean"
   },
   "IsValueType": {
    "type": "boolean"
   },
   "IsAbstract": {
    "type": "boolean"
   },
   "IsSealed": {
    "type": "boolean"
   },
   "BaseType": {
    "type": "string"
   },
   "Interfaces": {
    "type": "array",
    "items": {
     "type": "string"
    }
   },
   "GenericParameters": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Name": {
       "type": "string"
      },
      "Token": {
       "type": "integer"
      },
      "Number": {
       "type": "integer"
      }
     },
     "required": [
      "Name",
      "Token",
      "Number"
     ],
     "additionalProperties": false
    }
   },
   "Methods": {
    "type": "array",
    "items": {
     "oneOf": [
      {
       "type": "object",
       "properties": {
        "Name": {
         "type": "string"
        },
        "Token": {
         "type": "integer"
        },
        "Signature": {
         "type": "string"
        }
       },
       "required": [
        "Name",
        "Token",
        "Signature"
       ],
       "additionalProperties": false
      },
      {
       "type": "object",
       "properties": {
        "Name": {
         "type": "string"
        },
        "Token": {
         "type": "integer"
        },
        "Signature": {
         "type": "string"
        },
        "IsPublic": {
         "type": "boolean"
        },
        "IsStatic": {
         "type": "boolean"
        },
        "IsVirtual": {
         "type": "boolean"
        },
        "IsAbstract": {
         "type": "boolean"
        },
        "ReturnType": {
         "type": "string"
        },
        "ParameterTypes": {
         "type": "array",
         "items": {
          "type": "string"
         }
        },
        "Parameters": {
         "type": "array",
         "items": {
          "type": "object",
          "properties": {
           "Name": {
            "type": "string"
           },
           "Type": {
            "type": "string"
           },
           "Token": {
            "type": [
             "integer",
             "null"
            ]
           }
          },
          "required": [
           "Name",
           "Type",
           "Token"
          ],
          "additionalProperties": false
         }
        },
        "GenericParameters": {
         "type": "array",
         "items": {
          "type": "object",
          "properties": {
           "Name": {
            "type": "string"
           },
           "Token": {
            "type": "integer"
           },
           "Number": {
            "type": "integer"
           }
          },
          "required": [
           "Name",
           "Token",
           "Number"
          ],
          "additionalProperties": false
         }
        }
       },
       "required": [
        "Name",
        "Token",
        "Signature",
        "IsPublic",
        "IsStatic",
        "IsVirtual",
        "IsAbstract",
        "ReturnType",
        "ParameterTypes",
        "Parameters",
        "GenericParameters"
       ],
       "additionalProperties": false
      }
     ]
    }
   },
   "MethodsTotalCount": {
    "type": "integer"
   },
   "MethodsReturnedCount": {
    "type": "integer"
   },
   "FieldsCount": {
    "type": "integer"
   },
   "PropertiesCount": {
    "type": "integer"
   },
   "EventsCount": {
    "type": "integer"
   },
   "Fields": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Token": {
       "type": "integer"
      },
      "Name": {
       "type": "string"
      },
      "Type": {
       "type": "string"
      },
      "IsPublic": {
       "type": "boolean"
      },
      "IsStatic": {
       "type": "boolean"
      },
      "IsLiteral": {
       "type": "boolean"
      },
      "Constant": {},
      "Offset": {
       "type": "integer"
      },
      "OffsetSource": {
       "type": "string"
      },
      "Il2CppToken": {
       "type": "integer"
      }
     },
     "required": [
      "Token",
      "Name",
      "Type"
     ],
     "additionalProperties": false
    }
   },
   "Properties": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Token": {
       "type": "integer"
      },
      "Name": {
       "type": "string"
      },
      "Type": {
       "type": "string"
      },
      "CanRead": {
       "type": "boolean"
      },
      "CanWrite": {
       "type": "boolean"
      }
     },
     "required": [
      "Token",
      "Name",
      "Type"
     ],
     "additionalProperties": false
    }
   },
   "Events": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Token": {
       "type": "integer"
      },
      "Name": {
       "type": "string"
      },
      "Type": {
       "type": "string"
      },
      "HasAdd": {
       "type": "boolean"
      },
      "HasRemove": {
       "type": "boolean"
      },
      "HasInvoke": {
       "type": "boolean"
      }
     },
     "required": [
      "Token",
      "Name",
      "Type"
     ],
     "additionalProperties": false
    }
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "Token",
   "FullName",
   "Namespace",
   "Name",
   "IsPublic",
   "IsClass",
   "IsInterface",
   "IsEnum",
   "IsValueType",
   "IsAbstract",
   "IsSealed",
   "BaseType",
   "Interfaces",
   "GenericParameters",
   "Methods",
   "MethodsTotalCount",
   "MethodsReturnedCount",
   "FieldsCount",
   "PropertiesCount",
   "EventsCount"
  ],
  "additionalProperties": false
 },
 "search_types": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "oneOf": [
      {
       "type": "object",
       "properties": {
        "AssemblyName": {
         "type": "string"
        },
        "Token": {
         "type": "integer"
        },
        "FullName": {
         "type": "string"
        },
        "Namespace": {
         "type": "string"
        },
        "Name": {
         "type": "string"
        },
        "IsPublic": {
         "type": "boolean"
        },
        "IsNested": {
         "type": "boolean"
        },
        "IsCompilerGenerated": {
         "type": "boolean"
        },
        "DeclaringType": {
         "type": [
          "string",
          "null"
         ]
        }
       },
       "required": [
        "AssemblyName",
        "Token",
        "FullName",
        "Namespace",
        "Name",
        "IsPublic",
        "IsNested",
        "IsCompilerGenerated",
        "DeclaringType"
       ],
       "additionalProperties": false
      },
      {
       "type": "string"
      }
     ]
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "search_members": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "oneOf": [
      {
       "type": "object",
       "properties": {
        "assembly": {
         "type": "string"
        },
        "declaring_type": {
         "type": "string"
        },
        "member_kind": {
         "type": "string"
        },
        "name": {
         "type": "string"
        },
        "signature": {
         "type": "string"
        },
        "token": {
         "type": "integer"
        },
        "is_static": {
         "type": "boolean"
        },
        "is_public": {
         "type": "boolean"
        },
        "offset": {
         "type": "integer"
        },
        "offset_source": {
         "type": "string"
        },
        "il2cpp_token": {
         "type": "integer"
        }
       },
       "required": [
        "assembly",
        "declaring_type",
        "member_kind",
        "name",
        "signature",
        "token",
        "is_static",
        "is_public"
       ],
       "additionalProperties": false
      },
      {
       "type": "string"
      }
     ]
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "find_callers": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "caller_assembly": {
       "type": "string"
      },
      "caller_type": {
       "type": "string"
      },
      "caller_method": {
       "type": "string"
      },
      "caller_token": {
       "type": "integer"
      },
      "signature": {
       "type": "string"
      },
      "opcode": {
       "type": "string"
      },
      "reference": {
       "type": "string"
      },
      "il_index": {
       "type": "integer"
      },
      "il_offset": {
       "type": "integer"
      }
     },
     "required": [
      "caller_assembly",
      "caller_type",
      "caller_method",
      "caller_token",
      "signature",
      "opcode",
      "reference",
      "il_index",
      "il_offset"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "find_references": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "caller_assembly": {
       "type": "string"
      },
      "caller_type": {
       "type": "string"
      },
      "caller_method": {
       "type": "string"
      },
      "caller_token": {
       "type": "integer"
      },
      "signature": {
       "type": "string"
      },
      "opcode": {
       "type": "string"
      },
      "reference": {
       "type": "string"
      },
      "il_index": {
       "type": "integer"
      },
      "il_offset": {
       "type": "integer"
      }
     },
     "required": [
      "caller_assembly",
      "caller_type",
      "caller_method",
      "caller_token",
      "signature",
      "opcode",
      "reference",
      "il_index",
      "il_offset"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "find_callees": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "ref_kind": {
       "type": "string"
      },
      "signature": {
       "type": "string"
      },
      "token": {
       "type": [
        "integer",
        "null"
       ]
      },
      "target_assembly": {
       "type": [
        "string",
        "null"
       ]
      },
      "opcodes": {
       "type": "array",
       "items": {
        "type": "string"
       }
      },
      "occurrences": {
       "type": "integer"
      },
      "first_il_index": {
       "type": "integer"
      }
     },
     "required": [
      "ref_kind",
      "signature",
      "token",
      "target_assembly",
      "opcodes",
      "occurrences",
      "first_il_index"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "find_overrides": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "type": {
       "type": "string"
      },
      "method": {
       "type": "string"
      },
      "signature": {
       "type": "string"
      },
      "token": {
       "type": "integer"
      },
      "assembly": {
       "type": "string"
      },
      "is_abstract": {
       "type": "boolean"
      },
      "is_interface_impl": {
       "type": "boolean"
      }
     },
     "required": [
      "type",
      "method",
      "signature",
      "token",
      "assembly",
      "is_abstract"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "search_string_literals": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "value": {
       "type": "string"
      },
      "assembly": {
       "type": "string"
      },
      "type": {
       "type": "string"
      },
      "method": {
       "type": "string"
      },
      "method_token": {
       "type": "integer"
      },
      "signature": {
       "type": "string"
      },
      "il_index": {
       "type": "integer"
      },
      "il_offset": {
       "type": "integer"
      }
     },
     "required": [
      "value",
      "assembly",
      "type",
      "method",
      "method_token",
      "signature",
      "il_index",
      "il_offset"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "list_string_constants": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "value": {
       "type": "string"
      },
      "type": {
       "type": "string"
      },
      "method": {
       "type": "string"
      },
      "method_token": {
       "type": "integer"
      },
      "signature": {
       "type": "string"
      },
      "il_index": {
       "type": "integer"
      },
      "il_offset": {
       "type": "integer"
      }
     },
     "required": [
      "value",
      "type",
      "method",
      "method_token",
      "signature",
      "il_index",
      "il_offset"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "search_constants": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "value": {
       "type": "number"
      },
      "assembly": {
       "type": "string"
      },
      "type": {
       "type": "string"
      },
      "method": {
       "type": "string"
      },
      "method_token": {
       "type": "integer"
      },
      "signature": {
       "type": "string"
      },
      "il_index": {
       "type": "integer"
      },
      "il_offset": {
       "type": "integer"
      },
      "opcode": {
       "type": "string"
      }
     },
     "required": [
      "value",
      "assembly",
      "type",
      "method",
      "method_token",
      "signature",
      "il_index",
      "il_offset",
      "opcode"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "find_unity_messages": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "assembly": {
       "type": "string"
      },
      "type": {
       "type": "string"
      },
      "message": {
       "type": "string"
      },
      "signature": {
       "type": "string"
      },
      "token": {
       "type": "integer"
      },
      "parameter_types": {
       "type": "array",
       "items": {
        "type": "string"
       }
      },
      "is_static": {
       "type": "boolean"
      }
     },
     "required": [
      "assembly",
      "type",
      "message",
      "signature",
      "token",
      "parameter_types",
      "is_static"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "find_by_attribute": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "assembly": {
       "type": "string"
      },
      "target_kind": {
       "type": "string"
      },
      "declaring_type": {
       "type": "string"
      },
      "name": {
       "type": "string"
      },
      "signature": {
       "type": "string"
      },
      "token": {
       "type": "integer"
      },
      "attribute": {
       "type": "string"
      }
     },
     "required": [
      "assembly",
      "target_kind",
      "declaring_type",
      "name",
      "signature",
      "token",
      "attribute"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "get_type_fields": {
  "type": "object",
  "properties": {
   "Type": {
    "type": "string"
   },
   "Pattern": {
    "type": "string"
   },
   "MatchCount": {
    "type": "integer"
   },
   "ReturnedCount": {
    "type": "integer"
   },
   "Fields": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Name": {
       "type": "string"
      },
      "Type": {
       "type": "string"
      },
      "IsPublic": {
       "type": "boolean"
      },
      "IsStatic": {
       "type": "boolean"
      },
      "IsLiteral": {
       "type": "boolean"
      },
      "IsReadOnly": {
       "type": "boolean"
      },
      "Attributes": {
       "type": "string"
      },
      "Offset": {
       "type": "integer"
      },
      "OffsetSource": {
       "type": "string"
      },
      "Il2CppToken": {
       "type": "integer"
      }
     },
     "required": [
      "Name",
      "Type",
      "IsPublic",
      "IsStatic",
      "IsLiteral",
      "IsReadOnly",
      "Attributes"
     ],
     "additionalProperties": false
    }
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "Type",
   "Pattern",
   "MatchCount",
   "ReturnedCount",
   "Fields"
  ],
  "additionalProperties": false
 },
 "get_type_property": {
  "type": "object",
  "properties": {
   "Name": {
    "type": "string"
   },
   "Type": {
    "type": "string"
   },
   "CanRead": {
    "type": "boolean"
   },
   "CanWrite": {
    "type": "boolean"
   },
   "GetMethod": {
    "oneOf": [
     {
      "type": "object",
      "properties": {
       "Name": {
        "type": "string"
       },
       "IsPublic": {
        "type": "boolean"
       },
       "IsStatic": {
        "type": "boolean"
       }
      },
      "required": [
       "Name",
       "IsPublic",
       "IsStatic"
      ],
      "additionalProperties": false
     },
     {
      "type": "null"
     }
    ]
   },
   "SetMethod": {
    "oneOf": [
     {
      "type": "object",
      "properties": {
       "Name": {
        "type": "string"
       },
       "IsPublic": {
        "type": "boolean"
       },
       "IsStatic": {
        "type": "boolean"
       }
      },
      "required": [
       "Name",
       "IsPublic",
       "IsStatic"
      ],
      "additionalProperties": false
     },
     {
      "type": "null"
     }
    ]
   },
   "Attributes": {
    "type": "string"
   },
   "CustomAttributes": {
    "type": "array",
    "items": {
     "type": "string"
    }
   }
  },
  "required": [
   "Name",
   "Type",
   "CanRead",
   "CanWrite",
   "GetMethod",
   "SetMethod",
   "Attributes",
   "CustomAttributes"
  ],
  "additionalProperties": false
 },
 "find_path_to_type": {
  "type": "object",
  "properties": {
   "FromType": {
    "type": "string"
   },
   "ToType": {
    "type": "string"
   },
   "PathsFound": {
    "type": "integer"
   },
   "Paths": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "Path": {
       "type": "string"
      },
      "Depth": {
       "type": "integer"
      },
      "Steps": {
       "type": "array",
       "items": {
        "type": "string"
       }
      }
     },
     "required": [
      "Path",
      "Depth",
      "Steps"
     ],
     "additionalProperties": false
    }
   }
  },
  "required": [
   "FromType",
   "ToType",
   "PathsFound",
   "Paths"
  ],
  "additionalProperties": false
 },
 "list_methods": {
  "type": "object",
  "properties": {
   "items": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "name": {
       "type": "string"
      },
      "token": {
       "type": "integer"
      },
      "signature": {
       "type": "string"
      },
      "return_type": {
       "type": "string"
      },
      "parameter_types": {
       "type": "array",
       "items": {
        "type": "string"
       }
      },
      "parameters": {
       "type": "array",
       "items": {
        "type": "object",
        "properties": {
         "name": {
          "type": "string"
         },
         "type": {
          "type": "string"
         },
         "token": {
          "type": [
           "integer",
           "null"
          ]
         }
        },
        "required": [
         "name",
         "type",
         "token"
        ],
        "additionalProperties": false
       }
      },
      "generic_parameters": {
       "type": "array",
       "items": {
        "type": "object",
        "properties": {
         "name": {
          "type": "string"
         },
         "token": {
          "type": "integer"
         },
         "number": {
          "type": "integer"
         }
        },
        "required": [
         "name",
         "token",
         "number"
        ],
        "additionalProperties": false
       }
      },
      "is_static": {
       "type": "boolean"
      },
      "is_virtual": {
       "type": "boolean"
      },
      "is_abstract": {
       "type": "boolean"
      },
      "has_body": {
       "type": "boolean"
      }
     },
     "required": [
      "name",
      "token",
      "signature",
      "return_type",
      "parameter_types",
      "parameters",
      "generic_parameters",
      "is_static",
      "is_virtual",
      "is_abstract",
      "has_body"
     ],
     "additionalProperties": false
    }
   },
   "total_count": {
    "type": "integer"
   },
   "returned_count": {
    "type": "integer"
   },
   "nextCursor": {
    "type": "string"
   }
  },
  "required": [
   "items",
   "total_count",
   "returned_count"
  ],
  "additionalProperties": false
 },
 "get_method_il": {
  "type": "object",
  "properties": {
   "method": {
    "type": "object",
    "properties": {
     "name": {
      "type": "string"
     },
     "token": {
      "type": "integer"
     },
     "signature": {
      "type": "string"
     }
    },
    "required": [
     "name",
     "token",
     "signature"
    ],
    "additionalProperties": false
   },
   "instructions": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "offset": {
       "type": "integer"
      },
      "opcode": {
       "type": "string"
      },
      "operand": {
       "type": "string"
      }
     },
     "required": [
      "index",
      "offset",
      "opcode",
      "operand"
     ],
     "additionalProperties": false
    }
   },
   "max_stack": {
    "type": "integer"
   },
   "init_locals": {
    "type": "boolean"
   },
   "keep_old_max_stack": {
    "type": "boolean"
   },
   "local_var_sig_tok": {
    "type": "integer"
   },
   "locals": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "type": {
       "type": "string"
      },
      "name": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "index",
      "type",
      "name"
     ],
     "additionalProperties": false
    }
   },
   "exception_handlers": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "handler_type": {
       "type": "string"
      },
      "try_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "try_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "filter_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "catch_type": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "handler_type",
      "try_start",
      "try_end",
      "handler_start",
      "handler_end",
      "filter_start",
      "catch_type"
     ],
     "additionalProperties": false
    }
   },
   "has_pending_patch": {
    "type": "boolean"
   }
  },
  "required": [
   "method",
   "instructions",
   "max_stack",
   "init_locals",
   "keep_old_max_stack",
   "local_var_sig_tok",
   "locals",
   "exception_handlers",
   "has_pending_patch"
  ],
  "additionalProperties": false
 },
 "patch_method_il": {
  "type": "object",
  "properties": {
   "method": {
    "type": "object",
    "properties": {
     "name": {
      "type": "string"
     },
     "token": {
      "type": "integer"
     },
     "signature": {
      "type": "string"
     }
    },
    "required": [
     "name",
     "token",
     "signature"
    ],
    "additionalProperties": false
   },
   "instructions": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "offset": {
       "type": "integer"
      },
      "opcode": {
       "type": "string"
      },
      "operand": {
       "type": "string"
      }
     },
     "required": [
      "index",
      "offset",
      "opcode",
      "operand"
     ],
     "additionalProperties": false
    }
   },
   "max_stack": {
    "type": "integer"
   },
   "init_locals": {
    "type": "boolean"
   },
   "keep_old_max_stack": {
    "type": "boolean"
   },
   "local_var_sig_tok": {
    "type": "integer"
   },
   "locals": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "type": {
       "type": "string"
      },
      "name": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "index",
      "type",
      "name"
     ],
     "additionalProperties": false
    }
   },
   "exception_handlers": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "handler_type": {
       "type": "string"
      },
      "try_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "try_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "filter_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "catch_type": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "handler_type",
      "try_start",
      "try_end",
      "handler_start",
      "handler_end",
      "filter_start",
      "catch_type"
     ],
     "additionalProperties": false
    }
   },
   "has_pending_patch": {
    "type": "boolean"
   },
   "method_token": {
    "type": "integer"
   },
   "method_token_hex": {
    "type": "string"
   },
   "checkpoint": {
    "type": "object"
   },
   "history": {
    "type": "object"
   },
   "confirmed_risks": {
    "type": "array",
    "items": {
     "type": "object"
    }
   },
   "compatibility_warning": {
    "type": "string"
   },
   "backup_path": {
    "type": "null"
   },
   "edits_applied": {
    "type": "integer"
   }
  },
  "required": [
   "method",
   "instructions",
   "max_stack",
   "init_locals",
   "keep_old_max_stack",
   "local_var_sig_tok",
   "locals",
   "exception_handlers",
   "has_pending_patch",
   "method_token",
   "method_token_hex",
   "checkpoint",
   "history",
   "compatibility_warning",
   "backup_path",
   "edits_applied"
  ],
  "additionalProperties": false
 },
 "force_return": {
  "type": "object",
  "properties": {
   "method": {
    "type": "object",
    "properties": {
     "name": {
      "type": "string"
     },
     "token": {
      "type": "integer"
     },
     "signature": {
      "type": "string"
     }
    },
    "required": [
     "name",
     "token",
     "signature"
    ],
    "additionalProperties": false
   },
   "instructions": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "offset": {
       "type": "integer"
      },
      "opcode": {
       "type": "string"
      },
      "operand": {
       "type": "string"
      }
     },
     "required": [
      "index",
      "offset",
      "opcode",
      "operand"
     ],
     "additionalProperties": false
    }
   },
   "max_stack": {
    "type": "integer"
   },
   "init_locals": {
    "type": "boolean"
   },
   "keep_old_max_stack": {
    "type": "boolean"
   },
   "local_var_sig_tok": {
    "type": "integer"
   },
   "locals": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "type": {
       "type": "string"
      },
      "name": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "index",
      "type",
      "name"
     ],
     "additionalProperties": false
    }
   },
   "exception_handlers": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "handler_type": {
       "type": "string"
      },
      "try_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "try_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "filter_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "catch_type": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "handler_type",
      "try_start",
      "try_end",
      "handler_start",
      "handler_end",
      "filter_start",
      "catch_type"
     ],
     "additionalProperties": false
    }
   },
   "has_pending_patch": {
    "type": "boolean"
   },
   "method_token": {
    "type": "integer"
   },
   "method_token_hex": {
    "type": "string"
   },
   "checkpoint": {
    "type": "object"
   },
   "history": {
    "type": "object"
   },
   "confirmed_risks": {
    "type": "array",
    "items": {
     "type": "object"
    }
   },
   "compatibility_warning": {
    "type": "string"
   },
   "backup_path": {
    "type": "null"
   },
   "forced_return": {
    "type": "boolean"
   },
   "return_behavior": {
    "type": "string"
   }
  },
  "required": [
   "method",
   "instructions",
   "max_stack",
   "init_locals",
   "keep_old_max_stack",
   "local_var_sig_tok",
   "locals",
   "exception_handlers",
   "has_pending_patch",
   "method_token",
   "method_token_hex",
   "checkpoint",
   "history",
   "compatibility_warning",
   "backup_path",
   "forced_return",
   "return_behavior"
  ],
  "additionalProperties": false
 },
 "nop_method": {
  "type": "object",
  "properties": {
   "method": {
    "type": "object",
    "properties": {
     "name": {
      "type": "string"
     },
     "token": {
      "type": "integer"
     },
     "signature": {
      "type": "string"
     }
    },
    "required": [
     "name",
     "token",
     "signature"
    ],
    "additionalProperties": false
   },
   "instructions": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "offset": {
       "type": "integer"
      },
      "opcode": {
       "type": "string"
      },
      "operand": {
       "type": "string"
      }
     },
     "required": [
      "index",
      "offset",
      "opcode",
      "operand"
     ],
     "additionalProperties": false
    }
   },
   "max_stack": {
    "type": "integer"
   },
   "init_locals": {
    "type": "boolean"
   },
   "keep_old_max_stack": {
    "type": "boolean"
   },
   "local_var_sig_tok": {
    "type": "integer"
   },
   "locals": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "type": {
       "type": "string"
      },
      "name": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "index",
      "type",
      "name"
     ],
     "additionalProperties": false
    }
   },
   "exception_handlers": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "handler_type": {
       "type": "string"
      },
      "try_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "try_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "filter_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "catch_type": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "handler_type",
      "try_start",
      "try_end",
      "handler_start",
      "handler_end",
      "filter_start",
      "catch_type"
     ],
     "additionalProperties": false
    }
   },
   "has_pending_patch": {
    "type": "boolean"
   },
   "method_token": {
    "type": "integer"
   },
   "method_token_hex": {
    "type": "string"
   },
   "checkpoint": {
    "type": "object"
   },
   "history": {
    "type": "object"
   },
   "confirmed_risks": {
    "type": "array",
    "items": {
     "type": "object"
    }
   },
   "compatibility_warning": {
    "type": "string"
   },
   "backup_path": {
    "type": "null"
   },
   "nopped": {
    "type": "boolean"
   },
   "return_behavior": {
    "type": "string"
   }
  },
  "required": [
   "method",
   "instructions",
   "max_stack",
   "init_locals",
   "keep_old_max_stack",
   "local_var_sig_tok",
   "locals",
   "exception_handlers",
   "has_pending_patch",
   "method_token",
   "method_token_hex",
   "checkpoint",
   "history",
   "compatibility_warning",
   "backup_path",
   "nopped",
   "return_behavior"
  ],
  "additionalProperties": false
 },
 "revert_method_il": {
  "type": "object",
  "properties": {
   "method": {
    "type": "object",
    "properties": {
     "name": {
      "type": "string"
     },
     "token": {
      "type": "integer"
     },
     "signature": {
      "type": "string"
     }
    },
    "required": [
     "name",
     "token",
     "signature"
    ],
    "additionalProperties": false
   },
   "instructions": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "offset": {
       "type": "integer"
      },
      "opcode": {
       "type": "string"
      },
      "operand": {
       "type": "string"
      }
     },
     "required": [
      "index",
      "offset",
      "opcode",
      "operand"
     ],
     "additionalProperties": false
    }
   },
   "max_stack": {
    "type": "integer"
   },
   "init_locals": {
    "type": "boolean"
   },
   "keep_old_max_stack": {
    "type": "boolean"
   },
   "local_var_sig_tok": {
    "type": "integer"
   },
   "locals": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "index": {
       "type": "integer"
      },
      "type": {
       "type": "string"
      },
      "name": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "index",
      "type",
      "name"
     ],
     "additionalProperties": false
    }
   },
   "exception_handlers": {
    "type": "array",
    "items": {
     "type": "object",
     "properties": {
      "handler_type": {
       "type": "string"
      },
      "try_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "try_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "handler_end": {
       "type": [
        "integer",
        "null"
       ]
      },
      "filter_start": {
       "type": [
        "integer",
        "null"
       ]
      },
      "catch_type": {
       "type": [
        "string",
        "null"
       ]
      }
     },
     "required": [
      "handler_type",
      "try_start",
      "try_end",
      "handler_start",
      "handler_end",
      "filter_start",
      "catch_type"
     ],
     "additionalProperties": false
    }
   },
   "has_pending_patch": {
    "type": "boolean"
   },
   "reverted": {
    "type": "boolean"
   },
   "history": {
    "type": "object"
   },
   "compatibility_warning": {
    "type": "string"
   }
  },
  "required": [
   "method",
   "instructions",
   "max_stack",
   "init_locals",
   "keep_old_max_stack",
   "local_var_sig_tok",
   "locals",
   "exception_handlers",
   "has_pending_patch",
   "reverted",
   "compatibility_warning"
  ],
  "additionalProperties": false
 },
 "rename_symbol_by_token": {
  "oneOf": [
   {
    "type": "object",
    "properties": {
     "changed": {
      "type": "boolean"
     },
     "target_kind": {
      "type": "string"
     },
     "token": {
      "type": "integer"
     },
     "token_hex": {
      "type": "string"
     },
     "old_name": {
      "type": "string"
     },
     "new_name": {
      "type": "string"
     },
     "updated_type_references": {
      "type": "integer"
     },
     "updated_member_references": {
      "type": "integer"
     },
     "checkpoint": {
      "type": "object"
     },
     "history": {
      "type": "object"
     },
     "confirmed_risks": {
      "type": "array",
      "items": {
       "type": "object"
      }
     },
     "compatibility_warning": {
      "type": "string"
     },
     "backup_path": {
      "type": "null"
     }
    },
    "required": [
     "changed",
     "target_kind",
     "token",
     "token_hex",
     "old_name",
     "new_name",
     "checkpoint",
     "history",
     "compatibility_warning",
     "backup_path"
    ],
    "additionalProperties": false
   },
   {
    "type": "object",
    "properties": {
     "changed": {
      "type": "boolean"
     },
     "changed_count": {
      "type": "integer"
     },
     "target_kind": {
      "type": "string"
     },
     "enum_token": {
      "type": "integer"
     },
     "enum_token_hex": {
      "type": "string"
     },
     "enum_full_name": {
      "type": "string"
     },
     "updated_member_references": {
      "type": "integer"
     },
     "members": {
      "type": "array",
      "items": {
       "type": "object",
       "properties": {
        "field_token": {
         "type": "integer"
        },
        "field_token_hex": {
         "type": "string"
        },
        "value": {
         "type": "number"
        },
        "old_name": {
         "type": "string"
        },
        "new_name": {
         "type": "string"
        },
        "changed": {
         "type": "boolean"
        },
        "updated_member_references": {
         "type": "integer"
        }
       },
       "required": [
        "field_token",
        "field_token_hex",
        "value",
        "old_name",
        "new_name",
        "changed",
        "updated_member_references"
       ],
       "additionalProperties": false
      }
     },
     "checkpoint": {
      "type": "object"
     },
     "history": {
      "type": "object"
     },
     "confirmed_risks": {
      "type": "array",
      "items": {
       "type": "object"
      }
     },
     "compatibility_warning": {
      "type": "string"
     },
     "backup_path": {
      "type": "null"
     }
    },
    "required": [
     "changed",
     "changed_count",
     "target_kind",
     "enum_token",
     "enum_token_hex",
     "enum_full_name",
     "updated_member_references",
     "members",
     "checkpoint",
     "history",
     "compatibility_warning",
     "backup_path"
    ],
    "additionalProperties": false
   }
  ]
 },
 "save_assembly": {
  "type": "object",
  "properties": {
   "saved_to": {
    "type": "string"
   },
   "bytes_written": {
    "type": "integer"
   },
   "backup_path": {
    "type": "null"
   },
   "source_preserved": {
    "type": "boolean"
   },
   "sha256": {
    "type": "string"
   },
   "file_id": {
    "type": "string"
   },
   "lineage_id": {
    "type": "string"
   },
   "checkpoint_id": {
    "type": "string"
   },
   "warnings": {
    "type": "array",
    "items": {
     "type": "string"
    }
   }
  },
  "required": [
   "saved_to",
   "bytes_written",
   "backup_path",
   "source_preserved",
   "sha256",
   "file_id",
   "lineage_id",
   "checkpoint_id",
   "warnings"
  ],
  "additionalProperties": false
 },
 "decompile_method": {
  "type": "string",
  "description": "原样文本；不是 JSON 投影"
 },
 "decompile_by_token": {
  "type": "string",
  "description": "原样文本；不是 JSON 投影"
 },
 "decompile_type": {
  "type": "string",
  "description": "原样文本；不是 JSON 投影"
 },
 "generate_bepinex_plugin": {
  "type": "string",
  "description": "原样文本；不是 JSON 投影"
 },
 "generate_harmony_patch": {
  "type": "string",
  "description": "原样文本；不是 JSON 投影"
 }
}
```

## 附录 B：冻结调试 JSON Schema（自包含）

所有 `#/$defs/...` 都在下面同一 JSON 对象的 `$defs` 中；`*_args` 是调用参数，`*_result` 是成功信封内 `result`，其余定义包含事件、分页、句柄与状态/错误。固定禁用 API 的 args 也收录，但不代表可用。

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "urn:dnspy:extension:mcp:contracts:dnspy.debug.v1.schema.json",
  "title": "dnspy.debug.v1 public debug contract (API-DYN-001..025, TYPE-DYN-001..019, EVT-DYN-001..021, 3.4 envelope)",
  "description": "Structural contract only. UTF-8 byte limits live exclusively in dnspy.debug.utf8-limits.json; JSON Schema maxLength counts characters and must never replace the byte stage (EVD-SCHEMA-001). All request/result/error/event-payload objects are additionalProperties=false (CON-DYN-013).",
  "anyOf": [
    {
      "$ref": "#/$defs/envelope_success"
    },
    {
      "$ref": "#/$defs/envelope_failure"
    },
    {
      "$ref": "#/$defs/event_envelope"
    }
  ],
  "$defs": {
    "non_negative_int": {
      "type": "integer",
      "minimum": 0,
      "maximum": 9007199254740991
    },
    "pid": {
      "type": "integer",
      "minimum": 1,
      "maximum": 4294967295
    },
    "exit_code": {
      "type": "integer",
      "minimum": -2147483648,
      "maximum": 2147483647
    },
    "rfc3339_utc_ms": {
      "type": "string",
      "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$"
    },
    "uuid": {
      "type": "string",
      "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
    },
    "address": {
      "type": "string",
      "pattern": "^0x[0-9a-f]{1,16}$"
    },
    "sha256_hex": {
      "type": "string",
      "pattern": "^[0-9a-f]{64}$"
    },
    "mvid": {
      "type": "string",
      "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
    },
    "method_token": {
      "type": "string",
      "pattern": "^0x[0-9a-f]{8}$"
    },
    "volume_serial": {
      "type": "string",
      "pattern": "^0x[0-9a-f]{16}$"
    },
    "file_id": {
      "type": "string",
      "pattern": "^[0-9a-f]{32}$"
    },
    "opaque_handle": {
      "type": "string",
      "pattern": "^[A-Za-z0-9_-]{1,1024}$"
    },
    "session_id": {
      "type": "string",
      "pattern": "^[A-Za-z0-9_-]{1,1024}$"
    },
    "page_cursor": {
      "type": "string",
      "pattern": "^[A-Za-z0-9_-]{1,1024}$"
    },
    "warning": {
      "type": "string",
      "minLength": 1
    },
    "runtime_family": {
      "enum": [
        "net48",
        "coreclr"
      ]
    },
    "architecture": {
      "enum": [
        "x86",
        "x64"
      ]
    },
    "coordinator_state": {
      "enum": [
        "idle",
        "starting",
        "running",
        "paused",
        "restarting",
        "stopping",
        "faulted"
      ]
    },
    "launch_mode": {
      "enum": [
        "auto",
        "net48-exe",
        "coreclr-apphost",
        "coreclr-dotnet",
        "harness"
      ]
    },
    "break_kind": {
      "enum": [
        "none",
        "process",
        "module_cctor_or_entry",
        "entry"
      ]
    },
    "event_kind": {
      "enum": [
        "session_start",
        "start_failed",
        "process_created",
        "process_exited",
        "runtime_created",
        "module_loaded",
        "module_unloaded",
        "thread_created",
        "thread_exited",
        "paused",
        "continued",
        "breakpoint_bound",
        "breakpoint_hit",
        "exception",
        "step_completed",
        "output",
        "ownership_lost",
        "recovery",
        "payload_omitted",
        "session_end",
        "control_failed"
      ]
    },
    "debug_context": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "event_cursor": {
          "$ref": "#/$defs/non_negative_int"
        },
        "state": {
          "$ref": "#/$defs/coordinator_state"
        }
      },
      "required": [
        "generation",
        "pause_epoch",
        "event_cursor",
        "state"
      ]
    },
    "page_request": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        }
      }
    },
    "module_identity": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "runtime_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "name": {
          "type": "string",
          "minLength": 1
        },
        "path": {
          "type": "string",
          "minLength": 1
        },
        "mvid": {
          "$ref": "#/$defs/mvid"
        },
        "sha256": {
          "$ref": "#/$defs/sha256_hex"
        },
        "base_address": {
          "$ref": "#/$defs/address"
        },
        "size": {
          "$ref": "#/$defs/non_negative_int"
        },
        "layout": {
          "enum": [
            "file",
            "memory"
          ]
        },
        "identity_strength": {
          "enum": [
            "disk_strong",
            "runtime_weak"
          ]
        }
      },
      "required": [
        "module_handle",
        "runtime_handle",
        "name",
        "mvid",
        "base_address",
        "size",
        "layout",
        "identity_strength"
      ]
    },
    "location": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "method_token": {
          "$ref": "#/$defs/method_token"
        },
        "il_offset": {
          "$ref": "#/$defs/non_negative_int"
        },
        "native_ip": {
          "$ref": "#/$defs/address"
        }
      },
      "required": [
        "module_handle"
      ],
      "allOf": [
        {
          "if": {
            "required": [
              "method_token"
            ]
          },
          "then": {
            "required": [
              "il_offset"
            ]
          }
        },
        {
          "if": {
            "required": [
              "il_offset"
            ]
          },
          "then": {
            "required": [
              "method_token"
            ]
          }
        }
      ]
    },
    "exception_policy": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "break_on": {
          "enum": [
            "unhandled",
            "first_chance_and_unhandled",
            "none"
          ]
        }
      },
      "required": [
        "break_on"
      ]
    },
    "value_node": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "value_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "parent_value_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "depth": {
          "type": "integer",
          "minimum": 0,
          "maximum": 4
        },
        "name": {
          "type": "string",
          "minLength": 1
        },
        "kind": {
          "enum": [
            "this",
            "parameter",
            "local",
            "field",
            "array_element"
          ]
        },
        "type": {
          "type": "string",
          "minLength": 1
        },
        "display": {
          "type": "string"
        },
        "has_children": {
          "type": "boolean"
        },
        "is_null": {
          "type": "boolean"
        },
        "truncated": {
          "type": "boolean"
        },
        "unavailable_reason": {
          "enum": [
            "requires_function_evaluation",
            "value_handle_limit"
          ]
        }
      },
      "required": [
        "depth",
        "name",
        "kind",
        "type",
        "display",
        "has_children",
        "is_null",
        "truncated"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "has_children": {
                "const": true
              },
              "is_null": {
                "const": false
              }
            },
            "required": [
              "has_children",
              "is_null"
            ],
            "not": {
              "required": [
                "unavailable_reason"
              ]
            }
          },
          "then": {
            "required": [
              "value_handle"
            ]
          }
        },
        {
          "if": {
            "anyOf": [
              {
                "properties": {
                  "has_children": {
                    "const": false
                  }
                },
                "required": [
                  "has_children"
                ]
              },
              {
                "properties": {
                  "is_null": {
                    "const": true
                  }
                },
                "required": [
                  "is_null"
                ]
              },
              {
                "required": [
                  "unavailable_reason"
                ]
              }
            ]
          },
          "then": {
            "not": {
              "required": [
                "value_handle"
              ]
            }
          }
        }
      ]
    },
    "artifact": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "artifact_id": {
          "type": "string",
          "minLength": 1
        },
        "path": {
          "type": "string",
          "minLength": 1
        },
        "kind": {
          "enum": [
            "raw",
            "reconstructed"
          ]
        },
        "layout": {
          "enum": [
            "file",
            "memory"
          ]
        },
        "size": {
          "$ref": "#/$defs/non_negative_int"
        },
        "sha256": {
          "$ref": "#/$defs/sha256_hex"
        },
        "source_module": {
          "$ref": "#/$defs/module_identity"
        },
        "manifest_path": {
          "type": "string",
          "minLength": 1
        }
      },
      "required": [
        "artifact_id",
        "path",
        "kind",
        "layout",
        "size",
        "sha256",
        "source_module",
        "manifest_path"
      ]
    },
    "breakpoint": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        },
        "owned": {
          "const": true
        },
        "enabled": {
          "type": "boolean"
        },
        "bound": {
          "type": "boolean"
        },
        "module_identity": {
          "$ref": "#/$defs/module_identity"
        },
        "method_token": {
          "$ref": "#/$defs/method_token"
        },
        "il_offset": {
          "$ref": "#/$defs/non_negative_int"
        },
        "last_error": {
          "$ref": "#/$defs/domain_error"
        }
      },
      "required": [
        "breakpoint_id",
        "owned",
        "enabled",
        "bound",
        "module_identity",
        "method_token",
        "il_offset"
      ]
    },
    "runtime_matrix_entry": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "launch_mode": {
          "enum": [
            "net48-exe",
            "coreclr-apphost",
            "coreclr-dotnet"
          ]
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "product_supported": {
          "const": true
        },
        "launch": {
          "type": "boolean"
        },
        "attach": {
          "const": false
        },
        "restart": {
          "type": "boolean"
        },
        "host_path_required": {
          "type": "boolean"
        },
        "unavailable_reason": {
          "const": "host_architecture_mismatch"
        }
      },
      "required": [
        "launch_mode",
        "runtime_family",
        "architecture",
        "product_supported",
        "launch",
        "attach",
        "restart",
        "host_path_required"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "launch": {
                "const": false
              }
            },
            "required": [
              "launch"
            ]
          },
          "then": {
            "required": [
              "unavailable_reason"
            ]
          }
        },
        {
          "if": {
            "properties": {
              "launch": {
                "const": true
              }
            },
            "required": [
              "launch"
            ]
          },
          "then": {
            "not": {
              "required": [
                "unavailable_reason"
              ]
            }
          }
        }
      ]
    },
    "domain_error": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "code": {
          "enum": [
            "DEBUG_DISABLED",
            "CAPABILITY_UNAVAILABLE",
            "INVALID_STATE",
            "STALE_HANDLE",
            "TARGET_MISMATCH",
            "NOT_FOUND",
            "ALREADY_EXISTS",
            "LIMIT_EXCEEDED",
            "TIMEOUT",
            "OWNERSHIP_LOST",
            "REQUEST_ID_REUSE",
            "INTERNAL_ERROR"
          ]
        },
        "message": {
          "type": "string",
          "minLength": 1
        },
        "recovery": {
          "type": "string",
          "minLength": 1
        },
        "current_state": {
          "$ref": "#/$defs/coordinator_state"
        },
        "required_states": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/coordinator_state"
          },
          "uniqueItems": true,
          "maxItems": 7
        },
        "retry_after_ms": {
          "type": "integer",
          "minimum": 0,
          "maximum": 30000
        },
        "details": {
          "$ref": "#/$defs/unsupported_target_details"
        }
      },
      "required": [
        "code",
        "message",
        "recovery",
        "current_state",
        "required_states"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "code": {
                "const": "LIMIT_EXCEEDED"
              }
            },
            "required": [
              "code"
            ]
          },
          "then": {
            "properties": {
              "retry_after_ms": {
                "const": 1000
              }
            },
            "required": [
              "retry_after_ms"
            ]
          }
        },
        {
          "if": {
            "properties": {
              "code": {
                "const": "TIMEOUT"
              }
            },
            "required": [
              "code"
            ]
          },
          "then": {
            "properties": {
              "retry_after_ms": {
                "const": 0
              }
            },
            "required": [
              "retry_after_ms"
            ]
          }
        },
        {
          "if": {
            "properties": {
              "code": {
                "enum": [
                  "DEBUG_DISABLED",
                  "CAPABILITY_UNAVAILABLE",
                  "INVALID_STATE",
                  "STALE_HANDLE",
                  "TARGET_MISMATCH",
                  "NOT_FOUND",
                  "ALREADY_EXISTS",
                  "OWNERSHIP_LOST",
                  "REQUEST_ID_REUSE",
                  "INTERNAL_ERROR"
                ]
              }
            },
            "required": [
              "code"
            ]
          },
          "then": {
            "not": {
              "required": [
                "retry_after_ms"
              ]
            }
          }
        },
        {
          "if": {
            "properties": {
              "code": {
                "const": "INVALID_STATE"
              }
            },
            "required": [
              "code"
            ]
          },
          "then": {
            "properties": {
              "required_states": {
                "minItems": 1
              }
            }
          }
        },
        {
          "if": {
            "not": {
              "properties": {
                "code": {
                  "const": "INVALID_STATE"
                }
              },
              "required": [
                "code"
              ]
            }
          },
          "then": {
            "properties": {
              "required_states": {
                "maxItems": 0
              }
            }
          }
        }
      ]
    },
    "file_identity": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "role": {
          "enum": [
            "target",
            "host",
            "harness",
            "working_directory"
          ]
        },
        "object_kind": {
          "enum": [
            "file",
            "directory"
          ]
        },
        "final_path": {
          "type": "string",
          "minLength": 1
        },
        "volume_serial": {
          "$ref": "#/$defs/volume_serial"
        },
        "file_id": {
          "$ref": "#/$defs/file_id"
        },
        "sha256": {
          "$ref": "#/$defs/sha256_hex"
        }
      },
      "required": [
        "role",
        "object_kind",
        "final_path",
        "volume_serial",
        "file_id"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "object_kind": {
                "const": "file"
              }
            },
            "required": [
              "object_kind"
            ]
          },
          "then": {
            "required": [
              "sha256"
            ]
          }
        },
        {
          "if": {
            "properties": {
              "object_kind": {
                "const": "directory"
              }
            },
            "required": [
              "object_kind"
            ]
          },
          "then": {
            "not": {
              "required": [
                "sha256"
              ]
            }
          }
        }
      ]
    },
    "owned_process": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "process_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "pid": {
          "$ref": "#/$defs/pid"
        },
        "start_time_utc": {
          "$ref": "#/$defs/rfc3339_utc_ms"
        },
        "filename": {
          "type": "string",
          "minLength": 1
        },
        "image_identity": {
          "$ref": "#/$defs/file_identity"
        },
        "runtime_identity": {
          "type": "string",
          "minLength": 1
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        }
      },
      "required": [
        "process_handle",
        "pid",
        "start_time_utc",
        "filename",
        "image_identity",
        "runtime_identity",
        "runtime_family",
        "architecture"
      ]
    },
    "attachable_process": {
      "description": "Reserved disabled type (TYPE-DYN-014): a v1 handler must never instantiate or return this object.",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "pid": {
          "$ref": "#/$defs/pid"
        },
        "start_time_utc": {
          "$ref": "#/$defs/rfc3339_utc_ms"
        },
        "runtime_identity": {
          "type": "string",
          "minLength": 1
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "runtime_name": {
          "type": "string",
          "minLength": 1
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "filename": {
          "type": "string",
          "minLength": 1
        },
        "attach_nonce": {
          "type": "string",
          "minLength": 1
        },
        "nonce_expires_utc": {
          "$ref": "#/$defs/rfc3339_utc_ms"
        }
      },
      "required": [
        "pid",
        "start_time_utc",
        "runtime_identity",
        "runtime_family",
        "runtime_name",
        "architecture",
        "filename",
        "attach_nonce",
        "nonce_expires_utc"
      ]
    },
    "thread_info": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "managed_id": {
          "type": "integer"
        },
        "os_id": {
          "$ref": "#/$defs/non_negative_int"
        },
        "name": {
          "type": "string"
        },
        "state": {
          "enum": [
            "running",
            "paused",
            "exited",
            "unknown"
          ]
        },
        "is_current": {
          "type": "boolean"
        }
      },
      "required": [
        "thread_handle",
        "state",
        "is_current"
      ]
    },
    "frame_info": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "frame_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "index": {
          "$ref": "#/$defs/non_negative_int"
        },
        "location": {
          "$ref": "#/$defs/location"
        },
        "display_name": {
          "type": "string",
          "minLength": 1
        }
      },
      "required": [
        "frame_handle",
        "index",
        "location",
        "display_name"
      ]
    },
    "value_budgets": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "depth_limit": {
          "const": 4
        },
        "node_limit": {
          "const": 1024
        },
        "value_handle_limit": {
          "const": 4096
        },
        "string_utf8_limit": {
          "const": 65536
        },
        "response_utf8_limit": {
          "const": 8388608
        },
        "depth_used": {
          "type": "integer",
          "minimum": 0,
          "maximum": 4
        },
        "nodes_used": {
          "type": "integer",
          "minimum": 0,
          "maximum": 1024
        },
        "value_handles_used": {
          "type": "integer",
          "minimum": 0,
          "maximum": 4096
        },
        "truncated": {
          "type": "boolean"
        }
      },
      "required": [
        "depth_limit",
        "node_limit",
        "value_handle_limit",
        "string_utf8_limit",
        "response_utf8_limit",
        "depth_used",
        "nodes_used",
        "value_handles_used",
        "truncated"
      ]
    },
    "unsupported_target_evidence": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "kind": {
          "enum": [
            "pe_headers",
            "clr_metadata",
            "runtime_contract",
            "module_identity"
          ]
        },
        "value": {
          "type": "string",
          "minLength": 1
        }
      },
      "required": [
        "kind",
        "value"
      ]
    },
    "unsupported_target_details": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "detected_target_kind": {
          "enum": [
            "unity_mono",
            "mixed_mode",
            "pure_native",
            "unsupported_managed_runtime"
          ]
        },
        "evidence": {
          "type": "array",
          "minItems": 1,
          "maxItems": 8,
          "items": {
            "$ref": "#/$defs/unsupported_target_evidence"
          }
        },
        "recommended_workflow": {
          "enum": [
            "mono_dynamic_analysis",
            "pe_x64dbg_ida_dynamic_analysis",
            "managed_static_analysis"
          ]
        }
      },
      "required": [
        "detected_target_kind",
        "evidence",
        "recommended_workflow"
      ]
    },
    "debug_capabilities_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {},
      "maxProperties": 0
    },
    "debug_status_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        }
      }
    },
    "debug_launch_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "target_path": {
          "type": "string",
          "minLength": 1
        },
        "expected_sha256": {
          "$ref": "#/$defs/sha256_hex"
        },
        "launch_mode": {
          "$ref": "#/$defs/launch_mode"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "target_argv": {
          "type": "array",
          "items": {
            "type": "string"
          }
        },
        "working_directory": {
          "type": "string",
          "minLength": 1
        },
        "break_kind": {
          "$ref": "#/$defs/break_kind"
        },
        "host_path": {
          "type": "string",
          "minLength": 1
        },
        "host_sha256": {
          "$ref": "#/$defs/sha256_hex"
        },
        "host_argv": {
          "type": "array",
          "items": {
            "type": "string"
          }
        },
        "harness_path": {
          "type": "string",
          "minLength": 1
        },
        "harness_sha256": {
          "$ref": "#/$defs/sha256_hex"
        },
        "harness_argv": {
          "type": "array",
          "items": {
            "type": "string"
          }
        },
        "exception_policy": {
          "$ref": "#/$defs/exception_policy"
        }
      },
      "required": [
        "request_id",
        "target_path",
        "expected_sha256",
        "launch_mode",
        "architecture"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "launch_mode": {
                "const": "coreclr-dotnet"
              }
            },
            "required": [
              "launch_mode"
            ]
          },
          "then": {
            "required": [
              "host_path",
              "host_sha256"
            ],
            "not": {
              "anyOf": [
                {
                  "required": [
                    "harness_path"
                  ]
                },
                {
                  "required": [
                    "harness_sha256"
                  ]
                },
                {
                  "required": [
                    "harness_argv"
                  ]
                }
              ]
            },
            "properties": {
              "break_kind": {
                "enum": [
                  "none",
                  "module_cctor_or_entry",
                  "entry"
                ]
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "launch_mode": {
                "const": "harness"
              }
            },
            "required": [
              "launch_mode"
            ]
          },
          "then": {
            "required": [
              "harness_path",
              "harness_sha256"
            ],
            "not": {
              "anyOf": [
                {
                  "required": [
                    "target_argv"
                  ]
                },
                {
                  "required": [
                    "host_path"
                  ]
                },
                {
                  "required": [
                    "host_sha256"
                  ]
                },
                {
                  "required": [
                    "host_argv"
                  ]
                }
              ]
            },
            "properties": {
              "break_kind": {
                "const": "none"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "launch_mode": {
                "enum": [
                  "auto",
                  "net48-exe",
                  "coreclr-apphost"
                ]
              }
            },
            "required": [
              "launch_mode"
            ]
          },
          "then": {
            "not": {
              "anyOf": [
                {
                  "required": [
                    "harness_path"
                  ]
                },
                {
                  "required": [
                    "harness_sha256"
                  ]
                },
                {
                  "required": [
                    "harness_argv"
                  ]
                }
              ]
            }
          }
        }
      ]
    },
    "debug_pause_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        }
      },
      "required": [
        "session_id",
        "generation",
        "request_id"
      ]
    },
    "debug_continue_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "request_id"
      ]
    },
    "debug_restart_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        }
      },
      "required": [
        "session_id",
        "generation",
        "request_id"
      ]
    },
    "debug_terminate_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        }
      },
      "required": [
        "session_id",
        "generation",
        "request_id"
      ]
    },
    "debug_read_events_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "after_cursor": {
          "$ref": "#/$defs/non_negative_int"
        },
        "limit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "kinds": {
          "type": "array",
          "minItems": 0,
          "maxItems": 21,
          "uniqueItems": true,
          "items": {
            "$ref": "#/$defs/event_kind"
          }
        }
      },
      "required": [
        "session_id"
      ]
    },
    "debug_wait_event_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "after_cursor": {
          "$ref": "#/$defs/non_negative_int"
        },
        "limit": {
          "type": "integer",
          "minimum": 1,
          "maximum": 50
        },
        "kinds": {
          "type": "array",
          "minItems": 0,
          "maxItems": 21,
          "uniqueItems": true,
          "items": {
            "$ref": "#/$defs/event_kind"
          }
        },
        "timeout_ms": {
          "type": "integer",
          "minimum": 0,
          "maximum": 30000
        }
      },
      "required": [
        "session_id"
      ]
    },
    "debug_set_breakpoint_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "module_sha256": {
          "$ref": "#/$defs/sha256_hex"
        },
        "mvid": {
          "$ref": "#/$defs/mvid"
        },
        "method_token": {
          "$ref": "#/$defs/method_token"
        },
        "il_offset": {
          "$ref": "#/$defs/non_negative_int"
        },
        "enabled": {
          "type": "boolean"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "request_id",
        "module_handle",
        "mvid",
        "method_token",
        "il_offset"
      ]
    },
    "debug_list_breakpoints_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "enabled": {
          "type": "boolean"
        }
      },
      "required": [
        "session_id",
        "generation"
      ]
    },
    "debug_set_breakpoint_enabled_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        },
        "enabled": {
          "type": "boolean"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "request_id",
        "breakpoint_id",
        "enabled"
      ]
    },
    "debug_remove_breakpoint_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "request_id",
        "breakpoint_id"
      ]
    },
    "debug_list_threads_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch"
      ]
    },
    "debug_get_stack_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "thread_handle"
      ]
    },
    "debug_step_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "kind": {
          "enum": [
            "into",
            "over",
            "out"
          ]
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "request_id",
        "thread_handle",
        "kind"
      ]
    },
    "debug_get_locals_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "frame_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "frame_handle"
      ]
    },
    "debug_expand_value_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "value_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "depth": {
          "type": "integer",
          "minimum": 1,
          "maximum": 4
        },
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "value_handle"
      ]
    },
    "debug_list_modules_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        }
      },
      "required": [
        "session_id",
        "generation"
      ]
    },
    "debug_read_memory_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "address": {
          "$ref": "#/$defs/address"
        },
        "length": {
          "type": "integer",
          "minimum": 1,
          "maximum": 65536
        },
        "encoding": {
          "enum": [
            "base64",
            "hex"
          ]
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "module_handle",
        "address",
        "length"
      ]
    },
    "debug_dump_module_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "relative_name": {
          "type": "string",
          "minLength": 1,
          "maxLength": 128,
          "pattern": "^(?!\\.{1,2}$)(?!.*[. ]$)[^/\\\\:\\s]+$"
        }
      },
      "required": [
        "session_id",
        "generation",
        "pause_epoch",
        "request_id",
        "module_handle"
      ]
    },
    "debug_set_exception_policy_args": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "policy": {
          "$ref": "#/$defs/exception_policy"
        }
      },
      "required": [
        "session_id",
        "generation",
        "request_id",
        "policy"
      ]
    },
    "debug_list_attachable_processes_args": {
      "description": "Reserved disabled API (API-DYN-004): never advertised; schema-valid direct calls must fail with CAPABILITY_UNAVAILABLE and zero side effects.",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "page_size": {
          "type": "integer",
          "minimum": 1,
          "maximum": 100
        },
        "page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "name_filter": {
          "type": "string"
        }
      }
    },
    "debug_attach_args": {
      "description": "Reserved disabled API (API-DYN-005).",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "pid": {
          "$ref": "#/$defs/pid"
        },
        "runtime_identity": {
          "type": "string",
          "minLength": 1
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "attach_nonce": {
          "type": "string",
          "minLength": 1
        },
        "exception_policy": {
          "$ref": "#/$defs/exception_policy"
        }
      },
      "required": [
        "request_id",
        "pid",
        "runtime_identity",
        "runtime_family",
        "architecture",
        "attach_nonce"
      ]
    },
    "debug_detach_args": {
      "description": "Reserved disabled API (API-DYN-010).",
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        }
      },
      "required": [
        "session_id",
        "generation",
        "request_id"
      ]
    },
    "debug_capabilities_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "debug_enabled": {
          "type": "boolean"
        },
        "schema_version": {
          "const": "dnspy.debug.v1"
        },
        "extension_version": {
          "type": "string",
          "minLength": 1
        },
        "dnspy_api": {
          "const": "v6.6.0"
        },
        "host_architecture": {
          "$ref": "#/$defs/architecture"
        },
        "ownership_model": {
          "const": "dedicated_instance_operational_isolation"
        },
        "dedicated_instance_required": {
          "const": true
        },
        "dedicated_instance_acknowledged": {
          "type": "boolean"
        },
        "attach_supported": {
          "const": false
        },
        "tools": {
          "type": "array",
          "items": {
            "enum": [
              "debug_capabilities",
              "debug_status",
              "debug_launch",
              "debug_pause",
              "debug_continue",
              "debug_restart",
              "debug_terminate",
              "debug_read_events",
              "debug_wait_event",
              "debug_set_breakpoint",
              "debug_list_breakpoints",
              "debug_set_breakpoint_enabled",
              "debug_remove_breakpoint",
              "debug_list_threads",
              "debug_get_stack",
              "debug_step",
              "debug_get_locals",
              "debug_expand_value",
              "debug_list_modules",
              "debug_read_memory",
              "debug_dump_module",
              "debug_set_exception_policy"
            ]
          }
        },
        "runtime_matrix": {
          "type": "array",
          "minItems": 6,
          "maxItems": 6,
          "items": {
            "$ref": "#/$defs/runtime_matrix_entry"
          }
        },
        "execution_environment": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "classification": {
              "enum": [
                "vmware",
                "virtualbox",
                "physical",
                "unknown"
              ]
            },
            "execution_allowed": {
              "type": "boolean"
            },
            "local_process_override_active": {
              "type": "boolean"
            },
            "detection_source": {
              "const": "windows_bios_registry_v1"
            },
            "marker_tags": {
              "type": "array",
              "maxItems": 2,
              "uniqueItems": true,
              "items": {
                "enum": [
                  "vmware",
                  "virtualbox",
                  "innotek_gmbh"
                ]
              }
            }
          },
          "required": [
            "classification",
            "execution_allowed",
            "local_process_override_active",
            "detection_source",
            "marker_tags"
          ]
        },
        "security": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "bind_mode": {
              "enum": [
                "loopback",
                "remote_host_only"
              ]
            },
            "auth_required": {
              "type": "boolean"
            },
            "cidr_required": {
              "type": "boolean"
            },
            "sample_output_policy": {
              "const": "all_tool_output_is_untrusted_data"
            }
          },
          "required": [
            "bind_mode",
            "auth_required",
            "cidr_required",
            "sample_output_policy"
          ]
        },
        "artifact_policy": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "retention_scope": {
              "const": "current_extension_process"
            },
            "retained_integrity": {
              "const": "process_lifetime_no_write_delete_share_handles"
            },
            "external_child_race": {
              "const": "current_admission_may_complete_next_admission_fail_closed"
            },
            "cancel_pending": {
              "const": "control_proceeds_store_fail_closed_until_final_completion"
            },
            "restart_existing": {
              "const": "stale_untrusted_read_only_quota_counted"
            },
            "automatic_cleanup": {
              "const": false
            }
          },
          "required": [
            "retention_scope",
            "retained_integrity",
            "external_child_race",
            "cancel_pending",
            "restart_existing",
            "automatic_cleanup"
          ]
        },
        "limits": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "request_body_bytes": {
              "const": 1048576
            },
            "tool_result_bytes": {
              "const": 8388608
            },
            "transport_sessions": {
              "const": 16
            },
            "parallel_short_requests": {
              "const": 16
            },
            "long_connections": {
              "const": 8
            },
            "waits": {
              "const": 8
            },
            "transport_idle_seconds": {
              "const": 600
            },
            "control_operation_seconds": {
              "const": 30
            },
            "event_count": {
              "const": 4096
            },
            "event_bytes": {
              "const": 8388608
            },
            "memory_read_bytes": {
              "const": 65536
            },
            "side_effect_cache_entries": {
              "const": 4096
            },
            "side_effect_cache_bytes": {
              "const": 268435456
            },
            "side_effect_cached_envelope_bytes": {
              "const": 65536
            },
            "command_queue_entries": {
              "const": 64
            },
            "control_queue_entries": {
              "const": 8
            },
            "general_queue_entries": {
              "const": 56
            },
            "value_snapshots_per_pause": {
              "const": 2
            },
            "value_handles_per_pause": {
              "const": 4096
            },
            "artifact_operation_seconds": {
              "const": 30
            },
            "artifact_cancel_grace_ms": {
              "const": 2000
            },
            "artifact_io_chunk_bytes": {
              "const": 1048576
            },
            "artifact_file_bytes": {
              "const": 536870912
            },
            "artifact_session_bytes": {
              "const": 1073741824
            },
            "artifact_store_bytes": {
              "const": 8589934592
            },
            "artifact_sessions": {
              "const": 128
            },
            "artifact_root_children": {
              "const": 128
            },
            "artifact_session_children": {
              "const": 4096
            },
            "artifact_store_children": {
              "const": 4096
            }
          },
          "required": [
            "request_body_bytes",
            "tool_result_bytes",
            "transport_sessions",
            "parallel_short_requests",
            "long_connections",
            "waits",
            "transport_idle_seconds",
            "control_operation_seconds",
            "event_count",
            "event_bytes",
            "memory_read_bytes",
            "side_effect_cache_entries",
            "side_effect_cache_bytes",
            "side_effect_cached_envelope_bytes",
            "command_queue_entries",
            "control_queue_entries",
            "general_queue_entries",
            "value_snapshots_per_pause",
            "value_handles_per_pause",
            "artifact_operation_seconds",
            "artifact_cancel_grace_ms",
            "artifact_io_chunk_bytes",
            "artifact_file_bytes",
            "artifact_session_bytes",
            "artifact_store_bytes",
            "artifact_sessions",
            "artifact_root_children",
            "artifact_session_children",
            "artifact_store_children"
          ]
        },
        "unsupported": {
          "type": "array",
          "minItems": 3,
          "maxItems": 3,
          "uniqueItems": true,
          "items": {
            "enum": [
              "debug_list_attachable_processes",
              "debug_attach",
              "debug_detach"
            ]
          }
        }
      },
      "required": [
        "debug_enabled",
        "schema_version",
        "extension_version",
        "dnspy_api",
        "host_architecture",
        "ownership_model",
        "dedicated_instance_required",
        "dedicated_instance_acknowledged",
        "attach_supported",
        "tools",
        "runtime_matrix",
        "execution_environment",
        "security",
        "artifact_policy",
        "limits",
        "unsupported"
      ]
    },
    "debug_status_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "state": {
          "$ref": "#/$defs/coordinator_state"
        },
        "active_session_id": {
          "$ref": "#/$defs/session_id"
        },
        "last_session_id": {
          "$ref": "#/$defs/session_id"
        },
        "owned_process": {
          "$ref": "#/$defs/owned_process"
        },
        "observed_process_state": {
          "enum": [
            "running",
            "paused",
            "unknown"
          ]
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "start_kind": {
          "const": "launch"
        },
        "launch_mode": {
          "$ref": "#/$defs/launch_mode"
        },
        "fault": {
          "$ref": "#/$defs/domain_error"
        }
      },
      "required": [
        "state"
      ],
      "allOf": [
        {
          "if": {
            "required": [
              "owned_process"
            ]
          },
          "then": {
            "required": [
              "observed_process_state"
            ]
          }
        }
      ]
    },
    "debug_launch_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "session_id": {
          "$ref": "#/$defs/session_id"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "state": {
          "$ref": "#/$defs/coordinator_state"
        },
        "claim_deadline_utc": {
          "$ref": "#/$defs/rfc3339_utc_ms"
        },
        "launch_mode": {
          "$ref": "#/$defs/launch_mode"
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "file_identities": {
          "type": "array",
          "minItems": 1,
          "maxItems": 4,
          "items": {
            "$ref": "#/$defs/file_identity"
          }
        }
      },
      "required": [
        "session_id",
        "generation",
        "state",
        "claim_deadline_utc",
        "launch_mode",
        "runtime_family",
        "architecture",
        "file_identities"
      ]
    },
    "debug_pause_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "state": {
          "const": "paused"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        },
        "reason": {
          "enum": [
            "manual",
            "process",
            "entry",
            "breakpoint",
            "exception",
            "step",
            "unknown"
          ]
        },
        "request_effect": {
          "const": "state_satisfied"
        },
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "location": {
          "$ref": "#/$defs/location"
        },
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        },
        "step_id": {
          "$ref": "#/$defs/opaque_handle"
        }
      },
      "required": [
        "state",
        "pause_epoch",
        "reason",
        "request_effect"
      ]
    },
    "debug_continue_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "state": {
          "const": "running"
        },
        "pause_epoch": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "state",
        "pause_epoch"
      ]
    },
    "debug_restart_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "state": {
          "$ref": "#/$defs/coordinator_state"
        },
        "generation": {
          "$ref": "#/$defs/non_negative_int"
        },
        "claim_deadline_utc": {
          "$ref": "#/$defs/rfc3339_utc_ms"
        }
      },
      "required": [
        "state",
        "generation",
        "claim_deadline_utc"
      ]
    },
    "debug_terminate_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "state": {
          "const": "idle"
        },
        "exit_code": {
          "$ref": "#/$defs/exit_code"
        },
        "terminal_cursor": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "state",
        "terminal_cursor"
      ]
    },
    "debug_read_events_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "events": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/event_envelope"
          }
        },
        "next_cursor": {
          "$ref": "#/$defs/non_negative_int"
        },
        "earliest_cursor": {
          "$ref": "#/$defs/non_negative_int"
        },
        "events_lost": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "events",
        "next_cursor",
        "earliest_cursor",
        "events_lost"
      ]
    },
    "debug_wait_event_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "events": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/event_envelope"
          }
        },
        "next_cursor": {
          "$ref": "#/$defs/non_negative_int"
        },
        "earliest_cursor": {
          "$ref": "#/$defs/non_negative_int"
        },
        "events_lost": {
          "$ref": "#/$defs/non_negative_int"
        },
        "timed_out": {
          "type": "boolean"
        }
      },
      "required": [
        "events",
        "next_cursor",
        "earliest_cursor",
        "events_lost",
        "timed_out"
      ]
    },
    "debug_set_breakpoint_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "breakpoint": {
          "$ref": "#/$defs/breakpoint"
        }
      },
      "required": [
        "breakpoint"
      ]
    },
    "debug_list_breakpoints_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/breakpoint"
          }
        },
        "next_page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "truncated": {
          "type": "boolean"
        },
        "total_known": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "items",
        "truncated"
      ]
    },
    "debug_set_breakpoint_enabled_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "breakpoint": {
          "$ref": "#/$defs/breakpoint"
        }
      },
      "required": [
        "breakpoint"
      ]
    },
    "debug_remove_breakpoint_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "removed": {
          "const": true
        },
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        }
      },
      "required": [
        "removed",
        "breakpoint_id"
      ]
    },
    "debug_list_threads_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/thread_info"
          }
        },
        "next_page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "truncated": {
          "type": "boolean"
        },
        "total_known": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "items",
        "truncated"
      ]
    },
    "debug_get_stack_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/frame_info"
          }
        },
        "next_page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "truncated": {
          "type": "boolean"
        },
        "total_known": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "items",
        "truncated"
      ]
    },
    "debug_step_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "step_id": {
          "$ref": "#/$defs/opaque_handle"
        },
        "state": {
          "const": "running"
        }
      },
      "required": [
        "step_id",
        "state"
      ]
    },
    "debug_get_locals_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/value_node"
          }
        },
        "next_page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "truncated": {
          "type": "boolean"
        },
        "total_known": {
          "$ref": "#/$defs/non_negative_int"
        },
        "evaluation_mode": {
          "const": "no_func_eval_raw"
        },
        "budgets": {
          "$ref": "#/$defs/value_budgets"
        }
      },
      "required": [
        "items",
        "truncated",
        "evaluation_mode",
        "budgets"
      ]
    },
    "debug_expand_value_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/value_node"
          }
        },
        "next_page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "truncated": {
          "type": "boolean"
        },
        "total_known": {
          "$ref": "#/$defs/non_negative_int"
        },
        "evaluation_mode": {
          "const": "no_func_eval_raw"
        },
        "budgets": {
          "$ref": "#/$defs/value_budgets"
        }
      },
      "required": [
        "items",
        "truncated",
        "evaluation_mode",
        "budgets"
      ]
    },
    "debug_list_modules_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/module_identity"
          }
        },
        "next_page_cursor": {
          "$ref": "#/$defs/page_cursor"
        },
        "truncated": {
          "type": "boolean"
        },
        "total_known": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "items",
        "truncated"
      ]
    },
    "debug_read_memory_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "address": {
          "$ref": "#/$defs/address"
        },
        "length": {
          "$ref": "#/$defs/non_negative_int"
        },
        "encoding": {
          "enum": [
            "base64",
            "hex"
          ]
        },
        "data": {
          "type": "string",
          "minLength": 1
        },
        "read_semantics": {
          "const": "dnspy-zero-fill"
        }
      },
      "required": [
        "module_handle",
        "address",
        "length",
        "encoding",
        "data",
        "read_semantics"
      ]
    },
    "debug_dump_module_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "artifact": {
          "$ref": "#/$defs/artifact"
        }
      },
      "required": [
        "artifact"
      ]
    },
    "debug_set_exception_policy_result": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "previous": {
          "$ref": "#/$defs/exception_policy"
        },
        "current": {
          "$ref": "#/$defs/exception_policy"
        }
      },
      "required": [
        "previous",
        "current"
      ]
    },
    "envelope_success": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "schema_version": {
          "const": "dnspy.debug.v1"
        },
        "ok": {
          "const": true
        },
        "debug_context": {
          "$ref": "#/$defs/debug_context"
        },
        "result": {
          "type": "object"
        },
        "warnings": {
          "type": "array",
          "minItems": 0,
          "maxItems": 32,
          "items": {
            "$ref": "#/$defs/warning"
          }
        },
        "untrusted_sample_data": {
          "type": "boolean"
        }
      },
      "required": [
        "schema_version",
        "ok",
        "debug_context",
        "result",
        "warnings",
        "untrusted_sample_data"
      ],
      "not": {
        "required": [
          "error"
        ]
      }
    },
    "envelope_failure": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "schema_version": {
          "const": "dnspy.debug.v1"
        },
        "ok": {
          "const": false
        },
        "debug_context": {
          "$ref": "#/$defs/debug_context"
        },
        "error": {
          "$ref": "#/$defs/domain_error"
        },
        "warnings": {
          "type": "array",
          "minItems": 0,
          "maxItems": 32,
          "items": {
            "$ref": "#/$defs/warning"
          }
        },
        "untrusted_sample_data": {
          "type": "boolean"
        }
      },
      "required": [
        "schema_version",
        "ok",
        "debug_context",
        "error",
        "warnings",
        "untrusted_sample_data"
      ],
      "not": {
        "required": [
          "result"
        ]
      }
    },
    "event_envelope": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "schema_version": {
          "const": "dnspy.debug.v1"
        },
        "cursor": {
          "type": "integer",
          "minimum": 1,
          "maximum": 9007199254740991
        },
        "timestamp_utc": {
          "$ref": "#/$defs/rfc3339_utc_ms"
        },
        "kind": {
          "$ref": "#/$defs/event_kind"
        },
        "debug_context": {
          "$ref": "#/$defs/debug_context"
        },
        "payload": {
          "type": "object"
        },
        "untrusted_sample_data": {
          "type": "boolean"
        }
      },
      "required": [
        "schema_version",
        "cursor",
        "timestamp_utc",
        "kind",
        "debug_context",
        "payload",
        "untrusted_sample_data"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "kind": {
                "const": "session_start"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_session_start"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "start_failed"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_start_failed"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "process_created"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_process_created"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "process_exited"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_process_exited"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "runtime_created"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_runtime_created"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "module_loaded"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_module_loaded"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "module_unloaded"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_module_unloaded"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "thread_created"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_thread_created"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "thread_exited"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_thread_exited"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "paused"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_paused"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "continued"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_continued"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "breakpoint_bound"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_breakpoint_bound"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "breakpoint_hit"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_breakpoint_hit"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "exception"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_exception"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "step_completed"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_step_completed"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "output"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_output"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "ownership_lost"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_ownership_lost"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "recovery"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_recovery"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "payload_omitted"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_payload_omitted"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "session_end"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_session_end"
              }
            }
          }
        },
        {
          "if": {
            "properties": {
              "kind": {
                "const": "control_failed"
              }
            },
            "required": [
              "kind"
            ]
          },
          "then": {
            "properties": {
              "payload": {
                "$ref": "#/$defs/event_control_failed"
              }
            }
          }
        }
      ]
    },
    "event_session_start": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "operation": {
          "enum": [
            "launch",
            "restart"
          ]
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "architecture": {
          "$ref": "#/$defs/architecture"
        },
        "launch_mode": {
          "$ref": "#/$defs/launch_mode"
        }
      },
      "required": [
        "operation",
        "runtime_family",
        "architecture",
        "launch_mode"
      ]
    },
    "event_start_failed": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "operation": {
          "enum": [
            "launch",
            "restart"
          ]
        },
        "error": {
          "$ref": "#/$defs/domain_error"
        }
      },
      "required": [
        "operation",
        "error"
      ]
    },
    "event_process_created": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "process_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "pid": {
          "$ref": "#/$defs/pid"
        },
        "filename": {
          "type": "string",
          "minLength": 1
        }
      },
      "required": [
        "process_handle",
        "pid",
        "filename"
      ]
    },
    "event_process_exited": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "process_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "exit_code": {
          "$ref": "#/$defs/exit_code"
        }
      },
      "required": [
        "process_handle",
        "exit_code"
      ]
    },
    "event_runtime_created": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "runtime_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "runtime_identity": {
          "type": "string",
          "minLength": 1
        },
        "runtime_family": {
          "$ref": "#/$defs/runtime_family"
        },
        "version": {
          "type": "string",
          "minLength": 1
        }
      },
      "required": [
        "runtime_handle",
        "runtime_identity",
        "runtime_family"
      ]
    },
    "event_module_loaded": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "module": {
          "$ref": "#/$defs/module_identity"
        }
      },
      "required": [
        "module"
      ]
    },
    "event_module_unloaded": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "mvid": {
          "$ref": "#/$defs/mvid"
        }
      },
      "required": [
        "module_handle",
        "mvid"
      ]
    },
    "event_thread_created": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "managed_id": {
          "type": "integer"
        },
        "os_id": {
          "$ref": "#/$defs/non_negative_int"
        }
      },
      "required": [
        "thread_handle"
      ]
    },
    "event_thread_exited": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "exit_code": {
          "$ref": "#/$defs/exit_code"
        }
      },
      "required": [
        "thread_handle"
      ]
    },
    "event_paused": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "reason": {
          "enum": [
            "manual",
            "process",
            "module_cctor_or_entry",
            "entry",
            "breakpoint",
            "exception",
            "step",
            "unknown"
          ]
        },
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "location": {
          "$ref": "#/$defs/location"
        },
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        }
      },
      "required": [
        "reason"
      ]
    },
    "event_continued": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "reason": {
          "enum": [
            "manual",
            "step",
            "restart"
          ]
        }
      },
      "required": [
        "reason"
      ]
    },
    "event_breakpoint_bound": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        },
        "bound": {
          "type": "boolean"
        },
        "module_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "error": {
          "$ref": "#/$defs/domain_error"
        }
      },
      "required": [
        "breakpoint_id",
        "bound"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "bound": {
                "const": true
              }
            },
            "required": [
              "bound"
            ]
          },
          "then": {
            "required": [
              "module_handle"
            ],
            "not": {
              "required": [
                "error"
              ]
            }
          }
        },
        {
          "if": {
            "properties": {
              "bound": {
                "const": false
              }
            },
            "required": [
              "bound"
            ]
          },
          "then": {
            "required": [
              "error"
            ]
          }
        }
      ]
    },
    "event_breakpoint_hit": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "breakpoint_id": {
          "$ref": "#/$defs/opaque_handle"
        },
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "location": {
          "$ref": "#/$defs/location"
        }
      },
      "required": [
        "breakpoint_id",
        "thread_handle",
        "location"
      ]
    },
    "event_exception": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "first_chance": {
          "type": "boolean"
        },
        "unhandled": {
          "type": "boolean"
        },
        "type": {
          "type": "string",
          "minLength": 1
        },
        "message": {
          "type": "string"
        },
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        }
      },
      "required": [
        "first_chance",
        "unhandled",
        "type",
        "message"
      ]
    },
    "event_step_completed": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "step_id": {
          "$ref": "#/$defs/opaque_handle"
        },
        "kind": {
          "enum": [
            "into",
            "over",
            "out"
          ]
        },
        "thread_handle": {
          "$ref": "#/$defs/opaque_handle"
        },
        "location": {
          "$ref": "#/$defs/location"
        }
      },
      "required": [
        "step_id",
        "kind",
        "thread_handle",
        "location"
      ]
    },
    "event_output": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "category": {
          "type": "string",
          "minLength": 1
        },
        "text": {
          "type": "string"
        }
      },
      "required": [
        "category",
        "text"
      ]
    },
    "event_ownership_lost": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "claim_request_id": {
          "$ref": "#/$defs/uuid"
        },
        "observed_processes": {
          "type": "array",
          "minItems": 0,
          "maxItems": 16,
          "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
              "pid": {
                "$ref": "#/$defs/pid"
              },
              "runtime_identity": {
                "type": "string",
                "minLength": 1
              },
              "runtime_family": {
                "$ref": "#/$defs/runtime_family"
              },
              "architecture": {
                "$ref": "#/$defs/architecture"
              }
            },
            "required": [
              "pid",
              "runtime_identity",
              "runtime_family",
              "architecture"
            ]
          }
        },
        "observed_processes_truncated": {
          "type": "boolean"
        },
        "recovery": {
          "const": "manual_resolve_then_wait_idle"
        }
      },
      "required": [
        "claim_request_id",
        "observed_processes",
        "observed_processes_truncated",
        "recovery"
      ]
    },
    "event_recovery": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "reason": {
          "enum": [
            "owned_process_exited",
            "manager_became_idle_without_new_objects"
          ]
        },
        "terminal_state": {
          "const": "idle"
        }
      },
      "required": [
        "reason",
        "terminal_state"
      ]
    },
    "event_payload_omitted": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "original_kind": {
          "$ref": "#/$defs/event_kind"
        },
        "original_utf8_bytes": {
          "$ref": "#/$defs/non_negative_int"
        },
        "sha256": {
          "$ref": "#/$defs/sha256_hex"
        },
        "payload_omitted": {
          "const": true
        }
      },
      "required": [
        "original_kind",
        "original_utf8_bytes",
        "sha256",
        "payload_omitted"
      ]
    },
    "event_session_end": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "reason": {
          "enum": [
            "terminated",
            "target_exited",
            "start_failed",
            "restart_failed",
            "ownership_recovered"
          ]
        },
        "exit_code": {
          "$ref": "#/$defs/exit_code"
        },
        "terminal_state": {
          "const": "idle"
        }
      },
      "required": [
        "reason",
        "terminal_state"
      ],
      "allOf": [
        {
          "if": {
            "properties": {
              "reason": {
                "enum": [
                  "start_failed",
                  "restart_failed",
                  "ownership_recovered"
                ]
              }
            },
            "required": [
              "reason"
            ]
          },
          "then": {
            "not": {
              "required": [
                "exit_code"
              ]
            }
          }
        }
      ]
    },
    "event_control_failed": {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "operation": {
          "enum": [
            "pause",
            "terminate",
            "restart"
          ]
        },
        "request_id": {
          "$ref": "#/$defs/uuid"
        },
        "control_epoch": {
          "type": "integer",
          "minimum": 1,
          "maximum": 9007199254740991
        },
        "phase": {
          "enum": [
            "scheduled",
            "issued"
          ]
        },
        "error": {
          "allOf": [
            {
              "$ref": "#/$defs/domain_error"
            },
            {
              "properties": {
                "code": {
                  "enum": [
                    "TIMEOUT",
                    "INTERNAL_ERROR"
                  ]
                }
              }
            }
          ]
        },
        "late_completion_policy": {
          "enum": [
            "reconcile_owned_pause",
            "finish_owned_termination_only",
            "finish_restart_as_failed"
          ]
        }
      },
      "required": [
        "operation",
        "request_id",
        "control_epoch",
        "phase",
        "error",
        "late_completion_policy"
      ]
    }
  }
}
```

## 附录 C：冻结编辑 JSON Schema（自包含）

顶层每个工具键含 `inputSchema` 与 `outputSchema`，包括全部 39 个 `edit_apply.operation.oneOf` 分支与不通告测试缝；`$defs` 收纳重复结构。所有 `$ref` 都在本 JSON 对象内闭合；递归展开每个工具后与源 `p03-tool-schemas.json` 精确相等，包含 `oneOf`/`anyOf`/`required`/`additionalProperties`/`null`。`edit_compile` 独立于该源文件，接口在正文说明。

```json
{
 "$defs": {
  "Shared001": {
   "additionalProperties": false,
   "properties": {
    "exception_handlers": {
     "$ref": "#/$defs/Shared029"
    },
    "init_locals": {
     "type": "boolean"
    },
    "instructions": {
     "$ref": "#/$defs/Shared012"
    },
    "locals": {
     "$ref": "#/$defs/Shared041"
    },
    "max_stack": {
     "maximum": 65535,
     "minimum": 0,
     "type": "integer"
    },
    "sequence_points": {
     "$ref": "#/$defs/Shared022"
    },
    "scope": {
     "$ref": "#/$defs/Shared002"
    },
    "import_scopes": {
     "$ref": "#/$defs/Shared040"
    }
   },
   "required": [
    "init_locals",
    "max_stack",
    "instructions",
    "locals",
    "exception_handlers"
   ],
   "type": "object"
  },
  "Shared002": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "locals",
    "constants",
    "namespaces",
    "scopes"
   ],
   "properties": {
    "start_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "end_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "locals": {
     "$ref": "#/$defs/Shared081"
    },
    "constants": {
     "$ref": "#/$defs/Shared051"
    },
    "namespaces": {
     "type": "array",
     "maxItems": 128,
     "items": {
      "type": "string",
      "maxLength": 512
     }
    },
    "import_scope": {
     "type": "string",
     "maxLength": 64
    },
    "scopes": {
     "$ref": "#/$defs/Shared003"
    },
    "startIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "endIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "importScope": {
     "type": "string",
     "maxLength": 64
    }
   },
   "allOf": [
    {
     "oneOf": [
      {
       "required": [
        "start_il"
       ]
      },
      {
       "required": [
        "startIl"
       ]
      }
     ]
    },
    {
     "oneOf": [
      {
       "required": [
        "end_il"
       ]
      },
      {
       "required": [
        "endIl"
       ]
      }
     ]
    },
    {
     "not": {
      "required": [
       "import_scope",
       "importScope"
      ]
     }
    }
   ]
  },
  "Shared003": {
   "type": "array",
   "maxItems": 256,
   "items": {
    "$ref": "#/$defs/Shared004"
   }
  },
  "Shared004": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "locals",
    "constants",
    "namespaces",
    "scopes"
   ],
   "properties": {
    "start_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "end_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "locals": {
     "$ref": "#/$defs/Shared081"
    },
    "constants": {
     "$ref": "#/$defs/Shared051"
    },
    "namespaces": {
     "type": "array",
     "maxItems": 128,
     "items": {
      "type": "string",
      "maxLength": 512
     }
    },
    "import_scope": {
     "type": "string",
     "maxLength": 64
    },
    "scopes": {
     "$ref": "#/$defs/Shared005"
    },
    "startIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "endIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "importScope": {
     "type": "string",
     "maxLength": 64
    }
   },
   "allOf": [
    {
     "oneOf": [
      {
       "required": [
        "start_il"
       ]
      },
      {
       "required": [
        "startIl"
       ]
      }
     ]
    },
    {
     "oneOf": [
      {
       "required": [
        "end_il"
       ]
      },
      {
       "required": [
        "endIl"
       ]
      }
     ]
    },
    {
     "not": {
      "required": [
       "import_scope",
       "importScope"
      ]
     }
    }
   ]
  },
  "Shared005": {
   "type": "array",
   "maxItems": 256,
   "items": {
    "$ref": "#/$defs/Shared006"
   }
  },
  "Shared006": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "locals",
    "constants",
    "namespaces",
    "scopes"
   ],
   "properties": {
    "start_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "end_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "locals": {
     "$ref": "#/$defs/Shared081"
    },
    "constants": {
     "$ref": "#/$defs/Shared051"
    },
    "namespaces": {
     "type": "array",
     "maxItems": 128,
     "items": {
      "type": "string",
      "maxLength": 512
     }
    },
    "import_scope": {
     "type": "string",
     "maxLength": 64
    },
    "scopes": {
     "$ref": "#/$defs/Shared019"
    },
    "startIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "endIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "importScope": {
     "type": "string",
     "maxLength": 64
    }
   },
   "allOf": [
    {
     "oneOf": [
      {
       "required": [
        "start_il"
       ]
      },
      {
       "required": [
        "startIl"
       ]
      }
     ]
    },
    {
     "oneOf": [
      {
       "required": [
        "end_il"
       ]
      },
      {
       "required": [
        "endIl"
       ]
      }
     ]
    },
    {
     "not": {
      "required": [
       "import_scope",
       "importScope"
      ]
     }
    }
   ]
  },
  "Shared007": {
   "type": "array",
   "maxItems": 64,
   "items": {
    "$ref": "#/$defs/Shared008"
   }
  },
  "Shared008": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "kind"
   ],
   "properties": {
    "$ref": "#/$defs/Shared009"
   }
  },
  "Shared009": {
   "kind": {
    "$ref": "#/$defs/Shared095"
   },
   "reference": {
    "type": "string",
    "pattern": "^(0x[0-9a-fA-F]{8}|obj-[0-9]{3}-00)$"
   },
   "type": {
    "type": "string",
    "pattern": "^(0x[0-9a-fA-F]{8}|obj-[0-9]{3}-00)$"
   },
   "instruction": {
    "$ref": "#/$defs/Shared105"
   },
   "steps": {
    "$ref": "#/$defs/Shared059"
   },
   "ranges": {
    "$ref": "#/$defs/Shared128"
   },
   "documents": {
    "$ref": "#/$defs/Shared035"
   },
   "text": {
    "type": "string",
    "maxLength": 128
   },
   "texts": {
    "type": "array",
    "maxItems": 1024,
    "items": {
     "type": "string",
     "maxLength": 512
    }
   },
   "flags": {
    "type": "array",
    "maxItems": 1024,
    "items": {
     "type": "boolean"
    }
   },
   "base64": {
    "type": "string",
    "maxLength": 131072
   },
   "states": {
    "$ref": "#/$defs/Shared083"
   }
  },
  "Shared010": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared119"
    },
    {
     "$ref": "#/$defs/Shared121"
    },
    {
     "$ref": "#/$defs/Shared116"
    },
    {
     "$ref": "#/$defs/Shared120"
    },
    {
     "$ref": "#/$defs/Shared108"
    },
    {
     "$ref": "#/$defs/Shared117"
    },
    {
     "$ref": "#/$defs/Shared103"
    },
    {
     "$ref": "#/$defs/Shared110"
    },
    {
     "$ref": "#/$defs/Shared100"
    },
    {
     "$ref": "#/$defs/Shared097"
    },
    {
     "$ref": "#/$defs/Shared129"
    },
    {
     "$ref": "#/$defs/Shared124"
    },
    {
     "$ref": "#/$defs/Shared092"
    },
    {
     "$ref": "#/$defs/Shared122"
    }
   ]
  },
  "Shared011": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared039"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  },
  "Shared012": {
   "items": {
    "$ref": "#/$defs/Shared013"
   },
   "maxItems": 4096,
   "type": "array"
  },
  "Shared013": {
   "additionalProperties": false,
   "properties": {
    "opcode": {
     "maxLength": 64,
     "minLength": 1,
     "type": "string"
    },
    "operand": {
     "$ref": "#/$defs/Shared015"
    }
   },
   "required": [
    "opcode"
   ],
   "type": "object"
  },
  "Shared014": {
   "additionalProperties": false,
   "properties": {
    "apply_cache_bytes": {
     "$ref": "#/$defs/Shared093"
    },
    "apply_cache_entries": {
     "$ref": "#/$defs/Shared093"
    },
    "diff_bytes": {
     "$ref": "#/$defs/Shared093"
    },
    "normalized_operation_bytes": {
     "$ref": "#/$defs/Shared093"
    },
    "object_ids": {
     "$ref": "#/$defs/Shared093"
    },
    "operations": {
     "$ref": "#/$defs/Shared093"
    },
    "review_tombstone_entries": {
     "$ref": "#/$defs/Shared093"
    },
    "review_tombstone_bytes": {
     "$ref": "#/$defs/Shared093"
    }
   },
   "required": [
    "operations",
    "object_ids",
    "normalized_operation_bytes",
    "diff_bytes",
    "apply_cache_entries",
    "apply_cache_bytes",
    "review_tombstone_entries",
    "review_tombstone_bytes"
   ],
   "type": "object"
  },
  "Shared015": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared016"
    },
    {
     "type": "null"
    }
   ]
  },
  "Shared016": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared107"
    },
    {
     "$ref": "#/$defs/Shared101"
    },
    {
     "$ref": "#/$defs/Shared098"
    },
    {
     "$ref": "#/$defs/Shared096"
    },
    {
     "$ref": "#/$defs/Shared122"
    },
    {
     "$ref": "#/$defs/Shared102"
    },
    {
     "$ref": "#/$defs/Shared111"
    },
    {
     "$ref": "#/$defs/Shared104"
    },
    {
     "$ref": "#/$defs/Shared090"
    },
    {
     "$ref": "#/$defs/Shared109"
    },
    {
     "$ref": "#/$defs/Shared106"
    }
   ]
  },
  "Shared017": {
   "oneOf": [
    {
     "type": "null"
    },
    {
     "$ref": "#/$defs/Shared018"
    }
   ]
  },
  "Shared018": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "kind"
   ],
   "properties": {
    "kind": {
     "enum": [
      "simple",
      "raw",
      "fixed_string",
      "fixed_array",
      "array",
      "safe_array",
      "custom",
      "interface"
     ]
    },
    "native": {
     "enum": [
      "NotInitialized",
      "Void",
      "Boolean",
      "I1",
      "U1",
      "I2",
      "U2",
      "I4",
      "U4",
      "I8",
      "U8",
      "R4",
      "R8",
      "Currency",
      "BStr",
      "LPStr",
      "LPWStr",
      "LPTStr",
      "LPUTF8Str",
      "FixedSysString",
      "ObjectRef",
      "Decimal",
      "Struct",
      "IntF",
      "Int",
      "UInt",
      "IntPtr",
      "ByValStr",
      "TBStr",
      "ANSIBStr",
      "IDispatch",
      "IUnknown",
      "StructEnd",
      "SafeArray",
      "FixedArray",
      "NestedStruct",
      "CustomMarshaler",
      "Error",
      "IInspectable",
      "HString",
      "Ptr",
      "Array",
      "Func",
      "AsAny",
      "Variant",
      "SysChar",
      "Void2"
     ]
    },
    "element": {
     "enum": [
      "NotInitialized",
      "Void",
      "Boolean",
      "I1",
      "U1",
      "I2",
      "U2",
      "I4",
      "U4",
      "I8",
      "U8",
      "R4",
      "R8",
      "Currency",
      "BStr",
      "LPStr",
      "LPWStr",
      "LPTStr",
      "LPUTF8Str",
      "FixedSysString",
      "ObjectRef",
      "Decimal",
      "Struct",
      "IntF",
      "Int",
      "UInt",
      "IntPtr",
      "ByValStr",
      "TBStr",
      "ANSIBStr",
      "IDispatch",
      "IUnknown",
      "StructEnd",
      "SafeArray",
      "FixedArray",
      "NestedStruct",
      "CustomMarshaler",
      "Error",
      "IInspectable",
      "HString",
      "Ptr",
      "Array",
      "Func",
      "AsAny",
      "Variant",
      "SysChar",
      "Void2"
     ]
    },
    "size": {
     "type": "integer",
     "minimum": 0,
     "maximum": 2147483647
    },
    "param_number": {
     "type": "integer",
     "minimum": 0,
     "maximum": 2147483647
    },
    "flags": {
     "type": "integer",
     "minimum": 0,
     "maximum": 2147483647
    },
    "iid_param_index": {
     "type": "integer",
     "minimum": 0,
     "maximum": 2147483647
    },
    "variant": {
     "enum": [
      "Empty",
      "Null",
      "I2",
      "I4",
      "R4",
      "R8",
      "CY",
      "Date",
      "BStr",
      "Dispatch",
      "Error",
      "Bool",
      "Variant",
      "Unknown",
      "Decimal",
      "I1",
      "UI1",
      "UI2",
      "UI4",
      "I8",
      "UI8",
      "Int",
      "UInt",
      "Void",
      "HResult",
      "Ptr",
      "SafeArray",
      "CArray",
      "UserDefined",
      "Record",
      "IntPtr",
      "UIntPtr"
     ]
    },
    "user_defined_type": {
     "type": "string",
     "minLength": 1,
     "maxLength": 4096
    },
    "custom_marshaler_type": {
     "type": "string",
     "minLength": 1,
     "maxLength": 4096
    },
    "data_base64": {
     "type": "string",
     "pattern": "^[A-Za-z0-9+/]*={0,2}$",
     "maxLength": 4096
    },
    "guid_base64": {
     "type": "string",
     "pattern": "^[A-Za-z0-9+/]*={0,2}$",
     "maxLength": 512
    },
    "native_name_base64": {
     "type": "string",
     "pattern": "^[A-Za-z0-9+/]*={0,2}$",
     "maxLength": 512
    },
    "cookie_base64": {
     "type": "string",
     "pattern": "^[A-Za-z0-9+/]*={0,2}$",
     "maxLength": 4096
    }
   }
  },
  "Shared019": {
   "type": "array",
   "maxItems": 256,
   "items": {
    "$ref": "#/$defs/Shared020"
   }
  },
  "Shared020": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "locals",
    "constants",
    "namespaces",
    "scopes"
   ],
   "properties": {
    "start_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "end_il": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "locals": {
     "$ref": "#/$defs/Shared081"
    },
    "constants": {
     "$ref": "#/$defs/Shared051"
    },
    "namespaces": {
     "type": "array",
     "maxItems": 128,
     "items": {
      "type": "string",
      "maxLength": 512
     }
    },
    "import_scope": {
     "type": "string",
     "maxLength": 64
    },
    "scopes": {
     "type": "array",
     "maxItems": 0
    },
    "startIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "endIl": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    },
    "importScope": {
     "type": "string",
     "maxLength": 64
    }
   },
   "allOf": [
    {
     "oneOf": [
      {
       "required": [
        "start_il"
       ]
      },
      {
       "required": [
        "startIl"
       ]
      }
     ]
    },
    {
     "oneOf": [
      {
       "required": [
        "end_il"
       ]
      },
      {
       "required": [
        "endIl"
       ]
      }
     ]
    },
    {
     "not": {
      "required": [
       "import_scope",
       "importScope"
      ]
     }
    }
   ]
  },
  "Shared021": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "schema_version",
    "ok",
    "state",
    "error",
    "warnings",
    "untrusted_sample_data"
   ],
   "properties": {
    "schema_version": {
     "const": "dnspy.edit.v1"
    },
    "ok": {
     "const": false
    },
    "state": {
     "$ref": "#/$defs/Shared132"
    },
    "error": {
     "$ref": "#/$defs/Shared032"
    },
    "warnings": {
     "type": "array",
     "maxItems": 16,
     "items": {
      "type": "string",
      "minLength": 1,
      "maxLength": 512
     }
    },
    "untrusted_sample_data": {
     "const": true
    }
   }
  },
  "Shared022": {
   "type": "array",
   "maxItems": 4096,
   "items": {
    "$ref": "#/$defs/Shared023"
   }
  },
  "Shared023": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "document",
    "start",
    "end"
   ],
   "properties": {
    "document": {
     "$ref": "#/$defs/Shared038"
    },
    "start": {
     "$ref": "#/$defs/Shared082"
    },
    "end": {
     "$ref": "#/$defs/Shared082"
    }
   }
  },
  "Shared024": {
   "oneOf": [
    {
     "type": "null"
    },
    {
     "$ref": "#/$defs/Shared025"
    }
   ]
  },
  "Shared025": {
   "type": "array",
   "items": {
    "$ref": "#/$defs/Shared026"
   },
   "maxItems": 64
  },
  "Shared026": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "declaration"
   ],
   "properties": {
    "method": {
     "$ref": "#/$defs/Shared050"
    },
    "declaration": {
     "$ref": "#/$defs/Shared050"
    }
   }
  },
  "Shared027": {
   "items": {
    "$ref": "#/$defs/Shared028"
   },
   "maxItems": 256,
   "type": "array"
  },
  "Shared028": {
   "additionalProperties": false,
   "properties": {
    "after": {
     "oneOf": [
      {
       "maxLength": 32,
       "minLength": 1,
       "type": "string"
      },
      {
       "type": "null"
      }
     ]
    },
    "before": {
     "oneOf": [
      {
       "maxLength": 32,
       "minLength": 1,
       "type": "string"
      },
      {
       "type": "null"
      }
     ]
    },
    "kind": {
     "enum": [
      "type_add",
      "type_update",
      "type_remove",
      "method_add",
      "method_update",
      "method_remove",
      "field_add",
      "field_update",
      "field_remove",
      "property_add",
      "property_update",
      "property_remove",
      "event_add",
      "event_update",
      "event_remove",
      "parameter_add",
      "parameter_update",
      "parameter_remove",
      "generic_parameter_add",
      "generic_parameter_update",
      "generic_parameter_remove",
      "method_body_replace",
      "attribute_add",
      "attribute_remove",
      "security_add",
      "security_remove",
      "assembly_update",
      "module_update",
      "assembly_ref_update",
      "entry_point_set",
      "managed_resource_add",
      "managed_resource_update",
      "managed_resource_remove",
      "win32_resource_add",
      "win32_resource_update",
      "win32_resource_remove",
      "strong_name_remove",
      "interface_add",
      "reference_add"
     ]
    },
    "operation_index": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "path": {
     "maxLength": 48,
     "minLength": 1,
     "type": "string"
    },
    "risk_ids": {
     "items": {
      "maxLength": 48,
      "minLength": 1,
      "type": "string"
     },
     "maxItems": 17,
     "type": "array"
    },
    "target": {
     "maxLength": 32,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "operation_index",
    "kind",
    "target",
    "path",
    "before",
    "after",
    "risk_ids"
   ],
   "type": "object"
  },
  "Shared029": {
   "items": {
    "$ref": "#/$defs/Shared030"
   },
   "maxItems": 512,
   "type": "array"
  },
  "Shared030": {
   "additionalProperties": false,
   "properties": {
    "catch_type": {
     "$ref": "#/$defs/Shared053"
    },
    "filter_start": {
     "oneOf": [
      {
       "maximum": 4294967295,
       "minimum": 0,
       "type": "integer"
      },
      {
       "type": "null"
      }
     ]
    },
    "handler_end": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "handler_start": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "kind": {
     "enum": [
      "catch",
      "finally",
      "fault",
      "filter"
     ]
    },
    "try_end": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "try_start": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "try_start",
    "try_end",
    "handler_start",
    "handler_end",
    "filter_start",
    "catch_type"
   ],
   "type": "object"
  },
  "Shared031": {
   "items": {
    "$ref": "#/$defs/Shared033"
   },
   "maxItems": 240,
   "minItems": 240,
   "type": "array",
   "uniqueItems": true
  },
  "Shared032": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "code",
    "message",
    "current_state",
    "recovery",
    "details"
   ],
   "properties": {
    "code": {
     "$ref": "#/$defs/Shared045"
    },
    "message": {
     "type": "string",
     "minLength": 1,
     "maxLength": 4096
    },
    "current_state": {
     "$ref": "#/$defs/Shared132"
    },
    "recovery": {
     "type": "string",
     "minLength": 1,
     "maxLength": 4096
    },
    "details": {}
   }
  },
  "Shared033": {
   "additionalProperties": false,
   "properties": {
    "boundary": {
     "enum": [
      "before",
      "after"
     ]
    },
    "direction": {
     "enum": [
      "forward",
      "reverse"
     ]
    },
    "fault_id": {
     "$ref": "#/$defs/Shared135"
    },
    "operation_index": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "operation_kind": {
     "enum": [
      "type_add",
      "type_update",
      "type_remove",
      "method_add",
      "method_update",
      "method_remove",
      "field_add",
      "field_update",
      "field_remove",
      "property_add",
      "property_update",
      "property_remove",
      "event_add",
      "event_update",
      "event_remove",
      "parameter_add",
      "parameter_update",
      "parameter_remove",
      "generic_parameter_add",
      "generic_parameter_update",
      "generic_parameter_remove",
      "method_body_replace"
     ]
    },
    "primitive_kind": {
     "maxLength": 16,
     "minLength": 1,
     "type": "string"
    },
    "step": {
     "maxLength": 96,
     "minLength": 1,
     "type": "string"
    },
    "step_index": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "target": {
     "maxLength": 64,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "fault_id",
    "operation_index",
    "step_index",
    "operation_kind",
    "direction",
    "boundary",
    "primitive_kind",
    "target",
    "step"
   ],
   "type": "object"
  },
  "Shared034": {
   "oneOf": [
    {
     "type": "boolean"
    },
    {
     "type": "number"
    },
    {
     "type": "string",
     "maxLength": 4096
    },
    {
     "type": "null"
    },
    {
     "$ref": "#/$defs/Shared063"
    },
    {
     "$ref": "#/$defs/Shared060"
    }
   ]
  },
  "Shared035": {
   "type": "array",
   "maxItems": 1024,
   "items": {
    "$ref": "#/$defs/Shared038"
   }
  },
  "Shared036": {
   "additionalProperties": false,
   "properties": {
    "max_body_exception_handlers": {
     "const": 512
    },
    "max_body_instructions": {
     "const": 4096
    },
    "max_body_locals": {
     "const": 1024
    },
    "max_diff_bytes": {
     "const": 524288
    },
    "max_dispatcher_ms": {
     "const": 1000
    },
    "max_il_instructions": {
     "const": 250000
    },
    "max_live_steps": {
     "const": 256
    },
    "max_metadata_rows": {
     "const": 100000
    },
    "max_module_bytes": {
     "const": 16777216
    },
    "max_normalized_operation_bytes": {
     "const": 8388608
    },
    "max_object_ids": {
     "const": 4096
    },
    "max_operations": {
     "const": 256
    },
    "max_pdb_bytes": {
     "const": 8388608
    },
    "max_resource_bytes": {
     "const": 8388608
    }
   },
   "required": [
    "max_operations",
    "max_object_ids",
    "max_normalized_operation_bytes",
    "max_diff_bytes",
    "max_body_instructions",
    "max_body_locals",
    "max_body_exception_handlers",
    "max_live_steps",
    "max_module_bytes",
    "max_metadata_rows",
    "max_resource_bytes",
    "max_pdb_bytes",
    "max_il_instructions",
    "max_dispatcher_ms"
   ],
   "type": "object"
  },
  "Shared037": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared077"
    },
    {
     "$ref": "#/$defs/Shared061"
    }
   ]
  },
  "Shared038": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "name"
   ],
   "properties": {
    "$ref": "#/$defs/Shared047"
   },
   "allOf": [
    {
     "not": {
      "required": [
       "hash_algorithm",
       "hashAlgorithm"
      ]
     }
    }
   ]
  },
  "Shared039": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "schema_version",
    "ok",
    "state",
    "result",
    "warnings",
    "untrusted_sample_data"
   ],
   "properties": {
    "schema_version": {
     "const": "dnspy.edit.v1"
    },
    "ok": {
     "const": true
    },
    "state": {
     "$ref": "#/$defs/Shared132"
    },
    "result": {
     "$ref": "#/$defs/Shared075"
    },
    "warnings": {
     "type": "array",
     "maxItems": 16,
     "items": {
      "type": "string",
      "minLength": 1,
      "maxLength": 512
     }
    },
    "untrusted_sample_data": {
     "const": true
    }
   }
  },
  "Shared040": {
   "type": "array",
   "minItems": 1,
   "maxItems": 256,
   "items": {
    "$ref": "#/$defs/Shared042"
   }
  },
  "Shared041": {
   "items": {
    "$ref": "#/$defs/Shared043"
   },
   "maxItems": 1024,
   "type": "array"
  },
  "Shared042": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "id",
    "imports"
   ],
   "properties": {
    "id": {
     "type": "string",
     "maxLength": 64
    },
    "parent": {
     "type": "string",
     "maxLength": 64
    },
    "imports": {
     "$ref": "#/$defs/Shared055"
    }
   }
  },
  "Shared043": {
   "additionalProperties": false,
   "properties": {
    "$ref": "#/$defs/Shared049"
   },
   "required": [
    "type",
    "name"
   ],
   "type": "object"
  },
  "Shared044": {
   "items": {
    "$ref": "#/$defs/Shared046"
   },
   "maxItems": 256,
   "type": "array"
  },
  "Shared045": {
   "type": "string",
   "enum": [
    "EDIT_TRANSACTION_BUSY",
    "EDIT_TRANSACTION_NOT_FOUND",
    "EDIT_OWNER_REQUIRED",
    "EDIT_OWNER_MISMATCH",
    "EDIT_REVISION_CONFLICT",
    "EDIT_LIVE_MODULE_CONFLICT",
    "EDIT_REVIEW_STALE",
    "EDIT_VALIDATION_FAILED",
    "EDIT_RISK_CONFIRMATION_REQUIRED",
    "EDIT_CAPABILITY_UNAVAILABLE",
    "EDIT_CAPACITY_EXCEEDED",
    "EDIT_DEBUG_NOT_IDLE",
    "EDIT_LIVE_STATE_UNKNOWN",
    "EDIT_CHECKPOINT_INVALID",
    "EDIT_CHECKPOINT_COMMIT_FAILED",
    "EDIT_CHECKPOINT_CLEANUP_FAILED",
    "EDIT_EXPORT_BLOCKED",
    "EDIT_REPLAY_CONFIRMATION_REQUIRED",
    "EDIT_REPLAY_UNVERIFIED",
    "EDIT_OPERATION_VERSION_UNSUPPORTED",
    "EDIT_HISTORY_CONFLICT",
    "EDIT_BRANCH_SELECTION_REQUIRED",
    "EDIT_LINEAGE_DIVERGED",
    "EDIT_SOURCE_IDENTITY_CONFLICT",
    "EDIT_RECOVERY_NOT_FOUND",
    "EDIT_INTERNAL_ERROR",
    "REQUEST_ID_REUSE"
   ]
  },
  "Shared046": {
   "additionalProperties": false,
   "properties": {
    "confirmation_required": {
     "type": "boolean"
    },
    "description": {
     "maxLength": 96,
     "minLength": 1,
     "type": "string"
    },
    "kind": {
     "enum": [
      "assembly_identity_change",
      "assembly_ref_change",
      "attribute_change",
      "body_change",
      "cross_assembly_inbound",
      "data_section_change",
      "eh_change",
      "entry_point_change",
      "external_code_entry",
      "layout_change",
      "module_identity_change",
      "public_delete",
      "resource_change",
      "security_change",
      "signature_change",
      "strong_name_change",
      "visibility_change"
     ]
    },
    "object": {
     "maxLength": 32,
     "minLength": 1,
     "type": "string"
    },
    "risk_id": {
     "maxLength": 48,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "risk_id",
    "kind",
    "object",
    "description",
    "confirmation_required"
   ],
   "type": "object"
  },
  "Shared047": {
   "name": {
    "type": "string",
    "minLength": 1,
    "maxLength": 1024
   },
   "language": {
    "$ref": "#/$defs/Shared142"
   },
   "vendor": {
    "$ref": "#/$defs/Shared142"
   },
   "hash": {
    "type": "string",
    "maxLength": 96
   },
   "type": {
    "$ref": "#/$defs/Shared142"
   },
   "hash_algorithm": {
    "$ref": "#/$defs/Shared142"
   },
   "hashAlgorithm": {
    "$ref": "#/$defs/Shared142"
   }
  },
  "Shared048": {
   "additionalProperties": false,
   "properties": {
    "last_activity_monotonic_ms": {
     "maximum": 9223372036854775807,
     "minimum": 0,
     "type": "integer"
    },
    "operation_count": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "review_revision": {
     "oneOf": [
      {
       "maximum": 4294967295,
       "minimum": 0,
       "type": "integer"
      },
      {
       "type": "null"
      }
     ]
    },
    "started_at_monotonic_ms": {
     "maximum": 9223372036854775807,
     "minimum": 0,
     "type": "integer"
    },
    "transaction_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    },
    "work_revision": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "transaction_id",
    "work_revision",
    "review_revision",
    "operation_count",
    "started_at_monotonic_ms",
    "last_activity_monotonic_ms"
   ],
   "type": "object"
  },
  "Shared049": {
   "name": {
    "oneOf": [
     {
      "maxLength": 512,
      "minLength": 1,
      "type": "string"
     },
     {
      "type": "null"
     }
    ]
   },
   "type": {
    "$ref": "#/$defs/Shared057"
   }
  },
  "Shared050": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared078"
    },
    {
     "$ref": "#/$defs/Shared118"
    },
    {
     "$ref": "#/$defs/Shared123"
    }
   ]
  },
  "Shared051": {
   "type": "array",
   "maxItems": 256,
   "items": {
    "$ref": "#/$defs/Shared056"
   }
  },
  "Shared052": {
   "items": {
    "$ref": "#/$defs/Shared057"
   },
   "maxItems": 64,
   "type": "array"
  },
  "Shared053": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared057"
    },
    {
     "type": "null"
    }
   ]
  },
  "Shared054": {
   "additionalProperties": false,
   "properties": {
    "errors": {
     "$ref": "#/$defs/Shared069"
    },
    "rule_count": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "state": {
     "enum": [
      "passed",
      "failed"
     ]
    }
   },
   "required": [
    "state",
    "rule_count",
    "errors"
   ],
   "type": "object"
  },
  "Shared055": {
   "type": "array",
   "maxItems": 128,
   "items": {
    "$ref": "#/$defs/Shared058"
   }
  },
  "Shared056": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "name",
    "type",
    "value"
   ],
   "properties": {
    "$ref": "#/$defs/Shared065"
   },
   "allOf": [
    {
     "oneOf": [
      {
       "required": [
        "value_kind"
       ]
      },
      {
       "required": [
        "valueKind"
       ]
      }
     ]
    }
   ]
  },
  "Shared057": {
   "oneOf": [
    {
     "maxLength": 4096,
     "minLength": 1,
     "type": "string",
     "x-dnspy-contract": "p02-typesig-v1"
    },
    {
     "$ref": "#/$defs/Shared063"
    }
   ]
  },
  "Shared058": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "kind"
   ],
   "properties": {
    "$ref": "#/$defs/Shared066"
   },
   "allOf": [
    {
     "not": {
      "required": [
       "assembly_name",
       "assemblyName"
      ]
     }
    }
   ]
  },
  "Shared059": {
   "type": "array",
   "maxItems": 1024,
   "items": {
    "$ref": "#/$defs/Shared062"
   }
  },
  "Shared060": {
   "type": "array",
   "maxItems": 64,
   "items": {
    "$ref": "#/$defs/Shared063"
   }
  },
  "Shared061": {
   "additionalProperties": false,
   "properties": {
    "owner_method": {
     "$ref": "#/$defs/Shared077"
    },
    "parameter_index": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "owner_method",
    "parameter_index"
   ],
   "type": "object"
  },
  "Shared062": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "yield",
    "breakpoint"
   ],
   "properties": {
    "yield": {
     "$ref": "#/$defs/Shared105"
    },
    "breakpoint": {
     "$ref": "#/$defs/Shared105"
    }
   }
  },
  "Shared063": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "kind",
    "type"
   ],
   "properties": {
    "$ref": "#/$defs/Shared068"
   }
  },
  "Shared064": {
   "oneOf": [
    {
     "type": "null"
    },
    {
     "$ref": "#/$defs/Shared067"
    }
   ]
  },
  "Shared065": {
   "name": {
    "type": "string",
    "maxLength": 512
   },
   "type": {
    "type": "string",
    "minLength": 1,
    "maxLength": 2048
   },
   "value_kind": {
    "$ref": "#/$defs/Shared125"
   },
   "value": {},
   "valueKind": {
    "$ref": "#/$defs/Shared125"
   }
  },
  "Shared066": {
   "kind": {
    "$ref": "#/$defs/Shared115"
   },
   "alias": {
    "type": "string",
    "maxLength": 512
   },
   "namespace": {
    "type": "string",
    "maxLength": 512
   },
   "assembly_name": {
    "type": "string",
    "maxLength": 512
   },
   "type": {
    "type": "string",
    "maxLength": 2048
   },
   "assemblyName": {
    "type": "string",
    "maxLength": 512
   }
  },
  "Shared067": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "module_name"
   ],
   "properties": {
    "module_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "entry_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "charset": {
     "enum": [
      "none",
      "ansi",
      "unicode",
      "auto"
     ]
    },
    "no_mangle": {
     "type": "boolean"
    },
    "last_error": {
     "type": "boolean"
    },
    "calling_convention": {
     "enum": [
      "winapi",
      "cdecl",
      "stdcall",
      "thiscall",
      "fastcall"
     ]
    }
   }
  },
  "Shared068": {
   "kind": {
    "const": "type"
   },
   "type": {
    "$ref": "#/$defs/Shared072"
   }
  },
  "Shared069": {
   "items": {
    "$ref": "#/$defs/Shared074"
   },
   "maxItems": 64,
   "type": "array"
  },
  "Shared070": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared126"
    },
    {
     "$ref": "#/$defs/Shared137"
    },
    {
     "$ref": "#/$defs/Shared139"
    }
   ]
  },
  "Shared071": {
   "additionalProperties": false,
   "properties": {
    "baseline_live": {
     "maxLength": 64,
     "minLength": 1,
     "pattern": "^[0-9a-f]{64}$",
     "type": "string"
    },
    "current_live": {
     "maxLength": 64,
     "minLength": 1,
     "pattern": "^[0-9a-f]{64}$",
     "type": "string"
    },
    "private": {
     "maxLength": 64,
     "minLength": 1,
     "pattern": "^[0-9a-f]{64}$",
     "type": "string"
    }
   },
   "required": [
    "baseline_live",
    "current_live",
    "private"
   ],
   "type": "object"
  },
  "Shared072": {
   "type": "object",
   "required": [
    "Kind"
   ],
   "properties": {
    "Kind": {
     "$ref": "#/$defs/Shared088"
    }
   },
   "x-dnspy-contract": "EditStructuredSignatureCodec.TypeNode"
  },
  "Shared073": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared077"
    },
    {
     "type": "null"
    }
   ]
  },
  "Shared074": {
   "additionalProperties": false,
   "properties": {
    "location": {
     "maxLength": 48,
     "minLength": 1,
     "type": "string"
    },
    "message": {
     "maxLength": 192,
     "minLength": 1,
     "type": "string"
    },
    "object": {
     "maxLength": 48,
     "minLength": 1,
     "type": "string"
    },
    "rule_id": {
     "maxLength": 48,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "rule_id",
    "object",
    "location",
    "message"
   ],
   "type": "object"
  },
  "Shared075": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "from_checkpoint_id",
    "to_checkpoint_id",
    "history",
    "replay"
   ],
   "properties": {
    "from_checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    },
    "to_checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    },
    "history": {
     "type": "object"
    },
    "replay": {
     "type": "object"
    }
   }
  },
  "Shared076": {
   "additionalProperties": false,
   "properties": {
    "debug_idle": {
     "const": true
    },
    "residual_path": {
     "type": "null"
    },
    "temp_delete_attempted": {
     "const": false
    },
    "temp_deleted": {
     "const": true
    },
    "terminate_attempted": {
     "const": false
    }
   },
   "required": [
    "terminate_attempted",
    "debug_idle",
    "temp_delete_attempted",
    "temp_deleted",
    "residual_path"
   ],
   "type": "object"
  },
  "Shared077": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared118"
    },
    {
     "$ref": "#/$defs/Shared123"
    }
   ]
  },
  "Shared078": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "owner_type",
    "name"
   ],
   "properties": {
    "owner_type": {
     "type": "string",
     "minLength": 1,
     "maxLength": 4096
    },
    "name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "parameter_types": {
     "type": "array",
     "items": {
      "type": "string",
      "minLength": 1,
      "maxLength": 4096
     },
     "maxItems": 64
    }
   }
  },
  "Shared079": {
   "items": {
    "$ref": "#/$defs/Shared084"
   },
   "maxItems": 0,
   "type": "array"
  },
  "Shared080": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared127"
    },
    {
     "$ref": "#/$defs/Shared123"
    }
   ]
  },
  "Shared081": {
   "type": "array",
   "maxItems": 1024,
   "items": {
    "$ref": "#/$defs/Shared087"
   }
  },
  "Shared082": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "il",
    "line",
    "column"
   ],
   "properties": {
    "il": {
     "type": "integer",
     "minimum": 0,
     "maximum": 4095
    },
    "line": {
     "oneOf": [
      {
       "type": "integer",
       "minimum": 0,
       "maximum": 1048575
      },
      {
       "const": 16707566
      }
     ]
    },
    "column": {
     "type": "integer",
     "minimum": 0,
     "maximum": 1048575
    }
   }
  },
  "Shared083": {
   "type": "array",
   "maxItems": 4096,
   "items": {
    "$ref": "#/$defs/Shared089"
   }
  },
  "Shared084": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "maxLength": 64,
     "minLength": 1,
     "type": "string"
    },
    "sequence": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "summary": {
     "maxLength": 256,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "sequence",
    "kind",
    "summary"
   ],
   "type": "object"
  },
  "Shared085": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared126"
    },
    {
     "$ref": "#/$defs/Shared139"
    }
   ]
  },
  "Shared086": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "attribute_type"
   ],
   "properties": {
    "attribute_type": {
     "type": "string",
     "minLength": 1,
     "maxLength": 4096
    },
    "parameter_types": {
     "type": "array",
     "items": {
      "type": "string",
      "minLength": 1,
      "maxLength": 4096
     },
     "maxItems": 64
    }
   }
  },
  "Shared087": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "index",
    "name",
    "attributes"
   ],
   "properties": {
    "index": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    },
    "name": {
     "type": "string",
     "maxLength": 512
    },
    "attributes": {
     "type": "integer",
     "minimum": 0,
     "maximum": 63
    }
   }
  },
  "Shared088": {
   "type": "string",
   "enum": [
    "CorLibTypeSig",
    "ClassSig",
    "ValueTypeSig",
    "GenericVar",
    "GenericMVar",
    "FnPtrSig",
    "GenericInstSig",
    "CModReqdSig",
    "CModOptSig",
    "ArraySig",
    "ValueArraySig",
    "ModuleSig",
    "PtrSig",
    "ByRefSig",
    "SZArraySig",
    "PinnedSig",
    "SentinelSig"
   ]
  },
  "Shared089": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "syntax_offset",
    "state"
   ],
   "properties": {
    "syntax_offset": {
     "type": "integer",
     "minimum": -2147483648,
     "maximum": 2147483647
    },
    "state": {
     "type": "integer",
     "minimum": -2147483648,
     "maximum": 2147483647
    }
   }
  },
  "Shared090": {
   "additionalProperties": false,
   "properties": {
    "instruction_indices": {
     "$ref": "#/$defs/Shared144"
    },
    "kind": {
     "const": "switch"
    }
   },
   "required": [
    "kind",
    "instruction_indices"
   ],
   "type": "object"
  },
  "Shared091": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "kind"
   ],
   "properties": {
    "kind": {
     "enum": [
      "auto",
      "sequential",
      "explicit"
     ]
    },
    "pack": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    },
    "size": {
     "type": "integer",
     "minimum": 0,
     "maximum": 4294967295
    }
   }
  },
  "Shared092": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "char"
    },
    "value": {
     "$ref": "#/$defs/Shared136"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared093": {
   "additionalProperties": false,
   "properties": {
    "current": {
     "maximum": 9223372036854775807,
     "minimum": 0,
     "type": "integer"
    },
    "maximum": {
     "maximum": 9223372036854775807,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "current",
    "maximum"
   ],
   "type": "object"
  },
  "Shared094": {
   "additionalProperties": false,
   "properties": {
    "path": {
     "maxLength": 4096,
     "minLength": 1,
     "type": "string"
    },
    "sha256": {
     "maxLength": 64,
     "minLength": 1,
     "pattern": "^[0-9a-f]{64}$",
     "type": "string"
    }
   },
   "required": [
    "path",
    "sha256"
   ],
   "type": "object"
  },
  "Shared095": {
   "type": "string",
   "enum": [
    "hoisted",
    "async",
    "iterator",
    "state_machine_type_name",
    "type_documents",
    "default_namespace",
    "tuple",
    "dynamic",
    "embedded_source",
    "source_link",
    "enc_local",
    "enc_lambda",
    "unknown",
    "enc_state_map"
   ]
  },
  "Shared096": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "f64"
    },
    "value": {
     "maximum": 1.7976931348623157e+308,
     "minimum": -1.7976931348623157e+308,
     "type": "number"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared097": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "r8"
    },
    "value": {
     "maximum": 1.7976931348623157e+308,
     "minimum": -1.7976931348623157e+308,
     "type": "number"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared098": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "f32"
    },
    "value": {
     "maximum": 3.4028234663852886e+38,
     "minimum": -3.4028234663852886e+38,
     "type": "number"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared099": {
   "oneOf": [
    {
     "type": "null"
    },
    {
     "$ref": "#/$defs/Shared112"
    }
   ]
  },
  "Shared100": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "r4"
    },
    "value": {
     "maximum": 3.4028234663852886e+38,
     "minimum": -3.4028234663852886e+38,
     "type": "number"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared101": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "i64"
    },
    "value": {
     "maximum": 9223372036854775807,
     "minimum": -9223372036854775808,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared102": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "token"
    },
    "token": {
     "maxLength": 10,
     "minLength": 1,
     "pattern": "^0x[0-9a-fA-F]{8}$",
     "type": "string"
    }
   },
   "required": [
    "kind",
    "token"
   ],
   "type": "object"
  },
  "Shared103": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "i8"
    },
    "value": {
     "maximum": 9223372036854775807,
     "minimum": -9223372036854775808,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared104": {
   "additionalProperties": false,
   "properties": {
    "instruction_index": {
     "maximum": 4095,
     "minimum": 0,
     "type": "integer"
    },
    "kind": {
     "const": "label"
    }
   },
   "required": [
    "kind",
    "instruction_index"
   ],
   "type": "object"
  },
  "Shared105": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "method",
    "index"
   ],
   "properties": {
    "method": {
     "type": "string",
     "maxLength": 64
    },
    "index": {
     "type": "integer",
     "minimum": -1,
     "maximum": 4095
    }
   }
  },
  "Shared106": {
   "additionalProperties": false,
   "properties": {
    "argument_index": {
     "maximum": 65535,
     "minimum": 0,
     "type": "integer"
    },
    "kind": {
     "const": "arg"
    }
   },
   "required": [
    "kind",
    "argument_index"
   ],
   "type": "object"
  },
  "Shared107": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "i32"
    },
    "value": {
     "maximum": 2147483647,
     "minimum": -2147483648,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared108": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "i4"
    },
    "value": {
     "maximum": 2147483647,
     "minimum": -2147483648,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared109": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "local"
    },
    "local_index": {
     "maximum": 65535,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "local_index"
   ],
   "type": "object"
  },
  "Shared110": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "u8"
    },
    "value": {
     "maximum": 18446744073709551615,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared111": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "object"
    },
    "object_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "kind",
    "object_id"
   ],
   "type": "object"
  },
  "Shared112": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "bytes_base64"
   ],
   "properties": {
    "bytes_base64": {
     "type": "string",
     "pattern": "^[A-Za-z0-9+/]*={0,2}$",
     "maxLength": 1398102
    }
   }
  },
  "Shared113": {
   "type": "string",
   "enum": [
    "embedded",
    "linked",
    "win32"
   ],
   "description": "linked path imports are normalized to embedded bytes; win32 uses the native type/name/language identity."
  },
  "Shared114": {
   "additionalProperties": false,
   "properties": {
    "created": {
     "const": false
    },
    "path": {
     "type": "null"
    },
    "sha256": {
     "type": "null"
    }
   },
   "required": [
    "created",
    "path",
    "sha256"
   ],
   "type": "object"
  },
  "Shared115": {
   "type": "string",
   "enum": [
    "namespace",
    "assembly_namespace",
    "type",
    "xml",
    "assembly_reference_alias",
    "alias_assembly",
    "alias_namespace",
    "alias_assembly_namespace",
    "alias_type"
   ]
  },
  "Shared116": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "i2"
    },
    "value": {
     "maximum": 32767,
     "minimum": -32768,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared117": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "u4"
    },
    "value": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared118": {
   "additionalProperties": false,
   "properties": {
    "token": {
     "maxLength": 10,
     "minLength": 1,
     "pattern": "^0x[0-9a-fA-F]{8}$",
     "type": "string"
    }
   },
   "required": [
    "token"
   ],
   "type": "object"
  },
  "Shared119": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "i1"
    },
    "value": {
     "maximum": 127,
     "minimum": -128,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared120": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "u2"
    },
    "value": {
     "maximum": 65535,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared121": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "u1"
    },
    "value": {
     "maximum": 255,
     "minimum": 0,
     "type": "integer"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared122": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "string"
    },
    "value": {
     "maxLength": 65535,
     "type": "string"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared123": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "object_id"
   ],
   "properties": {
    "object_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    }
   }
  },
  "Shared124": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "boolean"
    },
    "value": {
     "type": "boolean"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared125": {
   "type": "string",
   "enum": [
    "null",
    "Boolean",
    "String",
    "Char",
    "SByte",
    "Byte",
    "Int16",
    "UInt16",
    "Int32",
    "UInt32",
    "Int64",
    "UInt64",
    "Single",
    "Double"
   ]
  },
  "Shared126": {
   "additionalProperties": false,
   "properties": {
    "token": {
     "pattern": "^0x[0-9a-fA-F]{1,8}$",
     "type": "string"
    }
   },
   "required": [
    "token"
   ],
   "type": "object"
  },
  "Shared127": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "token"
   ],
   "properties": {
    "token": {
     "type": "string",
     "pattern": "^0x[0-9a-fA-F]{8}$"
    }
   }
  },
  "Shared128": {
   "type": "array",
   "maxItems": 4096,
   "items": {
    "$ref": "#/$defs/Shared143"
   }
  },
  "Shared129": {
   "additionalProperties": false,
   "properties": {
    "kind": {
     "const": "null"
    },
    "value": {
     "type": "null"
    }
   },
   "required": [
    "kind",
    "value"
   ],
   "type": "object"
  },
  "Shared130": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "name"
   ],
   "properties": {
    "name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    }
   }
  },
  "Shared131": {
   "type": "string",
   "enum": [
    "prewrite",
    "readback",
    "finalize",
    "cleanup",
    "navigate_forward",
    "navigate_inverse",
    "live_apply",
    "export_reload"
   ]
  },
  "Shared132": {
   "type": "string",
   "enum": [
    "idle",
    "editing",
    "reviewed",
    "applying",
    "committing",
    "committed_without_checkpoint",
    "live_state_unknown"
   ]
  },
  "Shared133": {
   "default": 0,
   "maximum": 63,
   "minimum": 0,
   "type": "integer",
   "x-dnspy-defined-bit-mask": 63,
   "x-dnspy-enum": "GenericParamAttributes"
  },
  "Shared134": {
   "default": 0,
   "maximum": 12319,
   "minimum": 0,
   "type": "integer",
   "x-dnspy-defined-bit-mask": 12319,
   "x-dnspy-enum": "ParamAttributes"
  },
  "Shared135": {
   "maxLength": 64,
   "minLength": 1,
   "pattern": "^fp-[0-9]+-(forward|reverse)-[0-9]+-[a-z0-9_]+-(before|after)$",
   "type": "string"
  },
  "Shared136": {
   "maxLength": 2,
   "minLength": 1,
   "pattern": "^(?:[^\\uD800-\\uDFFF]|[\\uD800-\\uDBFF][\\uDC00-\\uDFFF])$",
   "type": "string"
  },
  "Shared137": {
   "additionalProperties": false,
   "properties": {
    "object_id": {
     "type": "string"
    }
   },
   "required": [
    "object_id"
   ],
   "type": "object"
  },
  "Shared138": {
   "oneOf": [
    {
     "type": "null"
    },
    {
     "type": "array",
     "items": {
      "type": "string",
      "minLength": 1,
      "maxLength": 4096
     },
     "maxItems": 64
    }
   ]
  },
  "Shared139": {
   "additionalProperties": false,
   "properties": {
    "scope": {
     "const": "assembly"
    }
   },
   "required": [
    "scope"
   ],
   "type": "object"
  },
  "Shared140": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "action"
   ],
   "properties": {
    "action": {
     "const": "reset"
    }
   }
  },
  "Shared141": {
   "additionalProperties": false,
   "properties": {
    "action": {
     "const": "read"
    }
   },
   "required": [
    "action"
   ],
   "type": "object"
  },
  "Shared142": {
   "type": "string",
   "pattern": "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
  },
  "Shared143": {
   "type": "array",
   "minItems": 2,
   "maxItems": 2,
   "items": {
    "type": "integer",
    "minimum": -1,
    "maximum": 4095
   }
  },
  "Shared144": {
   "items": {
    "maximum": 4294967295,
    "minimum": 0,
    "type": "integer"
   },
   "maxItems": 4096,
   "type": "array"
  }
 },
 "edit_apply": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {
    "expected_revision": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "operation": {
     "oneOf": [
      {
       "additionalProperties": false,
       "properties": {
        "attributes": {
         "default": 0,
         "maximum": 16219583,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 16219583,
         "x-dnspy-enum": "TypeAttributes"
        },
        "base_type": {
         "$ref": "#/$defs/Shared053"
        },
        "kind": {
         "const": "type_add"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "namespace": {
         "maxLength": 512,
         "type": "string"
        },
        "owner_type": {
         "$ref": "#/$defs/Shared077"
        },
        "layout": {
         "$ref": "#/$defs/Shared091"
        }
       },
       "required": [
        "kind",
        "name"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "minProperties": 3,
       "properties": {
        "attributes": {
         "maximum": 16219583,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 16219583,
         "x-dnspy-enum": "TypeAttributes"
        },
        "base_type": {
         "$ref": "#/$defs/Shared053"
        },
        "kind": {
         "const": "type_update"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "namespace": {
         "maxLength": 512,
         "type": "string"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        },
        "layout": {
         "$ref": "#/$defs/Shared091"
        }
       },
       "required": [
        "kind",
        "target"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "kind": {
         "const": "type_remove"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target",
        "remove_mode"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "attributes": {
         "default": 128,
         "maximum": 65535,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 65535,
         "x-dnspy-enum": "MethodAttributes"
        },
        "body": {
         "$ref": "#/$defs/Shared001"
        },
        "impl_attributes": {
         "default": 0,
         "maximum": 6143,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 6143,
         "x-dnspy-enum": "MethodImplAttributes"
        },
        "kind": {
         "const": "method_add"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "owner_type": {
         "$ref": "#/$defs/Shared077"
        },
        "signature": {
         "additionalProperties": false,
         "properties": {
          "generic_parameters": {
           "items": {
            "additionalProperties": false,
            "properties": {
             "attributes": {
              "$ref": "#/$defs/Shared133"
             },
             "name": {
              "maxLength": 512,
              "minLength": 1,
              "type": "string"
             },
             "constraints": {
              "oneOf": [
               {
                "type": "null"
               },
               {
                "type": "array",
                "maxItems": 64,
                "items": {
                 "oneOf": [
                  {
                   "type": "string",
                   "minLength": 1,
                   "maxLength": 4096
                  },
                  {
                   "$ref": "#/$defs/Shared063"
                  }
                 ]
                }
               }
              ]
             }
            },
            "required": [
             "name"
            ],
            "type": "object"
           },
           "maxItems": 64,
           "type": "array"
          },
          "has_this": {
           "type": "boolean"
          },
          "parameters": {
           "items": {
            "additionalProperties": false,
            "properties": {
             "attributes": {
              "$ref": "#/$defs/Shared134"
             },
             "name": {
              "oneOf": [
               {
                "maxLength": 512,
                "minLength": 1,
                "type": "string"
               },
               {
                "type": "null"
               }
              ]
             },
             "type": {
              "$ref": "#/$defs/Shared057"
             }
            },
            "required": [
             "type"
            ],
            "type": "object"
           },
           "maxItems": 256,
           "type": "array"
          },
          "return_type": {
           "$ref": "#/$defs/Shared057"
          }
         },
         "required": [
          "return_type",
          "parameters",
          "has_this",
          "generic_parameters"
         ],
         "type": "object"
        },
        "overrides": {
         "$ref": "#/$defs/Shared024"
        },
        "pinvoke": {
         "$ref": "#/$defs/Shared064"
        },
        "custom_debug_infos": {
         "$ref": "#/$defs/Shared007"
        }
       },
       "required": [
        "kind",
        "owner_type",
        "name",
        "signature"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "minProperties": 3,
       "properties": {
        "attributes": {
         "maximum": 65535,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 65535,
         "x-dnspy-enum": "MethodAttributes"
        },
        "has_this": {
         "type": "boolean"
        },
        "impl_attributes": {
         "maximum": 6143,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 6143,
         "x-dnspy-enum": "MethodImplAttributes"
        },
        "kind": {
         "const": "method_update"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "return_type": {
         "$ref": "#/$defs/Shared057"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        },
        "overrides": {
         "$ref": "#/$defs/Shared024"
        },
        "pinvoke": {
         "$ref": "#/$defs/Shared064"
        }
       },
       "required": [
        "kind",
        "target"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "kind": {
         "const": "method_remove"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target",
        "remove_mode"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "attributes": {
         "default": 0,
         "maximum": 47095,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 47095,
         "x-dnspy-enum": "FieldAttributes"
        },
        "constant": {
         "$ref": "#/$defs/Shared010"
        },
        "field_type": {
         "$ref": "#/$defs/Shared057"
        },
        "kind": {
         "const": "field_add"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "owner_type": {
         "$ref": "#/$defs/Shared077"
        },
        "field_offset": {
         "oneOf": [
          {
           "type": "null"
          },
          {
           "type": "integer",
           "minimum": 0,
           "maximum": 4294967295
          }
         ]
        },
        "initial_data": {
         "$ref": "#/$defs/Shared099"
        },
        "marshal": {
         "$ref": "#/$defs/Shared017"
        }
       },
       "required": [
        "kind",
        "owner_type",
        "name",
        "field_type"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "allOf": [
        {
         "not": {
          "required": [
           "constant",
           "clear_constant"
          ]
         }
        }
       ],
       "minProperties": 3,
       "properties": {
        "attributes": {
         "maximum": 47095,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 47095,
         "x-dnspy-enum": "FieldAttributes"
        },
        "clear_constant": {
         "const": true
        },
        "constant": {
         "$ref": "#/$defs/Shared010"
        },
        "field_type": {
         "$ref": "#/$defs/Shared057"
        },
        "kind": {
         "const": "field_update"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        },
        "field_offset": {
         "oneOf": [
          {
           "type": "null"
          },
          {
           "type": "integer",
           "minimum": 0,
           "maximum": 4294967295
          }
         ]
        },
        "initial_data": {
         "$ref": "#/$defs/Shared099"
        },
        "marshal": {
         "$ref": "#/$defs/Shared017"
        }
       },
       "required": [
        "kind",
        "target"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "kind": {
         "const": "field_remove"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target",
        "remove_mode"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "attributes": {
         "default": 0,
         "maximum": 5632,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 5632,
         "x-dnspy-enum": "PropertyAttributes"
        },
        "getter": {
         "$ref": "#/$defs/Shared077"
        },
        "index_parameter_types": {
         "$ref": "#/$defs/Shared052"
        },
        "kind": {
         "const": "property_add"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "owner_type": {
         "$ref": "#/$defs/Shared077"
        },
        "property_type": {
         "$ref": "#/$defs/Shared057"
        },
        "setter": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "owner_type",
        "name",
        "property_type"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "minProperties": 3,
       "properties": {
        "attributes": {
         "maximum": 5632,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 5632,
         "x-dnspy-enum": "PropertyAttributes"
        },
        "getter": {
         "$ref": "#/$defs/Shared073"
        },
        "index_parameter_types": {
         "$ref": "#/$defs/Shared052"
        },
        "kind": {
         "const": "property_update"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "property_type": {
         "$ref": "#/$defs/Shared057"
        },
        "setter": {
         "$ref": "#/$defs/Shared073"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "kind": {
         "const": "property_remove"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target",
        "remove_mode"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "add_method": {
         "$ref": "#/$defs/Shared077"
        },
        "attributes": {
         "default": 0,
         "maximum": 1536,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 1536,
         "x-dnspy-enum": "EventAttributes"
        },
        "event_type": {
         "$ref": "#/$defs/Shared057"
        },
        "kind": {
         "const": "event_add"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "owner_type": {
         "$ref": "#/$defs/Shared077"
        },
        "raise_method": {
         "$ref": "#/$defs/Shared077"
        },
        "remove_method": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "owner_type",
        "name",
        "event_type",
        "add_method",
        "remove_method"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "minProperties": 3,
       "properties": {
        "add_method": {
         "$ref": "#/$defs/Shared077"
        },
        "attributes": {
         "maximum": 1536,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 1536,
         "x-dnspy-enum": "EventAttributes"
        },
        "event_type": {
         "$ref": "#/$defs/Shared057"
        },
        "kind": {
         "const": "event_update"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "raise_method": {
         "$ref": "#/$defs/Shared073"
        },
        "remove_method": {
         "$ref": "#/$defs/Shared077"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "kind": {
         "const": "event_remove"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target",
        "remove_mode"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "attributes": {
         "$ref": "#/$defs/Shared134"
        },
        "kind": {
         "const": "parameter_add"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "owner_method": {
         "$ref": "#/$defs/Shared077"
        },
        "parameter_index": {
         "maximum": 4294967295,
         "minimum": 0,
         "type": "integer"
        },
        "parameter_type": {
         "$ref": "#/$defs/Shared057"
        },
        "marshal": {
         "$ref": "#/$defs/Shared017"
        }
       },
       "required": [
        "kind",
        "owner_method",
        "parameter_index",
        "name",
        "parameter_type"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "minProperties": 3,
       "properties": {
        "attributes": {
         "maximum": 12319,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 12319,
         "x-dnspy-enum": "ParamAttributes"
        },
        "kind": {
         "const": "parameter_update"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "parameter_target": {
         "$ref": "#/$defs/Shared037"
        },
        "parameter_type": {
         "$ref": "#/$defs/Shared057"
        },
        "marshal": {
         "$ref": "#/$defs/Shared017"
        }
       },
       "required": [
        "kind",
        "parameter_target"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "kind": {
         "const": "parameter_remove"
        },
        "parameter_target": {
         "$ref": "#/$defs/Shared037"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        }
       },
       "required": [
        "kind",
        "parameter_target",
        "remove_mode"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "attributes": {
         "$ref": "#/$defs/Shared133"
        },
        "generic_index": {
         "maximum": 4294967295,
         "minimum": 0,
         "type": "integer"
        },
        "kind": {
         "const": "generic_parameter_add"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "owner": {
         "$ref": "#/$defs/Shared077"
        },
        "constraints": {
         "$ref": "#/$defs/Shared138"
        }
       },
       "required": [
        "kind",
        "owner",
        "generic_index",
        "name"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "minProperties": 3,
       "properties": {
        "attributes": {
         "maximum": 63,
         "minimum": 0,
         "type": "integer",
         "x-dnspy-defined-bit-mask": 63,
         "x-dnspy-enum": "GenericParamAttributes"
        },
        "kind": {
         "const": "generic_parameter_update"
        },
        "name": {
         "maxLength": 512,
         "minLength": 1,
         "type": "string"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        },
        "constraints": {
         "$ref": "#/$defs/Shared138"
        }
       },
       "required": [
        "kind",
        "target"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "kind": {
         "const": "generic_parameter_remove"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        }
       },
       "required": [
        "kind",
        "target",
        "remove_mode"
       ],
       "type": "object"
      },
      {
       "additionalProperties": false,
       "properties": {
        "body": {
         "$ref": "#/$defs/Shared001"
        },
        "kind": {
         "const": "method_body_replace"
        },
        "target": {
         "$ref": "#/$defs/Shared077"
        },
        "custom_debug_infos": {
         "$ref": "#/$defs/Shared007"
        }
       },
       "required": [
        "kind",
        "target",
        "body"
       ],
       "type": "object"
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "target",
        "constructor"
       ],
       "properties": {
        "kind": {
         "const": "attribute_add"
        },
        "target": {
         "$ref": "#/$defs/Shared070"
        },
        "constructor": {
         "$ref": "#/$defs/Shared086"
        },
        "fixed_arguments": {
         "type": "array",
         "items": {
          "$ref": "#/$defs/Shared034"
         },
         "maxItems": 64
        },
        "named_arguments": {
         "type": "array",
         "items": {
          "type": "object",
          "additionalProperties": false,
          "required": [
           "kind",
           "type",
           "name",
           "value"
          ],
          "properties": {
           "kind": {
            "enum": [
             "field",
             "property"
            ]
           },
           "type": {
            "type": "string",
            "minLength": 1,
            "maxLength": 4096
           },
           "name": {
            "type": "string",
            "minLength": 1,
            "maxLength": 512
           },
           "value": {
            "$ref": "#/$defs/Shared034"
           }
          }
         },
         "maxItems": 64
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "target",
        "match"
       ],
       "properties": {
        "kind": {
         "const": "attribute_remove"
        },
        "target": {
         "$ref": "#/$defs/Shared070"
        },
        "match": {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "constructor"
         ],
         "properties": {
          "constructor": {
           "$ref": "#/$defs/Shared086"
          },
          "index": {
           "type": "integer",
           "minimum": 0,
           "maximum": 4294967295
          }
         }
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "parent",
        "action",
        "xml"
       ],
       "properties": {
        "kind": {
         "const": "security_add"
        },
        "parent": {
         "$ref": "#/$defs/Shared085"
        },
        "action": {
         "enum": [
          "deny",
          "permit_only",
          "request_minimum",
          "request_optional",
          "request_refuse",
          "assert",
          "link_demand",
          "inherit_demand",
          "demand"
         ]
        },
        "xml": {
         "type": "string",
         "minLength": 1,
         "maxLength": 65536
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "parent",
        "action"
       ],
       "properties": {
        "kind": {
         "const": "security_remove"
        },
        "parent": {
         "$ref": "#/$defs/Shared085"
        },
        "action": {
         "enum": [
          "deny",
          "permit_only",
          "request_minimum",
          "request_optional",
          "request_refuse",
          "assert",
          "link_demand",
          "inherit_demand",
          "demand"
         ]
        },
        "index": {
         "type": "integer",
         "minimum": 0,
         "maximum": 4294967295
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind"
       ],
       "properties": {
        "kind": {
         "const": "assembly_update"
        },
        "name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 512
        },
        "version": {
         "type": "string",
         "pattern": "^\\d{1,9}(\\.\\d{1,9}){0,3}$"
        },
        "culture": {
         "type": "string",
         "maxLength": 64
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "name"
       ],
       "properties": {
        "kind": {
         "const": "module_update"
        },
        "name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 512
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "target"
       ],
       "properties": {
        "kind": {
         "const": "assembly_ref_update"
        },
        "target": {
         "$ref": "#/$defs/Shared080"
        },
        "name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 512
        },
        "version": {
         "type": "string",
         "pattern": "^\\d{1,9}(\\.\\d{1,9}){0,3}$"
        },
        "culture": {
         "type": "string",
         "maxLength": 64
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind"
       ],
       "properties": {
        "kind": {
         "const": "entry_point_set"
        },
        "entry_point": {
         "$ref": "#/$defs/Shared080"
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "name",
        "data_base64"
       ],
       "properties": {
        "kind": {
         "const": "managed_resource_add"
        },
        "name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 512
        },
        "attributes": {
         "type": "integer",
         "minimum": 0,
         "maximum": 3
        },
        "data_base64": {
         "type": "string",
         "minLength": 1,
         "maxLength": 12582912
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "target"
       ],
       "properties": {
        "kind": {
         "const": "managed_resource_update"
        },
        "target": {
         "$ref": "#/$defs/Shared130"
        },
        "entry": {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "name",
          "value_kind",
          "value"
         ],
         "properties": {
          "name": {
           "type": "string",
           "minLength": 1,
           "maxLength": 512
          },
          "value_kind": {
           "type": "string",
           "enum": [
            "string",
            "boolean",
            "i1",
            "u1",
            "i2",
            "u2",
            "i4",
            "u4",
            "i8",
            "u8",
            "r4",
            "r8",
            "bytes"
           ]
          },
          "value": {}
         }
        },
        "data_base64": {
         "type": "string",
         "minLength": 1,
         "maxLength": 12582912
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "target",
        "remove_mode"
       ],
       "properties": {
        "kind": {
         "const": "managed_resource_remove"
        },
        "target": {
         "$ref": "#/$defs/Shared130"
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "data_base64"
       ],
       "properties": {
        "kind": {
         "const": "win32_resource_add"
        },
        "type_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "type_name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 256
        },
        "name_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "name_string": {
         "type": "string",
         "minLength": 1,
         "maxLength": 256
        },
        "lang_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "data_base64": {
         "type": "string",
         "minLength": 1,
         "maxLength": 12582912
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "data_base64"
       ],
       "properties": {
        "kind": {
         "const": "win32_resource_update"
        },
        "type_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "type_name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 256
        },
        "name_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "name_string": {
         "type": "string",
         "minLength": 1,
         "maxLength": 256
        },
        "lang_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "data_base64": {
         "type": "string",
         "minLength": 1,
         "maxLength": 12582912
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "remove_mode"
       ],
       "properties": {
        "kind": {
         "const": "win32_resource_remove"
        },
        "type_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "type_name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 256
        },
        "name_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "name_string": {
         "type": "string",
         "minLength": 1,
         "maxLength": 256
        },
        "lang_id": {
         "type": "integer",
         "minimum": 0,
         "maximum": 65535
        },
        "remove_mode": {
         "const": "reject_if_referenced"
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "dynamic_failure"
       ],
       "properties": {
        "kind": {
         "const": "strong_name_remove"
        },
        "dynamic_failure": {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "session_id",
          "event_cursor",
          "event_kind"
         ],
         "properties": {
          "session_id": {
           "type": "string",
           "minLength": 1,
           "maxLength": 128
          },
          "event_cursor": {
           "type": "integer",
           "minimum": 1
          },
          "event_kind": {
           "type": "string",
           "enum": [
            "start_failed",
            "process_exited",
            "exception",
            "module_load_failed"
           ]
          }
         }
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "owner_type",
        "interface"
       ],
       "properties": {
        "kind": {
         "const": "interface_add"
        },
        "owner_type": {
         "$ref": "#/$defs/Shared077"
        },
        "interface": {
         "type": "object",
         "additionalProperties": false,
         "properties": {
          "reference": {
           "$ref": "#/$defs/Shared077"
          },
          "type": {
           "$ref": "#/$defs/Shared072"
          }
         },
         "oneOf": [
          {
           "required": [
            "reference"
           ],
           "not": {
            "required": [
             "type"
            ]
           }
          },
          {
           "required": [
            "type"
           ],
           "not": {
            "required": [
             "reference"
            ]
           }
          }
         ]
        }
       }
      },
      {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "kind",
        "reference"
       ],
       "properties": {
        "kind": {
         "const": "reference_add"
        },
        "reference": {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "form"
         ],
         "properties": {
          "form": {
           "type": "string",
           "enum": [
            "assembly_ref",
            "type_ref",
            "type_spec",
            "member_ref",
            "method_spec"
           ]
          },
          "name": {
           "type": "string",
           "minLength": 1,
           "maxLength": 512
          },
          "version": {
           "type": "string",
           "minLength": 1,
           "maxLength": 64
          },
          "culture": {
           "type": "string",
           "maxLength": 128
          },
          "public_key_or_token": {
           "type": "object",
           "additionalProperties": false,
           "required": [
            "kind"
           ],
           "properties": {
            "kind": {
             "type": "string",
             "enum": [
              "none",
              "token",
              "public_key"
             ]
            },
            "base64": {
             "type": "string"
            }
           }
          },
          "flags": {
           "type": "integer",
           "minimum": 0
          },
          "scope": {
           "$ref": "#/$defs/Shared077"
          },
          "namespace": {
           "type": "string",
           "maxLength": 1024
          },
          "signature": {
           "type": "object"
          },
          "member_kind": {
           "type": "string",
           "enum": [
            "method",
            "field"
           ]
          },
          "owner": {
           "$ref": "#/$defs/Shared077"
          },
          "method": {
           "$ref": "#/$defs/Shared077"
          },
          "arguments": {
           "type": "array",
           "maxItems": 64,
           "items": {
            "type": "object"
           }
          }
         }
        }
       }
      }
     ]
    },
    "request_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    },
    "transaction_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "request_id",
    "transaction_id",
    "expected_revision",
    "operation"
   ],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "capacity": {
         "$ref": "#/$defs/Shared014"
        },
        "created_object_ids": {
         "items": {
          "maxLength": 128,
          "minLength": 1,
          "type": "string"
         },
         "maxItems": 321,
         "type": "array"
        },
        "diffs": {
         "$ref": "#/$defs/Shared027"
        },
        "fingerprints": {
         "$ref": "#/$defs/Shared071"
        },
        "kind": {
         "enum": [
          "type_add",
          "type_update",
          "type_remove",
          "method_add",
          "method_update",
          "method_remove",
          "field_add",
          "field_update",
          "field_remove",
          "property_add",
          "property_update",
          "property_remove",
          "event_add",
          "event_update",
          "event_remove",
          "parameter_add",
          "parameter_update",
          "parameter_remove",
          "generic_parameter_add",
          "generic_parameter_update",
          "generic_parameter_remove",
          "method_body_replace",
          "attribute_add",
          "attribute_remove",
          "security_add",
          "security_remove",
          "assembly_update",
          "module_update",
          "assembly_ref_update",
          "entry_point_set",
          "managed_resource_add",
          "managed_resource_update",
          "managed_resource_remove",
          "win32_resource_add",
          "win32_resource_update",
          "win32_resource_remove",
          "strong_name_remove",
          "interface_add",
          "reference_add"
         ]
        },
        "operation_index": {
         "maximum": 4294967295,
         "minimum": 0,
         "type": "integer"
        },
        "review_cleared": {
         "const": true
        },
        "risks": {
         "$ref": "#/$defs/Shared044"
        },
        "transaction": {
         "$ref": "#/$defs/Shared048"
        }
       },
       "required": [
        "transaction",
        "operation_index",
        "kind",
        "created_object_ids",
        "fingerprints",
        "diffs",
        "risks",
        "review_cleared",
        "capacity"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_begin": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {
    "assembly_name": {
     "maxLength": 512,
     "minLength": 1,
     "type": "string"
    },
    "module_mvid": {
     "maxLength": 36,
     "minLength": 1,
     "type": "string"
    },
    "request_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    },
    "source_family_id": {
     "type": "string",
     "pattern": "^family-[0-9a-f]{32}$"
    }
   },
   "required": [
    "request_id",
    "assembly_name"
   ],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "capabilities": {
         "additionalProperties": false,
         "properties": {
          "dynamic_validation": {
           "type": "boolean"
          },
          "operation_kinds": {
           "items": {
            "enum": [
             "type_add",
             "type_update",
             "type_remove",
             "method_add",
             "method_update",
             "method_remove",
             "field_add",
             "field_update",
             "field_remove",
             "property_add",
             "property_update",
             "property_remove",
             "event_add",
             "event_update",
             "event_remove",
             "parameter_add",
             "parameter_update",
             "parameter_remove",
             "generic_parameter_add",
             "generic_parameter_update",
             "generic_parameter_remove",
             "method_body_replace",
             "attribute_add",
             "attribute_remove",
             "security_add",
             "security_remove",
             "assembly_update",
             "module_update",
             "assembly_ref_update",
             "entry_point_set",
             "managed_resource_add",
             "managed_resource_update",
             "managed_resource_remove",
             "win32_resource_add",
             "win32_resource_update",
             "win32_resource_remove",
             "strong_name_remove",
             "interface_add",
             "reference_add"
            ]
           },
           "maxItems": 39,
           "minItems": 39,
           "type": "array",
           "uniqueItems": true
          },
          "test_apply_restore": {
           "type": "boolean"
          }
         },
         "required": [
          "operation_kinds",
          "dynamic_validation",
          "test_apply_restore"
         ],
         "type": "object"
        },
        "capacity": {
         "$ref": "#/$defs/Shared014"
        },
        "fingerprints": {
         "$ref": "#/$defs/Shared071"
        },
        "limits": {
         "$ref": "#/$defs/Shared036"
        },
        "source": {
         "additionalProperties": false,
         "properties": {
          "assembly_name": {
           "maxLength": 512,
           "minLength": 1,
           "type": "string"
          },
          "file_path": {
           "oneOf": [
            {
             "maxLength": 32767,
             "minLength": 1,
             "type": "string"
            },
            {
             "type": "null"
            }
           ]
          },
          "file_sha256": {
           "oneOf": [
            {
             "maxLength": 64,
             "minLength": 1,
             "pattern": "^[0-9a-f]{64}$",
             "type": "string"
            },
            {
             "type": "null"
            }
           ]
          },
          "live_fingerprint": {
           "maxLength": 64,
           "minLength": 1,
           "pattern": "^[0-9a-f]{64}$",
           "type": "string"
          },
          "module_name": {
           "maxLength": 512,
           "minLength": 1,
           "type": "string"
          },
          "mvid": {
           "maxLength": 36,
           "minLength": 1,
           "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
           "type": "string"
          }
         },
         "required": [
          "assembly_name",
          "module_name",
          "mvid",
          "file_path",
          "file_sha256",
          "live_fingerprint"
         ],
         "type": "object"
        },
        "transaction": {
         "$ref": "#/$defs/Shared048"
        },
        "history": {
         "type": "object"
        }
       },
       "required": [
        "transaction",
        "source",
        "fingerprints",
        "limits",
        "capacity",
        "capabilities",
        "history"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_review": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {
    "dynamic_validation": {
     "additionalProperties": false,
     "properties": {
      "args": {
       "items": {
        "maxLength": 32767,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 256,
       "type": "array"
      },
      "mode": {
       "const": "run"
      },
      "runtime_profile": {
       "maxLength": 128,
       "minLength": 1,
       "type": "string"
      },
      "timeout_ms": {
       "maximum": 120000,
       "minimum": 1000,
       "type": "integer"
      },
      "working_directory": {
       "maxLength": 32767,
       "minLength": 1,
       "type": "string"
      }
     },
     "required": [
      "mode",
      "runtime_profile"
     ],
     "type": "object"
    },
    "expected_revision": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "request_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    },
    "transaction_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "request_id",
    "transaction_id",
    "expected_revision"
   ],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "diffs": {
         "$ref": "#/$defs/Shared027"
        },
        "dynamic_validation": {
         "oneOf": [
          {
           "additionalProperties": false,
           "properties": {
            "artifact": {
             "$ref": "#/$defs/Shared114"
            },
            "cleanup": {
             "$ref": "#/$defs/Shared076"
            },
            "events": {
             "$ref": "#/$defs/Shared079"
            },
            "failure": {
             "type": "null"
            },
            "requested": {
             "const": false
            },
            "runtime_profile": {
             "type": "null"
            },
            "state": {
             "const": "not_requested"
            }
           },
           "required": [
            "state",
            "requested",
            "runtime_profile",
            "artifact",
            "events",
            "cleanup",
            "failure"
           ],
           "type": "object"
          },
          {
           "additionalProperties": false,
           "properties": {
            "artifact": {
             "$ref": "#/$defs/Shared114"
            },
            "cleanup": {
             "$ref": "#/$defs/Shared076"
            },
            "events": {
             "$ref": "#/$defs/Shared079"
            },
            "failure": {
             "type": "null"
            },
            "requested": {
             "const": true
            },
            "runtime_profile": {
             "maxLength": 128,
             "minLength": 1,
             "type": "string"
            },
            "state": {
             "const": "not_applicable"
            }
           },
           "required": [
            "state",
            "requested",
            "runtime_profile",
            "artifact",
            "events",
            "cleanup",
            "failure"
           ],
           "type": "object"
          },
          {
           "additionalProperties": false,
           "properties": {
            "artifact": {
             "additionalProperties": false,
             "properties": {
              "created": {
               "const": true
              },
              "path": {
               "maxLength": 4096,
               "minLength": 1,
               "type": "string"
              },
              "sha256": {
               "maxLength": 64,
               "minLength": 1,
               "pattern": "^[0-9a-f]{64}$",
               "type": "string"
              }
             },
             "required": [
              "created",
              "path",
              "sha256"
             ],
             "type": "object"
            },
            "cleanup": {
             "additionalProperties": false,
             "properties": {
              "debug_idle": {
               "const": true
              },
              "residual_path": {
               "type": "null"
              },
              "temp_delete_attempted": {
               "const": true
              },
              "temp_deleted": {
               "const": true
              },
              "terminate_attempted": {
               "const": true
              }
             },
             "required": [
              "terminate_attempted",
              "debug_idle",
              "temp_delete_attempted",
              "temp_deleted",
              "residual_path"
             ],
             "type": "object"
            },
            "events": {
             "items": {
              "$ref": "#/$defs/Shared084"
             },
             "maxItems": 64,
             "minItems": 1,
             "type": "array"
            },
            "failure": {
             "type": "null"
            },
            "requested": {
             "const": true
            },
            "runtime_profile": {
             "maxLength": 128,
             "minLength": 1,
             "type": "string"
            },
            "state": {
             "const": "passed"
            }
           },
           "required": [
            "state",
            "requested",
            "runtime_profile",
            "artifact",
            "events",
            "cleanup",
            "failure"
           ],
           "type": "object"
          }
         ]
        },
        "fingerprints": {
         "$ref": "#/$defs/Shared071"
        },
        "limits": {
         "$ref": "#/$defs/Shared036"
        },
        "review": {
         "additionalProperties": false,
         "properties": {
          "required_confirmation_ids": {
           "items": {
            "maxLength": 48,
            "minLength": 1,
            "type": "string"
           },
           "maxItems": 256,
           "type": "array"
          },
          "review_id": {
           "maxLength": 128,
           "minLength": 1,
           "type": "string"
          },
          "review_revision": {
           "maximum": 4294967295,
           "minimum": 0,
           "type": "integer"
          }
         },
         "required": [
          "review_id",
          "review_revision",
          "required_confirmation_ids"
         ],
         "type": "object"
        },
        "risks": {
         "$ref": "#/$defs/Shared044"
        },
        "roundtrip_validation": {
         "$ref": "#/$defs/Shared054"
        },
        "structural_validation": {
         "$ref": "#/$defs/Shared054"
        },
        "transaction": {
         "$ref": "#/$defs/Shared048"
        }
       },
       "required": [
        "transaction",
        "review",
        "fingerprints",
        "diffs",
        "structural_validation",
        "roundtrip_validation",
        "dynamic_validation",
        "risks",
        "limits"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_rollback": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {
    "request_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    },
    "transaction_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "request_id",
    "transaction_id"
   ],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "end_reason": {
         "const": "client_rollback"
        },
        "original_live_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "released": {
         "additionalProperties": false,
         "properties": {
          "apply_cache_entries": {
           "maximum": 4294967295,
           "minimum": 0,
           "type": "integer"
          },
          "private_modules": {
           "maximum": 4294967295,
           "minimum": 0,
           "type": "integer"
          },
          "review_slots": {
           "maximum": 4294967295,
           "minimum": 0,
           "type": "integer"
          },
          "validation_modules": {
           "maximum": 4294967295,
           "minimum": 0,
           "type": "integer"
          }
         },
         "required": [
          "private_modules",
          "validation_modules",
          "apply_cache_entries",
          "review_slots"
         ],
         "type": "object"
        },
        "rolled_back": {
         "const": true
        }
       },
       "required": [
        "rolled_back",
        "end_reason",
        "original_live_fingerprint",
        "released"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_status": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {},
   "required": [],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "oneOf": [
        {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "busy",
          "state",
          "history",
          "recovery",
          "capacity"
         ],
         "properties": {
          "busy": {
           "type": "boolean"
          },
          "state": {
           "type": "string",
           "enum": [
            "idle",
            "committing",
            "committed_without_checkpoint",
            "live_state_unknown"
           ]
          },
          "history": {
           "type": "object"
          },
          "recovery": {
           "oneOf": [
            {
             "type": "object"
            },
            {
             "type": "null"
            }
           ]
          },
          "capacity": {
           "type": "object"
          }
         }
        },
        {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "busy",
          "state",
          "owner_transport_kind"
         ],
         "properties": {
          "busy": {
           "const": true
          },
          "state": {
           "type": "string",
           "enum": [
            "editing",
            "reviewed",
            "applying",
            "committing"
           ]
          },
          "owner_transport_kind": {
           "type": "string",
           "enum": [
            "legacy_sse",
            "streamable_http"
           ]
          }
         }
        },
        {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "busy",
          "state",
          "transaction",
          "fingerprints",
          "review",
          "history",
          "recovery",
          "capacity",
          "risks"
         ],
         "properties": {
          "busy": {
           "const": true
          },
          "state": {
           "type": "string",
           "enum": [
            "editing",
            "reviewed",
            "applying",
            "committing"
           ]
          },
          "transaction": {
           "type": "object"
          },
          "fingerprints": {
           "type": "object"
          },
          "review": {
           "oneOf": [
            {
             "type": "object"
            },
            {
             "type": "null"
            }
           ]
          },
          "history": {
           "type": "object"
          },
          "recovery": {
           "oneOf": [
            {
             "type": "object"
            },
            {
             "type": "null"
            }
           ]
          },
          "capacity": {
           "type": "object"
          },
          "risks": {
           "type": "array",
           "items": {
            "type": "object"
           }
          }
         }
        }
       ]
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_apply_and_restore": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {
    "confirmed_risk_ids": {
     "items": {
      "maxLength": 128,
      "minLength": 1,
      "type": "string"
     },
     "maxItems": 256,
     "type": "array"
    },
    "expected_revision": {
     "maximum": 4294967295,
     "minimum": 0,
     "type": "integer"
    },
    "request_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    },
    "review_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    },
    "transaction_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "request_id",
    "transaction_id",
    "review_id",
    "expected_revision",
    "confirmed_risk_ids"
   ],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "applied_and_restored": {
         "const": true
        },
        "dispatcher_callbacks_ms": {
         "items": {
          "minimum": 0,
          "type": "number"
         },
         "maxItems": 64,
         "type": "array"
        },
        "execution_evidence": {
         "additionalProperties": false,
         "properties": {
          "actual_mutation_trace": {
           "items": {
            "$ref": "#/$defs/Shared033"
           },
           "maxItems": 24,
           "type": "array"
          },
          "armed_fault": {
           "oneOf": [
            {
             "$ref": "#/$defs/Shared033"
            },
            {
             "type": "null"
            }
           ]
          },
          "covered_faults": {
           "items": {
            "$ref": "#/$defs/Shared033"
           },
           "maxItems": 1,
           "type": "array",
           "uniqueItems": true
          },
          "fault_manifest": {
           "$ref": "#/$defs/Shared031"
          },
          "oracle_faults": {
           "$ref": "#/$defs/Shared031"
          }
         },
         "required": [
          "armed_fault",
          "fault_manifest",
          "oracle_faults",
          "actual_mutation_trace",
          "covered_faults"
         ],
         "type": "object"
        },
        "post_apply_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "post_restore_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "pre_live_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "private_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        }
       },
       "required": [
        "applied_and_restored",
        "pre_live_fingerprint",
        "private_fingerprint",
        "post_apply_fingerprint",
        "post_restore_fingerprint",
        "execution_evidence",
        "dispatcher_callbacks_ms"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_barrier": {
  "inputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "action": {
       "const": "arm"
      },
      "name": {
       "enum": [
        "begin_after_copy",
        "apply_before_mutation",
        "review_before_validation",
        "commit_after_guard_before_temp",
        "commit_after_temp_validate",
        "commit_dispatcher_queued",
        "commit_after_live_first_mutation",
        "commit_after_live_complete",
        "commit_after_package_switch_before_response"
       ]
      }
     },
     "required": [
      "action",
      "name"
     ],
     "type": "object"
    },
    {
     "additionalProperties": false,
     "properties": {
      "action": {
       "const": "snapshot"
      }
     },
     "required": [
      "action"
     ],
     "type": "object"
    },
    {
     "additionalProperties": false,
     "properties": {
      "action": {
       "const": "release"
      }
     },
     "required": [
      "action"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared140"
    }
   ]
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "active_generation": {
         "oneOf": [
          {
           "maximum": 9223372036854775807,
           "minimum": 0,
           "type": "integer"
          },
          {
           "type": "null"
          }
         ]
        },
        "active_transaction_id": {
         "oneOf": [
          {
           "maxLength": 128,
           "minLength": 1,
           "type": "string"
          },
          {
           "type": "null"
          }
         ]
        },
        "armed": {
         "type": "boolean"
        },
        "entered": {
         "type": "boolean"
        },
        "name": {
         "oneOf": [
          {
           "enum": [
            "begin_after_copy",
            "apply_before_mutation",
            "review_before_validation"
           ]
          },
          {
           "type": "null"
          }
         ]
        },
        "operation_waiters": {
         "maximum": 4294967295,
         "minimum": 0,
         "type": "integer"
        },
        "owner_session_id": {
         "oneOf": [
          {
           "maxLength": 128,
           "minLength": 1,
           "type": "string"
          },
          {
           "type": "null"
          }
         ]
        },
        "pending_request_key": {
         "oneOf": [
          {
           "maxLength": 512,
           "minLength": 1,
           "type": "string"
          },
          {
           "type": "null"
          }
         ]
        },
        "released": {
         "type": "boolean"
        }
       },
       "required": [
        "armed",
        "name",
        "owner_session_id",
        "entered",
        "released",
        "operation_waiters",
        "pending_request_key",
        "active_generation",
        "active_transaction_id"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_clock": {
  "inputSchema": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared141"
    },
    {
     "$ref": "#/$defs/Shared140"
    },
    {
     "additionalProperties": false,
     "properties": {
      "action": {
       "const": "advance"
      },
      "advance_ms": {
       "maximum": 9223372036854775807,
       "minimum": 0,
       "type": "integer"
      }
     },
     "required": [
      "action",
      "advance_ms"
     ],
     "type": "object"
    }
   ]
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "monotonic_ms": {
         "maximum": 9223372036854775807,
         "minimum": 0,
         "type": "integer"
        },
        "offset_ms": {
         "maximum": 9223372036854775807,
         "minimum": 0,
         "type": "integer"
        }
       },
       "required": [
        "monotonic_ms",
        "offset_ms"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_external_mutation": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {
    "case_id": {
     "enum": [
      "fp-channel-modulemetadata:mutate",
      "fp-channel-dnlibobjectgraph:mutate",
      "fp-channel-methodbodyil:mutate",
      "fp-channel-managedresource:mutate",
      "fp-channel-embeddedpdb:mutate",
      "fp-canonical-global-order:reorder",
      "live-conflict:mutate-entrypoint",
      "live-conflict:mutate-layout",
      "live-conflict:mutate-cdi"
     ]
    },
    "transaction_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "transaction_id",
    "case_id"
   ],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "after_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "before_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "canonical_readback_after": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "canonical_readback_before": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "case_id": {
         "enum": [
          "fp-channel-modulemetadata:mutate",
          "fp-channel-dnlibobjectgraph:mutate",
          "fp-channel-methodbodyil:mutate",
          "fp-channel-managedresource:mutate",
          "fp-channel-embeddedpdb:mutate",
          "fp-canonical-global-order:reorder"
         ]
        },
        "changed": {
         "type": "boolean"
        },
        "component": {
         "enum": [
          "ModuleMetadata",
          "DnlibObjectGraph",
          "MethodBodyIl",
          "ManagedResource",
          "EmbeddedPdb",
          "GlobalCanonicalOrder"
         ]
        },
        "evidence_artifact": {
         "$ref": "#/$defs/Shared094"
        },
        "located_slice_after": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "located_slice_before": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "raw_order_after": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "raw_order_before": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "recipe_id": {
         "enum": [
          "fp-channel-modulemetadata",
          "fp-channel-dnlibobjectgraph",
          "fp-channel-methodbodyil",
          "fp-channel-managedresource",
          "fp-channel-embeddedpdb",
          "fp-canonical-global-order"
         ]
        },
        "recipe_sha256": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "restored": {
         "const": true
        },
        "restored_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "semantic_change": {
         "type": "boolean"
        }
       },
       "required": [
        "case_id",
        "recipe_id",
        "component",
        "recipe_sha256",
        "evidence_artifact",
        "located_slice_before",
        "located_slice_after",
        "raw_order_before",
        "raw_order_after",
        "canonical_readback_before",
        "canonical_readback_after",
        "before_fingerprint",
        "after_fingerprint",
        "restored_fingerprint",
        "changed",
        "semantic_change",
        "restored"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_fault": {
  "inputSchema": {
   "oneOf": [
    {
     "$ref": "#/$defs/Shared141"
    },
    {
     "$ref": "#/$defs/Shared140"
    },
    {
     "additionalProperties": false,
     "properties": {
      "action": {
       "const": "arm"
      },
      "fault_id": {
       "maxLength": 128,
       "minLength": 1,
       "type": "string"
      }
     },
     "required": [
      "action",
      "fault_id"
     ],
     "type": "object"
    }
   ]
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "armed_fault_id": {
         "oneOf": [
          {
           "maxLength": 128,
           "minLength": 1,
           "type": "string"
          },
          {
           "type": "null"
          }
         ]
        },
        "emergency_cleanup": {
         "type": "boolean"
        },
        "known_fault_ids": {
         "items": {
          "maxLength": 128,
          "minLength": 1,
          "type": "string"
         },
         "maxItems": 1024,
         "type": "array"
        }
       },
       "required": [
        "armed_fault_id",
        "known_fault_ids",
        "emergency_cleanup"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_live_mutation": {
  "inputSchema": {
   "additionalProperties": false,
   "properties": {
    "action": {
     "enum": [
      "mutate",
      "restore"
     ]
    },
    "transaction_id": {
     "maxLength": 128,
     "minLength": 1,
     "type": "string"
    }
   },
   "required": [
    "transaction_id",
    "action"
   ],
   "type": "object"
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "after_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "before_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "canonical_readback_after": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "canonical_readback_before": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "case_id": {
         "enum": [
          "live-conflict:mutate",
          "live-conflict:restore"
         ]
        },
        "changed": {
         "type": "boolean"
        },
        "component": {
         "const": "ModuleMetadata"
        },
        "evidence_artifact": {
         "$ref": "#/$defs/Shared094"
        },
        "located_slice_after": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "located_slice_before": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "raw_order_after": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "raw_order_before": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "recipe_id": {
         "const": "live-conflict"
        },
        "recipe_sha256": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "restored": {
         "type": "boolean"
        },
        "restored_fingerprint": {
         "maxLength": 64,
         "minLength": 1,
         "pattern": "^[0-9a-f]{64}$",
         "type": "string"
        },
        "semantic_change": {
         "const": true
        }
       },
       "required": [
        "case_id",
        "recipe_id",
        "component",
        "recipe_sha256",
        "evidence_artifact",
        "located_slice_before",
        "located_slice_after",
        "raw_order_before",
        "raw_order_after",
        "canonical_readback_before",
        "canonical_readback_after",
        "before_fingerprint",
        "after_fingerprint",
        "restored_fingerprint",
        "changed",
        "semantic_change",
        "restored"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_commit": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "transaction_id",
    "expected_revision",
    "review_id",
    "review_revision",
    "confirmed_risk_ids"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "transaction_id": {
     "type": "string",
     "pattern": "^edit-[0-9a-f]{32}$"
    },
    "expected_revision": {
     "type": "integer",
     "minimum": 0,
     "maximum": 4294967295
    },
    "review_id": {
     "type": "string",
     "pattern": "^review-[0-9a-f]{32}$"
    },
    "review_revision": {
     "type": "integer",
     "minimum": 0,
     "maximum": 4294967295
    },
    "confirmed_risk_ids": {
     "type": "array",
     "maxItems": 256,
     "uniqueItems": true,
     "items": {
      "type": "string",
      "minLength": 1,
      "maxLength": 128
     }
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "checkpoint",
        "history",
        "fingerprints",
        "confirmed_risks",
        "live_recovery"
       ],
       "properties": {
        "checkpoint": {
         "type": "object"
        },
        "history": {
         "type": "object"
        },
        "fingerprints": {
         "type": "object"
        },
        "confirmed_risks": {
         "type": "array",
         "items": {
          "type": "object"
         }
        },
        "live_recovery": {
         "type": "object",
         "additionalProperties": false,
         "properties": {
          "inverse_plan": {
           "const": "pregenerated_compiled_state"
          },
          "inverse_plan_operations": {
           "type": "integer",
           "minimum": 0,
           "maximum": 4294967295
          },
          "inverse_plan_bound_checkpoint_id": {
           "type": "string",
           "minLength": 1,
           "maxLength": 128
          },
          "inverse_plan_complete_before_live_write": {
           "const": true
          }
         },
         "required": [
          "inverse_plan",
          "inverse_plan_operations",
          "inverse_plan_bound_checkpoint_id",
          "inverse_plan_complete_before_live_write"
         ]
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_history": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "properties": {
    "lineage_id": {
     "type": "string",
     "pattern": "^lineage-[0-9a-f]{32}$"
    },
    "checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    },
    "cursor": {
     "type": "string",
     "maxLength": 1024
    },
    "page_size": {
     "type": "integer",
     "minimum": 1,
     "maximum": 100
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "view",
        "next_cursor"
       ],
       "properties": {
        "view": {
         "type": "string",
         "enum": [
          "lineages",
          "checkpoints",
          "checkpoint"
         ]
        },
        "lineages": {
         "type": "array",
         "items": {
          "type": "object"
         }
        },
        "checkpoints": {
         "type": "array",
         "items": {
          "type": "object"
         }
        },
        "checkpoint": {
         "oneOf": [
          {
           "type": "object"
          },
          {
           "type": "null"
          }
         ]
        },
        "next_cursor": {
         "oneOf": [
          {
           "type": "string",
           "maxLength": 1024
          },
          {
           "type": "null"
          }
         ]
        },
        "capacity": {
         "type": "object"
        },
        "recovery": {
         "oneOf": [
          {
           "type": "object"
          },
          {
           "type": "null"
          }
         ]
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "view",
        "busy",
        "state"
       ],
       "properties": {
        "view": {
         "const": "summary"
        },
        "busy": {
         "type": "boolean"
        },
        "state": {
         "$ref": "#/$defs/Shared132"
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_undo": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "lineage_id",
    "expected_checkpoint_id"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "lineage_id": {
     "type": "string",
     "pattern": "^lineage-[0-9a-f]{32}$"
    },
    "expected_checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    }
   }
  },
  "outputSchema": {
   "$ref": "#/$defs/Shared011"
  }
 },
 "edit_redo": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "lineage_id",
    "expected_checkpoint_id"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "lineage_id": {
     "type": "string",
     "pattern": "^lineage-[0-9a-f]{32}$"
    },
    "expected_checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    },
    "child_checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    }
   }
  },
  "outputSchema": {
   "$ref": "#/$defs/Shared011"
  }
 },
 "edit_restore": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "lineage_id",
    "checkpoint_id",
    "action"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "lineage_id": {
     "type": "string",
     "pattern": "^lineage-[0-9a-f]{32}$"
    },
    "checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    },
    "action": {
     "type": "string",
     "enum": [
      "assess",
      "apply"
     ]
    },
    "replay_id": {
     "type": "string",
     "pattern": "^replay-[0-9a-f]{32}$"
    },
    "expected_live_fingerprint": {
     "type": "string",
     "pattern": "^[0-9a-f]{64}$"
    },
    "confirm_validated_drift": {
     "type": "boolean"
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "replay"
       ],
       "properties": {
        "replay": {
         "type": "object"
        },
        "history": {
         "type": "object"
        },
        "migration_checkpoint": {
         "type": "object"
        },
        "from_checkpoint_id": {
         "type": "string",
         "pattern": "^checkpoint-[0-9a-f]{32}$"
        },
        "to_checkpoint_id": {
         "type": "string",
         "pattern": "^checkpoint-[0-9a-f]{32}$"
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_export": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "lineage_id",
    "checkpoint_id"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "lineage_id": {
     "type": "string",
     "pattern": "^lineage-[0-9a-f]{32}$"
    },
    "checkpoint_id": {
     "type": "string",
     "pattern": "^checkpoint-[0-9a-f]{32}$"
    },
    "output_path": {
     "type": "string",
     "minLength": 1,
     "maxLength": 32767
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "checkpoint",
        "output",
        "replay"
       ],
       "properties": {
        "checkpoint": {
         "type": "object"
        },
        "output": {
         "type": "object",
         "additionalProperties": false,
         "required": [
          "path",
          "length",
          "sha256",
          "file_id"
         ],
         "properties": {
          "path": {
           "type": "string",
           "minLength": 1,
           "maxLength": 32767
          },
          "length": {
           "type": "integer",
           "minimum": 0
          },
          "sha256": {
           "type": "string",
           "pattern": "^[0-9a-f]{64}$"
          },
          "file_id": {
           "type": "string",
           "minLength": 1,
           "maxLength": 256
          }
         }
        },
        "replay": {
         "type": "object"
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_recover": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "recovery_id",
    "action"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "recovery_id": {
     "type": "string",
     "pattern": "^recovery-[0-9a-f]{32}$"
    },
    "action": {
     "type": "string",
     "enum": [
      "retry_checkpoint",
      "undo_live",
      "cleanup_temp"
     ]
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "resolved",
        "action",
        "history"
       ],
       "properties": {
        "resolved": {
         "type": "boolean"
        },
        "action": {
         "type": "string",
         "enum": [
          "retry_checkpoint",
          "undo_live",
          "cleanup_temp"
         ]
        },
        "checkpoint": {
         "type": "object"
        },
        "restored_fingerprint": {
         "type": "string",
         "pattern": "^[0-9a-f]{64}$"
        },
        "removed_temp": {
         "type": "boolean"
        },
        "history": {
         "type": "object"
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_accept_live": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "assembly_name",
    "source_family_id",
    "superseded_lineage_id",
    "expected_live_fingerprint",
    "acknowledge_new_baseline"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "assembly_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "source_family_id": {
     "type": "string",
     "pattern": "^family-[0-9a-f]{32}$"
    },
    "superseded_lineage_id": {
     "type": "string",
     "pattern": "^lineage-[0-9a-f]{32}$"
    },
    "expected_live_fingerprint": {
     "type": "string",
     "pattern": "^[0-9a-f]{64}$"
    },
    "acknowledge_new_baseline": {
     "const": true
    },
    "module_mvid": {
     "$ref": "#/$defs/Shared142"
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "source_identity",
        "lineage",
        "root_checkpoint",
        "superseded_lineage_id"
       ],
       "properties": {
        "source_identity": {
         "type": "object"
        },
        "lineage": {
         "type": "object"
        },
        "root_checkpoint": {
         "type": "object"
        },
        "superseded_lineage_id": {
         "type": "string",
         "pattern": "^lineage-[0-9a-f]{32}$"
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_storage_fault": {
  "inputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "action",
      "stage"
     ],
     "properties": {
      "action": {
       "const": "arm"
      },
      "stage": {
       "$ref": "#/$defs/Shared131"
      }
     }
    },
    {
     "$ref": "#/$defs/Shared140"
    }
   ]
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "action",
        "armed"
       ],
       "properties": {
        "action": {
         "type": "string",
         "enum": [
          "arm",
          "reset"
         ]
        },
        "stage": {
         "$ref": "#/$defs/Shared131"
        },
        "armed": {
         "type": "boolean"
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_lineage_mutation": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "action",
    "assembly_name"
   ],
   "properties": {
    "action": {
     "type": "string",
     "enum": [
      "mutate",
      "restore"
     ]
    },
    "assembly_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "module_mvid": {
     "type": "string",
     "pattern": "^[0-9a-fA-F-]{36}$"
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "type": "object",
     "additionalProperties": false,
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "properties": {
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "ok": {
       "const": true
      },
      "state": {
       "$ref": "#/$defs/Shared132"
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "action",
        "before_fingerprint",
        "after_fingerprint",
        "restored"
       ],
       "properties": {
        "action": {
         "type": "string",
         "enum": [
          "mutate",
          "restore"
         ]
        },
        "before_fingerprint": {
         "type": "string",
         "pattern": "^[0-9a-f]{64}$"
        },
        "after_fingerprint": {
         "type": "string",
         "pattern": "^[0-9a-f]{64}$"
        },
        "restored": {
         "type": "boolean"
        }
       }
      },
      "warnings": {
       "type": "array",
       "maxItems": 16,
       "items": {
        "type": "string",
        "minLength": 1,
        "maxLength": 512
       }
      },
      "untrusted_sample_data": {
       "const": true
      }
     }
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_import": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "transaction_id",
    "expected_revision",
    "compile_id",
    "targets"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "transaction_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "expected_revision": {
     "type": "integer",
     "minimum": 0,
     "maximum": 4294967295
    },
    "compile_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "targets": {
     "type": "array",
     "minItems": 1,
     "maxItems": 256,
     "items": {
      "type": "object",
      "additionalProperties": false,
      "required": [
       "compiled",
       "action"
      ],
      "properties": {
       "compiled": {
        "type": "string",
        "minLength": 1,
        "maxLength": 8192
       },
       "action": {
        "type": "string",
        "enum": [
         "replace_body",
         "add"
        ]
       },
       "target": {
        "$ref": "#/$defs/Shared080"
       }
      }
     }
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "transaction": {
         "$ref": "#/$defs/Shared048"
        },
        "fingerprints": {
         "$ref": "#/$defs/Shared071"
        },
        "diffs": {
         "$ref": "#/$defs/Shared027"
        },
        "risks": {
         "$ref": "#/$defs/Shared044"
        },
        "review_cleared": {
         "const": true
        },
        "capacity": {
         "$ref": "#/$defs/Shared014"
        },
        "operation_count": {
         "type": "integer",
         "minimum": 0,
         "maximum": 4294967295
        },
        "import": {
         "type": "object",
         "additionalProperties": false,
         "properties": {
          "compile_id": {
           "type": "string"
          },
          "target_count": {
           "type": "integer",
           "minimum": 0,
           "maximum": 4294967295
          },
          "rows": {
           "type": "array",
           "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
             "kind": {
              "type": "string"
             },
             "artifact_member": {
              "type": "string"
             },
             "target": {
              "type": "string"
             }
            },
            "required": [
             "kind",
             "artifact_member",
             "target"
            ]
           }
          },
          "created_object_ids": {
           "type": "array",
           "items": {
            "type": "string"
           }
          }
         },
         "required": [
          "compile_id",
          "target_count",
          "rows",
          "created_object_ids"
         ]
        }
       },
       "required": [
        "transaction",
        "fingerprints",
        "diffs",
        "risks",
        "review_cleared",
        "capacity",
        "operation_count",
        "import"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_impact_scan": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "transaction_id",
    "expected_revision"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "transaction_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "expected_revision": {
     "type": "integer",
     "minimum": 0,
     "maximum": 4294967295
    }
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "properties": {
        "transaction": {
         "$ref": "#/$defs/Shared048"
        },
        "impact": {
         "type": "object",
         "additionalProperties": false,
         "properties": {
          "scope": {
           "const": "loaded_modules"
          },
          "modules": {
           "type": "array",
           "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
             "name": {
              "type": "string"
             },
             "inbound_reference_count": {
              "type": "integer",
              "minimum": 0,
              "maximum": 4294967295
             }
            },
            "required": [
             "name",
             "inbound_reference_count"
            ]
           }
          },
          "inbound_references": {
           "type": "array",
           "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
             "module": {
              "type": "string"
             },
             "assembly_ref_token": {
              "type": "string"
             },
             "matched_name": {
              "type": "string"
             },
             "sites": {
              "type": "array",
              "items": {
               "type": "string"
              }
             },
             "risk_id": {
              "type": "string"
             }
            },
            "required": [
             "module",
             "assembly_ref_token",
             "matched_name",
             "sites",
             "risk_id"
            ]
           }
          },
          "risk_ids": {
           "type": "array",
           "items": {
            "type": "string"
           }
          },
          "identity_operations": {
           "type": "array",
           "items": {
            "type": "object",
            "additionalProperties": false,
            "properties": {
             "operation_index": {
              "type": "integer",
              "minimum": 0,
              "maximum": 4294967295
             },
             "kind": {
              "type": "string"
             },
             "staged_name": {
              "oneOf": [
               {
                "type": "string"
               },
               {
                "type": "null"
               }
              ]
             }
            },
            "required": [
             "operation_index",
             "kind"
            ]
           }
          }
         },
         "required": [
          "scope",
          "modules",
          "inbound_references",
          "risk_ids",
          "identity_operations"
         ]
        }
       },
       "required": [
        "transaction",
        "impact"
       ]
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_resource_import": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "transaction_id",
    "expected_revision",
    "vm_path",
    "resource_name"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "transaction_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "expected_revision": {
     "type": "integer",
     "minimum": 0,
     "maximum": 4294967295
    },
    "vm_path": {
     "type": "string",
     "minLength": 2,
     "maxLength": 1024
    },
    "resource_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "resource_type": {
     "$ref": "#/$defs/Shared113"
    },
    "type_id": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    },
    "type_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "name_id": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    },
    "lang_id": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    }
   },
   "not": {
    "required": [
     "type_id",
     "type_name"
    ]
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "additionalProperties": false,
       "properties": {
        "capacity": {
         "$ref": "#/$defs/Shared014"
        },
        "created_object_ids": {
         "items": {
          "maxLength": 128,
          "minLength": 1,
          "type": "string"
         },
         "maxItems": 321,
         "type": "array"
        },
        "diffs": {
         "$ref": "#/$defs/Shared027"
        },
        "fingerprints": {
         "$ref": "#/$defs/Shared071"
        },
        "kind": {
         "enum": [
          "type_add",
          "type_update",
          "type_remove",
          "method_add",
          "method_update",
          "method_remove",
          "field_add",
          "field_update",
          "field_remove",
          "property_add",
          "property_update",
          "property_remove",
          "event_add",
          "event_update",
          "event_remove",
          "parameter_add",
          "parameter_update",
          "parameter_remove",
          "generic_parameter_add",
          "generic_parameter_update",
          "generic_parameter_remove",
          "method_body_replace",
          "attribute_add",
          "attribute_remove",
          "security_add",
          "security_remove",
          "assembly_update",
          "module_update",
          "assembly_ref_update",
          "entry_point_set",
          "managed_resource_add",
          "managed_resource_update",
          "managed_resource_remove",
          "win32_resource_add",
          "win32_resource_update",
          "win32_resource_remove",
          "strong_name_remove",
          "interface_add",
          "reference_add"
         ]
        },
        "operation_index": {
         "maximum": 4294967295,
         "minimum": 0,
         "type": "integer"
        },
        "review_cleared": {
         "const": true
        },
        "risks": {
         "$ref": "#/$defs/Shared044"
        },
        "transaction": {
         "$ref": "#/$defs/Shared048"
        },
        "import": {
         "type": "object",
         "additionalProperties": false,
         "properties": {
          "file_id": {
           "type": "string",
           "pattern": "^[0-9a-f]{32}$"
          },
          "length": {
           "type": "integer",
           "minimum": 0,
           "maximum": 8388608
          },
          "sha256": {
           "type": "string",
           "pattern": "^[0-9a-f]{64}$"
          },
          "vm_path": {
           "type": "string",
           "minLength": 1
          },
          "resource_name": {
           "type": "string",
           "minLength": 1
          },
          "resource_type": {
           "type": "string",
           "enum": [
            "embedded",
            "linked",
            "win32"
           ]
          }
         },
         "required": [
          "file_id",
          "length",
          "sha256",
          "vm_path",
          "resource_name",
          "resource_type"
         ]
        }
       },
       "required": [
        "transaction",
        "operation_index",
        "kind",
        "created_object_ids",
        "fingerprints",
        "diffs",
        "risks",
        "review_cleared",
        "capacity",
        "import"
       ],
       "type": "object"
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_resource_export": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "request_id",
    "assembly_name",
    "resource_name",
    "output_path"
   ],
   "properties": {
    "request_id": {
     "type": "string",
     "minLength": 1,
     "maxLength": 128
    },
    "assembly_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "resource_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "output_path": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "resource_type": {
     "$ref": "#/$defs/Shared113"
    },
    "type_id": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    },
    "type_name": {
     "type": "string",
     "minLength": 1,
     "maxLength": 512
    },
    "name_id": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    },
    "lang_id": {
     "type": "integer",
     "minimum": 0,
     "maximum": 65535
    }
   },
   "not": {
    "required": [
     "type_id",
     "type_name"
    ]
   }
  },
  "outputSchema": {
   "oneOf": [
    {
     "additionalProperties": false,
     "properties": {
      "ok": {
       "const": true
      },
      "result": {
       "type": "object",
       "additionalProperties": false,
       "required": [
        "export"
       ],
       "properties": {
        "export": {
         "type": "object",
         "additionalProperties": false,
         "properties": {
          "file_id": {
           "type": "string",
           "pattern": "^[0-9a-f]{32}$"
          },
          "length": {
           "type": "integer",
           "minimum": 0,
           "maximum": 8388608
          },
          "sha256": {
           "type": "string",
           "pattern": "^[0-9a-f]{64}$"
          },
          "path": {
           "type": "string",
           "minLength": 1
          }
         },
         "required": [
          "file_id",
          "length",
          "sha256",
          "path"
         ]
        }
       }
      },
      "schema_version": {
       "const": "dnspy.edit.v1"
      },
      "state": {
       "enum": [
        "idle",
        "editing",
        "reviewed",
        "applying",
        "committing",
        "committed_without_checkpoint",
        "live_state_unknown"
       ]
      },
      "untrusted_sample_data": {
       "const": true
      },
      "warnings": {
       "items": {
        "maxLength": 256,
        "minLength": 1,
        "type": "string"
       },
       "maxItems": 8,
       "type": "array"
      }
     },
     "required": [
      "schema_version",
      "ok",
      "state",
      "result",
      "warnings",
      "untrusted_sample_data"
     ],
     "type": "object"
    },
    {
     "$ref": "#/$defs/Shared021"
    }
   ]
  }
 },
 "edit_test_strong_name": {
  "inputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "assembly_name",
    "assembly_version",
    "public_key_token"
   ],
   "properties": {
    "assembly_name": {
     "type": "string"
    },
    "assembly_version": {
     "type": "string"
    },
    "public_key_token": {
     "type": "string",
     "pattern": "^[0-9a-fA-F]{16}$"
    }
   }
  },
  "outputSchema": {
   "type": "object",
   "additionalProperties": false,
   "required": [
    "ok"
   ],
   "properties": {
    "ok": {
     "type": "boolean"
    },
    "session_id": {
     "type": "string"
    },
    "event_cursor": {
     "type": "integer"
    }
   }
  }
 }
}
```

## 附录 D：冻结调试 UTF-8 字节限制

JSON Pointer 指向附录 B 中的定义；`direction` 区分入站、出站与双向。此表是独立的字节阶段，不能仅用字符长度替代。

```json
{
  "schema_version": "dnspy.debug.utf8-limits.v1",
  "limits": [
    {
      "pointer": "/$defs/debug_dump_module_args/properties/relative_name",
      "direction": "input",
      "max_utf8_bytes": 128
    },
    {
      "pointer": "/$defs/debug_list_attachable_processes_args/properties/name_filter",
      "direction": "input",
      "max_utf8_bytes": 256
    },
    {
      "pointer": "/$defs/opaque_handle",
      "direction": "both",
      "max_utf8_bytes": 1024
    },
    {
      "pointer": "/$defs/page_cursor",
      "direction": "both",
      "max_utf8_bytes": 1024
    },
    {
      "pointer": "/$defs/session_id",
      "direction": "both",
      "max_utf8_bytes": 1024
    },
    {
      "pointer": "/$defs/unsupported_target_evidence/properties/value",
      "direction": "output",
      "max_utf8_bytes": 1024
    },
    {
      "pointer": "/$defs/value_node/properties/display",
      "direction": "output",
      "max_utf8_bytes": 65536
    },
    {
      "pointer": "/$defs/value_node/properties/name",
      "direction": "output",
      "max_utf8_bytes": 65536
    },
    {
      "pointer": "/$defs/value_node/properties/type",
      "direction": "output",
      "max_utf8_bytes": 65536
    },
    {
      "pointer": "/$defs/warning",
      "direction": "output",
      "max_utf8_bytes": 1024
    }
  ]
}
```

## 来源与边界

- `McpTools.cs` SHA256 `c5c540240b154d798c631fa16f87974f3f91edf62120d1a8e77ae6ecbf425427`；`Tools/McpToolRegistry.cs` SHA256 `88160449fa7d020295fc35712d3f993233e608c46120ffcf73a599f81e63af2e`；`Debugger/DebugToolProvider.cs` SHA256 `43ec74d0314f497e82c1e8cf035bda7c7576568f550a83c8c56490c39356cde4`。
- `tests/debug/contracts/dnspy.debug.v1.schema.json` SHA256 `673b25f624aa066e70d96e8d512c7f478b91546b8d31c4c121bdd2496e53d02b`；`Editing/Contracts/p03-tool-schemas.json` SHA256 `e6033fe86785a99f978545198f4525381a9caa073b3fa1c6ee7e3d92bc43d822`；`Editing/EditToolProvider.cs` SHA256 `45c956b3da385c46e5d938a46ba630dfa74a309ed670d8e8537147280740db24`；`Editing/EditCompileFrontend.cs` SHA256 `72100b8b608b4333b96e4249f529f3b97a917e1a827cdb79afbf246a50b3ec75`。
- `tests/debug/contracts/dnspy.debug.utf8-limits.json` SHA256 `bf8741dd5054cbff6cbf23a429adeec533ab0b6e84689655763062621ee04b7f`；`McpServer.cs` SHA256 `cdde4fcd3408febe53c6d32a60d369a66eb1eb7ca2ac9481589abd5581358261`。
- 这是源码接口手册，不是 VM 功能验收报告。运行时条件、实例配置、文件身份和目标架构须以 `debug_capabilities`、`tools/list`、调用返回和实际环境核对。
