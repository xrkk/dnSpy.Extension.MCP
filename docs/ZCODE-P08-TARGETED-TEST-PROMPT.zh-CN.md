# 第三方 AI：P08 资源大载荷与强名称定向验收提示词

## 目标

针对 P08（资源大载荷与强名称）做定向回归验收，退出判据＝全部断言通过且无遗留活动事务/进程。

## 前置

- dnSpy + MCP 插件已部署，loopback 健康检查通过（`http://localhost:<port>/health`）。
- `tools/list` 包含本阶段新工具（以注册表快照核对）。
- 阶段 fixtures 已就位。

## 步骤

1. managed/win32 资源 CRUD+图标组断链拒绝+CON-019 哨兵；VM 路径导入导出身份；`strong_name_remove` 当前因无可信且绑定目标的因果证据源而恒拒绝，必须保存拒绝来源/失败模块绑定证据并将 ACC016 记 BLOCKED，不得声称成功去强名称。这只是当前实现限制，不改写正式验收要求：获得合法证据来源后仍须证明“来源事件→目标模块”绑定、真实成功去强名称及该证据一次消费；三项未完成前 ACC016 不得判 PASS。
2. 每个失败样本必须断言稳定错误码/状态/恢复建议三元组。
3. 结束时清理：回滚或提交全部事务，终止全部调试会话，`edit_status` 必须 idle。

## 证据

- 每步的请求/响应转录（request_id、错误码）。
- 前后指纹（拒绝类断言必须证明零副作用）。
- 文件清单（资源/导出类断言）。

### 资源路径导入导出补充

`edit_resource_import` 仅从 `AllowedSampleRoot` 内的非 reparse 普通文件读取，容量在读取分配前检查；返回的 `file_id`、长度和 SHA-256 来自同一个 Windows 文件句柄。`resource_type` 可选 `embedded`、`linked` 或 `win32`；`linked` 导入读取后转为内嵌字节，不保留运行时外部文件依赖。

`edit_resource_export` 的默认类型为 `embedded`；Win32 行需指定 `resource_type=win32`，以 `type_id` 或 `type_name`（默认 `RCDATA`）、`name_id` 或 `resource_name`、`lang_id`（默认 0）定位。`type_id` 与 `type_name` 互斥，提供 `name_id` 时它优先于 `resource_name`。输出复用检查点存储的原子写入和真实文件身份，目标必须在 `ArtifactRoot` 下，不能覆盖源样本。

新增回归：在 x64/x86 分别验证 linked 路径导入、Win32 数字/文本标识及语言导出；根外路径、目录、reparse、超限文件拒绝且 revision 不变；重复读取同一文件的 file_id 稳定且匹配句柄观测；已有输出在写入失败时旧 SHA 不变，且无残留临时文件。Windows 不可用的项必须标注阻断，不能用 Linux 逻辑探针代替。
