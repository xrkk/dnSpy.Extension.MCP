# dnSpy MCP — 工具完整说明（中文）

与线上 `tools/list` 注册表机检一致（通告 78 个工具；另有 8 个 `edit_test_*` 测试面有 schema 但不通告，且需 `DNMDP_TEST=1`）。本文档的全部计数来自 `tools/export_tool_registry.py` 导出快照，绝不手写。

另见：[README.zh-CN.md](../README.zh-CN.md) · [English reference](MCP-TOOLS.md)

## 1. 线协议

- 传输：Streamable HTTP（含 legacy SSE），`http://localhost:<端口>/mcp`（默认 15378，Options 页显示实际端口）。
- 信封：每个工具返回 `{"schema_version": "...", "ok": bool, "state": "...", ...}`；失败携带 `error.code`、`error.message`、`error.current_state`、`error.recovery`。
- 会话：编辑/调试事务由单个已初始化 MCP 会话拥有；`request_id` 提供幂等重试。

## 2. 静态分析与代码生成（32 个工具）

open_files · list_assemblies · get_assembly_info · list_types · get_type_info · list_methods · get_type_fields · get_type_property · decompile_method · decompile_type · decompile_member · search_string_constants · search_constants · search_arrays · find_references · find_overrides · find_derived_types · 类型关系导航 · rename_symbol_by_token · save_assembly · patch_method_il · force_return · nop_method · revert_method · generate_bepinex_plugin · generate_harmony_patch · 结构搜索 · Unity 场景/方法发现 · get_method_il · find_type_relationship · get_type_layout —— 每个工具的参数见 README §功能。

## 3. 仅启动式动态调试（28 个工具）

debug_capabilities · debug_status · debug_launch · debug_pause · debug_continue · debug_restart · debug_terminate · debug_read_events · debug_wait_event · debug_set_breakpoint · debug_list_breakpoints · debug_set_breakpoint_enabled · debug_remove_breakpoint · debug_list_threads · debug_get_stack · debug_step · debug_get_locals · debug_expand_value · debug_list_modules · debug_read_memory · debug_dump_module · debug_set_exception_policy —— 加上 `DNMCP_TEST=1` 门禁的冻结测试面（debug_test_*）。启动是唯一执行门禁：静态工具绝不运行样本代码。

## 4. 事务式结构化编辑（通告 18 个）

### 4.1 生命周期

| 工具 | 用途 |
| --- | --- |
| `edit_begin` | 为一个已加载纯托管单模块程序集取得进程级编辑租约；创建私有副本 |
| `edit_status` | 不改变事务地查询状态/修订/指纹/容量/风险 |
| `edit_apply` | 向私有副本应用 **37** 类操作之一（必须携带 `request_id` 与 `expected_revision`） |
| `edit_review` | 审查固定修订；规范 diff + 必需风险确认 |
| `edit_commit` | 线性化到实时模块 + 持久化检查点（单步可恢复） |
| `edit_rollback` | 丢弃私有副本；释放租约 |
| `edit_history` / `edit_undo` / `edit_redo` / `edit_restore` | 浏览/导航持久检查点谱系 |
| `edit_export` | 在 ArtifactRoot 下导出精确检查点 |
| `edit_recover` / `edit_accept_live` | 解决部分提交恢复；接纳 UI 已偏离模块为新基线 |

### 4.2 编译 → 导入

| 工具 | 用途 |
| --- | --- |
| `edit_compile` | 经 dnSpy 公开 Roslyn 编译器编译 C#（程序集 + Portable PDB 留在内存；无 analyzer/generator/脚本面） |
| `edit_import` | 把编译产物成员以冻结操作导入私有副本——结构化签名匹配、生成子树整体处理、全或无拒绝；符号行随体移植；保存镜像仅内嵌 PDB |
| `edit_impact_scan` | 已加载模块范围的跨程序集影响（scope=loaded_modules）；入站引用转为需确认风险 |

### 4.3 身份/资源（edit_apply 种类）

- `assembly_update`、`module_update`、`assembly_ref_update`、`entry_point_set`
- `managed_resource_add/update/remove`、`win32_resource_add/update/remove`
- `strong_name_remove`（证据门禁）

### 4.4 37 类操作清单

type_add/update/remove · method_add/update/remove · field_add/update/remove · property_add/update/remove · event_add/update/remove · parameter_add/update/remove · generic_parameter_add/update/remove · method_body_replace · attribute_add/remove · security_add/remove · assembly_update · module_update · assembly_ref_update · entry_point_set · managed_resource_add/update/remove · win32_resource_add/update/remove · strong_name_remove

## 5. 错误码与恢复（冻结）

`EditWire.Message` / `EditWire.Recovery` 为事实来源；稳定集合含 EDIT_TRANSACTION_BUSY、EDIT_TRANSACTION_NOT_FOUND、EDIT_OWNER_REQUIRED/MISMATCH、EDIT_REVISION_CONFLICT、EDIT_LIVE_MODULE_CONFLICT、EDIT_REVIEW_STALE、EDIT_VALIDATION_FAILED、EDIT_RISK_CONFIRMATION_REQUIRED、EDIT_CAPABILITY_UNAVAILABLE、EDIT_CAPACITY_EXCEEDED、EDIT_DEBUG_NOT_IDLE、EDIT_LIVE_STATE_UNKNOWN、EDIT_CHECKPOINT_INVALID/COMMIT_FAILED/CLEANUP_FAILED、EDIT_EXPORT_BLOCKED、EDIT_REPLAY_CONFIRMATION_REQUIRED/UNVERIFIED、EDIT_OPERATION_VERSION_UNSUPPORTED、EDIT_HISTORY_CONFLICT、EDIT_BRANCH_SELECTION_REQUIRED、EDIT_LINEAGE_DIVERGED、EDIT_SOURCE_IDENTITY_CONFLICT、EDIT_RECOVERY_NOT_FOUND、REQUEST_ID_REUSE。

## 6. 资源面

MCP resources 面通告 14 个具体资源（程序集列表、类型索引、编辑状态、调试事件……）；`resources/templates/list` 有意为空。tools/list 与 resources 面是两个机器可读注册表。

## 7. dnSpy UI：MCP Edit Explorer

View 菜单的只读浏览窗口：当前事务、暂存操作、diff、风险、检查点谱系树与逐检查点详情（含空闲历史浏览）；本地取消面向当前活动事务（属主在线即可，操作/提交执行期间除外；孤儿事务无条件可取消），走等价回滚路径——UI 不提供任何提交/恢复入口，不绕 MCP 门禁。

### 资源路径导入导出补充

`edit_resource_import` 仅从 `AllowedSampleRoot` 内的非 reparse 普通文件读取，容量在读取分配前检查；返回的 `file_id`、长度和 SHA-256 来自同一个 Windows 文件句柄。`resource_type` 可选 `embedded`、`linked` 或 `win32`；`linked` 导入读取后转为内嵌字节，不保留运行时外部文件依赖。

`edit_resource_export` 的默认类型为 `embedded`；Win32 行需指定 `resource_type=win32`，以 `type_id` 或 `type_name`（默认 `RCDATA`）、`name_id` 或 `resource_name`、`lang_id`（默认 0）定位。`type_id` 与 `type_name` 互斥，提供 `name_id` 时它优先于 `resource_name`。输出复用检查点存储的原子写入和真实文件身份，目标必须在 `ArtifactRoot` 下，不能覆盖源样本。
