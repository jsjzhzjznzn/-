using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 共鸣者影像簿（玩家档案页）：左侧资料面板 + 右侧选中角色大图，背景循环播放一段世界实机录像。
///
/// 数据只读：昵称固定「冒险者」+ 服务端玩家 ID；生日/索拉/联觉是展示元数据；
/// 角色卡与右侧大图取自 <see cref="BattleHeroCatalog"/> 的档案展示字段（鸣潮头像）。
/// 头像/大图的 Sprite 在构建器里直接烘进预制体，运行时不加载任何资源。
/// 预制体由 ProfileViewBuilder 程序化生成（菜单 Tools/UI/重建影像簿界面）。
/// </summary>
public class ProfileView : UIView
{
    /// <summary>上次选中的角色卡（BattleHeroCatalog 下标）</summary>
    private const string LastCharKey = "Profile.LastChar";

    private const int CardCount = 3;

    // 展示元数据：项目没有生日/索拉/联觉养成系统，这几行只用于版式
    private const string Birthday = "5月1日";
    private const string SolarLevel = "5阶";
    private const string ResonanceLevel = "40级";

    private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);
    private static readonly Color Gold = new Color(0.851f, 0.737f, 0.486f, 1f);
    private static readonly Color GoldSoft = new Color(0.851f, 0.737f, 0.486f, 0.28f);

    private RectTransform _cardRoot;
    private TextMeshProUGUI _nameText;
    private TextMeshProUGUI _codeText;
    private TextMeshProUGUI _signatureText;
    private TextMeshProUGUI _birthdayText;
    private TextMeshProUGUI _solarText;
    private TextMeshProUGUI _resonanceText;
    private TextMeshProUGUI _stageCodeText;
    private TextMeshProUGUI _statusText;

    private readonly List<CardRef> _cards = new List<CardRef>();
    private int _selected;
    private bool _pausePushed;
    private CursorLockMode _prevLockState;
    private bool _prevCursorVisible;

    private struct CardRef
    {
        public Button Button;
        public Image Frame;
        public Image Avatar;
        public GameObject Underline;
        public TextMeshProUGUI Label;
        public int CharId;
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        BindNodes();

        _selected = Mathf.Clamp(PlayerPrefs.GetInt(LastCharKey, 0), 0, CardCount - 1);

        RefreshPlayerInfo();
        CollectCards();
        RefreshSelection();

        PauseManager.Push(PauseManager.Layer.Other);
        _pausePushed = true;
        // 打开前的光标状态要记下来：主页是释放状态，战斗场景是锁定状态，关闭时按原样还回去
        _prevLockState = Cursor.lockState;
        _prevCursorVisible = Cursor.visible;
        SetCursorReleased(true);
        if (_statusText != null) _statusText.SetText(string.Empty);

        // 全屏页常驻对象池：切场景必须收起，否则底板会盖在新场景上
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    public override void OnAddListener()
    {
        Bind(Find("CloseButton"), OnCloseClicked);
        Bind(Find("BackdropButton"), OnCloseClicked);
        Bind(Find("CopyCodeButton"), OnCopyCode);
        Bind(Find("MoreButton"), OnNotAvailable);
        Bind(Find("EditNameButton"), OnNotAvailable);
        Bind(Find("SignatureButton"), OnNotAvailable);
        Bind(Find("SolarLevelInfoButton"), OnNotAvailable);
        Bind(Find("EditButton"), OnNotAvailable);
        Bind(Find("ShootButton"), OnNotAvailable);
        Bind(Find("EyeButton"), OnNotAvailable);
    }

    public override void OnRemoveListener()
    {
        foreach (string name in new[]
                 {
                     "CloseButton", "BackdropButton", "CopyCodeButton", "MoreButton", "EditNameButton",
                     "SignatureButton", "SolarLevelInfoButton", "EditButton", "ShootButton", "EyeButton",
                 })
        {
            Button button = Find(name);
            if (button != null) button.onClick.RemoveAllListeners();
        }

        foreach (CardRef card in _cards)
            if (card.Button != null) card.Button.onClick.RemoveAllListeners();
    }

    public override void OnClose()
    {
        // 页面自己的暂停层自己 Pop，不依赖 HUD 轮询兜底
        if (_pausePushed)
        {
            _pausePushed = false;
            PauseManager.Pop(PauseManager.Layer.Other);
        }
        Cursor.lockState = _prevLockState;
        Cursor.visible = _prevCursorVisible;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        base.OnClose();
    }

    public override void OnCancel()
    {
        OnCloseClicked();
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }

    /// <summary>切场景自动收起：常驻实例不该压在新场景上</summary>
    private void OnActiveSceneChanged(Scene previous, Scene next)
    {
        if (this == null || !gameObject.activeInHierarchy) return;
        if (UIManager.Instance == null || !UIManager.Instance.IsOpen(UIType.ProfileView)) return;
        UIManager.Instance.Close(UIType.ProfileView).Forget();
    }

    // ──────────────────────────────
    // 节点查找
    // ──────────────────────────────

    /// <summary>
    /// 本页控件按节点名取：UIView.OnInit 阶段的 UIControlData 绑定在预制体脚本未就绪时会静默失败，
    /// 按名查找没有这个时序问题，且改名即失效、不会假成功。
    /// </summary>
    private void BindNodes()
    {
        _nameText = GetDeep<TextMeshProUGUI>("NameText");
        _codeText = GetDeep<TextMeshProUGUI>("CodeText");
        _signatureText = GetDeep<TextMeshProUGUI>("SignatureText");
        _birthdayText = GetDeep<TextMeshProUGUI>("BirthdayValueText");
        _solarText = GetDeep<TextMeshProUGUI>("SolarLevelValueText");
        _resonanceText = GetDeep<TextMeshProUGUI>("ResonanceLevelValueText");
        _stageCodeText = GetDeep<TextMeshProUGUI>("StageCode");
        _statusText = GetDeep<TextMeshProUGUI>("StatusText");
        _cardRoot = GetDeep<RectTransform>("CardRoot");
    }

    private Button Find(string name) => GetDeep<Button>(name);

    private T GetDeep<T>(string nodeName) where T : Component
    {
        Transform node = FindDeep(transform, nodeName);
        return node != null ? node.GetComponent<T>() : null;
    }

    private static T Child<T>(Transform root, string name) where T : Component
    {
        Transform node = FindDeep(root, name);
        return node != null ? node.GetComponent<T>() : null;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (string.Equals(root.name, name, StringComparison.Ordinal)) return root;

        int count = root.childCount;
        for (int i = 0; i < count; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static void Bind(Button button, UnityEngine.Events.UnityAction action)
    {
        if (button == null) return;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(action);
    }

    // ──────────────────────────────
    // 数据
    // ──────────────────────────────

    private static int PlayerId => ClientNetworkManager.Instance != null
        ? ClientNetworkManager.Instance.MyPlayerId
        : 0;

    private void RefreshPlayerInfo()
    {
        int playerId = PlayerId;
        if (_nameText != null) _nameText.SetText("冒险者");
        if (_codeText != null) _codeText.SetText(playerId > 0 ? $"特征码：{playerId}" : "特征码：--");
        if (_stageCodeText != null) _stageCodeText.SetText(playerId > 0 ? $"特征码：{playerId}" : "特征码：--");
        if (_signatureText != null) _signatureText.SetText("点击以编辑签名");

        if (_birthdayText != null) _birthdayText.SetText(Birthday);
        if (_solarText != null) _solarText.SetText(SolarLevel);
        if (_resonanceText != null) _resonanceText.SetText(ResonanceLevel);
    }

    /// <summary>角色卡由构建器预建（Card0/1/2），这里只取引用与绑定点击</summary>
    private void CollectCards()
    {
        _cards.Clear();
        if (_cardRoot == null) return;

        for (int i = 0; i < _cardRoot.childCount && _cards.Count < CardCount; i++)
        {
            Transform child = _cardRoot.GetChild(i);
            var button = child.GetComponent<Button>();
            if (button == null) continue;

            var card = new CardRef
            {
                Button = button,
                Frame = Child<Image>(child, "Frame"),
                Avatar = Child<Image>(child, "Avatar"),
                Underline = Child<Transform>(child, "Underline")?.gameObject,
                Label = Child<TextMeshProUGUI>(child, "Name"),
                CharId = _cards.Count,
            };

            // 卡片数量可能多于已登记角色（第三张是版式占位）：超出的一律显示「未解锁」且不可选
            bool registered = card.CharId < BattleHeroCatalog.Count;
            if (card.Label != null)
                card.Label.SetText(registered
                    ? BattleHeroCatalog.Get(card.CharId).DisplayName
                    : "未解锁");
            if (card.Frame != null && !registered) card.Frame.color = new Color(Gold.r, Gold.g, Gold.b, 0.12f);
            if (card.Avatar != null) card.Avatar.gameObject.SetActive(registered);

            int charId = card.CharId;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => Select(charId));
            _cards.Add(card);
        }
    }

    /// <summary>选中角色卡；未登记的角色（版式占位）只给提示</summary>
    private void Select(int charId)
    {
        if (charId < 0 || charId >= _cards.Count || charId == _selected) return;
        if (charId >= BattleHeroCatalog.Count)
        {
            SetStatus("该角色尚未加入影像簿");
            return;
        }

        _selected = charId;
        PlayerPrefs.SetInt(LastCharKey, charId);
        PlayerPrefs.Save();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_cards.Count == 0) return;
        _selected = Mathf.Clamp(_selected, 0, _cards.Count - 1);

        for (int i = 0; i < _cards.Count; i++)
        {
            CardRef card = _cards[i];
            bool selected = i == _selected;

            if (card.Frame != null) card.Frame.color = selected ? Gold : GoldSoft;
            if (card.Underline != null) card.Underline.SetActive(selected);
            if (card.Avatar != null)
            {
                Color color = card.Avatar.color;
                color.a = selected ? 1f : 0.62f;
                card.Avatar.color = color;
            }
            if (card.Label != null) card.Label.color = selected ? Color.white : new Color(1f, 1f, 1f, 0.55f);
        }
    }

    // ──────────────────────────────
    // 交互
    // ──────────────────────────────

    private void OnCopyCode()
    {
        int playerId = PlayerId;
        if (playerId <= 0)
        {
            SetStatus("尚未登录，没有可复制的特征码");
            return;
        }
        GUIUtility.systemCopyBuffer = playerId.ToString();
        SetStatus("特征码已复制");
    }

    private void OnNotAvailable() => SetStatus("该功能暂未开放");

    private void OnCloseClicked() => UIManager.Instance.Close(UIType.ProfileView).Forget();

    private void SetStatus(string message)
    {
        if (_statusText != null) _statusText.SetText(message);
    }

    private static void SetCursorReleased(bool released)
    {
        Cursor.lockState = released ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = released;
    }
}
