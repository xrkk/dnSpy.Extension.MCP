# F07 符号重命名重构 — 族级规格

- 样本: renametree-01（IRepository/OldRepository/OldService/UglyName_*, 6 类型）
- 骨架: open → search_types(Old) → get_type_info → list_methods → rename(变体目标: OldRepository/OldService/UglyName_1 轮换) → decompile_type 复核(新名出现) → get_type_property → edit_begin → edit_export(检查点) → edit_history → edit_restore(预期错误: 无 drift) → rollback → 探针(edit_recover v6-10 / edit_accept_live v1-5) → v1-5 save_assembly → edit_status
- 强断言: rename 后类型面集合更新精确; export 产物路径; 探针错误码; save source_preserved
- 变体差异: rename 目标与新名、探针工具、save 分支

| S-F07-01 | rename→OldService2 | 探针 accept_live |
| S-F07-02 | rename→UglyName_12 | 探针 accept_live |
| S-F07-03 | rename→OldRepository2 | 探针 accept_live |
| S-F07-04 | rename→OldService2 | 探针 accept_live |
| S-F07-05 | rename→UglyName_12 | 探针 accept_live |
| S-F07-06 | rename→OldRepository2 | 探针 recover |
| S-F07-07 | rename→OldService2 | 探针 recover |
| S-F07-08 | rename→UglyName_12 | 探针 recover |
| S-F07-09 | rename→OldRepository2 | 探针 recover |
| S-F07-10 | rename→OldService2 | 探针 recover |
