# F03 恶意样本静态分析 — 族级规格

- 样本: malfeat-01（Loader/CredGrabber/MarkerAttribute, 4 类型, 3 个 Marker 标注, http 特征串）
- 骨架: open → get_assembly_info → list_types → 变体特征串检索(http/SOFTWARE/Global/beacon/Run) → find_by_attribute(Marker) → decompile_by_token(标注目标 token) → find_references(方法) → search_constants(30000) → get_type_fields(BrowserPaths) → search_members → decompile_method(BeaconTemplate) → list_methods
- 强断言: 类型面集合; 特征串命中数与所属方法精确(BeaconTemplate); Marker 目标集合(Loader+2); 常量 30000 归属 SleepMs; 字段数=2
- 变体差异: 特征查询串与期望命中、decompile 的 token 目标、references 目标方法

| 变体 | 查询串 | 期望命中(方法) |
| --- | --- | --- |
| S-F03-01 | http | BeaconTemplate |
| S-F03-02 | SOFTWARE | Describe |
| S-F03-03 | Global | Describe |
| S-F03-04 | beacon | BeaconTemplate |
| S-F03-05 | Run | Describe |
| S-F03-06 | http | BeaconTemplate |
| S-F03-07 | SOFTWARE | Describe |
| S-F03-08 | Global | Describe |
| S-F03-09 | beacon | BeaconTemplate |
| S-F03-10 | Run | Describe |
