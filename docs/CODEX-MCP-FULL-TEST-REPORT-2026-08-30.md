# Codex ↔ dnSpy MCP 修复后全功能验收报告

- 日期：2026-08-30
- 子智能体：四个全新 `gpt-5.6-terra`，推理强度 `high`（x64/x86 全流程各一个，value expansion 专项各一个）
- 链路：Codex → `/opt/dnspy-mcp-client/bin/dnspy-mcp-stdio` → `192.168.204.149:15378` → dnSpy MCP
- 测试方式：只通过已配置的 `dnspy-vm` MCP 黑盒调用；未用 curl、PowerShell 或手工 JSON-RPC 代替工具调用

## 最终结论

- x64：14/14 资源、54/54 工具 PASS，FAIL 0，BLOCKED 0。
- x86：14/14 资源、54/54 工具 PASS，FAIL 0，BLOCKED 0。
- x86/x64 均完成真实 launch、模块身份校验、断点 disable/enable/remove、命中、栈/locals、内存、step、pause/continue、restart、dump、幂等与 terminate；最终均为 `idle`。
- 修复前第三方报告的两项契约问题，以及额外发现的 resource templates 和 dump 产物冲突问题，均已修复并实机通过。
- 验收完成后 VM 已恢复运行 `C:\Tools\dnSpy\dnSpy.exe`（x64）。

## 已修复问题

1. 22 个 `debug_*` 的 inputSchema 不再发布缺失 `$defs` 的本地 `$ref`，而是自包含的扁平字段 schema；递归位置使用有界对象占位。
2. 22 个 debug outputSchema 描述实际完整 envelope：`schema_version`、`ok`、`debug_context`、`result/error`、`warnings`、`untrusted_sample_data`。
3. `list_assemblies` 的 `structuredContent` 从数组改为对象 `{ "assemblies": [...] }`，outputSchema 同步改为对象。
4. 实现 `resources/templates/list`，当前按标准返回空的 `resourceTemplates` 页面。
5. `list_assemblies.Culture` 规范化为普通字符串，避免序列化 dnlib `UTF8String` 对象。
6. 动态 dump 安全账本改放在 `ArtifactRoot\.dnspy-mcp-debug`。静态 `save_assembly` 产物可以留在 ArtifactRoot 根层，不再触发误报 `TARGET_MISMATCH`；动态账本内部的跨重启残留、防篡改、限额和不覆盖策略保持不变。

## 协议与资源

- MCP protocol：`2025-06-18`。
- tools：54（静态 32，动态 22）。
- resources：14/14 可读。
- `resources/templates/list`：PASS，返回空列表。
- server instructions：PASS，包含文档入口。
- `list_assemblies`：PASS，对象型 `structuredContent`。
- 22 个 debug schema：扁平 input 与完整 envelope output 均 PASS，无 `unknown & unknown`。

资源清单：

- `bepinex://docs/plugin-structure`
- `bepinex://docs/harmony-patching`
- `bepinex://docs/configuration`
- `bepinex://docs/common-scenarios`
- `bepinex://docs/il2cpp-guide`
- `bepinex://docs/mono-vs-il2cpp`
- `dnspy://docs/index`
- `dnspy://docs/overview`
- `dnspy://docs/static-analysis`
- `dnspy://docs/il-editing`
- `dnspy://docs/dynamic-debugging`
- `dnspy://docs/security`
- `dnspy://docs/python-client`
- `dnspy://docs/tool-workflows`

## 静态工具证据

两个子智能体均逐项调用 32 个静态工具。真实写路径覆盖：

- `force_return`、`nop_method`、`patch_method_il`：每次修改后读取 IL 验证，再 `revert_method_il` 恢复 20 条指令基线。
- `rename_symbol_by_token`：同一 `0x06000001` token 的方法更名后重新查询/反编译验证。
- `save_assembly`：分别保存为 `StaticEditFixture-e2e-final-x64.exe` 与 `StaticEditFixture-e2e-final-x86.exe`，均为 4608 bytes，未覆盖源文件。
- 源码类工具返回非空 text。按 MCP 2025-06-18，`structuredContent` 是可选项，未将纯文本源码结果误判为失败。

