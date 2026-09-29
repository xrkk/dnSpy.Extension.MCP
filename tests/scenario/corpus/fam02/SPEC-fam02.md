# F02 BepInEx/Harmony 模组开发 — 族级规格

- 样本: hooktarget-01（Combatant/Tank/Scout/Battle, 5 类型）
- 骨架: open → get_type_info(Combatant) → list_methods → find_unity_messages(0) → 生成器(变体五五分) → decompile_method(Damage) → edit_begin → edit_compile(方法体文档) → edit_import → edit_apply(type_add 最小操作) → edit_impact_scan → edit_review → v6-10 edit_commit / v1-5 edit_rollback → edit_status
- 强断言: 类型面集合(5); 生成器输出含插件类名/补丁注释结构化子串; 事务 work_revision 递增; commit 后 lineage 计数; rollback 后 state=idle
- 变体差异: 生成器与 hook 目标(Tank.Damage/Scout.Damage/Combatant.Attack 轮换)、compile 文档方法名、commit/rollback 分支

| 变体 | 生成器 | hook 目标 | 收尾 |
| --- | --- | --- | --- |
| S-F02-01 | generate_bepinex_plugin | Scout.Damage | edit_rollback |
| S-F02-02 | generate_bepinex_plugin | Combatant.Attack | edit_rollback |
| S-F02-03 | generate_bepinex_plugin | Tank.Damage | edit_rollback |
| S-F02-04 | generate_bepinex_plugin | Scout.Damage | edit_rollback |
| S-F02-05 | generate_bepinex_plugin | Combatant.Attack | edit_rollback |
| S-F02-06 | generate_harmony_patch | Tank.Damage | edit_commit |
| S-F02-07 | generate_harmony_patch | Scout.Damage | edit_commit |
| S-F02-08 | generate_harmony_patch | Combatant.Attack | edit_commit |
| S-F02-09 | generate_harmony_patch | Tank.Damage | edit_commit |
| S-F02-10 | generate_harmony_patch | Scout.Damage | edit_commit |
