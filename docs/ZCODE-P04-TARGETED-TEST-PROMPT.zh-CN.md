# 第三方 AI：P04 高级结构化元数据操作定向验收提示词

## 目标

针对 P04（高级结构化元数据操作）做定向回归验收，退出判据＝全部断言通过且无遗留活动事务/进程。

## 前置

- dnSpy + MCP 插件已部署，loopback 健康检查通过（`http://localhost:<port>/health`）。
- `tools/list` 包含本阶段新工具（以注册表快照核对）。
- 阶段 fixtures 已就位。

## 步骤

1. 当前 39 类操作中的 attribute/security/layout/marshal/pinvoke 种族；harness --advanced-metadata-matrix；非法属性位/版本文法拒绝。`interface_add`、`reference_add` 也必须出现在注册表/operation schema 中。
2. 每个失败样本必须断言稳定错误码/状态/恢复建议三元组。
3. 结束时清理：回滚或提交全部事务，终止全部调试会话，`edit_status` 必须 idle。

## 证据

- 每步的请求/响应转录（request_id、错误码）。
- 前后指纹（拒绝类断言必须证明零副作用）。
- 文件清单（资源/导出类断言）。
