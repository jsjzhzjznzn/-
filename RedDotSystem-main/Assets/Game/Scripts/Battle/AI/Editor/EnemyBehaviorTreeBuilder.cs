#if UNITY_EDITOR
using System.Collections.Generic;

using BehaviorDesigner.Editor;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 挑战 BOSS 行为树：程序化生成 ExternalBehaviorTree 资产 + 挂到场景里的「怪兽」。
///
/// 为什么程序化建树：BD 的树资产本质是"任务对象图 + 序列化 JSON"，
/// 用代码建可以进版本管理、可重复生成、改结构能 diff（手拖节点做不到）。
///
/// 关键实现细节（文档 §16.4 的"阻塞"已由此修复，详见 §16.8）：
///  - 每个 Task 必须自带 NodeData 与唯一 ID，否则 JSONSerialization.SerializeNodeData
///    会 NRE（这才是"资产落盘 807 字节 types=[]“的真因，不是"只序列化窗口里的树"）；
///  - BehaviorSource.Save(entry, root, detached) 设置图，JSONSerialization.Save(source)
///    把图写进 source.TaskData（types/parentIndex 是旧格式遗留，空数组即可，JSON 里已带类型名）；
///  - 字段键是 "字段类型名 + 字段名"（例如 SingletimeoutSeconds），由 BD 自己生成，不要手写。
///
/// 树结构（与《敌人AI架构改进方案》§5.1 一致）：
///   Selector
///   ├─ Sequence 破防? → 等硬直结束
///   ├─ Sequence 目标有效? → Selector
///   │    ├─ Sequence 挑衅到期? → 挑衅 → 等动作结束
///   │    ├─ Sequence 可出手? → 在射程内? → 选招 → 攻击（攻击节点自己等结束；前摇自带转向）
///   │    ├─ Sequence 贴太近? → 35% → 后撤步
///   │    ├─ Sequence 被挡住? → 沿选定侧绕行
///   │    ├─ Sequence 45% → 环绕
///   │    └─ Sequence 追击（能出手就提前收手）
///   └─ 站桩
///
/// 菜单：Tools/Battle/AI/安装行为树 AI（BOSS）
/// </summary>
public static class EnemyBehaviorTreeBuilder
{
    private const string AssetPath = "Assets/Game/Configs/Battle/AI/BT_Enemy_Boss_Goblin.asset";
    private const string ScenePath = "Assets/Game/Scenes/tiaozhan.scene";
    private const string MonsterName = "怪兽";

    /// <summary>行为树 tick 间隔（文档 §5.3：0.15s；判定帧在动作层，不靠树）</summary>
    private const float TreeTickSeconds = 0.15f;

    [MenuItem("Tools/Battle/AI/安装行为树 AI（BOSS）")]
    public static void Install()
    {
        ExternalBehavior asset = BuildTreeAsset();
        if (asset == null) return;

        Scene scene = OpenTargetScene();
        if (!scene.IsValid()) return;

        GameObject monster = FindRoot(scene, MonsterName);
        if (monster == null)
        {
            Debug.LogError("[AI] 场景里找不到「" + MonsterName + "」");
            return;
        }

        EnemyBrain brain = monster.GetComponent<EnemyBrain>();
        if (brain == null) brain = monster.AddComponent<EnemyBrain>();

        BehaviorTree tree = monster.GetComponent<BehaviorTree>();
        if (tree == null) tree = monster.AddComponent<BehaviorTree>();
        tree.ExternalBehavior = asset;
        tree.StartWhenEnabled = true;
        tree.PauseWhenDisabled = true;

        // BD 的树是一次性的：根节点返回 Success/Failure 就"执行完成"并停住。
        // 常驻 AI 必须勾上 RestartWhenComplete，否则第一个 tick（例如目标还没绑定走 Idle）
        // 就会让树永久结束（实测症状：ExecutionStatus=Success、动作层永远没动作）。
        tree.RestartWhenComplete = true;
        tree.enabled = true;

        // 决策权交给树：内置效用决策关掉，避免两套决策同时下发意图
        brain.enableBuiltInDecision = false;

        ConfigureBehaviorManager();

        EditorUtility.SetDirty(tree);
        EditorUtility.SetDirty(brain);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Selection.activeGameObject = monster;
        Debug.Log($"[AI] 行为树已挂载：{monster.name}（资产 {asset.name}；内置决策已关闭；tick={TreeTickSeconds:F2}s）");
    }

    [MenuItem("Tools/Battle/AI/仅重建行为树资产")]
    public static void RebuildAssetOnly()
    {
        ExternalBehavior asset = BuildTreeAsset();
        if (asset != null) Selection.activeObject = asset;
    }

