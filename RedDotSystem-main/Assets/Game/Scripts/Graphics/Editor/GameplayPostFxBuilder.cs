#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// 3D 玩法场景的后处理兜底（画质分级系统方案 §5.4 的补充）。
///
/// 为什么需要它：fuben / tiaozhan 一直没有 Global Volume。档位服务虽然会在中/高档打开相机的
/// 后处理，但没有 Volume 就没有任何效果，画面是 URP 直出、和 duihua 的观感脱节。这里给两个
/// 场景各装一份轻量 Global Volume（ACES Tonemapping + 轻微 Bloom / Vignette），强度仍由
/// GraphicsQualityService 用 Volume.weight 按档缩放（低 0 / 中 0.75 / 高 1）。
///
/// 与 duihua_PostFX 的分工：那份偏叙事调色（白平衡/色相/更重的暗角）；这份只做「统一色彩响应
/// + 一点辉光和压角」，不覆盖美术打光。
///
/// 用菜单而不是手改场景 YAML：Volume 的字段是 package 内部序列化字段，手改容易漏 profile
/// 引用 / isGlobal / priority。幂等：已有 "Global Volume" 只重设参数，不重复创建。
/// </summary>
public static class GameplayPostFxBuilder
{
    private const string ProfilePath = "Assets/Game/Settings/Gameplay_PostFX.asset";
    private const string VolumeObjectName = "Global Volume";

    private static readonly string[] ScenePaths =
    {
        "Assets/Game/Scenes/tiaozhan.scene",
    };

    [MenuItem("Tools/Graphics/安装玩法场景后处理（tiaozhan）", priority = 6)]
    public static void InstallAll()
    {
        var profile = EnsureProfile();
        for (int i = 0; i < ScenePaths.Length; i++)
            InstallInto(ScenePaths[i], profile);

        AssetDatabase.SaveAssets();
        Debug.Log("[GraphicsQuality] 玩法场景后处理已安装：tiaozhan（profile=" + ProfilePath + "）");
    }

    /// <summary>创建或刷新共享的轻量后处理 Profile</summary>
    public static VolumeProfile EnsureProfile()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        bool created = false;
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            created = true;
        }

        var tonemap = GetOrAdd<Tonemapping>(profile);
        // 这两个场景的美术是按 URP 直出（无色调映射）打的：ACES 会把中灰整体压掉一半以上
        // （实测平均亮度 150 → 65），低档关后处理时明显比中/高档亮。Neutral 只做高光滚降、
        // 不压暗中灰（实测高档 128 vs 低档 149），再用下面的 postExposure 把差距补平。
        tonemap.active = true;
        tonemap.mode.overrideState = true;
        tonemap.mode.value = TonemappingMode.Neutral;

        // 曝光补偿：让"开后处理"和"关后处理"的亮度一致，任何档位都不会变暗
        var color = GetOrAdd<ColorAdjustments>(profile);
        color.active = true;
        color.postExposure.overrideState = true;
        color.postExposure.value = 0.22f;
        color.contrast.overrideState = true;
        color.contrast.value = 0f;
        color.saturation.overrideState = true;
        color.saturation.value = 0f;

        var bloom = GetOrAdd<Bloom>(profile);
        bloom.active = true;
        bloom.threshold.overrideState = true;
        bloom.threshold.value = 1.1f;
        bloom.intensity.overrideState = true;
        bloom.intensity.value = 0.15f;
        bloom.scatter.overrideState = true;
        bloom.scatter.value = 0.55f;
        bloom.highQualityFiltering.overrideState = true;
        bloom.highQualityFiltering.value = false;

        var vignette = GetOrAdd<Vignette>(profile);
        vignette.active = true;
        vignette.color.overrideState = true;
        vignette.color.value = Color.black;
        vignette.intensity.overrideState = true;
        // 0.25 在亮场景里压角压得偏重（平均亮度掉 7%），收到 0.10 只做轻微聚焦
        vignette.intensity.value = 0.10f;
        vignette.smoothness.overrideState = true;
        vignette.smoothness.value = 0.4f;
        vignette.rounded.overrideState = true;
        vignette.rounded.value = false;

        EditorUtility.SetDirty(profile);
        Debug.Log($"[GraphicsQuality] 后处理 Profile {(created ? "已创建" : "已刷新")}：{ProfilePath}");
        return profile;
    }

    /// <summary>
    /// 取或创建 Profile 里的组件。
    ///
    /// 不能直接用 VolumeProfile.Add：本工程的 URP 分支把 Add 里的 #if UNITY_EDITOR 块
    /// （AssetDatabase.AddObjectToAsset）删空了，Add 只把实例塞进内存 list，保存后
    /// components 里会留下一串 {fileID: 0}，运行时等于空 Profile。这里自己注册子资产。
    /// </summary>
    private static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        // 清掉以前误存成 null 的条目（Has/TryGet 会对 null 抛 NRE）
        for (int i = profile.components.Count - 1; i >= 0; i--)
            if (profile.components[i] == null) profile.components.RemoveAt(i);

        var existing = profile.components.Find(c => c is T) as T;
        if (existing != null) return existing;

        var component = ScriptableObject.CreateInstance<T>();
        component.name = typeof(T).Name;
        component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(component, profile);
        profile.components.Add(component);
        return component;
    }

    private static void InstallInto(string scenePath, VolumeProfile profile)
    {
        var loaded = SceneManager.GetSceneByPath(scenePath);
        bool alreadyLoaded = loaded.IsValid() && loaded.isLoaded;

        if (alreadyLoaded && loaded.isDirty)
        {
            Debug.LogWarning($"[GraphicsQuality] {scenePath} 已打开且有未保存改动，跳过（先保存场景再重跑菜单）");
            return;
        }

        var scene = alreadyLoaded ? loaded : EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
        try
        {
            var volume = FindGlobalVolume(scene);
            if (volume == null)
            {
                var go = new GameObject(VolumeObjectName);
                EditorSceneManager.MoveGameObjectToScene(go, scene);
                volume = go.AddComponent<Volume>();
            }

            volume.isGlobal = true;
            volume.priority = 0f;
            volume.weight = 1f;
            volume.sharedProfile = profile;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[GraphicsQuality] {Path.GetFileName(scenePath)}：{VolumeObjectName} → {ProfilePath}");
        }
        finally
        {
            if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true);
        }
    }

    private static Volume FindGlobalVolume(Scene scene)
    {
        var roots = scene.GetRootGameObjects();
        for (int i = 0; i < roots.Length; i++)
        {
            var v = roots[i].GetComponentInChildren<Volume>(true);
            if (v != null && v.isGlobal) return v;
        }
        return null;
    }
}
#endif
