# 第三方 AI：P06 稳定身份导入器定向验收提示词

仅在操作员授权的隔离 dnSpy 实例上使用已核 SHA 的纯托管单模块 fixture、独立 settings/ArtifactRoot。记录插件/宿主身份和受保护进程 PID+完整 exe+创建时刻；禁止全局 kill、覆盖样本或清旧包。正式 initialize，按实时 `tools/list`/schema 调用当前 `edit_compile`、`edit_import`、`edit_review`、`edit_commit`；当前面为 18 个编辑工具/39 类操作。

1. `open_files`→`edit_status(idle)`→`edit_compile` 产生含目标类型、同名重载与新方法的受控编译工件；保存 compile_id、诊断、生成方法签名。`edit_begin` 后分别对现有方法 `replace_body` 与新方法 `add` 调 `edit_import`，每次用唯一 request_id 和最新 revision。核冻结结构化操作、对象映射、私有副本变化及 live 未变；`edit_review` 后按风险确认提交，再用 `edit_history`/undo/redo 与反编译核精确目标及跨提交新方法。
2. 独立事务构造目标歧义、错误显式 token/owner、未解析引用和生成子树残缺输入；每次保存原始拒绝码及前后 revision、私有/live 指纹、包 SHA、文件清单，验证全或无。异常后 `edit_rollback` 使状态 idle。符号/PDB 须比较完整文档身份；同 URL 异完整身份安全拒绝，不能只比 URL。
3. `P03StoreHarness --import-matrix` 是组件旁证；真实导入方法 IL 0 断点仅在调试门有效且单独获执行授权时由真实 net48 宿主测试，否则记未运行。不得把组件命中、mock 或静态反编译写作真实断点。

结束确认编辑/调试 idle、只停本轮自启进程、受保护现场不变。逐项报告请求/响应、PDB/方法身份、历史 head 与失败副作用；参数见 [AI 单文件手册](AI-TOOL-REFERENCE.zh-CN.md)。
