# 第三方 AI：P07 程序集身份与入口点定向验收提示词

仅对操作员授权的隔离实例和可丢弃纯托管单模块 EXE/引用者 fixture 操作，先核插件/fixture SHA、独立 settings/ArtifactRoot、受保护 PID+完整 exe+创建时刻。正式 initialize 并取实时 `tools/list`/schema；当前 `edit_apply` 支持 39 类操作。禁止覆盖源样本、全局 kill 或把 `edit_impact_scan` 当作全磁盘引用扫描。

1. `open_files` 加载目标和已知引用者，记程序集名/版本、模块名、AssemblyRef、入口 token、live 指纹与文件清单。分别在新事务对 `assembly_update`、`module_update`、`assembly_ref_update`、`entry_point_set` 执行 apply→review→rollback，核 diff 与实时图未变；再选择受控正例 review→按风险 ID 确认 commit→`edit_history`/undo/redo→`edit_export` 到全新路径，独立重载核身份及入口 token。
2. 在暂存身份变更后调用 `edit_impact_scan(scope=loaded_modules)`，核返回实际加载模块列表、入站引用、风险 ID；确认后提交响应须回显确认。缺失的外部磁盘模块不能计为已扫描。结构性错误、错误引用目标、过期 review/修订应稳定拒绝且前后 revision/live/包/文件一致，确认风险不能绕过结构验证。
3. 改后入口实际运行只在执行门与调试门有效、另有隔离执行授权时，以真实 net48 宿主 `debug_launch`→事件/入口位置验证→`debug_terminate`；否则标未执行，不以静态入口 token 或组件模拟替代。

最后回滚活动事务并确认双 coordinator idle，只清理本轮对象，受保护现场不变。报告原始请求/响应、scope/风险/确认回报、重载身份、入口实测或未执行原因；参数见 [AI 单文件手册](AI-TOOL-REFERENCE.zh-CN.md)。
