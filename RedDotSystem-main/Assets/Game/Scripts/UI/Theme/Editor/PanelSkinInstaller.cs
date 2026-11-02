using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 给各业务面板统一套上 PanelSkin（浅色底图 + 顶部横幅）。
/// 可重复执行：已挂过的只更新横幅键，不会再叠第二份。
/// 菜单：Tools/UI/给面板套皮肤
/// </summary>
public static class PanelSkinInstaller
{
    private static readonly (string path, string bannerKey)[] Targets =
    {
        ("Assets/Game/UI/Friends/FriendView.prefab", "friend"),
        ("Assets/Game/UI/Mail/MailView.prefab", "mail"),
        ("Assets/Game/UI/Quest/QuestView.prefab", "quest"),
        ("Assets/Game/UI/Leaderboard/LeaderboardView.prefab", ""),
        ("Assets/Game/UI/Chat/WorldChatView.prefab", ""),
        ("Assets/Game/UI/Agent/AgentView.prefab", ""),
        ("Assets/Game/UI/Settings/SettingsView.prefab", "settings"),
        // BagView 的面板底是程序化瓦片，暂不套（避免浅色底图与格子底纹打架），后续单独处理
    };

    [MenuItem("Tools/UI/给面板套皮肤")]
    public static void Install()
    {
        int changed = 0;
        foreach (var (path, bannerKey) in Targets)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null)
            {
                Debug.LogWarning("[PanelSkinInstaller] 预制体不存在：" + path);
                continue;
            }

            var skin = contents.GetComponent<PanelSkin>();
            bool added = false;
            if (skin == null)
            {
                skin = contents.AddComponent<PanelSkin>();
                added = true;
            }

            var so = new SerializedObject(skin);
            so.FindProperty("bannerKey").stringValue = bannerKey;
            so.FindProperty("bannerHeight").floatValue = 110f;
            so.FindProperty("applyLightTheme").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();

            RestyleCloseButtons(contents);

            PrefabUtility.SaveAsPrefabAsset(contents, path);
            PrefabUtility.UnloadPrefabContents(contents);
            changed++;
            Debug.Log($"[PanelSkinInstaller] {System.IO.Path.GetFileName(path)} banner=[{bannerKey}] {(added ? "新增" : "更新")}");
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[PanelSkinInstaller] 完成，共处理 {changed} 个面板");
    }

    /// <summary>关闭键统一成"浅米圆形 + 深褐 ×"（原来各面板是红方/灰圆混用）</summary>
    private static void RestyleCloseButtons(GameObject root)
    {
        Sprite circle = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Game/Resources/ui/Theme/circle.png");
        int styled = 0;

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name;
            if (!n.Equals("CloseButton", System.StringComparison.OrdinalIgnoreCase)
                && !n.Equals("BtnClose", System.StringComparison.OrdinalIgnoreCase)
                && !n.Equals("CloseBtn", System.StringComparison.OrdinalIgnoreCase))
                continue;

            var img = t.GetComponent<Image>();
            if (img != null)
            {
                if (circle != null) img.sprite = circle;
                img.type = Image.Type.Simple;
                img.color = new Color(0.99f, 0.97f, 0.93f, 0.96f);
            }

            foreach (var text in t.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                text.color = UITheme.TextMain;
            }
            styled++;
        }

        if (styled > 0) Debug.Log($"[PanelSkinInstaller] 统一关闭键 {styled} 个（{root.name}）");
    }
}
