using System;
using System.Collections.Generic;
using Common.Network;
using Miemie.DialogSystem;
using MiMieMVVM;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 111 场景道具条件对话 demo：演示 DialogueFacts 全局事实黑板 + BagFactsBinder 链路
/// 玩法：登录 test → [发放铁矿×5] / [丢光铁矿] 改变背包镜像 → [和铁匠对话] →
/// 选项按 item_1012_count 条件自动隐藏（持有≥1 只显示"给看铁矿"分支，=0 只显示"没有铁矿"分支）
/// 注意：事实在对话开始瞬间播种（快照语义），对话进行中改背包不影响本次分支——这是设计决策
/// UI 全程序化创建；View 为自实现 IView（照 duihua GenshinDemo 先例，Runner 的 dialogView 留空自绑）
/// </summary>
public class ItemConditionDemoUI : MonoBehaviour, IView
{
    const int IronOreId = 1012;
    const string FactKey = "item_1012_count";

    private DialogueRunner runner;
    private DialogueViewModel vm;

    private TMP_Text statusText;
    private TMP_Text factsText;
    private TMP_Text speakerText;
    private TMP_Text dialogText;
    private GameObject dialogPanel;
    private RectTransform optionArea;
    private Button continueButton;

    private readonly List<GameObject> optionObjects = new();
    private Action<string> onBagStatus;

    public IViewModel ViewModel => vm;

    #region 生命周期

    private void Start()
    {
        runner = FindObjectOfType<DialogueRunner>();
        if (runner == null)
        {
            Debug.LogError("[ItemConditionDemo] 场景里没有 DialogueRunner，demo 无法运行");
            return;
        }

        BuildUI();
        Bind(runner.ViewModel);

        var net = ClientNetworkManager.Instance;
        if (net != null) net.LoginResponseReceived += OnLoginResponse;

        onBagStatus = msg => SetStatus(msg);
        if (BagManager.Instance != null)
            BagManager.Instance.StatusChanged += onBagStatus;
    }

    private void OnDestroy()
    {
        Unbind();
        var net = ClientNetworkManager.Instance;
        if (net != null) net.LoginResponseReceived -= OnLoginResponse;
        if (BagManager.Instance != null && onBagStatus != null)
            BagManager.Instance.StatusChanged -= onBagStatus;
    }

    private void Update()
    {
        if (factsText == null) return;
        var bag = BagManager.Instance;
        bool loaded = bag != null && bag.Capacity > 0;
        factsText.text = $"全局事实 {FactKey} = {DialogueFacts.GetIntFact(FactKey)}    （背包镜像：{(loaded ? "已拉取" : "未拉取")}）";
    }

    #endregion

    #region 网络操作

    private void OnLoginResponse(LoginResponse response)
    {
        var net = ClientNetworkManager.Instance;
        if (response.Success)
        {
            SetStatus($"登录成功 PlayerId={net?.MyPlayerId}，拉取背包…");
            BagManager.Instance?.RequestBag();
        }
        else
        {
            SetStatus($"登录失败：{response.Message}");
        }
    }

    private async void OnLoginClicked()
    {
        var net = ClientNetworkManager.Instance;
        if (net == null) { SetStatus("网络组件未自举"); return; }

        if (net.MyPlayerId > 0)
        {
            SetStatus("已登录，重新拉取背包");
            BagManager.Instance?.RequestBag();
            return;
        }

        SetStatus("连接 127.0.0.1:8888 …");
        bool ok;
        try { ok = await net.ConnectAsync("127.0.0.1", 8888); }
        catch (Exception e) { SetStatus($"连接异常：{e.Message}"); return; }

        if (!ok) { SetStatus("连接失败——服务端在跑吗？"); return; }
        SetStatus("已连接，登录中…");
        _ = net.SendLogin("test", "123456");
    }

    private void OnGrantClicked()
    {
        if (NotLoggedIn()) return;
        BagManager.Instance.Grant(IronOreId, 5);
        SetStatus("发放铁矿×5 请求已发送…");
    }

    private void OnDiscardAllClicked()
    {
        if (NotLoggedIn()) return;
        var bag = BagManager.Instance;
        if (bag.Capacity <= 0) { SetStatus("背包还没拉取，先点登录"); return; }

        int slots = 0;
        for (int i = 0; i < bag.Capacity; i++)
        {
            var slot = bag.GetSlot(i);
            if (slot == null || slot.ItemId != IronOreId || slot.Count <= 0) continue;
            bag.DiscardItem(i, slot.Count);
            slots++;
        }
        SetStatus(slots > 0 ? $"丢弃请求已发（{slots} 个槽）" : "背包里没有铁矿");
    }

