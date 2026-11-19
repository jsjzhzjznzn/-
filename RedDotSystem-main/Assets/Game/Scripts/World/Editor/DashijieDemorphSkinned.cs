using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 大世界静态建筑去蒙皮（技术选型评估里的 P2）。
///
/// 背景：城市实例的 239 个建筑用的是 SkinnedMeshRenderer，但没有任何动画——祖先链上没有
/// Animator、骨骼全部处于绑定姿态、每个网格只挂 1 根骨骼。这些是导出工具产出的伪蒙皮网格，
/// 运行时每帧仍要走蒙皮矩阵，而且 Umbra 遮挡剔除只收录 MeshRenderer，蒙皮网格不参与烘焙。
///
/// 做法：因为这些 SMR 的 sharedMesh 就是 FBX 里的原始 Mesh 资产（同一个实例）、骨骼处于
/// 绑定姿态且没有 BlendShape，直接换成 MeshFilter + MeshRenderer 引用同一个网格即可，
/// 外观逐像素一致，不需要 BakeMesh，也不产生新的网格资产。
///
/// 幂等：只处理仍是 SkinnedMeshRenderer 且满足「无 Animator、骨骼在绑定姿态」的物体，
/// 已转换的自动跳过，可以重复执行。
/// </summary>
public static class DashijieDemorphSkinned
{
    private const string ScenePath = "Assets/Game/Scenes/dashijie.scene";
    private const string CityRootName = "未命名 (1)";

    private static StaticEditorFlags BakeFlags =>
        StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.BatchingStatic;

    [MenuItem("Tools/大世界/静态建筑去蒙皮（P2）")]
    private static void Convert()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("去蒙皮", "请先退出 Play 模式。", "好");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        GameObject city = FindCityRoot(scene);
        if (city == null)
        {
            EditorUtility.DisplayDialog("去蒙皮", $"场景里找不到城市根节点「{CityRootName}」", "好");
            return;
        }

        SkinnedMeshRenderer[] smrs = city.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (smrs.Length == 0)
        {
            EditorUtility.DisplayDialog("去蒙皮", "城市下已经没有 SkinnedMeshRenderer，无需处理。", "好");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "静态建筑去蒙皮（P2）",
                $"将把 {smrs.Length} 个 SkinnedMeshRenderer 换成 MeshFilter + MeshRenderer。\n\n"
                + "前提核对：无 Animator、骨骼处于绑定姿态、无 BlendShape 的物体才会转换，\n"
                + "其余会被跳过并计入报告。\n\n继续吗？",
                "开始转换", "取消"))
            return;

        var converted = new List<string>();
        var skipped = new List<string>();
        int missingMesh = 0;

        foreach (SkinnedMeshRenderer smr in smrs)
        {
            GameObject go = smr.gameObject;

            if (smr.sharedMesh == null) { missingMesh++; skipped.Add(go.name + "（无网格）"); continue; }
            if (go.GetComponentInParent<Animator>() != null) { skipped.Add(go.name + "（有 Animator）"); continue; }
            if (!IsAtBindPose(smr)) { skipped.Add(go.name + "（骨骼偏离绑定姿态）"); continue; }
            if (smr.sharedMesh.blendShapeCount > 0) { skipped.Add(go.name + "（有 BlendShape）"); continue; }

            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (filter == null) filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = smr.sharedMesh;

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = smr.sharedMaterials;
            renderer.shadowCastingMode = smr.shadowCastingMode;
            renderer.receiveShadows = smr.receiveShadows;
            renderer.lightProbeUsage = smr.lightProbeUsage;
            renderer.reflectionProbeUsage = smr.reflectionProbeUsage;
            renderer.probeAnchor = smr.probeAnchor;
            renderer.renderingLayerMask = smr.renderingLayerMask;
            renderer.enabled = smr.enabled;

            UnityEngine.Object.DestroyImmediate(smr, true);

            // 静态标记重新打一遍：新加的 MeshRenderer 才能参与 Occlusion Culling 烘焙
            GameObjectUtility.SetStaticEditorFlags(go, BakeFlags);
            converted.Add(go.name);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"[P2 去蒙皮] 转换 {converted.Count} 个，跳过 {skipped.Count} 个，无网格 {missingMesh} 个");
        if (skipped.Count > 0)
            Debug.Log($"[P2 去蒙皮] 跳过清单：{string.Join("、", skipped.GetRange(0, Mathf.Min(20, skipped.Count)))}"
                      + (skipped.Count > 20 ? $" ……共 {skipped.Count} 个" : string.Empty));

        Verify(scene);
    }

    [MenuItem("Tools/大世界/检查去蒙皮结果")]
    private static void Inspect()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("检查", "请在编辑模式下检查。", "好");
            return;
        }

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Verify(SceneManager.GetActiveScene());
    }

    private static void Verify(Scene scene)
    {
        GameObject city = FindCityRoot(scene);
        if (city == null) return;

        int smr = city.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
        MeshRenderer[] mrs = city.GetComponentsInChildren<MeshRenderer>(true);
        int missingMesh = 0;
        foreach (MeshRenderer mr in mrs)
        {
            MeshFilter filter = mr.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) missingMesh++;
        }

        Debug.Log($"[P2 去蒙皮] 城市 SkinnedMeshRenderer={smr}  MeshRenderer={mrs.Length}  缺网格={missingMesh}");
        Debug.Log(smr == 0
            ? "[P2 去蒙皮] 已全部转换，可以重新烘焙遮挡剔除（Tools/大世界/烘焙遮挡剔除（P0））"
            : "[P2 去蒙皮] 仍有 SkinnedMeshRenderer 未转换");
    }

    private static GameObject FindCityRoot(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == CityRootName) return root;
        }
        return null;
    }

    /// <summary>
    /// 骨骼是否全部处于绑定姿态：世界姿态 == bindpose 的逆
    /// 只要有一根偏离就返回 false（这样的网格烘焙后外观会变）
    /// </summary>
    private static bool IsAtBindPose(SkinnedMeshRenderer smr)
    {
        Mesh mesh = smr.sharedMesh;
        Transform[] bones = smr.bones;
        if (mesh == null || bones == null || bones.Length == 0) return true;

        Matrix4x4[] bindposes = mesh.bindposes;
        if (bindposes.Length != bones.Length) return false;

        float tolerance = 0.0001f;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) return false;
            Matrix4x4 current = smr.transform.worldToLocalMatrix * bones[i].localToWorldMatrix;
            Vector3 position = current.GetColumn(3);
            Vector3 bind = bindposes[i].inverse.GetColumn(3);
            if ((position - bind).sqrMagnitude > tolerance) return false;
        }
        return true;
    }
}
