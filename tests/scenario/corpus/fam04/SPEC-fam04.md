# F04 混淆与加壳识别 — 族级规格

- 样本: obfuscated-01（命名空间 a, 类型 b/g/k, 单字符成员）
- 骨架: open → get_assembly_info → list_types(names_only) → search_members(变体成员名) → decompile_type(b/g/k 轮换) → get_type_info → find_path_to_type(变体对) → decompile_method(d/f/h/m 轮换) → list_methods → search_types
- 强断言: 混淆名类型集合精确(a.b/a.g/a.k+<>c); 成员命中集合; 方法 IL 存在; path 结果文本可判
- 变体差异: 成员查询名、decompile 目标类型/方法、path 端点对

| S-F04-01 | 目标 a.g / 方法 f |
| S-F04-02 | 目标 a.k / 方法 h |
| S-F04-03 | 目标 a.b / 方法 m |
| S-F04-04 | 目标 a.g / 方法 d |
| S-F04-05 | 目标 a.k / 方法 f |
| S-F04-06 | 目标 a.b / 方法 h |
| S-F04-07 | 目标 a.g / 方法 m |
| S-F04-08 | 目标 a.k / 方法 d |
| S-F04-09 | 目标 a.b / 方法 f |
| S-F04-10 | 目标 a.g / 方法 h |
