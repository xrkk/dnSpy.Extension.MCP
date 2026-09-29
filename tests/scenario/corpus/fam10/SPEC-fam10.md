# F10 Unity 游戏分析 — 族级规格

- 样本: unitymsgs-01（PlayerController: Awake/Start/Update/OnTriggerEnter + EnemySpawner: Awake/Start; 共 6 消息）+ TestIL(UnityComponent 3 消息)
- 骨架: open(unitymsgs+TestIL) → find_unity_messages(两集) → get_type_info(PlayerController) → list_methods → decompile_method(变体消息方法) → generate_bepinex_plugin(变体名/guid) → search_types(PlayerController) → get_method_il(变体) → find_callers(变体) → search_members
- 强断言: unitymsgs 消息集恰 6 项且类型归属精确; TestIL UnityComponent 3 消息; 生成器输出含变体插件名; IL 存在
- 变体差异: 消息方法(5 种)、插件名/GUID、caller 目标

| S-F10-01 | msg=Start plugin=UnityPlug01 |
| S-F10-02 | msg=Update plugin=UnityPlug02 |
| S-F10-03 | msg=OnTriggerEnter plugin=UnityPlug03 |
| S-F10-04 | msg=PeekHealth plugin=UnityPlug04 |
| S-F10-05 | msg=Awake plugin=UnityPlug05 |
| S-F10-06 | msg=Start plugin=UnityPlug06 |
| S-F10-07 | msg=Update plugin=UnityPlug07 |
| S-F10-08 | msg=OnTriggerEnter plugin=UnityPlug08 |
| S-F10-09 | msg=PeekHealth plugin=UnityPlug09 |
| S-F10-10 | msg=Awake plugin=UnityPlug10 |
