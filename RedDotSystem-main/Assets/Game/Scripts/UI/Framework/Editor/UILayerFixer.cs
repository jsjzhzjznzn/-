#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using SkierFramework;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 批量把已登记 UI 预制体的节点 layer 刷成 UI(5)。
/// 运行期 UIView.OnInit 会 SetLayerRecursively 兜住，所以这是"预制体规范化"，不影响功能；
/// 但不刷的话：预制体在编辑器里预览会被 UI 相机裁掉、和场景物体混在一起排查困难。
/// 菜单：Tools/UI/批量修复 UI 层(Layer 5)
/// 幂等：已经全部是 5 的预制体不会重写（避免无意义的 prefab diff）。
/// </summary>
public static class UILayerFixer
{
    private const int UILayer = 5;
    private const string ConfigPath = "Assets/Game/UI/UIConfig.json";

    [MenuItem("Tools/UI/批量修复 UI 层(Layer 5)")]
    public static void FixAll()
    {
        if (!File.Exists(ConfigPath))
        {
            Debug.LogError("[UILayerFixer] 找不到 " + ConfigPath);
            return;
        }

        UIConfigJsonList config = JsonUtility.FromJson<UIConfigJsonList>(File.ReadAllText(ConfigPath));
        if (config == null || config.items == null)
        {
            Debug.LogError("[UILayerFixer] UIConfig.json 解析失败");
            return;
        }

        var fixedViews = new List<string>();
        var alreadyOk = new List<string>();
        var missing = new List<string>();

        foreach (UIConfigJson item in config.items)
        {
            if (item == null || string.IsNullOrEmpty(item.path)) continue;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(item.path) == null)
            {
                missing.Add(item.uiType);
                continue;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(item.path);
            if (root == null)
            {
                missing.Add(item.uiType);
                continue;
            }

            try
            {
                int changed = 0;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.gameObject.layer == UILayer) continue;
                    t.gameObject.layer = UILayer;
                    changed++;
                }

                if (changed == 0)
                {
                    alreadyOk.Add(item.uiType);
                    continue;
                }

                PrefabUtility.SaveAsPrefabAsset(root, item.path);
                fixedViews.Add(item.uiType + "(" + changed + ")");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // 只 Refresh，不调 SaveAssets：SaveAssets 会把编辑器里其它"已加载且 dirty"的预制体
        // 一起落盘（等于替用户提交他没保存的改动），本工具只该改自己加载的那几个
        AssetDatabase.Refresh();

        Debug.Log("[UILayerFixer] 已修：" + (fixedViews.Count == 0 ? "无" : string.Join(", ", fixedViews))
                  + "｜本来就合规：" + alreadyOk.Count + " 个"
                  + (missing.Count == 0 ? "" : "｜找不到预制体：" + string.Join(", ", missing)));
    }
}
#endif
