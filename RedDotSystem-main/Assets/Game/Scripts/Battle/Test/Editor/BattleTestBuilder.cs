using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Animancer;
using Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// P4 单机战斗测试场景一键构建：
///   - 用项目现有 545 模型做玩家占位（P7 换成安比/艾莲）
///   - 用 FemaleMovementAnimsetPro 的动作片段生成 PlayerSO/ComboData（仅测试用）
///   - 生成 BattleTest.scene：地面、方向光、Camera+thirdcamera、玩家、木桩、测试引导
///
/// 菜单：Tools/Battle/P4 构建单机测试场景
/// </summary>
public static class BattleTestBuilder
{
    private const string TestDir = "Assets/Game/BattleTest";
    private const string ScenePath = "Assets/Game/Scenes/BattleTest.scene";
    private const string PlayerPrefabPath = TestDir + "/TestPlayer545.prefab";
    private const string PlayerSOpath = TestDir + "/PlayerSO_545.asset";
    private const string LightComboPath = TestDir + "/Combo_Light.asset";
    private const string SkillPath = TestDir + "/Combo_Skill.asset";

    private const string Fbx1 = "Assets/Content/Resource/aniamn/FemaleMovementAnimsetPro/Animations/FemaleMovementAnimsetPro_1.fbx";
    private const string Fbx2 = "Assets/Content/Resource/aniamn/FemaleMovementAnimsetPro/Animations/FemaleMovementAnimsetPro_2.fbx";
    private const string PlayerModelPath = "Assets/Content/Resource/renwu/daniya/545.fbx";

    [MenuItem("Tools/Battle/P4 构建单机测试场景", priority = 2)]
    public static void Build()
    {
        EnsureFolder(TestDir);

        var clips = LoadClips();
        AnimationClip C(string name) => clips.TryGetValue(name, out var clip) ? clip : null;

        // ---------------- 连招数据 ----------------
        var atk1 = CreateCombo("Combo_Atk1", "Atk1", C("ThrowSingle"), null, 0.25f, 0.35f);
        var atk2 = CreateCombo("Combo_Atk2", "Atk2", C("PushButton"), null, 0.25f, 0.35f);
        var atk3 = CreateCombo("Combo_Atk3", "Atk3", C("PickUp"), C("PushButton"), 0.35f, 0.35f);
        var skill = CreateCombo("Combo_Skill", "Skill", C("ThrowSingle"), null, 0.5f, 0.4f);

        var container = ScriptableObject.CreateInstance<ComboContainerData>();
        container.comboDates = new List<ComboData> { atk1, atk2, atk3 };
        container.ForwardATKData = atk1;
        AssetDatabase.DeleteAsset(LightComboPath);
        AssetDatabase.CreateAsset(container, LightComboPath);
        container.Init();

        // ---------------- PlayerSO ----------------
        var movementData = new PlayerMovementData();
        SetAuto(movementData, "idleData", MakeState<PlayerIdleData>(C("Idle"), 0.25f, 0.05f));
        var walk = MakeState<PlayerWalkData>(C("WalkFwd"), 0.15f, 0.06f);
        SetAuto(walk, "walkStopClip", C("WalkFwdStop_LU"));
        SetAuto(movementData, "walkData", walk);
        SetAuto(movementData, "dashData", MakeState<PlayerDashData>(C("RunFwdStart"), 0.1f, 0.05f));

        var dodge = MakeState<PlayerDodgeData>(C("Slide"), 0.1f, 0.05f);
        SetAuto(dodge, "invincibleWindow", 0.4f);
        SetAuto(dodge, "cooldown", 1.2f);
        SetAuto(movementData, "dodgeData", dodge);

        SetAuto(movementData, "hurtData", MakeState<PlayerHurtData>(C("LandHarder"), 0.1f, 0.05f));
        SetAuto(movementData, "deadData", MakeState<PlayerDeadData>(C("LayDown_Back"), 0.2f, 0f));
        SetAuto(movementData, "movementNullData", new PlayerMovementNullData());

        var comboSo = new PlayerComboSOData();
        SetAuto(comboSo, "lightCombo", container);
        SetAuto(comboSo, "skillCombo", skill);
        SetAuto(comboSo, "autoAimEnabled", true);
        SetAuto(comboSo, "autoAimRadius", 6f);
        SetAuto(comboSo, "autoAimMaxAngle", 180f);

        var comboData = new PlayerComboData();
        SetAuto(comboData, "comboData", comboSo);

        var stats = new CharacterStatsData();
        SetAuto(stats, "baseAttack", 100f);
        SetAuto(stats, "baseDefense", 0f);
        SetAuto(stats, "baseMaxHP", 300f);

        var playerSo = ScriptableObject.CreateInstance<PlayerSO>();
        SetAuto(playerSo, "movementData", movementData);
        SetAuto(playerSo, "comboData", comboData);
        SetAuto(playerSo, "statsData", stats);
        AssetDatabase.DeleteAsset(PlayerSOpath);
        AssetDatabase.CreateAsset(playerSo, PlayerSOpath);

        EditorUtility.SetDirty(container);
        EditorUtility.SetDirty(playerSo);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // ---------------- 玩家 prefab ----------------
        var playerPrefab = BuildPlayerPrefab(playerSo);

        // ---------------- 场景 ----------------
        BuildScene(playerPrefab);

        Debug.Log("[BattleTest] P4 测试场景构建完成");
    }

