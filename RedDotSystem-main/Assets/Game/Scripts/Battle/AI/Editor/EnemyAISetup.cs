#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 挑战 BOSS AI 一键接入（幂等）：
///   1. 从怪兽 FBX 里取真实动画片段（7 段攻击 + 闪避 + 眩晕 + 受击/死亡）；
///   2. 生成/更新 EnemyProfile 档案（招式表按真实片段配置）；
///   3. 给 tiaozhan 场景里的「怪兽」挂 EnemyActionController + EnemyBrain，
///      并把旧的 ChallengeMonster 停用（保留组件，随时可回退）。
///
/// 菜单：Tools/Battle/AI/安装挑战 BOSS AI（怪兽）
/// </summary>
public static class EnemyAISetup
{
    private const string ScenePath = "Assets/Game/Scenes/tiaozhan.scene";
    private const string MonsterFbx = "Assets/Content/Resource/人物/怪兽_by_silentradio_221780108ccc1e80ba186adb66f15dd0/怪兽/怪兽.fbx";
    private const string ProfileDir = "Assets/Game/Configs/Battle/AI";
    private const string ProfilePath = ProfileDir + "/EnemyProfile_Boss_Goblin.asset";

    [MenuItem("Tools/Battle/AI/安装挑战 BOSS AI（怪兽）")]
    public static void Install()
    {
        EnemyProfile profile = CreateOrUpdateProfile();
        if (profile == null) return;

        // 定位场景：优先当前已打开的 tiaozhan，避免无谓地丢弃用户未保存的编辑
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            scene = EditorSceneManager.OpenScene(ScenePath);
        }

        GameObject monster = FindRoot(scene, "怪兽");
        if (monster == null)
        {
            Debug.LogError("[AI] 场景里找不到「怪兽」");
            return;
        }

        EnemyActionController controller = monster.GetComponent<EnemyActionController>();
        if (controller == null) controller = monster.AddComponent<EnemyActionController>();

        EnemyBrain brain = monster.GetComponent<EnemyBrain>();
        if (brain == null) brain = monster.AddComponent<EnemyBrain>();

        brain.profile = profile;
        brain.enableBuiltInDecision = true;
        brain.decisionInterval = 0.15f;
        brain.logActions = true;

        GameObject player = FindRoot(scene, "安比dan");
        if (player != null) brain.target = player.transform;

        // 旧脚本停用（不删：出问题可以一键改回 enabled=true 对比手感）
        ChallengeMonster legacy = monster.GetComponent<ChallengeMonster>();
        if (legacy != null) legacy.enabled = false;

