# P03 探查关键发现（IMP-301/302 记录）

1. 静态写工具（patch_method_il/force_return/nop_method/revert_method_il/save_assembly/rename_symbol_by_token）要求**已初始化的 MCP 会话**（EDIT_OWNER_REQUIRED 语义=会话初始化, 非手工事务）; 初始化后可直调, 内部自管事务; 有手工事务开放时返回 EDIT_TRANSACTION_BUSY。
2. patch edits 形态: {op: replace|insert|delete|set_init_locals, index, opcode, operand}（P01 快照 inputSchema, 实测 replace/nop 语义待语料自测）。
3. force_return 直接可用: 返回新指令序列（ldc.i4.<n>; ret）, revert_method_il 还原（实测 10→7→10 闭环, hooktarget-01 Combatant.Damage）。
4. save_assembly: {saved_to, bytes_written, source_preserved:true, sha256}（产物写 ArtifactRoot, 探查件 probe-hooktarget.dll 已入清单追加）。
5. rename_symbol_by_token: 需 target_kind+token+new_name+assembly_name。
6. 编辑链 begin/rollback: initialize 后 edit_begin 返回 result.transaction{transaction_id,work_revision}; 会话关闭自动中止孤儿事务（P02 已证）。
7. search_string_literals 命中=方法内字面量使用; 纯 const 字段无使用点不出现（constmatrix "SELECT" 0 命中, "ALPHA-KEY-0001" 在 Join 内 1 命中）。强断言以此为准。
8. find_overrides 跨集接口实现可命中（IShape.Area → XRefApp.Square is_interface_impl=true + Circle）。
9. debug_capabilities.runtime_matrix: net48-exe/x64 launch=true（重启后复核）; runtarget-01.exe sha256 = 8aac59523304a3ccf45c99dd6b2d3f5887593fb74eacf53966c61c819d38bced（F05 expected_sha256）。
10. 样本再生成: csc 非确定性（sha 变）, 4 样本类型面重跑一致 → ACC-047 语义等价分支（build-all.ps1 重跑记录）。
