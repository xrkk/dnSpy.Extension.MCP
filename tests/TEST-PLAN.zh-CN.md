# dnSpy MCP 扩展测试方案

- 对象:`dnSpy.Extension.MCP`(含动态调试增强,方案 v16)
- 构建产物:`dist/dnSpy.Extension.MCP-net48.x.dll`、`dist/dnSpy.Extension.MCP-net10.0-windows.x.dll`
- 权威构建:CI(`verify.yml` → `build.yml`);**无 CI 环境用 `tests/run-verify-local.sh` 单机等价入口**(依赖守卫为 pwsh 版的 Python 等价)
- 方案契约:ACC-001..036(§6),运行规程见 `docs/deployment-dynamic-debugging.zh-CN.md`

## 分层总览

| 层 | 名称 | 环境 | 已执行 | 覆盖 |
|---|---|---|---|---|
| L0 | 契约验证 | Linux/CI | ✅ 189 例全绿 | schema/字节清单/fixtures |
| L1 | 组件 harness | Linux | ✅ 15 套全绿 | 纯逻辑组件 |
| L2 | HTTP 传输冒烟 | Linux | ✅ 35/35 | 回环+远程模式 wire 行为 |
| L3 | 进程内集成 | Windows VM | ✅ 实机 UI 查看/修改/持久化/恢复 | 装载/设置事务/工具广告 |
| L4 | 动态调试 E2E | Windows VM | ✅ 36/36、500/500 全绿 | ACC-001..036，含 x86/x64 正反矩阵 |
| L5 | 发布门禁 | CI | ✅ 已接线 | verify→build/release |

---

## L0 契约验证(已固化,CI 自动)

```bash
python tests/debug/contracts/validate.py
python -m unittest discover -s tests/python -v
```

覆盖:Draft 2020-12 schema 合法性;10 项 UTF-8 字节指针可解析;25 API args/22 result/21 EVT def 计数;fixtures 确定性再生(两次生成字节一致);189 例不变量(invalid-fields 必须结构无效、-32602 仅限 invalid-fields/byte-input、2025-06-18 text 与 structuredContent 深等、无 session 计数为 0、required_states 固定顺序且不含 current_state、EVT schema);Python 客户端 initialize/会话头/通知/工具/资源/错误/清理与 stdio 透明转发；只读 token verifier 的 WPF 绑定固定为 OneWay，防止打开设置页时因写回只读属性而异常。

## L1 组件 harness(已固化,Linux 可跑)

| harness | 断言数 | 组件 |
|---|---|---|
| snapcheck | 33 | 设置快照验证/恢复矩阵/JCS |
| transcheck | 46 | 认证/CIDR/请求体限额/准入/响应预算 |
| storecheck | 26 | ApplySnapshot 五步事务故障注入 |
| evtcheck | 23 | 事件缓冲(驱逐/改写/冻结) |
| ctlcheck | 35 | 控制记录相位/deadline + 暂停仲裁 |
| coorchek | 36 | 协调器七态状态机/观察对账 |
| imp4check | 44 | HandleStore/双 lane/request_id 缓存 |
| imp5check | 18 | gate 采样 + 控制适配器端到端 |
| launchcheck | 12+300 往返 | argv 编码器/五模式映射 |
| fidcheck | 27 | 禁用 stub/文件身份/路径关系/租约 |
| bpcheck | 25 | 断点存储身份规则 |
| valcheck | 28 | 取值快照记账 |
| artcheck | 32 | artifact 账本三态/配额/fail-closed |
| pagecheck | 19 | 分页 cursor 续读语义 |
| gatecheck | 19 | 六写工具门控 |

## L2 HTTP 传输冒烟(已固化,Linux 可跑)

`/home/adminn/tmp/smoke`(可复原):ApplySnapshot 启动→/health→initialize→tools/list(33 项)→CORS(回环保留/远程无)→1MiB+1 请求体 413 空体→malformed/非法 UTF-8 → 字节精确 -32700→8 SSE 长连接+第 9 个 429→远程 401(固定 WWW-Authenticate、无 CORS)/短 token 401/认证过 CIDR 拒绝 403/正确 token 200→远程端口被占 Apply 失败且旧监听恢复。

## L3 进程内集成(Windows,部署后手动/半自动)