        EditorUtility.SetDirty(monster);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Selection.activeGameObject = monster;
        Debug.Log($"[AI] 已接入：{monster.name}（EnemyBrain + 档案 {profile.name}；ChallengeMonster 已停用）");
    }

    [MenuItem("Tools/Battle/AI/仅生成默认档案（不改场景）")]
    public static void CreateProfileOnly()
    {
        EnemyProfile profile = CreateOrUpdateProfile();
        if (profile != null) Selection.activeObject = profile;
    }

    /// <summary>只重配受击分层（片段 + 上半身 AvatarMask），不动场景</summary>
    [MenuItem("Tools/Battle/AI/仅配置受击分层（片段 + 上半身 Mask）")]
    public static void SetupHitReactionOnly()
    {
        EnemyProfile profile = AssetDatabase.LoadAssetAtPath<EnemyProfile>(ProfilePath);
        if (profile == null)
        {
            Debug.LogError("[AI] 找不到档案：" + ProfilePath);
            return;
        }

        Dictionary<string, AnimationClip> clips = LoadClips();
        if (clips.Count == 0) return;

        ApplyHitReactionConfig(profile, clips);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();

        Debug.Log($"[AI] 受击分层已配置：轻击抖动 {profile.flinchClip?.name} / 重击 {profile.heavyHitFrontClip?.name}(正面) {profile.heavyHitBackClip?.name}(背面) / Mask {profile.flinchMask?.name}");
        Selection.activeObject = profile;
    }

    // ──────────────────────────────
    // 受击分层
    // ──────────────────────────────

    private const string MaskPath = ProfileDir + "/Boss_UpperBody.mask";
    private const string MonsterPrefab =
        "Assets/Content/Resource/人物/怪兽_by_silentradio_221780108ccc1e80ba186adb66f15dd0/怪兽/怪兽.prefab";

    private static void ApplyHitReactionConfig(EnemyProfile profile, Dictionary<string, AnimationClip> clips)
    {
        // 轻击：短受击片段 + 时长略大于玩家出刀间隔（连打时一直挂在受击里）
        profile.flinchClip = Get(clips, "Monster_Goblin_Ani_Hit_Shake");
        profile.flinchSeconds = 0.8f;

        profile.heavyHitFrontClip = Get(clips, "Goblin_Ani_Hit_L_Front");
        profile.heavyHitBackClip = Get(clips, "Goblin_Ani_Hit_L_Back");
        profile.heavyHitSeconds = 0.9f;
        profile.heavyHitSpeed = -1f;    // 自动：按片段长度/硬直时长算

        profile.flinchMask = EnsureUpperBodyMask();
    }

    /// <summary>
    /// 上半身 AvatarMask：排除 Pelvis / 腿 / 脚，其余（脊柱、手臂、头、挂件骨骼）全要。
    /// 轻击抖动只影响这些骨骼，动作层的腿/身体继续播。
    /// </summary>
    private static AvatarMask EnsureUpperBodyMask()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MonsterPrefab);
        if (prefab == null)
        {
            Debug.LogWarning("[AI] 找不到怪兽 prefab，无法生成上半身 Mask：" + MonsterPrefab);
            return null;
        }

        var paths = new List<string>();
        CollectUpperBodyPaths(prefab.transform, prefab.transform, "", paths);

        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath);
        bool isNew = mask == null;
        if (isNew)
        {
            mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, MaskPath);
        }

        mask.transformCount = paths.Count;
        for (int i = 0; i < paths.Count; i++)
        {
            mask.SetTransformPath(i, paths[i]);
            mask.SetTransformActive(i, true);
        }

        EditorUtility.SetDirty(mask);
        Debug.Log($"[AI] 上半身 Mask {(isNew ? "新建" : "更新")}：{MaskPath}（{paths.Count} 根骨骼）");
        return mask;
    }

    private static void CollectUpperBodyPaths(Transform root, Transform current, string path, List<string> paths)
    {
        // Mask 的路径相对 Animator 根（怪兽本体），不能再带一层根物体名字
        string full = current == root
            ? string.Empty
            : (string.IsNullOrEmpty(path) ? current.name : path + "/" + current.name);

        if (current != root && IsUpperBodyBone(current.name))
            paths.Add(full);

        foreach (Transform child in current)
            CollectUpperBodyPaths(root, child, full, paths);
    }

    private static bool IsUpperBodyBone(string boneName)
    {
        if (boneName == "Bip001") return false;
        if (boneName.Contains("Pelvis")) return false;
        if (boneName.Contains("Thigh")) return false;
        if (boneName.Contains("Calf")) return false;
        if (boneName.Contains("Foot")) return false;
        if (boneName.Contains("Toe")) return false;
        return true;
    }

    // ──────────────────────────────
    // 档案
    // ──────────────────────────────

    private static EnemyProfile CreateOrUpdateProfile()
    {
        Dictionary<string, AnimationClip> clips = LoadClips();
        if (clips.Count == 0)
        {
            Debug.LogError("[AI] 未能从怪兽 FBX 里读到动画片段：" + MonsterFbx);
            return null;
        }

        Directory.CreateDirectory(ProfileDir);
        EnemyProfile profile = AssetDatabase.LoadAssetAtPath<EnemyProfile>(ProfilePath);
        bool isNew = profile == null;
        if (isNew) profile = ScriptableObject.CreateInstance<EnemyProfile>();

        // ── 移动 / 感知 / 防御 ──
        profile.moveSpeed = 1.6f;
        profile.turnSpeedDeg = 240f;
        profile.attackRange = 2.3f;
        profile.stopDistance = 1.9f;
        profile.sightRange = 25f;
        profile.sightAngle = 160f;
        profile.loseTargetSeconds = 5f;
        profile.reactionDelay = 0.18f;
        profile.maxPoise = 60f;
        profile.poisePerDamage = 1.2f;
        profile.poiseRecoverDelay = 2f;
        profile.poiseRecoverSeconds = 3f;
        profile.staggerSeconds = 0.9f;
        profile.hurtStaggerSeconds = 0.4f;
        profile.heavyHitDamage = 15f;
        profile.globalCooldown = 0.55f;
        profile.intervalJitterMin = 0.85f;
        profile.intervalJitterMax = 1.15f;
        profile.strafeChance = 0.45f;
        profile.strafeDuration = 0.5f;
        profile.strafeRadius = 2.6f;
        profile.backstepChance = 0.35f;
        profile.backstepDistance = 2f;
        profile.phaseHpPercents = new[] { 100, 50 };
        profile.lightHitInterruptChance = 0.3f;
        profile.tauntIntervalMin = 15f;
        profile.tauntIntervalMax = 25f;
        profile.tauntMinDistanceFactor = 1.5f;

        // ── 表现片段 ──
        profile.idleClip = Get(clips, "Goblin_Ani_Idle");
        profile.runClip = Get(clips, "Goblin_Ani_Run");
        profile.hurtClip = Get(clips, "Goblin_Ani_Hit_L_Front");
        profile.staggerClip = Get(clips, "Goblin_Ani_Debuff_Stun_Start");
        profile.dodgeClip = Get(clips, "Goblin_Ani_Dodge");
        profile.deathClip = Get(clips, "Goblin_Ani_Death_Hit_Front");

        // ── 四向走片段：环绕时按移动方向播，避免正面跑步片段横向滑脚 ──
        profile.walkLeftClip = Get(clips, "Goblin_Ani_Walk_L");
        profile.walkRightClip = Get(clips, "Goblin_Ani_Walk_R");
        profile.walkForwardClip = Get(clips, "Goblin_Ani_Walk_F");
        profile.walkBackClip = Get(clips, "Goblin_Ani_Walk_B");
        profile.walkClipSpeed = 1f;

        // ── 受击分层：轻击只抖上半身（0.42s 短片段），重击按方向走全身受击 ──
        ApplyHitReactionConfig(profile, clips);

        // ── 招式表：真实片段，不造假变体 ──
        // 每个招式都裁剪掉片段尾部的收招段（endNormalized = recoveryStart + 0.12），
        // 再按 followUps 串成 1~3 连段，单招有效时长压到 0.85~2.84s。
        //   1) stamp 快拍：最短招，连段的主力填充
        //   2) swing 标准挥砍：两段判定，后接 stamp 收尾
        //   3) slam 大力砸：前摇长、削韧高，作为终结技不参与连段
        //   4) slam_phase2 阶段 2 招式，可续 stamp
        profile.attacks = new[]
        {
            new AttackDef
            {
                id = "stamp",
                clip = Get(clips, "Goblin_Ani_Attack_02_Stamp"),
                fadeIn = 0.06f,
                speed = 1.15f,
                hitFrames = new[] { 0.45f },
                recoveryStart = 0.62f,
                startNormalized = 0f,
                endNormalized = 0.74f,
                followUps = new[]
                {
                    new AttackDef.ComboLink { attackId = "stamp", atNormalized = 0.80f, chance = 0.55f },
                    new AttackDef.ComboLink { attackId = "swing", atNormalized = 0.82f, chance = 0.35f },
                },
                damage = 8f,
                poiseDamage = 12f,
                range = 2.0f,
                arc = 110f,
                advanceDistance = 0.5f,
                advanceEnd = 0.35f,
                cooldown = 1.4f,
                weight = 1.25f,
                minPhase = 1,
                telegraphVfxKey = "",
            },
            new AttackDef
            {
                id = "swing",
                clip = Get(clips, "Goblin_Ani_Attack_01"),
                fadeIn = 0.08f,
                speed = 1.25f,
                hitFrames = new[] { 0.33f, 0.52f },
                recoveryStart = 0.72f,
                startNormalized = 0f,
                endNormalized = 0.84f,
                followUps = new[]
                {
                    new AttackDef.ComboLink { attackId = "stamp", atNormalized = 0.90f, chance = 0.45f },
                },
                damage = 12f,
                poiseDamage = 20f,
                range = 2.4f,
                arc = 100f,
                advanceDistance = 0.9f,
                advanceEnd = 0.3f,
                cooldown = 2.2f,
                weight = 1f,
                minPhase = 1,
                telegraphVfxKey = "",
            },
            new AttackDef
            {
                id = "slam",
                clip = Get(clips, "Goblin_Ani_Attack_03"),
                fadeIn = 0.1f,
                speed = 1.45f,
                hitFrames = new[] { 0.45f },
                recoveryStart = 0.78f,
                startNormalized = 0f,
                endNormalized = 0.88f,
                followUps = new AttackDef.ComboLink[0],
                damage = 18f,
                poiseDamage = 30f,
                range = 2.8f,
                arc = 120f,
                advanceDistance = 1.1f,
                advanceEnd = 0.32f,
                cooldown = 3.0f,
                weight = 0.7f,
                minPhase = 1,
                telegraphVfxKey = "",
            },
            new AttackDef
            {
                id = "slam_phase2",
                clip = Get(clips, "Goblin_Ani_Attack_06"),
                fadeIn = 0.1f,
                speed = 1.15f,
                hitFrames = new[] { 0.4f, 0.62f },
                recoveryStart = 0.76f,
                startNormalized = 0f,
                endNormalized = 0.84f,
                followUps = new[]
                {
                    new AttackDef.ComboLink { attackId = "stamp", atNormalized = 0.88f, chance = 0.5f },
                },
                damage = 16f,
                poiseDamage = 26f,
                range = 2.9f,
                arc = 120f,
                advanceDistance = 1.2f,
                advanceEnd = 0.3f,
                cooldown = 2.6f,
                weight = 1f,
                minPhase = 2,
                telegraphVfxKey = "",
            },
        };

        for (int i = 0; i < profile.attacks.Length; i++) profile.attacks[i].Validate();

        if (isNew)
        {
            AssetDatabase.CreateAsset(profile, ProfilePath);
            Debug.Log("[AI] 新建档案：" + ProfilePath);
        }
        else
        {
            EditorUtility.SetDirty(profile);
            Debug.Log("[AI] 更新档案：" + ProfilePath);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return profile;
    }

    private static Dictionary<string, AnimationClip> LoadClips()
    {
        var map = new Dictionary<string, AnimationClip>();
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(MonsterFbx))
        {
            if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                map[clip.name] = clip;
        }
        return map;
    }

    private static AnimationClip Get(Dictionary<string, AnimationClip> clips, string name)
    {
        if (clips.TryGetValue(name, out AnimationClip clip)) return clip;
        Debug.LogWarning("[AI] 缺少动画片段：" + name);
        return null;
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