## x64 动态证据

- host/target：x64 / `net48-exe` x64。
- target SHA-256：`f6ccb2a1b3b7c51709eaa3088ed54c414d4733d783e472a7ff93e7e56b3e5a17`。
- 断点：`Compute` token `0x06000001`、IL offset 0，真实命中；栈顶与 locals 验证成功。
- memory：模块基址读到 `4d5a` PE 头。
- restart：generation 1 → 2。
- dump：原始 4608 bytes，artifact SHA 与目标完全一致。
- `debug_expand_value`：专项 fixture 的 payload 与 Child 两层合法 handle 均展开成功，字段值与构造代码一致。
- 幂等：同 request_id/同参数返回同一会话；同 request_id/不同参数返回 `REQUEST_ID_REUSE`。
- 终态：`idle`。

## x86 动态证据

- host/target：x86 / `net48-exe` x86，capabilities 明确 `launch=true`、`restart=true`。
- target SHA-256：`2a12e1a8b75c65826894493b71eeddb4de3e0b4ebbbd47ffad96c0b8b6a13067`。
- 断点：`Compute` token `0x06000001`、IL offset 0，真实命中；主线程与 `fixture-worker-1`、栈与 locals 均验证成功。
- memory：模块基址读到 `4d5a` PE 头。
- restart：generation 1 → 2。
- dump：artifact SHA 与 x86 目标完全一致。
- 幂等：同参数缓存成功，不同参数返回 `REQUEST_ID_REUSE`。
- `debug_expand_value`：专项 fixture 在 `Inspect` 的 IL offset 82 命中；`expandPayload` 为 `has_children=true` 的合法 handle。一级展开得到 `Number=0`、`Text="expand-0"` 与可继续展开的 `Child`，二级展开得到 Child 的 `Number=1`、`Text="child-0"`、`Child=null`。
- 终态：`idle`。

## Value expansion 对称覆盖

- x64 与 x86 均使用独立编译的 `ExpandValueFixture`，通过真实模块 identity 在 `Inspect(Int32)` token `0x06000002`、IL offset 82 建断点。
- 两个位数均真实调用 `debug_get_locals → debug_expand_value(payload) → debug_expand_value(payload.Child)`，验证两层字段内容后移除断点并 terminate。
- x86 首次在 offset 80（`stloc.0` 前）看到 payload 为 null，随后后移到 82；新 pause epoch 上复用旧句柄得到 `STALE_HANDLE` 后重新获取线程/帧并成功。这同时验证了句柄 epoch 隔离和恢复路径。

## 自动回归

- Python client/UI/live MCP：17/17 PASS，其中包含 ACC-004 的第 17 会话 HTTP 429 回归。
- debug JSON 契约：189/189 PASS，fixture 再生成确定性检查通过。
- transport/security harness：10/10 PASS。
- net48：构建成功，0 warning，0 error。
- net10.0-windows：构建成功，0 warning，0 error。
- 最终 host probe：health 200、54 tools、14 resources、文档入口、对象型 `list_assemblies`、stdio 透明桥接与会话 DELETE 全部 PASS。

## VM 留存证据

- x64 动态产物：`C:\Users\xxx\Desktop\dnspy-mcp-artifacts\.dnspy-mcp-debug-x64-evidence-20260830`
- x86 动态产物：`C:\Users\xxx\Desktop\dnspy-mcp-artifacts\.dnspy-mcp-debug-x86-evidence-20260830`
- 静态保存产物位于同一 ArtifactRoot 根层。
- 已部署 DLL SHA-256：`1EEAF2F28CE433476969CE736587D6A793A7F3E7C6DACA03BDF9521BE587570F`
