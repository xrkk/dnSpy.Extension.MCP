---
status: accepted
date: 2026-09-29
---

# 0002: 覆盖全集取生产配置下 tools/list 实测通告面，不含内部注入工具

情景链路测试"每接口 ≥5 情景"的覆盖全集定为生产配置（不设 `DNMCP_TEST=1`）下部署实例 `tools/list` 实际通告的工具（当前约 71：静态 32 + 调试生产面 22 + 编辑 17），排除 `edit_test_*`/`debug_test_*` 内部注入工具与 MCP 标准协议方法（`initialize`、`resources/*`）。精确名单以实测为准，不以文档转述为准（文档写"编辑通告 18"，代码 `EditToolProvider.ProductTools` 实为 17，此类差异由实测钉死）。

理由：情景测试的对象是用户真实可用的工作流面，即通告面；`*_test_*` 是组件验收专用注入工具（故障注入、时钟、屏障），不属于任何真实工作流，纳入则 22 个工具 × 5 = 110+ 个调用槽位被迫成为伪情景。广告面与执行面已核实：`*_test_*` 从不通告（或仅 `DNMCP_TEST=1` 下通告），直呼会被 `RequireTest()`/`TestModeEnabled` 门禁以 `EDIT_CAPABILITY_UNAVAILABLE`/`CAPABILITY_UNAVAILABLE` 拒绝。
