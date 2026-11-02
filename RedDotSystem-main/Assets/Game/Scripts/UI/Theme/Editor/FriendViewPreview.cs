using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 好友面板的离线预览渲染（只用于开发自查，不进包）。
///
/// 为什么不用 Play 截图：面板是 ScreenSpace 画布，Edit 模式直接截 Game View 看不到；
/// 这里临时搭一个空场景（正交相机 + 1920x1080 画布 + 实例化预制体），
/// 用 RenderTexture 出图，跑完把场景还原回 zhuye，主流程的 Play 会话不受影响。
///
/// 两版式：friend_skin_list.png（列表 + 空聊天态）、friend_skin_chat.png（带三条示例气泡）。
/// 菜单：Tools/UI/预览好友面板
/// </summary>
public static class FriendViewPreview
{
    private const string PrefabPath = "Assets/Game/UI/Friends/FriendView.prefab";
    private const string OutDir = "screenshots";
    private const int Width = 1400;
    private const int Height = 900;

    [MenuItem("Tools/UI/预览好友面板")]
    public static void RenderFromMenu() => Render();

    public static void Render()
    {
        if (!Directory.Exists(OutDir)) Directory.CreateDirectory(OutDir);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("PreviewCamera", typeof(Camera));
        var cam = camGo.GetComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 540f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.11f, 0.10f, 0.09f, 1f);
        cam.transform.position = new Vector3(0f, 0f, -20f);
        camGo.tag = "MainCamera";

        var canvasGo = new GameObject("PreviewCanvas", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 10f;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var view = PrefabUtility.InstantiatePrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), canvasGo.transform) as GameObject;
        SetLayer(view, 5);

        Transform panel = view.transform.Find("FriendPanel");
        Sprite background = Resources.Load<Sprite>("ui/Theme/bg_friend");
        if (panel != null && background != null)
        {
            var panelImage = panel.GetComponent<Image>();
            panelImage.sprite = background;
            panelImage.type = Image.Type.Simple;
            panelImage.color = Color.white;
        }

        MockFriends(panel);
        Render(cam, Path.Combine(OutDir, "friend_skin_list.png"));

        if (panel != null)
        {
            Transform hint = panel.Find("ChatEmptyHint");
            if (hint != null) hint.gameObject.SetActive(false);

            Transform title = panel.Find("ChatTitle");
            if (title != null) title.GetComponent<TMPro.TMP_Text>().text = "player8229";

            AddBubble(panel, "MessageList/Viewport/Content/MessageLeftTemplate", "今晚一起打副本吗？", false, 220f, -30f);
            AddBubble(panel, "MessageList/Viewport/Content/MessageRightTemplate", "好呀，八点见！", true, 190f, -82f);
            AddBubble(panel, "MessageList/Viewport/Content/MessageLeftTemplate", "那我先去做个任务～", false, 230f, -134f);
        }
        Render(cam, Path.Combine(OutDir, "friend_skin_chat.png"));

        EditorSceneManager.OpenScene("Assets/Game/Scenes/zhuye.scene");
        Debug.Log("[FriendSkin] 预览完成（screenshots/friend_skin_list.png / friend_skin_chat.png）");
    }

    private static void MockFriends(Transform panel)
    {
        if (panel == null) return;

        Transform content = panel.Find("ListPage/FriendList/Viewport/Content");
        if (content == null) return;
        GameObject template = content.Find("FriendItemTemplate").gameObject;

        string[] names = { "player8229", "yyy", "y", "ttt", "chate2ea" };
        int[] ids = { 4, 5, 6, 7, 8 };
        Sprite normal = Resources.Load<Sprite>("ui/Friends/row");
        Sprite selected = Resources.Load<Sprite>("ui/Friends/row_sel");

        for (int i = 0; i < names.Length; i++)
        {
            var row = Object.Instantiate(template, content);
            row.SetActive(true);
            row.name = "Friend_" + names[i];

            var rt = (RectTransform)row.transform;
            // 与 VirtualScrollList.TakeOrCreate 的锚定约定一致
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(296f, 44f);
            rt.anchoredPosition = new Vector2(0f, -(i * 48f + 24f));

            var rowImage = row.GetComponent<Image>();
            rowImage.sprite = i == 0 ? selected : normal;
            rowImage.type = Image.Type.Sliced;

            SetChildText(row.transform, "NameText", names[i]);
            SetChildText(row.transform, "IdText", "ID " + ids[i]);
            SetChildText(row.transform, "Avatar/AvatarInitial", names[i].Substring(0, 1).ToUpperInvariant());

            Transform dot = row.transform.Find("OnlineDot");
            if (dot != null)
                dot.GetComponent<Image>().color = i < 2
                    ? new Color(0.027f, 0.757f, 0.376f, 1f)
                    : new Color(0.62f, 0.62f, 0.62f, 1f);

            Transform badge = row.transform.Find("UnreadBadge");
            if (badge != null)
            {
                badge.gameObject.SetActive(i == 1);
                SetChildText(badge, "UnreadBadgeText", "3");            }
        }
    }

    private static void AddBubble(Transform panel, string templatePath, string text, bool mine, float width, float y)
    {
        Transform template = panel.Find(templatePath);
        if (template == null) return;

        var bubble = Object.Instantiate(template.gameObject, template.parent);
        bubble.SetActive(true);

        var rt = (RectTransform)bubble.transform;
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, 44f);

        // 与 FriendUIController.BindChatBubble 的同款摆位：距聊天区左右边缘 12px
        RectTransform content = (RectTransform)template.parent;
        float areaWidth = content.rect.width > 1f ? content.rect.width : 324f;
        float x = areaWidth * 0.5f - width * 0.5f - 12f;
        rt.anchoredPosition = new Vector2(mine ? x : -x, y);

        var label = bubble.transform.Find("BubbleText").GetComponent<TMPro.TMP_Text>();
        label.text = text;
        label.rectTransform.sizeDelta = new Vector2(width - 24f, 24f);
    }

    private static void SetChildText(Transform parent, string path, string value)
    {
        Transform child = parent.Find(path);
        if (child == null) return;
        var text = child.GetComponent<TMPro.TMP_Text>();
        if (text != null) text.text = value;
    }

    private static void Render(Camera camera, string path)
    {
        Canvas.ForceUpdateCanvases();

        var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        RenderTexture previous = camera.targetTexture;
        camera.targetTexture = rt;
        camera.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());

        camera.targetTexture = previous;
        RenderTexture.active = null;
        Object.DestroyImmediate(tex);
        rt.Release();
        Object.DestroyImmediate(rt);
    }

    private static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayer(child.gameObject, layer);
    }
}
