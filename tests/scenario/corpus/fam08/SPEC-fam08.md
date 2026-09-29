# F08 跨程序集影响分析 — 族级规格

- 样本对: xref-a（Square/Client）+ xref-b（IShape/Circle/Geometry）
- 骨架: open(双集) → find_overrides(IShape.Area → Square+Circle) → find_callers(Area→Describe) → find_callees(Client.TotalArea→xref-b 方法集) → find_references(变体 target_kind) → find_path_to_type(变体对) → decompile_method(变体) → search_members(Area) → get_type_info → list_methods → search_types
- 强断言: overrides 集合含 XRefApp.Square(is_interface_impl)与 Circle; callees 跨集目标集; 变体 kind 命中
- 变体差异: references 目标与方法、path 端点、decompile 目标

| S-F08-01 | refs→Circle |
| S-F08-02 | refs→Geometry.Describe |
| S-F08-03 | refs→Square.Area |
| S-F08-04 | refs→IShape.Area |
| S-F08-05 | refs→Circle |
| S-F08-06 | refs→Geometry.Describe |
| S-F08-07 | refs→Square.Area |
| S-F08-08 | refs→IShape.Area |
| S-F08-09 | refs→Circle |
| S-F08-10 | refs→Geometry.Describe |
