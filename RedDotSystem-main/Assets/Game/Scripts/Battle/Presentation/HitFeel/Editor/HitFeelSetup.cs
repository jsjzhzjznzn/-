#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 打击感一键安装（可重复执行）：
///   1) 建/更新 HitFeelProfile 资产（含命中音效引用）；
///   2) 给 4 段安比连招填顿帧与震屏数值；
///   3) 挑战场景：给「怪兽」挂 HitReaction，删掉脚本已丢失的 CameraShakeManager。
///
/// 为什么用脚本而不是手改：这套参数会反复调，脚本能保证"重新执行一次就回到基线"；
/// 顿帧/震屏的数值也在这里集中定义，避免散落在 Inspector 里没有出处。
/// </summary>
public static class HitFeelSetup
{
    private const string ProfilePath = "Assets/Game/Resources/Battle/HitFeelProfile.asset";
    private const string ScenePath = "Assets/Game/Scenes/tiaozhan.scene";
    private const string MonsterName = "怪兽";
    private const string ComboDir = "Assets/Game/Scripts/playso/安比连招";
    private const string AudioDir = "Assets/Content/Resource/audoi/Audio绝区零/Combo";

    /// <summary>四段连招的顿帧（秒）与震屏力度（0~1）：轻 → 重</summary>
    private static readonly (string asset, float pause, float shake)[] ComboHitFeel =
    {
        ("anbi_ATK1", 0.05f, 0.15f),
        ("anbi_ATK2", 0.06f, 0.18f),
        ("anbi_ATK3", 0.08f, 0.30f),
        ("anbi_ATK4", 0.12f, 0.55f),
    };

    [MenuItem("Tools/Battle/打击感/安装（配置 + 场景）")]
    public static void Install()
    {
        HitFeelProfile profile = EnsureProfile();
        EnsureDeflectVfx();
        ApplyComboValues();
        ApplyScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[打击感] 安装完成：{ProfilePath}；连招 {ComboHitFeel.Length} 段；场景 {ScenePath}");
        if (profile != null) Selection.activeObject = profile;
    }

    [MenuItem("Tools/Battle/打击感/仅重填连招数值")]
    public static void ApplyComboValuesOnly()
    {
        ApplyComboValues();
        AssetDatabase.SaveAssets();
        Debug.Log("[打击感] 连招顿帧/震屏数值已重填");
    }

    // ──────────────────────────────
    // 配置
    // ──────────────────────────────

    private static HitFeelProfile EnsureProfile()
    {
        HitFeelProfile profile = AssetDatabase.LoadAssetAtPath<HitFeelProfile>(ProfilePath);
        if (profile == null)
        {
            EnsureFolder("Assets/Game/Resources");
            EnsureFolder("Assets/Game/Resources/Battle");
            profile = ScriptableObject.CreateInstance<HitFeelProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
        }

        AudioClip[] light = LoadClips($"{AudioDir}/Hit/IMPT-punch-{{0}}.wav", 1, 5);
        AudioClip[] heavy = LoadClips($"{AudioDir}/Block/IMPT-gut-kick-{{0}}.wav", 1, 3);

        if (light.Length > 0) profile.lightImpactClips = light;
        if (heavy.Length > 0) profile.heavyImpactClips = heavy;

        // 弹刀金属音：工程里现成的 IMPT-blocks（区块文件夹在 Combo/ATK）
        AudioClip[] deflect = LoadClips($"{AudioDir}/ATK/IMPT-blocks-{{0}}.wav", 1, 5);
        if (deflect.Length > 0) profile.deflectClips = deflect;

        EditorUtility.SetDirty(profile);
        return profile;
    }

