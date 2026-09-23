# 第三方 AI：P08 资源大载荷与强名称定向验收提示词

## 目标

针对 P08（资源大载荷与强名称）做定向回归。强名称真实可信来源成功路径仍未验收，故本轮可交付资源与强名称安全拒绝证据，不能将 P08/ACC016 整体判通过。

## 前置

- 操作员授权的隔离 dnSpy 实例和可丢弃 fixture 已就位；核插件/样本 SHA、独立 settings/AllowedSampleRoot/ArtifactRoot、宿主架构及受保护 PID+完整 exe+创建时刻。只停本轮自启对象，禁止全局 kill、清旧包或覆盖源样本。
- 正式 initialize，读取实时 `tools/list` 和资源工具 input/outputSchema；当前生产面 18 个编辑工具、39 类操作。ZCode 若不暴露 resources，14 URI 由支持 resources 的宿主另验，不计本轮已读。

## 步骤

1. managed/win32 资源 CRUD+图标组断链拒绝+CON-019 哨兵；VM 路径导入导出身份；`strong_name_remove` 对缺失、不匹配或过期的可信证据应安全拒绝，匹配的存活 CLR loader 强名称拒绝事件可通过一次消费门控；必须保存拒绝来源/失败模块绑定证据并将 ACC016 记 BLOCKED，不得声称真实成功去强名称已验收。正式验收仍须证明“来源事件→目标模块”绑定、真实成功去强名称及该证据一次消费；三项未完成前 ACC016 不得判 PASS。
2. 对受控普通资源文件先核大小/路径/SHA，`edit_begin`→`edit_resource_import`（记录 `file_id`/长度/SHA）→`edit_review`→确认风险后 `edit_commit`→`edit_resource_export` 到 ArtifactRoot 下全新路径，独立读回 SHA；另做 managed/win32 CRUD、linked 归一、数字/文本 type/name 与语言定位。错误目标、根外、目录、reparse、超限及已有输出失败各记录前后 revision、live 指纹、包/输出 SHA；不得用工具启动失败代替业务拒绝。
3. 强名称仅在已授权隔离样本上验证缺失/无效证据时 `strong_name_remove` 安全拒绝，记录来源事件缺失、目标模块绑定和零副作用；可信动态验证来源、一次消费和真实成功去强名称均记未验证/阻断，不把测试缝 `edit_test_strong_name` 或净构建当作正例。
4. 结束时回滚活动事务，确认编辑/调试 idle；只清理本轮自有产物/进程，受保护现场不变。

## 证据

- 每步的请求/响应转录（request_id、错误码）。
- 前后指纹（拒绝类断言必须证明零副作用）。
- 文件清单（资源/导出类断言）。
- 每条标真实宿主/测试缝/静态检查及未执行项；接口字段见 [AI 单文件手册](AI-TOOL-REFERENCE.zh-CN.md)。

### 资源路径导入导出补充

`edit_resource_import` 仅从 `AllowedSampleRoot` 内的非 reparse 普通文件读取，容量在读取分配前检查；返回的 `file_id`、长度和 SHA-256 来自同一个 Windows 文件句柄。`resource_type` 可选 `embedded`、`linked` 或 `win32`；`linked` 导入读取后转为内嵌字节，不保留运行时外部文件依赖。

`edit_resource_export` 的默认类型为 `embedded`；Win32 行需指定 `resource_type=win32`，以 `type_id` 或 `type_name`（默认 `RCDATA`）、`name_id` 或 `resource_name`、`lang_id`（默认 0）定位。`type_id` 与 `type_name` 互斥，提供 `name_id` 时它优先于 `resource_name`。输出复用检查点存储的原子写入和真实文件身份，目标必须在 `ArtifactRoot` 下，不能覆盖源样本。

新增回归：在 x64/x86 分别验证 linked 路径导入、Win32 数字/文本标识及语言导出；根外路径、目录、reparse、超限文件拒绝且 revision 不变；重复读取同一文件的 file_id 稳定且匹配句柄观测；已有输出在写入失败时旧 SHA 不变，且无残留临时文件。Windows 不可用的项必须标注阻断，不能用 Linux 逻辑探针代替。
