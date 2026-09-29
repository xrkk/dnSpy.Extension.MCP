# F01 许可证/授权绕过 — 族级规格

- 样本: license-01（LicenseSample: LicenseGate/AppMain/LicenseStrings, 4 类型）
- 骨架: open → search_types(LicenseGate) → search_string_literals(INVALID) → list_string_constants(LicenseGate) → decompile_method(Check) → find_callers(Validate) → get_method_il(Check) → 变体写操作 → revert → (变体) save_assembly → edit_status → close
- 强断言: 类型面集合相等(4 类型); INVALID 字面量恰 1 处(Validate); 常量含 29; IL 指令数与首指令 opcode 精确; 写后指令变化精确(ldc.i4.n/ret); 还原后 IL 与原一致; save 的 source_preserved=true + 字节数>0
- 变体差异: 写工具(force_return@v1,2,7,10 / nop@v3,4,8 / patch replace@v5,6,9)、force_return 值(1/0/365)、是否 save、patch 目标方法(Check/DaysRemaining/Validate)

| 变体 | 写工具 | 值/目标 | save |
| --- | --- | --- | --- |
| S-F01-01 | force_return | 见 workflow.VARIANTS | 否 |
| S-F01-02 | force_return | 见 workflow.VARIANTS | 是 |
| S-F01-03 | nop_method | 见 workflow.VARIANTS | 否 |
| S-F01-04 | nop_method | 见 workflow.VARIANTS | 是 |
| S-F01-05 | patch_method_il | 见 workflow.VARIANTS | 是 |
| S-F01-06 | patch_method_il | 见 workflow.VARIANTS | 否 |
| S-F01-07 | force_return | 见 workflow.VARIANTS | 是 |
| S-F01-08 | nop_method | 见 workflow.VARIANTS | 否 |
| S-F01-09 | patch_method_il | 见 workflow.VARIANTS | 是 |
| S-F01-10 | force_return | 见 workflow.VARIANTS | 否 |
