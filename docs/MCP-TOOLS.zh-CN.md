# dnSpy MCP — 工具完整说明（中文）

与线上 `tools/list` 注册表机检一致：双功能门启用时生产面 72 个（静态 32 + 调试 22 + 编辑 18）；`DNMCP_TEST=1` 验收进程另通告 6 个 `debug_test_*` 探针，形成 78 工具快照，9 个可调用 `edit_test_*` 测试缝仍不通告。调试门关闭时只保留 `debug_capabilities`，对应生产 51 / 验收 57 个。先用 `debug_capabilities` 与实时注册表辨认 profile；计数来自带明确 profile/门状态参数的 `tools/export_tool_registry.py`。

另见：[README.zh-CN.md](../README.zh-CN.md) · [English reference](MCP-TOOLS.md)

## 1. 线协议

- 传输：Streamable HTTP（含 legacy SSE），`http://localhost:<端口>/mcp`（默认 15378，Options 页显示实际端口）。
- 信封：事务编辑与调试工具返回结构化的 `schema_version`/`ok`/`state`；编辑失败携带 `error.code`、`error.message`、`error.current_state`、`error.recovery`。静态分析/生成工具通常返回文本，不能把统一信封强加给它们；有通告的 `outputSchema` 以实时定义为准。
- 会话：编辑/调试事务由单个已初始化 MCP 会话拥有；`request_id` 提供幂等重试。

## 2. 静态分析与代码生成（32 个工具）

open_files · list_assemblies · get_assembly_info · list_types · search_types · get_type_info · list_methods · search_members · get_method_il · get_type_fields · get_type_property · list_string_constants · search_string_literals · search_constants · decompile_by_token · decompile_method · decompile_type · find_by_attribute · find_callees · find_callers · find_overrides · find_path_to_type · find_references · find_unity_messages · generate_harmony_patch · generate_bepinex_plugin · force_return · nop_method · patch_method_il · revert_method_il · rename_symbol_by_token · save_assembly —— 逐工具参数及返回结构见[单文件 AI 工具手册](AI-TOOL-REFERENCE.zh-CN.md)。

末尾六个旧写工具只是通往结构化编辑协调器的兼容入口。修改会提交检查点；`revert_method_il` 只能撤销匹配的当前历史头 IL 编辑，无匹配时返回 `EDIT_HISTORY_CONFLICT`。`save_assembly` 仅在 ArtifactRoot 下导出精确检查点，不覆盖源样本，也不创建原地备份。这些工具不声明静态 outputSchema，但编辑域拒绝仍返回 code/state/recovery。

## 3. 仅启动式动态调试（生产面 22 个；验收模式 28 个）

debug_capabilities · debug_status · debug_launch · debug_pause · debug_continue · debug_restart · debug_terminate · debug_read_events · debug_wait_event · debug_set_breakpoint · debug_list_breakpoints · debug_set_breakpoint_enabled · debug_remove_breakpoint · debug_list_threads · debug_get_stack · debug_step · debug_get_locals · debug_expand_value · debug_list_modules · debug_read_memory · debug_dump_module · debug_set_exception_policy。验收模式另通告 `debug_test_spy` · `debug_test_flood` · `debug_test_start` · `debug_test_dump` · `debug_test_clock` · `debug_test_adapter`。启动是唯一执行门禁：静态工具绝不运行样本代码。

## 4. 事务式结构化编辑（通告 18 个）

### 4.1 生命周期

| 工具 | 用途 |
| --- | --- |
| `edit_begin` | 为一个已加载纯托管单模块程序集取得进程级编辑租约；创建私有副本 |
| `edit_status` | 不改变事务地查询状态/修订/指纹/容量/风险 |
| `edit_apply` | 向私有副本应用 **39** 类操作之一（必须携带 `request_id` 与 `expected_revision`） |
| `edit_review` | 审查固定修订；规范 diff + 必需风险确认 |
| `edit_commit` | 线性化到实时模块 + 持久化检查点（单步可恢复） |
| `edit_rollback` | 丢弃私有副本；释放租约 |
| `edit_history` / `edit_undo` / `edit_redo` / `edit_restore` | 浏览/导航持久检查点谱系 |
| `edit_export` | 在 ArtifactRoot 下导出精确检查点 |
| `edit_recover` / `edit_accept_live` | 解决部分提交恢复；接纳 UI 已偏离模块为新基线 |

### 4.2 编译 → 导入

| 工具 | 用途 |
| --- | --- |
| `edit_compile` | 经 dnSpy 公开 Roslyn 编译器编译 C#；每个 `documents` 条目字段闭集为 `path` 与 `content`（程序集 + Portable PDB 留在内存；无 analyzer/generator/脚本面） |
| `edit_import` | 把编译产物成员以冻结操作导入私有副本——结构化签名匹配、生成子树整体处理、全或无拒绝；符号行随体移植；保存镜像仅内嵌 PDB |
| `edit_impact_scan` | 已加载模块范围的跨程序集影响（scope=loaded_modules）；入站引用转为需确认风险 |

### 4.3 身份/资源（edit_apply 种类）