前置:把对应 TFM 的 `dist/*.x.dll` 复制到 `<dnSpy>\bin\Extensions\dnSpy.Extension.MCP\`(目录名必须与 DLL 主名一致);net48 DLL 配 net48 dnSpy,net10 配 net10 dnSpy。

1. **装载**:启动 dnSpy → 日志出现 `TheExtension constructed`/`MCP Extension loaded`;`GET /health` 200。
2. **设置事务**:设置页改动 Host/Port → Apply → 一次 pending→transition→committed→swap→clear;改非法组合(远程无 CIDR)整页拒绝且零持久化;杀进程于各步骤间重启→只激活 committed(ACC-036 的进程内部分)。
3. **工具广告**:默认(未确认专用实例)tools/list 恰 33(32 静态+debug_capabilities),其余 debug_* 不广告;直接调用 debug_attach → 固定 CAPABILITY_UNAVAILABLE(无副作用)。
4. **写工具门控**:dnSpy 内手动调试任意程序(F5)→ 六写工具全部 INVALID_STATE;停止调试→恢复。
5. **静态回归**:32 个静态工具冒烟(对照 `tests/snapshots/static-tools.baseline.json`)。
6. **实机 UI 配置回归**:通过“视图 → 选项 → MCP 服务器”确认中文页面无绑定异常且可回读全部字段；经 UI 修改端口，验证新端口 health=200、旧端口不监听，退出并重启确认快照已持久化；再经 UI 恢复目标配置，验证 health 与持久化快照一致。2026-08-30 首轮实测证据：`ui-config-real-machine.json`（与全量 ACC 结果同目录）；默认端口迁移后使用 15378/15379。

## L4 动态调试 E2E(Windows VM,ACC-001..036)

**环境准备/复跑**:
```powershell
# VM(192.168.204.149)内,管理员 PowerShell——可逆防火墙
New-NetFirewallRule -DisplayName "dnspy-mcp-hostonly" -Direction Inbound `
  -Protocol TCP -LocalPort 15100 -RemoteAddress 192.168.204.0/24 -Action Allow
```
- 按部署文档第 1 节启动**专用非交互 dnSpy 实例**并确认 `DedicatedDebugInstanceAcknowledged`;
- 准备 `AllowedSampleRoot`(如 `C:\samples`)与空 `ArtifactRoot`;
- 构建六项 fixture:`tests\debug\fixtures\build-launch-fixtures.ps1`;
- E2E driver:`powershell -NoProfile -ExecutionPolicy Bypass -File tests\debug\run-debug-tests.ps1 -Case ACC-xxx`。所有 HTTP/MCP wire 操作统一经 `dnspy_mcp` Python 客户端；PowerShell 只负责 Windows fixture、dnSpy 生命周期和证据编排。

**ACC 分组与关键判据**(完整前置/命令/精确预期见方案 §6):

| 组 | ACC | 关键判据 |
|---|---|---|
| 静态兼容 | 001 | 32 工具 schema/语义与快照逐字段相等;调试关闭时不可见 |
| 能力/门禁 | 002,009,024,036 | capabilities 常量;三禁用 API 三版本不广告、CAPABILITY_UNAVAILABLE 零副作用;不支持目标 TYPE-DYN-019 交接;专用实例进程隔离 |
| 传输安全 | 003,004,023 | 全端点 401/403 空体无 CORS;429/413;撤销远程后仅回环 |
| 事件/句柄 | 005,006 | 游标单调/丢失报告;恢复后旧句柄 STALE_HANDLE |
| 生命周期 | 007,008,025..030,034 | net48/CoreCLR 六矩阵 launch;重启/终态不经过 idle;所有权丢失 faulted;TOCTOU 租约;harness 建权;局部 restart |
| 断点/步进 | 010..014,031,035 | 强/弱身份;非法 offset 拒绝;异常不污染全局;三步进;enable/disable;同 MVID 消歧 |
| 取值 | 015,016 | 无目标代码执行(行为验证);预算与截断 |
| 产物 | 017..019,032 | 模块身份;内存零填充;配额/零删除/跨重启 stale;FB-001 三分支 |
| 门控/不可信 | 020,021 | 活动调试阻断六写工具;untrusted_sample_data 标记 |
| CI 门禁 | 022 | verify.yml 成为 build/release 硬依赖(已接线,e2e leg 待 driver) |
| 协议 | 027,028 | transport 重连;schema/状态矩阵/幂等 fixture 对账 |

**退出判据**:36 项 ACC 全通过→方案完整落地;任一项失败→修复后回归该组与 L0-L3。

2026-08-30 最新 Python 客户端驱动全量结果为 36/36 case、500/500 assertion 通过。ACC-029 在真实 x64 dnSpy 与真实 x86 dnSpy 上分别完成 net48 生命周期，并覆盖 x86/x64 CoreCLR、x86 harness 以及两种交叉架构拒绝路径；结果目录：`C:\Tools\mcp-pyclient-a4b6af0\tests\debug\results\a4b6af07b2d9e6728e72df91ff4a17fdf71481c4`。

## 无 CI 的单机等价入口(L0+L1+依赖守卫+双 TFM 构建,一键)

CI 不可用时,verify.yml 的 contracts+build 两个 job 在本机等价执行:

```bash
# 一次性:准备 dnSpyEx v6.6.0 检出(含子模块)+ jsonschema venv(脚本自动创建)
DNSPY_DIR=/path/to/dnSpyEx tests/run-verify-local.sh
```

输出 = 契约 189 例 + 32 工具快照断言 + net48 依赖守卫(`tests/check-host-deps.py`,
pwsh 版的 Python 等价)+ 双 TFM Release 构建,并把产物写入 `dist/`(附 SHA-256)。
已在本机验证全绿;e2e job 的单机等价即 L3/L4(需 Windows)。

## L5 发布门禁(已接线)

`build.yml`/`release.yml` 的 packaging 均 `needs: verify`(contracts+build 必须绿,e2e 在 driver 存在时同样硬门禁)。

---

## 已知边界(如实声明)

- 本机为 Linux:`dist/` 产物在每次部署前都经 VM 实跑验证(第 34-61 轮),部署链含 SHA-256 比对;
- L4 已解除阻断(第 34 轮起):`tests/debug/run-debug-tests.ps1 -Case ACC-xxx` 36 案全部机械化实跑;
- CI 层:`.github/workflows/verify.yml` 的 harness job 以 `-VerifyHarness` 做驱动完整性硬门禁，e2e job 在专用 Windows runner 上强制执行 ACC-001..036，任一跳过或失败都会使发布门禁失败;
- 每轮测试捕获的产品缺陷均已当场修复并回归(见实施记录各轮);每案 result.json 证据与 dnspy.debug.test.v1 形状门禁自第 61 轮起实装。
