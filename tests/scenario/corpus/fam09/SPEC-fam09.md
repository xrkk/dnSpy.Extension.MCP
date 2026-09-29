# F09 字符串/常量取证 — 族级规格

- 样本: constmatrix-01（Strings.* 4 常量 + Numbers.*; Join 内联 ALPHA-KEY-0001 与 8443）
- 骨架: open → search_string_literals(变体查询: ALPHA/beta/cache.db/8443) → list_string_constants(Consumer) → search_constants(变体值: 8443/28/42/100) → decompile_method(Join/Sum/PathOf) → get_type_info → get_method_il(变体方法) → find_by_attribute(Obsolete→Strings) → search_types → list_methods
- 强断言: ALPHA-KEY-0001 恰 1 处(Join); 8443 归属 Join; Obsolete 目标=ConstMatrix.Strings; IL 指令数精确
- 变体差异: 查询串/常量值/方法目标

| S-F09-01 | q=beta v=100 m=Sum |
| S-F09-02 | q=cache.db v=28 m=PathOf |
| S-F09-03 | q=SELECT v=42 m=Join |
| S-F09-04 | q=ALPHA v=8443 m=Sum |
| S-F09-05 | q=beta v=100 m=PathOf |
| S-F09-06 | q=cache.db v=28 m=Join |
| S-F09-07 | q=SELECT v=42 m=Sum |
| S-F09-08 | q=ALPHA v=8443 m=PathOf |
| S-F09-09 | q=beta v=100 m=Join |
| S-F09-10 | q=cache.db v=28 m=Sum |
