# 第三方 AI：P03 检查点、历史导航与恢复定向验收提示词

仅对操作员授权的隔离 dnSpy 实例和可丢弃纯托管单模块样本执行。先记录插件 SHA、宿主架构、MCP URL、独立 settings/ArtifactRoot、样本 SHA、受保护 PID/完整 exe 路径/创建时刻；只按这三个进程身份停止本轮自启对象，禁止按名称全局 kill、删除旧包或覆盖原样本。使用正式初始化会话和实时 `tools/list`/inputSchema；当前生产注册表为 18 个 `edit_*`、39 类操作，调试门启用与否由 `debug_capabilities` 决定。ZCode 若不提供 resources，由支持 resources 的宿主另验，不能冒充本轮已读。

## 可执行流程

1. `open_files` 加载已核 SHA 的样本，`edit_status` 确认 idle；记录 live 方法 IL、源身份、ArtifactRoot 文件清单。`edit_begin`→`edit_apply`（安全的 `method_body_replace` 或 `type_add`，唯一 request_id/最新 expected_revision）→`edit_review`；核私有变更与 live 未变，先用 `edit_rollback` 完成撤销支路。
2. 新事务中 `edit_compile`→`edit_import` 一个新方法，审查风险、按 review 给出的确认 ID `edit_commit`；记录 lineage/checkpoint、方法体和符号/PDB 身份。随后 `edit_history`→`edit_undo`→`edit_redo`→`edit_restore(action=assess)`，逐次核预期 head、live 指纹/镜像与实际方法体；`edit_export` 只能导出 exact 检查点到全新 ArtifactRoot 路径，读回校验身份和 PDB。导出/重载后的断点实测仅在调试门启用且另获执行授权时运行，否则明确记未执行，不能用静态反编译替代。
3. 在**本轮自有测试模式实例**中用 `edit_test_storage_fault(action=arm, stage=navigate_forward)` 注入一次合法导航晚期失败；原始 `edit_undo` 错误应为 `EDIT_CHECKPOINT_COMMIT_FAILED`，核失败前后 live 指纹、head、包 SHA/文件清单相同、状态 idle，再无注入重试成功。`edit_recover` 仅对真实部分提交且响应广告的恢复动作使用；不得为了凑成功路径在共享实例制造故障。
4. 对 v1 exact-only 与显式接纳新 v2 谱系分别保留版本/漂移原始响应；过期 checkpoint ID 应 `EDIT_HISTORY_CONFLICT` 且零副作用。旧 PDB 包逆操作冲突及同 URL 不同完整文档身份须按当前安全拒绝契约记录原始错误/前后状态，不把拒绝写成导入成功。

结束时回滚未提交事务、终止本轮自启 debuggee，确认编辑/调试 idle；只清理本轮全新产物，原包与受保护进程保持不变。输出每步请求/响应、修订、指纹/head、包与导出 SHA、非空 PDB 身份及失败副作用差异；将真实 UI/宿主、测试缝和静态检查分栏，任何未执行项不得判 PASS。接口参数与返回结构见 [AI 单文件手册](AI-TOOL-REFERENCE.zh-CN.md) 和实时 schema。