    // =====================================================================
    // 资源
    // =====================================================================
    private static Dictionary<string, AnimationClip> LoadClips()
    {
        var map = new Dictionary<string, AnimationClip>();
        foreach (var path in new[] { Fbx1, Fbx2 })
        {
            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
            {
                if (clip == null || clip.name.StartsWith("__preview__")) continue;
                if (!map.ContainsKey(clip.name)) map.Add(clip.name, clip);
            }
        }
        return map;
    }

    private static ClipTransition Transition(AnimationClip clip)
    {
        if (clip == null) return null;
        return new ClipTransition { Clip = clip };
    }

    private static ComboData CreateCombo(string assetName, string comboName, AnimationClip attackClip,
        AnimationClip endClip, float damageMultiplier, float hitFrameTime)
    {
        var data = ScriptableObject.CreateInstance<ComboData>();
        SetField(data, "_attackStyle", AttackStyle.Attack);
        SetField(data, "_comboName", comboName);
        SetField(data, "_attackClip", Transition(attackClip));
        SetField(data, "_attackEndClip", Transition(endClip));
        SetField(data, "_damageMultiplier", damageMultiplier);
        SetField(data, "_critRate", 0f);
        SetField(data, "_critMultiplier", 1f);
        SetField(data, "_hitBuff", null);
        SetField(data, "_attackDistance", 1.4f);
        SetField(data, "_comboOffset", 0.9f);
        SetField(data, "_enemyLayer", (LayerMask)(1 << 7));   // enemy
        SetField(data, "_hitFrameTime", hitFrameTime);
        SetField(data, "_pauseFrameTime", 0.06f);
        SetField(data, "_shakeForce", 0f);
        SetField(data, "_linkCancelTime", 0.55f);
        SetField(data, "_comboColdTime", 0f);
        SetField(data, "_universalSound", SoundStyle.Null);
        data.characterName = CharacterNameList.AnBi;

        string path = TestDir + "/" + assetName + ".asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(data, path);
        return data;
    }

    private static T MakeState<T>(AnimationClip clip, float fadeDuration, float rotationTime)
        where T : PlayerStateData, new()
    {
        var data = new T();
        SetAuto(data, "animationClip", clip);
        SetAuto(data, "playSpeed", 1f);
        SetAuto(data, "fadeDuration", fadeDuration);
        SetAuto(data, "rotationTime", rotationTime);
        return data;
    }

    // =====================================================================
    // 玩家
    // =====================================================================
    private static GameObject BuildPlayerPrefab(PlayerSO playerSo)
    {
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerModelPath);
        if (fbx == null)
        {
            Debug.LogError($"[BattleTest] 找不到玩家模型 {PlayerModelPath}");
            return null;
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
        instance.name = "TestPlayer545";

        foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = 3;   // player

        var controller = instance.GetComponent<CharacterController>();
        if (controller == null) controller = instance.AddComponent<CharacterController>();
        controller.center = new Vector3(0f, 0.9f, 0f);
        controller.height = 1.8f;
        controller.radius = 0.35f;
        controller.skinWidth = 0.02f;

        if (instance.GetComponent<AnimancerComponent>() == null)
            instance.AddComponent<AnimancerComponent>();

        var player = instance.GetComponent<Player>();
        if (player == null) player = instance.AddComponent<Player>();
        var so = new SerializedObject(player);
        var playerSoProp = so.FindProperty("playerSO");
        if (playerSoProp != null) playerSoProp.objectReferenceValue = playerSo;
        so.ApplyModifiedPropertiesWithoutUndo();
        player.playerCameraUtility = new PlayerCameraUtility();

        if (instance.GetComponent<HealthModel>() == null) instance.AddComponent<HealthModel>();
        if (instance.GetComponent<AttributeComponent>() == null) instance.AddComponent<AttributeComponent>();
        if (instance.GetComponent<BuffComponent>() == null) instance.AddComponent<BuffComponent>();
        if (instance.GetComponent<CharacterState>() == null) instance.AddComponent<CharacterState>();

        AssetDatabase.DeleteAsset(PlayerPrefabPath);
        PrefabUtility.SaveAsPrefabAsset(instance, PlayerPrefabPath);
        UnityEngine.Object.DestroyImmediate(instance);

        Debug.Log($"[BattleTest] 玩家 prefab -> {PlayerPrefabPath}");
        return AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
    }

