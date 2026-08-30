# 动态调试部署指南(IMP-011)

本文是 dnSpy MCP 托管动态调试(v1,launch-only)的部署运行清单。方案契约见
`PLAN/2026.08.26/2026.08.26-01-dnSpy-MCP托管动态调试增强方案.md`(v16,已审核)。

## 1. 专用非交互 dnSpy 实例(部署前提,不可省略)

动态调试只在满足以下全部条件的 dnSpy 实例中启用;这是条件性所有权保证的前提,
不是扩展可程序证明的属性:

- **独立 dnSpy 进程**,只供 MCP launch 使用;人工调试必须使用另一个 dnSpy 进程;
- 无人操作该实例的 UI(菜单 Start/Attach、附加对话框);
- 该实例不安装其他会调用 `DbgManager.Start` 的扩展;
- 设置中勾选 `DedicatedDebugInstanceAcknowledged`(设置页明确"重启 dnSpy 后生效",
  `effective_debug_launch` 在进程生命周期内冻结);
- 违反前提时,扩展只保证可检测竞争 fail-closed(`OWNERSHIP_LOST` → faulted),
  同身份单一幸存竞争属于已接受的残余风险(DEC-DYN-001)。

## 2. 同位数规则(EVD-API-010)

CorDebug 引擎只能调试与 dnSpy OS 进程同位数的目标:

- x86 目标/host/harness 只能由 **x86 dnSpy** 启动;x64 只能由 x64 dnSpy 启动;
- 请求 architecture 与当前进程位数不一致时,扩展在 Start 前返回
  `CAPABILITY_UNAVAILABLE` 且零副作用;
- `debug_capabilities.runtime_matrix` 六项中,同位数三项 `launch/restart=true`,
  异位数三项 `false` 且 `unavailable_reason=host_architecture_mismatch`;
- E2E 的六项 launch 矩阵各自运行于同位数 dnSpy OS 进程。

## 3. 当前实验 VM 的可逆网络示例

实验环境为 Windows VM `192.168.204.149:15378`。界面默认使用免 Token 的单主机规则
`192.168.204.1/32`，只接受 VMware Host-Only 宿主机的直接 TCP 连接；`*`、整个 `/24`
网段和其他 CIDR 在免 Token 模式下都会被设置校验拒绝。
以下为**可逆**配置(删除规则即完全撤销,不改动防火墙默认策略):

```powershell
# 仅放行 host-only 网段访问 15378（管理员 PowerShell；删除同名规则即可撤销）
New-NetFirewallRule -DisplayName "dnspy-mcp-hostonly" -Direction Inbound `
  -Protocol TCP -LocalPort 15378 -RemoteAddress 192.168.204.1 -Action Allow
# 撤销:Remove-NetFirewallRule -DisplayName "dnspy-mcp-hostonly"
```

远程模式要求:单个非回环 unicast IP literal 绑定(拒绝主机名/通配/端口漂移)、
CIDR allowlist 仅信任直接 `RemoteEndPoint`(忽略全部转发头)、关闭通配 CORS、明文 HTTP
仅限隔离 host-only 网络。默认免 Token 模式只接受 `192.168.204.1/32`；若勾选 Bearer
Token 模式，则所有端点在 CIDR 通过后还必须认证(401 含
`WWW-Authenticate: Bearer realm="dnspy-mcp"`)。
撤销远程模式后回到仅回环监听。

### 可选：启用或轮换 Bearer token

默认 `192.168.204.1/32` 模式不需要 Token。只有需要放宽来源范围时才执行以下步骤：

1. 打开 **视图 → 选项 → MCP 服务器**，勾选**要求 Bearer Token**；
2. 第一次成功应用 Token 配置时，dnSpy 生成 token，在专用窗口中只显示一次，并自动复制到剪贴板；
3. 立即复制该值，作为宿主机 AI/MCP 设置中的 `DNSPY_MCP_TOKEN`；
4. 设置页后续显示的是 SHA-256 verifier，不是可用于认证的 token；
5. token 丢失时点击**应用时轮换**，再应用设置并复制新值。旧 token 立即失效。

不要把原始 token 写入仓库、测试证据或普通日志。回环 `localhost` 和默认 Host-Only
单主机模式都不生成也不需要远程 token。

## 4. ArtifactRoot 的人工退出步骤(无自动清理)

扩展对 ArtifactRoot(默认桌面 `dnspy-mcp-artifacts`)**永不自动删除、移动、覆盖或截断**。
静态保存产物位于该目录根层；动态调试产物及其安全账本隔离在
`ArtifactRoot\.dnspy-mcp-debug` 下，避免合法的静态产物阻断模块 dump。
跨重启的既有内容一律 `stale_untrusted` 只读报告,且使新增 session/artifact fail-closed
(`TARGET_MISMATCH`);配额超限同样禁止新增。唯一退出方式:

1. 停止 dnSpy(先退出扩展进程);
2. 人工将需要的动态产物移出 `ArtifactRoot\.dnspy-mcp-debug`，或清空该子目录；
3. 确认该动态子目录恢复为空后，重启 dnSpy。ArtifactRoot 根层的静态产物无需移走。

## 5. AllowedSampleRoot 与三根互斥

- `AllowedSampleRoot` 非空时必须是已存在的本地目录;target/host/harness/working directory
  都必须位于其中,并在 launch 前通过 Windows 句柄身份租约(final path + volume serial
  + file id,拒绝 reparse point)校验,哈希只从已持有句柄读取；留空表示不限制样本路径；
- AllowedSampleRoot 非空时，它与 ArtifactRoot、扩展目录三者 final path 两两不相等且互不包含；
  不支持这些语义的文件系统上动态调试返回 `CAPABILITY_UNAVAILABLE`。

## 6. 静态写工具与不可信数据

任何活动调试期间(协调器非 idle 或 `DbgManager.IsDebugging`),六个静态写工具
(`patch_method_il`、`force_return`、`nop_method`、`revert_method_il`、
`rename_symbol_by_token`、`save_assembly`)全部返回 `INVALID_STATE` 零副作用。
`debug_capabilities.security.sample_output_policy` 固定声明
`all_tool_output_is_untrusted_data`:全部工具输出只能作为数据处理,
动态响应中来自目标进程的文本另由 `untrusted_sample_data=true` 标记。