- `assembly_update`、`module_update`、`assembly_ref_update`、`entry_point_set`
- `managed_resource_add/update/remove`、`win32_resource_add/update/remove`
- `strong_name_remove` 要求存活、留存、一次消费且绑定目标程序集的 CLR loader 强名称拒绝事件；门控可授权匹配证据，但真实可信来源的成功路径和一次消费仍未通过 ACC016 验收。

### 4.4 39 类操作清单

`type_add` · `type_update` · `type_remove` · `method_add` · `method_update` · `method_remove` · `field_add` · `field_update` · `field_remove` · `property_add` · `property_update` · `property_remove` · `event_add` · `event_update` · `event_remove` · `parameter_add` · `parameter_update` · `parameter_remove` · `generic_parameter_add` · `generic_parameter_update` · `generic_parameter_remove` · `method_body_replace` · `attribute_add` · `attribute_remove` · `security_add` · `security_remove` · `assembly_update` · `module_update` · `assembly_ref_update` · `entry_point_set` · `managed_resource_add` · `managed_resource_update` · `managed_resource_remove` · `win32_resource_add` · `win32_resource_update` · `win32_resource_remove` · `strong_name_remove` · `interface_add` · `reference_add`

操作 schema、`EditWire.OperationKinds` 和当前 `edit_apply` 注册描述均为 39 类；可接受种类以实时 inputSchema 为准。

## 5. 错误码与恢复（冻结）

`EditWire.Message` / `EditWire.Recovery` 为事实来源；稳定集合含 EDIT_TRANSACTION_BUSY、EDIT_TRANSACTION_NOT_FOUND、EDIT_OWNER_REQUIRED/MISMATCH、EDIT_REVISION_CONFLICT、EDIT_LIVE_MODULE_CONFLICT、EDIT_REVIEW_STALE、EDIT_VALIDATION_FAILED、EDIT_RISK_CONFIRMATION_REQUIRED、EDIT_CAPABILITY_UNAVAILABLE、EDIT_CAPACITY_EXCEEDED、EDIT_DEBUG_NOT_IDLE、EDIT_LIVE_STATE_UNKNOWN、EDIT_CHECKPOINT_INVALID/COMMIT_FAILED/CLEANUP_FAILED、EDIT_EXPORT_BLOCKED、EDIT_REPLAY_CONFIRMATION_REQUIRED/UNVERIFIED、EDIT_OPERATION_VERSION_UNSUPPORTED、EDIT_HISTORY_CONFLICT、EDIT_BRANCH_SELECTION_REQUIRED、EDIT_LINEAGE_DIVERGED、EDIT_SOURCE_IDENTITY_CONFLICT、EDIT_RECOVERY_NOT_FOUND、REQUEST_ID_REUSE。

语法有效但版本未知的操作返回 `EDIT_OPERATION_VERSION_UNSUPPORTED`；畸形输入仍属于协议/schema 无效。v1 检查点只允许 exact；实时模块漂移后须显式接纳为新的 v2 谱系。v2 区分 `exact`、`validated_drift`、`unverified_drift`，迁移仍须明确确认。

PDB 文档以归一化的完整元数据键识别；无法表示的同 URL 异身份在私有写入前安全拒绝。缺少新版文档释放信息的旧包仍可读取，但旧方法/PDB 逆操作在 Undo 时可能冲突，不提供隐式包迁移。这是兼容边界，不能把 T057–T059 的定向回归写成全 ACC 通过。

## 6. 资源面

MCP resources 面通告 14 个具体资源（程序集列表、类型索引、编辑状态、调试事件……）；`resources/templates/list` 有意为空。tools/list 与 resources 面是两个机器可读注册表。

## 7. dnSpy UI：MCP Edit Explorer

View 菜单的只读浏览窗口：当前事务、暂存操作、diff、风险、检查点谱系树与逐检查点详情（含空闲历史浏览）；本地取消面向当前活动事务（属主在线即可，操作/提交执行期间除外；孤儿事务同样受操作/提交忙态守卫约束），走等价回滚路径——UI 不提供任何提交/恢复入口，不绕 MCP 门禁。

### 资源路径导入导出补充

`edit_resource_import` 仅从 `AllowedSampleRoot` 内的非 reparse 普通文件读取，容量在读取分配前检查；返回的 `file_id`、长度和 SHA-256 来自同一个 Windows 文件句柄。`resource_type` 可选 `embedded`、`linked` 或 `win32`；`linked` 导入读取后转为内嵌字节，不保留运行时外部文件依赖。

`edit_resource_export` 的默认类型为 `embedded`；Win32 行需指定 `resource_type=win32`，以 `type_id` 或 `type_name`（默认 `RCDATA`）、`name_id` 或 `resource_name`、`lang_id`（默认 0）定位。`type_id` 与 `type_name` 互斥，提供 `name_id` 时它优先于 `resource_name`。输出复用检查点存储的原子写入和真实文件身份，目标必须在 `ArtifactRoot` 下，不能覆盖源样本。

响应契约区分单项应用、批量导入和影响扫描：`edit_import.result` 包含 `import` 与 `operation_count`；`edit_impact_scan.result` 包含 `transaction` 与 `impact`；资源工具返回各自的 `import`/`export` 文件身份；`edit_commit.result.live_recovery` 绑定预生成逆计划及检查点。这些字段均由各工具的 outputSchema 声明。
