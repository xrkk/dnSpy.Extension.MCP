# F05 动态调试排障 — 族级规格

- 样本: runtarget-01.exe（sha256=8aac59523304a3ccf45c99dd6b2d3f5887593fb74eacf53966c61c819d38bced; Worker.Accumulate 确定性循环, TOTAL=70, 退出码 70）
- 骨架: debug_capabilities(前置能力) → open_files → debug_launch(net48-exe/x64, break_kind=entry) → debug_status → 断点(Worker.Step) → wait_event → get_stack → get_locals → 变体附加工具 → pause/continue → read_events → (v≤6: 断点管理) → terminate
- 强断言: capabilities launch=true(x64); launch 返回 session/generation; 断点命中事件 kind; 栈顶方法=Step; 局部变量 total/i 集合; 事件游标单调; terminate 后 idle
- 变体差异: 附加调试面(expand/read_memory/dump/step/threads/modules/restart/exception_policy); 断点方法(Step/Accumulate/Report 轮换)

| 变体 | 附加工具 | 断点方法 |
| --- | --- | --- |
| S-F05-01 | debug_expand_value, debug_restart | Accumulate |
| S-F05-02 | debug_expand_value, debug_read_memory | Report |
| S-F05-03 | debug_expand_value, debug_read_memory, debug_dump_module | Step |
| S-F05-04 | debug_expand_value, debug_read_memory, debug_dump_module, debug_step | Accumulate |
| S-F05-05 | debug_expand_value, debug_read_memory, debug_dump_module, debug_step, debug_list_threads | Report |
| S-F05-06 | debug_read_memory, debug_dump_module, debug_step, debug_list_threads, debug_list_modules, debug_set_exception_policy | Step |
| S-F05-07 | debug_dump_module, debug_step, debug_list_threads, debug_list_modules, debug_restart, debug_set_exception_policy | Accumulate |
| S-F05-08 | debug_step, debug_list_threads, debug_list_modules, debug_restart, debug_set_exception_policy | Report |
| S-F05-09 | debug_list_threads, debug_list_modules, debug_restart, debug_set_exception_policy | Step |
| S-F05-10 | debug_list_modules, debug_restart, debug_set_exception_policy | Accumulate |
