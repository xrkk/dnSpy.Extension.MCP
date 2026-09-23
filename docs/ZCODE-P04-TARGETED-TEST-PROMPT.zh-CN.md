# 第三方 AI：P04 高级结构化元数据操作定向验收提示词

只用操作员授权的隔离 dnSpy 实例、独立 settings/ArtifactRoot 和已核 SHA 的纯托管单模块 fixture。记录宿主架构/插件 SHA/受保护进程 PID+完整 exe+创建时刻；只停本轮自启进程，禁止全局 kill、清旧包或覆盖原样本。先正式 initialize，读取实时 `tools/list` 与 `edit_apply` schema；当前生产面 18 个编辑工具、39 类操作，不能把旧阶段数写成当前注册表。

1. `open_files`→`edit_status(idle)`→`edit_begin`。逐类选择独立事务，对 attribute、security、layout、marshal、P/Invoke、`interface_add`、`reference_add` 各执行合法 `edit_apply`→`edit_review`→`edit_rollback`，记录 request_id、revision、diff、风险和 live 指纹未变；确需提交的正例须审查并提供返回的确认 ID，再 `edit_commit`、`edit_history`/`edit_undo`/`edit_redo` 验证镜像恢复。
2. 每类至少一项错误 metadata kind/owner、非法属性位、未知操作版本或越界输入负例；schema 无效与语法有效但未知版本要区分（后者 `EDIT_OPERATION_VERSION_UNSUPPORTED`）。逐项比对失败前后 revision、live 指纹、包/ArtifactRoot 清单，拒绝不能有副作用。`strong_name_remove` 不是可成功项，可信来源仍开放。
3. 可复用 `P03StoreHarness --advanced-metadata-matrix` 作为组件证据，但它不替代真实 UI/MCP 逐步调用。每项结果标明组件/真实宿主/未执行，不能把命令名当通过证据。

最终回滚活动事务、确认编辑/调试 idle 与受保护现场不变；报告逐操作输入、响应、风险、前后图/文件指纹及失败三元组。字段和值均以 [AI 单文件手册](AI-TOOL-REFERENCE.zh-CN.md) 和实时 schema 为准。