    // =====================================================================
    // 场景
    // =====================================================================
    private static void BuildScene(GameObject playerPrefab)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 地面（层 6 environment：GroundDetection/斜坡检测用）
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.layer = 6;
        ground.transform.localScale = new Vector3(12f, 1f, 12f);

        // 主光
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1f;
        light.shadows = LightShadows.None;
        lightGo.transform.rotation = Quaternion.Euler(50f, 330f, 0f);

        // 座机位相机（Player.TryBindSceneCamera 按名字找 "Camera" / "thirdcamera"）
        var camGo = new GameObject("Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.09f, 0.11f, 0.14f);
        camGo.AddComponent<AudioListener>();
        camGo.AddComponent<CinemachineBrain>();
        camGo.transform.position = new Vector3(0f, 2.2f, -4.5f);
        camGo.transform.rotation = Quaternion.Euler(10f, 0f, 0f);

        // 玩家
        var playerGo = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
        playerGo.name = "Player";
        playerGo.transform.position = Vector3.zero;
        playerGo.transform.rotation = Quaternion.Euler(0f, 0f, 0f);

        // 跟随相机
        var vcamGo = new GameObject("thirdcamera");
        var vcam = vcamGo.AddComponent<CinemachineFreeLook>();
        vcam.Follow = playerGo.transform;
        vcam.LookAt = playerGo.transform;
        vcam.m_XAxis.m_InvertInput = false;
        vcam.m_YAxis.m_InvertInput = true;
        vcam.m_XAxis.m_MaxSpeed = 300f;
        vcam.m_YAxis.m_MaxSpeed = 2f;
        vcam.m_YAxis.m_MinValue = 0.1f;
        vcam.m_YAxis.m_MaxValue = 0.9f;
        var orbits = vcam.m_Orbits;
        orbits[0].m_Height = 1.7f; orbits[0].m_Radius = 2.8f;
        orbits[1].m_Height = 2.1f; orbits[1].m_Radius = 3.4f;
        orbits[2].m_Height = 2.5f; orbits[2].m_Radius = 4.0f;
        vcam.m_Orbits = orbits;

        // 木桩（层 7 enemy）
        var dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dummy.name = "Dummy";
        dummy.layer = 7;
        dummy.transform.position = new Vector3(0f, 1f, 3f);
        var dummyHealth = dummy.AddComponent<HealthModel>();
        var dummyAttr = dummy.AddComponent<AttributeComponent>();
        dummy.AddComponent<BuffComponent>();
        dummy.AddComponent<CharacterState>();
        dummyHealth.InitStats(300f);
        dummyAttr.SetBase(AttrType.Attack, 50f);
        dummyAttr.SetBase(AttrType.Defense, 0f);

        var dummyTest = dummy.AddComponent<BattleTestDummy>();
        dummyTest.target = playerGo.GetComponent<Player>();
        dummyTest.autoAttack = false;

        // 引导
        var bootGo = new GameObject("BattleTestBootstrap");
        var boot = bootGo.AddComponent<BattleTestBootstrap>();
        boot.player = playerGo.GetComponent<Player>();
        boot.dummy = dummyHealth;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[BattleTest] 场景 -> {ScenePath}");
    }

    // =====================================================================
    // 反射赋值（数据类属性只有 private set，编辑器脚本直接写 backing field）
    // =====================================================================
    private static void SetAuto(object target, string propertyName, object value)
    {
        var type = target.GetType();
        var field = FindField(type, $"<{propertyName}>k__BackingField");
        if (field == null)
            throw new Exception($"[BattleTest] 找不到属性 backing field：{type.Name}.{propertyName}");
        field.SetValue(target, value);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var type = target.GetType();
        var field = FindField(type, fieldName);
        if (field == null)
            throw new Exception($"[BattleTest] 找不到字段：{type.Name}.{fieldName}");
        field.SetValue(target, value);
    }

    /// <summary>沿继承链查找私有字段（数据类属性定义在基类 PlayerStateData 上）。</summary>
    private static FieldInfo FindField(Type type, string fieldName)
    {
        while (type != null)
        {
            var field = type.GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        string parent = path.Substring(0, slash);
        string leaf = path.Substring(slash + 1);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
