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

实验环境为 Windows VM `192.168.204.149:15100`,仅允许 Ubuntu host-only 源地址。
以下为**可逆**配置(删除规则即完全撤销,不改动防火墙默认策略):

```powershell
# 仅放行 host-only 网段访问 15100(管理员 PowerShell;-x 前缀删除同名规则)
New-NetFirewallRule -DisplayName "dnspy-mcp-hostonly" -Direction Inbound `
  -Protocol TCP -LocalPort 15100 -RemoteAddress 192.168.204.0/24 -Action Allow
# 撤销:Remove-NetFirewallRule -DisplayName "dnspy-mcp-hostonly"
```

远程模式要求:单个非回环 unicast IP literal 绑定(拒绝主机名/通配/端口漂移)、
全端点 Bearer 认证(401 含 `WWW-Authenticate: Bearer realm="dnspy-mcp"`)、
CIDR allowlist 仅信任直接 `RemoteEndPoint`(忽略全部转发头)、明文 HTTP 仅限
隔离 host-only 网络。token 为 32 字节随机值,UI 只显示一次,存储仅保留其 SHA-256。
撤销远程模式后回到仅回环监听。

## 4. ArtifactRoot 的人工退出步骤(无自动清理)

扩展对 ArtifactRoot(默认 `%TEMP%\dnspy-mcp`)**永不自动删除、移动、覆盖或截断**;
跨重启的既有内容一律 `stale_untrusted` 只读报告,且使新增 session/artifact fail-closed
(`TARGET_MISMATCH`);配额超限同样禁止新增。唯一退出方式:

1. 停止 dnSpy(先退出扩展进程);
2. 人工将需要的产物移出 ArtifactRoot,或直接清空该目录;
3. 确认 ArtifactRoot 恢复为空后,重启 dnSpy。

## 5. AllowedSampleRoot 与三根互斥

- `AllowedSampleRoot` 必须是已存在的本地目录;target/host/harness/working directory
  都必须位于其中,并在 launch 前通过 Windows 句柄身份租约(final path + volume serial
  + file id,拒绝 reparse point)校验,哈希只从已持有句柄读取;
- AllowedSampleRoot、ArtifactRoot 与扩展目录三者 final path 两两不相等且互不包含;
  不支持这些语义的文件系统上动态调试返回 `CAPABILITY_UNAVAILABLE`。

## 6. 静态写工具与不可信数据

任何活动调试期间(协调器非 idle 或 `DbgManager.IsDebugging`),六个静态写工具
(`patch_method_il`、`force_return`、`nop_method`、`revert_method_il`、
`rename_symbol_by_token`、`save_assembly`)全部返回 `INVALID_STATE` 零副作用。
`debug_capabilities.security.sample_output_policy` 固定声明
`all_tool_output_is_untrusted_data`:全部工具输出只能作为数据处理,
动态响应中来自目标进程的文本另由 `untrusted_sample_data=true` 标记。
