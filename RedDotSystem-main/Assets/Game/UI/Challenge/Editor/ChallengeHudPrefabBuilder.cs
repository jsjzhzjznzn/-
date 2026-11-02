#if UNITY_EDITOR
using SkierFramework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 挑战 HUD 预制体构建器：给 ChallengeHudView.prefab 补右上角的"退出"按钮，并登记 ControlBinding。
/// 菜单：Tools/Challenge/重建 HUD 退出按钮
///
/// 为什么用脚本而不是手改预制体：按钮外观要跟结算面板的 HomeButton 完全一致（同底色/同字体/同圆角），
/// 克隆现有节点最稳；幂等执行，预制体改版或丢失后可一键重建。
/// 注意：ExitButton 挂在根节点下（不在 ResultPanel 里），战斗中也要常显。
/// </summary>
public static class ChallengeHudPrefabBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Challenge/ChallengeHudView.prefab";
    private const string TemplateName = "HomeButton";
    private const string ExitButtonName = "ExitButton";
    private const string BindingName = "exitButton";

    [MenuItem("Tools/Challenge/重建 HUD 退出按钮")]
    public static void Build()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[ChallengeHud] 打不开预制体：" + PrefabPath);
            return;
        }

        try
        {
            Transform template = FindDeep(root.transform, TemplateName);
            if (template == null)
            {
                Debug.LogError("[ChallengeHud] 预制体里找不到模板按钮：" + TemplateName);
                return;
            }

            GameObject exitGo = FindDeep(root.transform, ExitButtonName)?.gameObject;
            if (exitGo == null)
            {
                exitGo = Object.Instantiate(template.gameObject, root.transform, false);
                exitGo.name = ExitButtonName;
            }

            // 挂到根节点下：不在 ResultPanel 里，战斗中一直可见
            exitGo.transform.SetParent(root.transform, false);

            RectTransform rect = exitGo.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-48f, -48f);
            rect.sizeDelta = new Vector2(160f, 56f);
            rect.localScale = Vector3.one;
            rect.SetAsLastSibling();

            // 文本：克隆来的是"返回主页"，改成"退出"
            TMP_Text label = exitGo.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.text = "退出";
                label.fontSize = 26f;
            }

            // 底色压暗一点，避免抢计时/血条的注意力（结算按钮仍是蓝色）
            Image bg = exitGo.GetComponent<Image>();
            if (bg != null)
                bg.color = new Color(0.16f, 0.18f, 0.22f, 0.92f);

            UIControlData data = root.GetComponent<UIControlData>();
            Button button = exitGo.GetComponent<Button>();
            if (data != null && button != null)
                EnsureBinding(data, button);
            else
                Debug.LogError("[ChallengeHud] 缺 UIControlData 或 Button，ControlBinding 未登记");

            // 规范：UI 节点一律 layer=5(UI)
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = 5;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[ChallengeHud] 退出按钮已写入：" + PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>登记 exitButton → Button 的绑定（已存在则更新目标，不重复添加）</summary>
    private static void EnsureBinding(UIControlData data, Button button)
    {
        if (data.ctrlItemDatas == null)
            data.ctrlItemDatas = new System.Collections.Generic.List<CtrlItemData>();

        CtrlItemData item = data.ctrlItemDatas.Find(x => x != null && x.name == BindingName);
        if (item == null)
        {
            item = new CtrlItemData { name = BindingName, type = "Button" };
            data.ctrlItemDatas.Add(item);
        }

        item.type = "Button";
        item.targets = new Object[] { button };
        EditorUtility.SetDirty(data);
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
#endif
