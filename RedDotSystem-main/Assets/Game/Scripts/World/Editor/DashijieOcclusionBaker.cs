using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 大世界遮挡剔除烘焙（技术选型评估里的 P0）。
///
/// 依据 Docs/Documents/大世界技术选型评估(dashijie).md：
///   城市建筑互相遮挡极强，但场景没有任何遮挡数据，出生点 174 个渲染器全部提交。
///   做法：给城市实例与 NPC 容器标记 OccluderStatic / OccludeeStatic / BatchingStatic，
///   然后烘焙 Occlusion Culling（默认参数先跑一版）。
///
/// 为什么做成菜单而不是脚本自动跑：烘焙是分钟级任务，且**切场景、进 Play 都会把烘焙取消**
/// （Unity 会弹「Computing Occlusion / Entering play mode will cancel the computation」）。
/// 它需要一个编辑器空闲的时间窗口，所以交给人在方便时点一下，进度与取消都由 Unity 自己的窗口管。
/// </summary>
public static class DashijieOcclusionBaker
{
    private const string ScenePath = "Assets/Game/Scenes/dashijie.scene";
    private const string CityRootName = "未命名 (1)";

    private static StaticEditorFlags BakeFlags =>
        StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.BatchingStatic;

    [MenuItem("Tools/大世界/烘焙遮挡剔除（P0）")]
    private static void Bake()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("遮挡剔除烘焙", "请先退出 Play 模式再烘焙。", "好");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        if (!EditorUtility.DisplayDialog(
                "遮挡剔除烘焙",
                $"目标场景：{ScenePath}\n"
                + "将做两件事：\n"
                + "  1) 给城市实例与 NPC 容器标记 OccluderStatic / OccludeeStatic / BatchingStatic\n"
                + "  2) 烘焙 Occlusion Culling（分钟级，期间不要切场景或进 Play，否则会被取消）\n\n"
                + "开始吗？",
                "开始烘焙", "取消"))
            return;

        int marked = MarkStatics(scene);
        EnableCameraOcclusion();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[大世界遮挡] 已标记 {marked} 个 GameObject 为静态并保存场景");

        StaticOcclusionCulling.SetDefaultOcclusionBakeSettings();
        Debug.Log($"[大世界遮挡] 烘焙参数 smallestOccluder={StaticOcclusionCulling.smallestOccluder} "
                  + $"smallestHole={StaticOcclusionCulling.smallestHole} backfaceThreshold={StaticOcclusionCulling.backfaceThreshold}");

        bool started = StaticOcclusionCulling.GenerateInBackground();
        Debug.Log(started
            ? "[大世界遮挡] 后台烘焙已启动；结束后用菜单「Tools/大世界/检查遮挡剔除产物」查看结果"
            : "[大世界遮挡] 烘焙未能启动（可能已有烘焙在跑）");
    }

    [MenuItem("Tools/大世界/检查遮挡剔除产物")]
    private static void Inspect()
    {
        // 先确认产物本身：当前场景的 umbraDataSize 只有在「已打开带数据的场景」时才有值，
        // 必须与场景资产里 m_OcclusionCullingData 的实际引用一起看，否则会误判成"有数据"
        int dataSize = StaticOcclusionCulling.umbraDataSize;
        Scene active = SceneManager.GetActiveScene();
        Debug.Log($"[大世界遮挡] isRunning={StaticOcclusionCulling.isRunning} umbraDataSize={dataSize} 当前场景={active.name}");

        if (!File.Exists(ScenePath))
        {
            Debug.LogWarning("[大世界遮挡] 找不到场景文件");
            return;
        }

        // 只读解析场景资产头部的 OcclusionCullingSettings，不打开场景（避免顺带改脏）
        string occlLine = null;
        string enabledLine = null;
        using (var reader = new StreamReader(ScenePath))
        {
            for (int i = 0; i < 40 && !reader.EndOfStream; i++)
            {
                string line = reader.ReadLine() ?? string.Empty;
                if (line.StartsWith("  m_OcclusionCullingData:")) occlLine = line.Trim();
                if (line.StartsWith("  m_OcclusionEnabled:")) enabledLine = line.Trim();
            }
        }

        bool hasBinding = occlLine != null && !occlLine.EndsWith("{fileID: 0}");
        Debug.Log($"[大世界遮挡] 场景资产：{occlLine} / {enabledLine}");
        if (!hasBinding || dataSize <= 0)
        {
            Debug.Log("[大世界遮挡] 没有可用的遮挡数据（场景未绑定数据资产），运行时不会剔除");
            return;
        }

        Scene target = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int statics = 0, total = 0;
        foreach (GameObject root in target.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                total++;
                if ((GameObjectUtility.GetStaticEditorFlags(t.gameObject) & StaticEditorFlags.OccluderStatic) != 0)
                    statics++;
            }
        }
        Camera cam = Camera.main;
        Debug.Log($"[大世界遮挡] 场景 GameObject={total} 标记 OccluderStatic 的={statics} "
                  + $"相机={(cam != null ? cam.name + " useOcclusionCulling=" + cam.useOcclusionCulling : "<无>")}");
        Debug.Log("[大世界遮挡] 已有遮挡数据，可以进 Play 量可见渲染器数量");
    }

    /// <summary>给城市实例与场景里所有 NPC 容器打静态标记，返回标记的 GameObject 数</summary>
    private static int MarkStatics(Scene scene)
    {
        int marked = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            // 城市实例整体标记；NPC/角色容器也要能被遮挡（否则它们不会被剔除）
            bool isCity = root.name == CityRootName;
            bool isNpc = root.name.StartsWith("WorldNpc", StringComparison.Ordinal);

            if (isCity)
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, BakeFlags);
                    marked++;
                }
            }
            else if (isNpc)
            {
                GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.OccludeeStatic);
                marked++;
            }
        }
        return marked;
    }

    private static void EnableCameraOcclusion()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("[大世界遮挡] 场景里没有主相机，跳过后一步（相机 useOcclusionCulling 需为 true 才生效）");
            return;
        }
        cam.useOcclusionCulling = true;
        EditorUtility.SetDirty(cam);
        Debug.Log($"[大世界遮挡] {cam.name}.useOcclusionCulling=true");
    }
}