    /// <summary>弹刀火花：复用拾取黄星迸发 prefab，注册成独立 key（独立池，语义清晰）</summary>
    private static void EnsureDeflectVfx()
    {
        const string catalogPath = "Assets/Game/Resources/VfxCatalog.asset";
        VfxCatalog catalog = AssetDatabase.LoadAssetAtPath<VfxCatalog>(catalogPath);
        if (catalog == null)
        {
            Debug.LogWarning($"[打击感] 找不到特效注册表：{catalogPath}");
            return;
        }

        if (catalog.TryGet(VfxKeys.DeflectSpark, out _)) return;

        const string prefabPath =
            "Assets/ThirdParty/TJGeneratorLibEffects/Prefabs/Interactive/Loot/ItemSparkleBurst/ItemSparkleBurstYellow.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[打击感] 找不到弹刀火花 prefab：{prefabPath}");
            return;
        }

        var serialized = new SerializedObject(catalog);
        SerializedProperty entries = serialized.FindProperty("entries");
        if (entries == null)
        {
            Debug.LogWarning("[打击感] VfxCatalog 结构变化，无法自动追加 deflect_spark");
            return;
        }

        int index = entries.arraySize;
        entries.arraySize = index + 1;
        SerializedProperty entry = entries.GetArrayElementAtIndex(index);
        entry.FindPropertyRelative("key").stringValue = VfxKeys.DeflectSpark;
        entry.FindPropertyRelative("prefab").objectReferenceValue = prefab;
        entry.FindPropertyRelative("prewarm").intValue = 1;
        entry.FindPropertyRelative("durationOverride").floatValue = 0.6f;
        entry.FindPropertyRelative("unscaled").boolValue = false;
        entry.FindPropertyRelative("scale").floatValue = 1f;
        entry.FindPropertyRelative("rotationOffsetEuler").vector3Value = Vector3.zero;
        entry.FindPropertyRelative("positionOffset").vector3Value = Vector3.zero;
        entry.FindPropertyRelative("comment").stringValue = "弹刀火花（打击感 P2）";
        serialized.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(catalog);
        Debug.Log("[打击感] 已注册特效 deflect_spark → ItemSparkleBurstYellow");
    }

    private static AudioClip[] LoadClips(string pattern, int from, int to)
    {
        var clips = new System.Collections.Generic.List<AudioClip>();
        for (int i = from; i <= to; i++)
        {
            string path = string.Format(pattern, i);
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null) clips.Add(clip);
            else Debug.LogWarning($"[打击感] 找不到音效：{path}");
        }
        return clips.ToArray();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;

        int split = path.LastIndexOf('/');
        if (split <= 0) return;

        string parent = path.Substring(0, split);
        string name = path.Substring(split + 1);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    // ──────────────────────────────
    // 连招数据
    // ──────────────────────────────

    private static void ApplyComboValues()
    {
        foreach ((string asset, float pause, float shake) in ComboHitFeel)
        {
            string path = $"{ComboDir}/{asset}.asset";
            ComboData data = AssetDatabase.LoadAssetAtPath<ComboData>(path);
            if (data == null)
            {
                Debug.LogWarning($"[打击感] 找不到连招配置：{path}");
                continue;
            }

            var serialized = new SerializedObject(data);
            SerializedProperty pauseProp = serialized.FindProperty("_pauseFrameTime");
            SerializedProperty shakeProp = serialized.FindProperty("_shakeForce");
            if (pauseProp == null || shakeProp == null)
            {
                Debug.LogWarning($"[打击感] {asset} 缺少 _pauseFrameTime/_shakeForce 字段");
                continue;
            }

            pauseProp.floatValue = pause;
            shakeProp.floatValue = shake;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(data);
        }
    }

    // ──────────────────────────────
    // 场景
    // ──────────────────────────────

    private static void ApplyScene()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            scene = EditorSceneManager.OpenScene(ScenePath);
        }

        bool dirty = false;

        GameObject monster = FindRoot(scene, MonsterName);
        if (monster == null)
        {
            Debug.LogError($"[打击感] 场景里找不到「{MonsterName}」");
        }
        else if (monster.GetComponent<HitReaction>() == null)
        {
            HitReaction reaction = monster.AddComponent<HitReaction>();
            reaction.jitterRootName = "Bip001";
            EditorUtility.SetDirty(reaction);
            dirty = true;
            Debug.Log($"[打击感] 已给「{MonsterName}」挂 HitReaction（闪白/抖动/击退）");
        }

        // 清掉脚本已丢失的 CameraShakeManager（guid e9b2e1a67c3727547baf4610f7848221 已不存在）
        GameObject shakeManager = FindRoot(scene, "CameraShakeManager");
        if (shakeManager != null)
        {
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(shakeManager);
            if (removed > 0)
            {
                Debug.Log($"[打击感] 已清理 CameraShakeManager 上 {removed} 个丢失脚本的组件");
                dirty = true;
            }

            if (shakeManager.transform.childCount == 0 && shakeManager.GetComponents<Component>().Length == 1)
            {
                Object.DestroyImmediate(shakeManager);
                Debug.Log("[打击感] 已删除空的 CameraShakeManager 物体");
                dirty = true;
            }
        }

        if (dirty)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
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