    /// <summary>
    /// 反序列化自检：把资产里的 JSON 还原成任务图并打印结构。
    /// 资产落盘后必须能过这一关，否则运行时 BehaviorTree 会 Inactive。
    /// </summary>
    [MenuItem("Tools/Battle/AI/验证行为树资产（反序列化）")]
    public static void ValidateAsset()
    {
        ExternalBehaviorTree ext = AssetDatabase.LoadAssetAtPath<ExternalBehaviorTree>(AssetPath);
        if (ext == null)
        {
            Debug.LogError($"[AI] 找不到行为树资产：{AssetPath}");
            return;
        }

        BehaviorSource source = new BehaviorSource(ext);
        if (ext.BehaviorSource == null || ext.BehaviorSource.TaskData == null
            || string.IsNullOrEmpty(ext.BehaviorSource.TaskData.JSONSerialization))
        {
            Debug.LogError("[AI] 资产里没有 JSONSerialization（先重建资产）");
            return;
        }

        JSONDeserialization.Load(ext.BehaviorSource.TaskData, source, true);
        if (source.RootTask == null)
        {
            Debug.LogError("[AI] 反序列化失败：RootTask 为空");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[AI] 行为树资产验证通过：{AssetPath}");
        DumpTask(source.RootTask, 0, sb);
        Debug.Log(sb.ToString());
    }

    // ──────────────────────────────
    // 建树
    // ──────────────────────────────

    private static ExternalBehavior BuildTreeAsset()
    {
        ExternalBehaviorTree ext = AssetDatabase.LoadAssetAtPath<ExternalBehaviorTree>(AssetPath);
        bool isNew = ext == null;
        if (isNew)
        {
            ext = ScriptableObject.CreateInstance<ExternalBehaviorTree>();
            AssetDatabase.CreateAsset(ext, AssetPath);
        }

        // 每次重建都换一份干净的 BehaviorSource：旧任务图（含已删任务类型）不残留
        BehaviorSource source = new BehaviorSource(ext);
        ext.SetBehaviorSource(source);

        // 树级共享变量：PickAttack 写入、Attack 读取（同一实例，避免两个空字段各挑一次）
        SharedString pendingAttackId = new SharedString { Name = "pendingAttackId" };
        source.Variables = new List<SharedVariable> { pendingAttackId };

        var builder = new TaskBuilder();
        Selector root = builder.Create<Selector>();

        // ── 1) 破防硬直：只等动作层结束，不做任何决策 ──
        Sequence stagger = builder.Create<Sequence>();
        stagger.AddChild(builder.Create<Cond_EnemyPoiseBroken>(), 0);
        Action_EnemyWaitActionEnd staggerWait = builder.Create<Action_EnemyWaitActionEnd>();
        staggerWait.timeoutSeconds = 2.5f;
        stagger.AddChild(staggerWait, 1);
        root.AddChild(stagger, 0);

        // ── 2) 目标有效 → 战斗分支 ──
        Sequence combatSeq = builder.Create<Sequence>();
        combatSeq.AddChild(builder.Create<Cond_EnemyTargetValid>(), 0);

        Selector combat = builder.Create<Selector>();

        // 2.1 挑衅（待机破绽）：间隔没到 / 动作层在忙时任务自己返回 Failure，落到下一分支
        Sequence taunt = builder.Create<Sequence>();
        taunt.AddChild(builder.Create<Action_EnemyTaunt>(), 0);
        combat.AddChild(taunt, 0);

        // 2.2 可出手 + 在射程 → 选招 → 攻击（攻击任务自己等动作结束）
        //     不插独立的"对准"动作：AttackAction 在前摇阶段（进度 < 首个判定帧）已经每帧转向，
        //     额外插一个对准动作会播 idle 片段，表现为"打完站着闪一下再挥下一刀"。
        Sequence attack = builder.Create<Sequence>();
        attack.AddChild(builder.Create<Cond_EnemyCanAttack>(), 0);
        Cond_EnemyInAttackRange inRange = builder.Create<Cond_EnemyInAttackRange>();
        inRange.tolerance = 0.6f;
        attack.AddChild(inRange, 1);
        Action_EnemyPickAttack pick = builder.Create<Action_EnemyPickAttack>();
        pick.sharedAttackId = pendingAttackId;
        attack.AddChild(pick, 2);
        Action_EnemyAttack strike = builder.Create<Action_EnemyAttack>();
        strike.attackId = pendingAttackId;
        attack.AddChild(strike, 3);
        combat.AddChild(attack, 1);

        // 2.3 贴太近 → 有概率后撤（拉开距离再突进，避免一直糊在脸上）
        Sequence backstep = builder.Create<Sequence>();
        Cond_EnemyTooClose tooClose = builder.Create<Cond_EnemyTooClose>();
        tooClose.factor = 0.75f;
        backstep.AddChild(tooClose, 0);
        Cond_EnemyRandom backstepRoll = builder.Create<Cond_EnemyRandom>();
        backstepRoll.chance = 0.35f;
        backstep.AddChild(backstepRoll, 1);
        backstep.AddChild(builder.Create<Action_EnemyBackstep>(), 2);
        combat.AddChild(backstep, 2);

        // 2.4 刚被障碍挡住 → 沿选定的一侧绕行（追击卡住后不要继续贴墙磨）
        Sequence detour = builder.Create<Sequence>();
        detour.AddChild(builder.Create<Cond_EnemyMoveBlocked>(), 0);
        Action_EnemyStrafe detourStrafe = builder.Create<Action_EnemyStrafe>();
        detourStrafe.useBlockedSide = true;
        detour.AddChild(detourStrafe, 1);
        combat.AddChild(detour, 3);

        // 2.5 中距离 → 有概率环绕（方向在 Action_EnemyStrafe 里未指定时随机）
        Sequence strafe = builder.Create<Sequence>();
        Cond_EnemyRandom strafeRoll = builder.Create<Cond_EnemyRandom>();
        strafeRoll.chance = 0.45f;
        strafe.AddChild(strafeRoll, 0);
        strafe.AddChild(builder.Create<Action_EnemyStrafe>(), 1);
        combat.AddChild(strafe, 4);

        // 2.6 其余情况 → 追击（Action_EnemyMoveTo 在"能出手"时会提前收手，
        //      MoveAction 自身也有 2s 超时与卡住检测兜底）
        Sequence chase = builder.Create<Sequence>();
        Action_EnemyMoveTo moveTo = builder.Create<Action_EnemyMoveTo>();
        moveTo.attackReadyWindow = 0.6f;
        chase.AddChild(moveTo, 0);
        combat.AddChild(chase, 5);

        combatSeq.AddChild(combat, 1);
        root.AddChild(combatSeq, 1);

        // ── 3) 没有目标/看不见 → 站桩 ──
        root.AddChild(builder.Create<Action_EnemyIdle>(), 2);

        source.Save(null, root, null);

        // 落盘：把内存任务图序列化成 BD 的 JSON（写进 source.TaskData）
        JSONSerialization.Save(source);

        TaskSerializationData data = source.TaskData;
        if (data == null || string.IsNullOrEmpty(data.JSONSerialization))
        {
            Debug.LogError("[AI] 行为树序列化失败：JSONSerialization 为空（检查任务是否都有 NodeData/ID）");
            return null;
        }

        EditorUtility.SetDirty(ext);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

        Debug.Log($"[AI] 行为树资产{(isNew ? "新建" : "重建")}：{AssetPath}（{builder.Count} 个节点，JSON {data.JSONSerialization.Length} 字符）");
        return ext;
    }

    /// <summary>BehaviorManager：0.15s tick（文档 §5.3），20 只怪时同帧全量 tick 会抖</summary>
    private static void ConfigureBehaviorManager()
    {
        BehaviorManager manager = Object.FindObjectOfType<BehaviorManager>();
        if (manager == null)
        {
            Debug.LogWarning("[AI] 场景里没有 BehaviorManager（运行时 BehaviorTree 会自己创建，tick 走默认 EveryFrame）");
            return;
        }

        manager.UpdateInterval = UpdateIntervalType.SpecifySeconds;
        manager.UpdateIntervalSeconds = TreeTickSeconds;
        EditorUtility.SetDirty(manager);
        Debug.Log($"[AI] BehaviorManager tick 间隔 = {TreeTickSeconds:F2}s");
    }

    // ──────────────────────────────
    // 任务工厂（NodeData + 唯一 ID 是 BD 序列化/编辑器图的前提）
    // ──────────────────────────────

    private sealed class TaskBuilder
    {
        private int nextId;

        public int Count { get; private set; }

        public T Create<T>() where T : Task, new()
        {
            T task = new T
            {
                NodeData = new NodeData(),
                ID = nextId++,
            };
            Count++;
            return task;
        }
    }

    private static void DumpTask(Task task, int depth, System.Text.StringBuilder sb)
    {
        sb.AppendLine(new string(' ', depth * 2) + "- " + task.GetType().Name + (task.Disabled ? " (disabled)" : ""));
        if (task is ParentTask parent && parent.Children != null)
        {
            for (int i = 0; i < parent.Children.Count; i++) DumpTask(parent.Children[i], depth + 1, sb);
        }
    }

    // ──────────────────────────────
    // 场景
    // ──────────────────────────────

    private static Scene OpenTargetScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path == ScenePath) return scene;

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return default;
        return EditorSceneManager.OpenScene(ScenePath);
    }

    private static GameObject FindRoot(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == name) return root;
        }
        return null;
    }
}
#endif