    private void OnTalkClicked()
    {
        if (runner == null) return;
        SetStatus("对话开始（事实在此时播种快照）");
        runner.StartDialog();
    }

    private bool NotLoggedIn()
    {
        var net = ClientNetworkManager.Instance;
        if (net != null && net.MyPlayerId > 0) return false;
        SetStatus("请先登录");
        return true;
    }

    #endregion

    #region IView 实现

    public void Bind(IViewModel viewModel)
    {
        vm = viewModel as DialogueViewModel;
        if (vm == null) return;
        vm.NodeChanged += OnNodeChanged;
        vm.OptionsChanged += OnOptionsChanged;
        vm.DialogEnded += OnDialogEnded;
    }

    public void Unbind()
    {
        if (vm == null) return;
        vm.NodeChanged -= OnNodeChanged;
        vm.OptionsChanged -= OnOptionsChanged;
        vm.DialogEnded -= OnDialogEnded;
        vm = null;
    }

    private void OnNodeChanged(DialogueNodeData node)
    {
        ShowDialogPanel(true);
        speakerText.text = node.SpeakerName;
        dialogText.text = node.DialogText;
        // 普通节点显示"继续"，选项节点等 OptionsChanged 摆选项
        continueButton.gameObject.SetActive(!node.IsOptionNode);
        ClearOptions();
    }

    private void OnOptionsChanged(IReadOnlyList<DialogueTransLineData> choices)
    {
        ClearOptions();
        for (int i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var go = CreateOptionButton(choice.labelText, i);
            optionObjects.Add(go);
        }
    }

    private void OnDialogEnded()
    {
        ShowDialogPanel(false);
        ClearOptions();
    }

    #endregion

    #region UI 构建（全程序化，深色 HUD 风格照项目配色）

    private void BuildUI()
    {
        var canvasGo = new GameObject("DemoCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = 1f;
        canvasGo.AddComponent<GraphicRaycaster>();

        var root = canvasGo.transform as RectTransform;

        // 标题
        var title = CreateText(root, "Title", 16, new Color(0.88f, 0.88f, 0.88f));
        SetRect(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -12), new Vector2(640, 24));
        title.rectTransform.pivot = new Vector2(0, 1);
        title.text = "道具条件对话 Demo —— item_1012_count 条件分支（条件系统零改动）";
        title.alignment = TextAlignmentOptions.MidlineLeft;

        // 事实行
        factsText = CreateText(root, "Facts", 15, new Color(0.5f, 0.7f, 0.85f));
        SetRect(factsText.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -40), new Vector2(640, 22));
        factsText.rectTransform.pivot = new Vector2(0, 1);
        factsText.alignment = TextAlignmentOptions.MidlineLeft;

