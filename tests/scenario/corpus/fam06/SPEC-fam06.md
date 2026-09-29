# F06 资源提取与替换 — 族级规格

- 样本: resource-01（嵌入 ResourceSample.strings: greeting/payload_hint/count 三键）
- 骨架: open → get_assembly_info → edit_begin → edit_resource_export(strings→ArtifactRoot) → 变体半数 edit_resource_import(回导) → edit_apply(managed_resource_add) → edit_review → commit → edit_history → v1-5 undo / v6-10 redo → 恢复面探针(v6-10 edit_recover / v1-5 edit_accept_live 预期错误) → edit_status
- 强断言: export 产物路径+字节数; import/apply 后 revision 递增; commit 后 lineage; 探针错误码精确(NO_RECOVERY/无待恢复类域包)
- 变体差异: import 与否、undo/redo 分支、探针工具

| 变体 | import | undo/redo | 探针 |
| --- | --- | --- | --- |
| S-F06-01 | 否 | undo | accept_live |
| S-F06-02 | 是 | undo | accept_live |
| S-F06-03 | 否 | undo | accept_live |
| S-F06-04 | 是 | undo | accept_live |
| S-F06-05 | 否 | undo | accept_live |
| S-F06-06 | 是 | redo | recover |
| S-F06-07 | 否 | redo | recover |
| S-F06-08 | 是 | redo | recover |
| S-F06-09 | 否 | redo | recover |
| S-F06-10 | 是 | redo | recover |