        // 状态行
        statusText = CreateText(root, "Status", 13, new Color(0.54f, 0.56f, 0.59f));
        SetRect(statusText.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -64), new Vector2(640, 20));
        statusText.rectTransform.pivot = new Vector2(0, 1);
        statusText.alignment = TextAlignmentOptions.MidlineLeft;
        SetStatus("先点右上「登录」（服务端在跑时自动拉背包）");

        // 右上按钮列
        var loginBtn = CreateButton(root, "BtnLogin", "登录 test/123456", new Color(0.03f, 0.76f, 0.35f), new Vector2(-20, -36), new Vector2(190, 38));
        loginBtn.onClick.AddListener(OnLoginClicked);
        var grantBtn = CreateButton(root, "BtnGrant", "发放铁矿 ×5", new Color(0.30f, 0.39f, 0.49f), new Vector2(-20, -80), new Vector2(190, 38));
        grantBtn.onClick.AddListener(OnGrantClicked);
        var discardBtn = CreateButton(root, "BtnDiscard", "丢光铁矿", new Color(0.30f, 0.39f, 0.49f), new Vector2(-20, -124), new Vector2(190, 38));
        discardBtn.onClick.AddListener(OnDiscardAllClicked);
        var talkBtn = CreateButton(root, "BtnTalk", "和铁匠对话", new Color(0.30f, 0.39f, 0.49f), new Vector2(-20, -168), new Vector2(190, 38));
        talkBtn.onClick.AddListener(OnTalkClicked);

        // 对话面板（底部横幅）
        var panelGo = new GameObject("DialogPanel");
        panelGo.transform.SetParent(root, false);
        var panelImage = panelGo.AddComponent<Image>();
        panelImage.color = new Color(0.149f, 0.168f, 0.20f, 0.95f);
        var panelRect = panelGo.GetComponent<RectTransform>();
        SetRect(panelRect, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 14), new Vector2(1080, 190));
        panelRect.pivot = new Vector2(0.5f, 0); // 锚底必须配 pivot 底，否则 anchoredPosition 定的是中心、面板下半截沉出屏幕
        dialogPanel = panelGo;

        speakerText = CreateText(panelRect, "Speaker", 17, new Color(0.5f, 0.7f, 0.85f));
        SetRect(speakerText.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -14), new Vector2(400, 24));
        speakerText.rectTransform.pivot = new Vector2(0, 1); // 角锚配角 pivot：pos 即左上角偏移，否则句首跑出横幅左缘
        speakerText.fontStyle = FontStyles.Bold;
        speakerText.alignment = TextAlignmentOptions.MidlineLeft;

        dialogText = CreateText(panelRect, "DialogText", 19, new Color(0.88f, 0.88f, 0.88f));
        SetRect(dialogText.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(24, -44), new Vector2(1032, 130));
        dialogText.rectTransform.pivot = new Vector2(0, 1);
        dialogText.alignment = TextAlignmentOptions.MidlineLeft;
        dialogText.enableWordWrapping = true;

        var contGo = new GameObject("Continue");
        contGo.transform.SetParent(panelRect, false);
        var contBtn = contGo.AddComponent<Button>();
        var contImg = contGo.AddComponent<Image>();
        contImg.color = new Color(0.30f, 0.39f, 0.49f, 0.9f);
        var contRect = contGo.GetComponent<RectTransform>();
        SetRect(contRect, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-16, 10), new Vector2(110, 32));
        contRect.pivot = new Vector2(1, 0); // 右下角锚配右下 pivot：内缩 16/10 即按钮右下角位置
        var contLabel = CreateText(contGo.GetComponent<RectTransform>(), "Label", 15, Color.white);
        SetRect(contLabel.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        contLabel.text = "继续 »";
        contLabel.alignment = TextAlignmentOptions.Center;
        contBtn.onClick.AddListener(() => vm?.GoNext());
        continueButton = contBtn;

        // 选项区（面板上方竖排）
        var optGo = new GameObject("OptionArea");
        optGo.transform.SetParent(root, false);
        var optRect = optGo.AddComponent<RectTransform>();
        SetRect(optRect, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 212), new Vector2(480, 200));
        optRect.pivot = new Vector2(0.5f, 0); // 同上：锚底配 pivot 底，选项区从横幅上沿(204)再抬 8px 起排
        optionArea = optRect;

        ShowDialogPanel(false);
    }

    private GameObject CreateOptionButton(string label, int index)
    {
        var go = new GameObject($"Option_{index}");
        go.transform.SetParent(optionArea, false);
        var btn = go.AddComponent<Button>();
        var img = go.AddComponent<Image>();
        img.color = new Color(0.85f, 0.85f, 0.85f, 0.12f);
        var rect = go.GetComponent<RectTransform>();
        SetRect(rect, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -index * 46), new Vector2(460, 40));

        var text = CreateText(rect, "Label", 16, new Color(0.92f, 0.92f, 0.92f));
        SetRect(text.rectTransform, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(14, 0), new Vector2(-28, 40));
        text.text = label;
        text.alignment = TextAlignmentOptions.MidlineLeft;

        int captured = index;
        btn.onClick.AddListener(() => vm?.SelectOption(captured));
        return go;
    }

    private void ClearOptions()
    {
        foreach (var go in optionObjects)
        {
            if (go != null) Destroy(go);
        }
        optionObjects.Clear();
    }

    private void ShowDialogPanel(bool show)
    {
        if (dialogPanel != null) dialogPanel.SetActive(show);
        if (optionArea != null && !show) ClearOptions();
    }

    private void SetStatus(string message) => statusText.text = message;

    #endregion

    #region UI 小工具

    private static TMP_Text CreateText(Transform parent, string name, float fontSize, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.color = color;
        text.raycastTarget = false; // 项目约定：非交互文本必须关 raycast，防挡下层按钮
        return text;
    }

    private static Button CreateButton(RectTransform parent, string name, string label, Color bg, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var btn = go.AddComponent<Button>();
        var img = go.AddComponent<Image>();
        img.color = bg;
        var btnRect = go.GetComponent<RectTransform>();
        SetRect(btnRect, new Vector2(1, 1), new Vector2(1, 1), anchoredPos, size);
        btnRect.pivot = new Vector2(1, 1); // 右上角锚配右上 pivot：pos 即右上角内缩，否则按钮右半截伸出屏幕

        var text = CreateText(go.transform, "Label", 15, Color.white);
        SetRect(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        // 按钮文字在可交互层上，保留 raycastTarget 让点击穿透到按钮
        text.raycastTarget = false;
        return btn;
    }

    private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
    }

    #endregion
}
