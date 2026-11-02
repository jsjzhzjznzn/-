using System.Collections.Generic;

using Common.Network;

using Cysharp.Threading.Tasks;

using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 角色页（框架面板，按参考图复刻版式）
/// 布局：顶部元素徽记与头像行、左侧六项菜单、中央 3D 模型预览（可拖动旋转）、
/// 右侧属性 / 武器详情两页、右下主按钮。
/// 「武器」页签把右侧换成武器详情（名称/类型/基础攻击/副属性/星级/强化等级/属性加成/简介），
/// 右下按钮随之换成「替换 · 强化」；「替换」打开武器选择页（四列卡片网格 + 排序 + 对比）。
/// 数值来自 EquipmentManager（服务端权威快照）与角色 PlayerSO；星级/等级等展示元数据
/// 来自 BattleHeroCatalog，项目当前没有对应养成系统，仅用于界面排版。
/// </summary>
public class CharacterView : UIView
{
    // ──────────────────────────────
    // 序列化引用（预制体构建器注入）
    // ──────────────────────────────

    [Header("面板")]
    [ControlBinding]
    public Button closeButton;

    [ControlBinding]
    public TMP_Text identityText;

    [ControlBinding]
    public TMP_Text hintText;

    [ControlBinding]
    public TMP_Text statusText;

    [ControlBinding]
    public Button upgradeButton;

    [ControlBinding("ElementEmblem")]
    public GameObject elementEmblem;

    [ControlBinding("MenuRoot")]
    public GameObject menuRoot;

    [ControlBinding("SwitchRoot")]
    public GameObject switchRoot;

    [ControlBinding("ReplaceButton")]
    public Button replaceButton;

    [ControlBinding("EnhanceButton")]
    public Button enhanceButton;

    [Header("顶部头像与角色切换")]
    [ControlBinding("CharacterScroll")]
    public RectTransform characterListContent;

    [ControlBinding("CharacterScroll/RowTemplate")]
    public RectTransform characterRowTemplate;

    [ControlBinding("SwitchRoot/PrevButton")]
    public Button prevCharacterButton;

    [ControlBinding("SwitchRoot/NextButton")]
    public Button nextCharacterButton;

    [Header("左侧菜单")]
    [ControlBinding("MenuRoot/AttrButton")]
    public Button attrTabButton;

    [ControlBinding("MenuRoot/AttrButton/Label")]
    public TMP_Text attrTabLabel;

    [ControlBinding("MenuRoot/AttrButton/Pill")]
    public Image attrTabPill;

    [ControlBinding("MenuRoot/WeaponButton")]
    public Button weaponTabButton;

    [ControlBinding("MenuRoot/WeaponButton/Label")]
    public TMP_Text weaponTabLabel;

    [ControlBinding("MenuRoot/WeaponButton/Pill")]
    public Image weaponTabPill;

    [ControlBinding("MenuRoot/RelicButton")]
    public Button relicTabButton;

    [ControlBinding("MenuRoot/ConstellationButton")]
    public Button constellationTabButton;

    [ControlBinding("MenuRoot/TalentButton")]
    public Button talentTabButton;

    [ControlBinding("MenuRoot/InfoButton")]
    public Button infoTabButton;

    [Header("中央模型")]
    [ControlBinding("PreviewRoot/PreviewRaw")]
    public RawImage previewRaw;

    [Header("属性页")]
    [ControlBinding("AttrPanel")]
    public GameObject attrPageRoot;

    [ControlBinding("AttrPanel/NameText")]
    public TMP_Text characterNameText;

    [ControlBinding("AttrPanel/StarRow")]
    public RectTransform starRow;

    [ControlBinding("AttrPanel/LevelText")]
    public TMP_Text levelText;

    [ControlBinding("AttrPanel/ExpText")]
    public TMP_Text expText;

    [ControlBinding("AttrPanel/ExpBar/Fill")]
    public Image expBarFill;

    [ControlBinding("AttrPanel/AttrMaxHpText")]
    public TMP_Text attrMaxHpText;

    [ControlBinding("AttrPanel/AttrAttackText")]
    public TMP_Text attrAttackText;

    [ControlBinding("AttrPanel/AttrDefenseText")]
    public TMP_Text attrDefenseText;

    [ControlBinding("AttrPanel/AttrDamageUpText")]
    public TMP_Text attrDamageUpText;

    [ControlBinding("AttrPanel/AttrDamageDownText")]
    public TMP_Text attrDamageDownText;

    [ControlBinding("AttrPanel/AttrMoveSpeedText")]
    public TMP_Text attrMoveSpeedText;

    [ControlBinding("AttrPanel/DetailButton")]
    public Button detailButton;

    [ControlBinding("AttrPanel/DescText")]
    public TMP_Text descText;

    [Header("武器详情页")]
    [ControlBinding("WeaponPanel")]
    public GameObject weaponPageRoot;

    [ControlBinding("WeaponPanel/NameText")]
    public TMP_Text weaponNameText;

    [ControlBinding("WeaponPanel/TypeText")]
    public TMP_Text weaponTypeText;

    [ControlBinding("WeaponPanel/StatRow0")]
    public GameObject weaponStatRow0;

    [ControlBinding("WeaponPanel/StatRow0/Label")]
    public TMP_Text weaponStatLabel0;

    [ControlBinding("WeaponPanel/StatRow0/Value")]
    public TMP_Text weaponStatValue0;

    [ControlBinding("WeaponPanel/StatRow1")]
    public GameObject weaponStatRow1;

    [ControlBinding("WeaponPanel/StatRow1/Label")]
    public TMP_Text weaponStatLabel1;

    [ControlBinding("WeaponPanel/StatRow1/Value")]
    public TMP_Text weaponStatValue1;

    [ControlBinding("WeaponPanel/StarRow")]
    public RectTransform weaponStarRow;

    [ControlBinding("WeaponPanel/LevelText")]
    public TMP_Text weaponLevelText;

    [ControlBinding("WeaponPanel/RefineRow")]
    public RectTransform weaponRefineRow;

    [ControlBinding("WeaponPanel/EffectTitleText")]
    public TMP_Text weaponEffectTitleText;

    [ControlBinding("WeaponPanel/EffectText")]
    public TMP_Text weaponEffectText;

    [ControlBinding("WeaponPanel/FlavorText")]
    public TMP_Text weaponFlavorText;

    [ControlBinding("WeaponPanel/CompareText")]
    public TMP_Text weaponCompareText;

    [ControlBinding("WeaponPanel/UnequipButton")]
    public Button unequipButton;

    [Header("武器选择页")]
    [ControlBinding("PickerRoot")]
    public RectTransform pickerRoot;

    [ControlBinding("PickerRoot/BackButton")]
    public Button backButton;

    [ControlBinding("PickerRoot/PickerTitleText")]
    public TMP_Text pickerTitleText;

    [ControlBinding("PickerRoot/CompareButton")]
    public Button compareButton;

    [ControlBinding("PickerRoot/CompareButton/Label")]
    public TMP_Text compareLabel;

    [ControlBinding("PickerRoot/WeaponScroll")]
    public ScrollRect weaponScroll;

    [ControlBinding("PickerRoot/WeaponScroll/Viewport/Content")]
    public RectTransform weaponGridContent;

    [ControlBinding("PickerRoot/WeaponScroll/Viewport/Content/RowTemplate")]
    public RectTransform weaponCardTemplate;

    [ControlBinding("PickerRoot/PickerEmptyText")]
    public TMP_Text pickerEmptyText;

    [ControlBinding("PickerRoot/SortButton")]
    public Button sortButton;

    [ControlBinding("PickerRoot/SortButton/Label")]
    public TMP_Text sortLabel;

    [ControlBinding("PickerRoot/FilterButton")]
    public Button filterButton;

    [Header("跨角色确认")]
    [ControlBinding("ConfirmRoot")]
    public RectTransform confirmRoot;

    [ControlBinding("ConfirmRoot/Panel/MessageText")]
    public TMP_Text confirmMessageText;

    [ControlBinding("ConfirmRoot/Panel/ConfirmOkButton")]
    public Button confirmOkButton;

    [ControlBinding("ConfirmRoot/Panel/ConfirmCancelButton")]
    public Button confirmCancelButton;

    [Header("角色基础属性（下标 = 角色ID，由构建器按 PlayerSO.characterId 注入）")]
    public PlayerSO[] heroStats;

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>上次查看的角色（PlayerPrefs 键，重开界面记住）</summary>
    private const string LastCharKey = "equipment_last_char";

    /// <summary>当前查看的角色</summary>
    private int selectedCharId;

    /// <summary>右侧是否停在「武器」页签（否则属性页）</summary>
    private bool weaponTabActive;

    /// <summary>武器选择页是否打开</summary>
    private bool pickerOpen;

    /// <summary>武器选择页当前选中的实例（0 = 未选）</summary>
    private long selectedWeaponId;

    /// <summary>武器网格排序：0 品质顺序 / 1 强化等级顺序</summary>
    private int sortMode;

    /// <summary>武器选择页是否显示与当前武器的对比</summary>
    private bool compareVisible;

    /// <summary>已加载到预览里的角色（-1 = 未加载；切角色时据此先卸载再加载）</summary>
    private int previewLoadedCharId = -1;

    private static readonly Color TextMain = new(1f, 1f, 1f, 1f);
    private static readonly Color Cream = new(0.957f, 0.922f, 0.843f, 1f);
    private static readonly Color CreamDim = new(0.957f, 0.922f, 0.843f, 0.62f);
    private static readonly Color Gold = new(0.851f, 0.698f, 0.361f, 1f);       // #D9B25C
    private static readonly Color Green = new(0.482f, 0.847f, 0.561f, 1f);      // #7BD88F
    private static readonly Color Red = new(0.878f, 0.424f, 0.424f, 1f);        // #E06C6C
    private static readonly Color Cyan = new(0.435f, 0.902f, 0.902f, 1f);       // #6FE6E6

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        if (characterRowTemplate != null) characterRowTemplate.gameObject.SetActive(false);
        if (weaponCardTemplate != null) weaponCardTemplate.gameObject.SetActive(false);
        if (pickerRoot != null) pickerRoot.gameObject.SetActive(false);
        if (weaponPageRoot != null) weaponPageRoot.gameObject.SetActive(false);
        if (confirmRoot != null) confirmRoot.gameObject.SetActive(false);
        if (attrPageRoot != null) attrPageRoot.SetActive(true);
        if (replaceButton != null) replaceButton.gameObject.SetActive(false);
        if (enhanceButton != null) enhanceButton.gameObject.SetActive(false);
    }

    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        if (EquipmentManager.Instance != null)
        {
            EquipmentManager.Instance.Changed += HandleEquipmentChanged;
            EquipmentManager.Instance.StatusChanged += SetStatus;
            EquipmentManager.Instance.EquipResult += HandleEquipResult;
        }
        if (BagManager.Instance != null)
        {
            // 名称/图标/稀有度来自背包的物品配置表，配置到位后重渲染一次
            BagManager.Instance.BagUpdated += RefreshAll;
        }

        SetupCharacterList();

        // 进入副本/挑战/对话等任何场景切换都要收起本页（全屏底板留着会盖住新场景）
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>切场景时自动收起：角色页是主页的功能页，不该跨场景保留</summary>
    private void OnActiveSceneChanged(Scene previous, Scene next)
    {
        if (this == null || !gameObject.activeInHierarchy) return;
        if (UIManager.Instance == null || !UIManager.Instance.IsOpen(UIType.CharacterView)) return;
        UIManager.Instance.Close(UIType.CharacterView).Forget();
    }

    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (attrTabButton != null) attrTabButton.onClick.AddListener(ShowAttributes);
        if (weaponTabButton != null) weaponTabButton.onClick.AddListener(ShowWeapon);
        if (relicTabButton != null) relicTabButton.onClick.AddListener(() => ShowLocked("圣遗物"));
        if (constellationTabButton != null) constellationTabButton.onClick.AddListener(() => ShowLocked("命之座"));
        if (talentTabButton != null) talentTabButton.onClick.AddListener(() => ShowLocked("天赋"));
        if (infoTabButton != null) infoTabButton.onClick.AddListener(() => ShowLocked("资料"));
        if (detailButton != null) detailButton.onClick.AddListener(() => ShowLocked("详细信息"));
        if (upgradeButton != null) upgradeButton.onClick.AddListener(() => ShowLocked("升级"));
        if (replaceButton != null) replaceButton.onClick.AddListener(OnReplaceClicked);
        if (enhanceButton != null) enhanceButton.onClick.AddListener(() => ShowLocked("强化"));
        if (prevCharacterButton != null) prevCharacterButton.onClick.AddListener(() => SwitchCharacter(-1));
        if (nextCharacterButton != null) nextCharacterButton.onClick.AddListener(() => SwitchCharacter(1));
        if (backButton != null) backButton.onClick.AddListener(ClosePicker);
        if (sortButton != null) sortButton.onClick.AddListener(ToggleSort);
        if (filterButton != null) filterButton.onClick.AddListener(() => ShowLocked("筛选"));
        if (compareButton != null) compareButton.onClick.AddListener(ToggleCompare);
        if (unequipButton != null) unequipButton.onClick.AddListener(OnUnequipClicked);
        if (confirmOkButton != null) confirmOkButton.onClick.AddListener(OnConfirmEquip);
        if (confirmCancelButton != null) confirmCancelButton.onClick.AddListener(CloseConfirm);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveAllListeners();
        if (attrTabButton != null) attrTabButton.onClick.RemoveAllListeners();
        if (weaponTabButton != null) weaponTabButton.onClick.RemoveAllListeners();
        if (relicTabButton != null) relicTabButton.onClick.RemoveAllListeners();
        if (constellationTabButton != null) constellationTabButton.onClick.RemoveAllListeners();
        if (talentTabButton != null) talentTabButton.onClick.RemoveAllListeners();
        if (infoTabButton != null) infoTabButton.onClick.RemoveAllListeners();
        if (detailButton != null) detailButton.onClick.RemoveAllListeners();
        if (upgradeButton != null) upgradeButton.onClick.RemoveAllListeners();
        if (replaceButton != null) replaceButton.onClick.RemoveAllListeners();
        if (enhanceButton != null) enhanceButton.onClick.RemoveAllListeners();
        if (prevCharacterButton != null) prevCharacterButton.onClick.RemoveAllListeners();
        if (nextCharacterButton != null) nextCharacterButton.onClick.RemoveAllListeners();
        if (backButton != null) backButton.onClick.RemoveAllListeners();
        if (sortButton != null) sortButton.onClick.RemoveAllListeners();
        if (filterButton != null) filterButton.onClick.RemoveAllListeners();
        if (compareButton != null) compareButton.onClick.RemoveAllListeners();
        if (unequipButton != null) unequipButton.onClick.RemoveAllListeners();
        if (confirmOkButton != null) confirmOkButton.onClick.RemoveAllListeners();
        if (confirmCancelButton != null) confirmCancelButton.onClick.RemoveAllListeners();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        SetStatus(string.Empty);
        selectedCharId = Mathf.Clamp(PlayerPrefs.GetInt(LastCharKey, BattleHeroCatalog.Anbi),
            0, Mathf.Max(0, BattleHeroCatalog.Count - 1));
        selectedWeaponId = 0;
        pickerOpen = false;
        weaponTabActive = false;
        sortMode = 0;
        compareVisible = false;
        if (confirmRoot != null) confirmRoot.gameObject.SetActive(false);
        ApplyPageVisibility();
        RefreshMenu();

        // 数据与模型预览同时开始：数据没到也能先看模型
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || net.MyPlayerId <= 0)
        {
            // 未登录不发请求（会返回"请先登录"），页面按空数据渲染
            SetStatus("请先登录");
        }
        else
        {
            EquipmentManager.Instance?.EnsureLoaded();
        }
        LoadPreview(selectedCharId);
        RefreshAvatarSelection();
        RefreshAll();

        // 页面可能在建帧/场景切换过程中打开，此时视口尺寸还是 0：下一帧补刷一次
        StartCoroutine(RefreshAfterLayout());
    }

    private System.Collections.IEnumerator RefreshAfterLayout()
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
        RefreshAll();
    }

    public override void OnClose()
    {
        // 预览模型与 UI 模型池绑定：关闭必须卸载，否则下次打开会叠影/占着池位
        UnloadPreview();
        base.OnClose();
    }

    public override void OnCancel()
    {
        if (confirmRoot != null && confirmRoot.gameObject.activeSelf) { CloseConfirm(); return; }
        if (pickerOpen) { ClosePicker(); return; }
        if (weaponTabActive) { ShowAttributes(); return; }
        base.OnCancel();
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        if (EquipmentManager.Instance != null)
        {
            EquipmentManager.Instance.Changed -= HandleEquipmentChanged;
            EquipmentManager.Instance.StatusChanged -= SetStatus;
            EquipmentManager.Instance.EquipResult -= HandleEquipResult;
        }
        if (BagManager.Instance != null) BagManager.Instance.BagUpdated -= RefreshAll;
    }

    private void HandleEquipmentChanged() => RefreshAll();

    // ──────────────────────────────
    // 页面切换
    // ──────────────────────────────

    /// <summary>属性页：右侧属性面板 + 右下「升级」</summary>
    private void ShowAttributes()
    {
        pickerOpen = false;
        weaponTabActive = false;
        selectedWeaponId = 0;
        SetStatus(string.Empty);
        ApplyPageVisibility();
        RefreshAll();
    }

    /// <summary>武器页：右侧武器详情 + 右下「替换 · 强化」</summary>
    private void ShowWeapon()
    {
        pickerOpen = false;
        weaponTabActive = true;
        selectedWeaponId = GetEquippedWeapon()?.InstanceId ?? 0;
        SetStatus(string.Empty);
        ApplyPageVisibility();
        RefreshAll();
    }

    private void OpenPicker()
    {
        if (EquipmentManager.Instance == null) return;
        if (pickerOpen) return;

        pickerOpen = true;
        weaponTabActive = true;
        selectedWeaponId = GetEquippedWeapon()?.InstanceId ?? 0;
        SetStatus(string.Empty);
        ApplyPageVisibility();
        RefreshWeaponGrid();
        RefreshWeaponPage();
        if (weaponScroll != null) weaponScroll.verticalNormalizedPosition = 1f;
        StartCoroutine(RefreshGridAfterLayout());
    }

    private void ClosePicker()
    {
        if (!pickerOpen) return;

        pickerOpen = false;
        selectedWeaponId = 0;
        if (confirmRoot != null) confirmRoot.gameObject.SetActive(false);
        ApplyPageVisibility();
        RefreshAll();
    }

    /// <summary>网格视口尺寸可能在打开那一帧还是 0，下一帧强制排版后重建</summary>
    private System.Collections.IEnumerator RefreshGridAfterLayout()
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
        RefreshWeaponGrid();
    }

    /// <summary>按当前状态切换左右两页、菜单可见性与右下按钮</summary>
    private void ApplyPageVisibility()
    {
        bool picker = pickerOpen;
        bool weaponPage = weaponTabActive;

        if (attrPageRoot != null) attrPageRoot.SetActive(!weaponPage);
        if (weaponPageRoot != null) weaponPageRoot.SetActive(weaponPage);
        if (pickerRoot != null) pickerRoot.gameObject.SetActive(picker);
        if (menuRoot != null) menuRoot.SetActive(!picker);
        if (switchRoot != null) switchRoot.SetActive(!picker);
        if (elementEmblem != null) elementEmblem.SetActive(!picker);
        if (identityText != null) identityText.gameObject.SetActive(!picker);
        if (hintText != null) hintText.gameObject.SetActive(!picker);
        if (pickerTitleText != null) pickerTitleText.gameObject.SetActive(picker);
        if (backButton != null) backButton.gameObject.SetActive(picker);
        if (compareButton != null) compareButton.gameObject.SetActive(picker);
        if (upgradeButton != null) upgradeButton.gameObject.SetActive(!weaponPage);
        if (replaceButton != null) replaceButton.gameObject.SetActive(weaponPage);
        if (enhanceButton != null) enhanceButton.gameObject.SetActive(weaponPage);
    }

    /// <summary>左侧菜单选中态：属性页 / 武器页互斥高亮</summary>
    private void RefreshMenu()
    {
        bool weapon = weaponTabActive;

        if (attrTabLabel != null) attrTabLabel.color = weapon ? CreamDim : Cream;
        if (attrTabPill != null) attrTabPill.gameObject.SetActive(!weapon);
        if (weaponTabLabel != null) weaponTabLabel.color = weapon ? Cream : CreamDim;
        if (weaponTabPill != null) weaponTabPill.gameObject.SetActive(weapon);
    }

    // ──────────────────────────────
    // 角色列
    // ──────────────────────────────

    private void SetupCharacterList()
    {
        if (characterListContent == null || characterRowTemplate == null) return;

        // 头像行只有 2 个角色，直接克隆模板；不再使用虚拟列表
        for (int i = characterListContent.childCount - 1; i >= 0; i--)
        {
            Transform child = characterListContent.GetChild(i);
            if (child == characterRowTemplate) continue;
            Destroy(child.gameObject);
        }

        for (int i = 0; i < BattleHeroCatalog.Count; i++)
        {
            RectTransform row = Instantiate(characterRowTemplate, characterListContent);
            row.gameObject.SetActive(true);
            BindCharacterRow(i, row);
        }

        RefreshAvatarSelection();
    }

    private void BindCharacterRow(int index, RectTransform row)
    {
        int charId = index;
        bool selected = charId == selectedCharId;

        Image ring = row.Find("Ring")?.GetComponent<Image>();
        if (ring != null) ring.color = selected ? Cyan : new Color(1f, 1f, 1f, 0.35f);

        Image portrait = row.Find("Frame/Portrait")?.GetComponent<Image>();
        if (portrait != null) LoadPortrait(portrait, charId).Forget();

        Transform selectedMark = row.Find("Selected");
        if (selectedMark != null) selectedMark.gameObject.SetActive(selected);

        row.localScale = selected ? Vector3.one * 1.06f : Vector3.one;

        Button button = row.GetComponent<Button>();
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => SelectCharacter(charId));
        }
    }

    /// <summary>刷新头像行的选中态（选中青色描边 + 下划线 + 轻微放大）</summary>
    private void RefreshAvatarSelection()
    {
        if (characterListContent == null || characterRowTemplate == null) return;

        int index = 0;
        for (int i = 0; i < characterListContent.childCount; i++)
        {
            RectTransform row = characterListContent.GetChild(i) as RectTransform;
            if (row == null || row == characterRowTemplate) continue;
            BindCharacterRow(index, row);
            index++;
        }
    }

    private static async UniTaskVoid LoadPortrait(Image portrait, int charId)
    {
        if (portrait == null) return;
        BattleHeroCatalog.HeroInfo hero = BattleHeroCatalog.Get(charId);
        if (string.IsNullOrEmpty(hero.PortraitPath)) return;

        Sprite sprite = await ResourceManager.Instance.LoadAssetAsync<Sprite>(hero.PortraitPath);
        if (sprite != null && portrait != null) portrait.sprite = sprite;
    }

    private void SelectCharacter(int charId)
    {
        if (charId == selectedCharId) return;

        selectedCharId = charId;
        selectedWeaponId = 0;
        PlayerPrefs.SetInt(LastCharKey, charId);
        PlayerPrefs.Save();

        LoadPreview(charId);
        RefreshAvatarSelection();
        RefreshAll();
    }

    /// <summary>左右箭头切角色（首尾循环）</summary>
    private void SwitchCharacter(int delta)
    {
        if (BattleHeroCatalog.Count <= 1) return;
        int next = (selectedCharId + delta + BattleHeroCatalog.Count) % BattleHeroCatalog.Count;
        SelectCharacter(next);
    }

    private void ShowLocked(string feature)
        => SetStatus($"{feature}功能尚未开放");

    // ──────────────────────────────
    // 3D 预览
    // ──────────────────────────────

    private void LoadPreview(int charId)
    {
        if (previewRaw == null || UIModelManager.Instance == null) return;
        if (previewLoadedCharId == charId) return;

        UnloadPreview();

        // 预览预制体放在 Resources 下（不走 YooAsset：预制体里引用的 FBX 依赖不在收集范围内）
        BattleHeroCatalog.HeroInfo hero = BattleHeroCatalog.Get(charId);
        GameObject prefab = Resources.Load<GameObject>(hero.PreviewPath);
        if (prefab == null)
        {
            Debug.LogError("[CharacterView] 预览预制体缺失：" + hero.PreviewPath);
            SetStatus($"预览资源缺失：{hero.DisplayName}");
            return;
        }

        GameObject model = Instantiate(prefab);
        HidePreviewWeapon(model);
        previewLoadedCharId = charId;
        // 角色页的取景比房间预览更满：同一角色在角色页放大 18%
        UIModelManager.Instance.LoadModelToRawImage(
            model, previewRaw, canDrag: true,
            offset: new Vector3(0f, hero.PreviewOffsetY, 0f),
            isOrth: true, orthSizeOrFOV: hero.PreviewOrthSize * 0.82f);
    }

    /// <summary>
    /// 预览模型自带武器默认隐藏：属性页展示的是角色本体，
    /// 武器数据与外观由武器页单独表达（当前没有外观挂点，避免"未装备却手持武器"的矛盾）。
    /// </summary>
    private static void HidePreviewWeapon(GameObject model)
    {
        if (model == null) return;

        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            string nodeName = renderer.gameObject.name.ToLowerInvariant();
            if (nodeName.Contains("weapon") || nodeName.Contains("blade") || nodeName.Contains("sword")
                || nodeName.Contains("knife") || nodeName.Contains("scythe"))
            {
                renderer.enabled = false;
            }
        }
    }

    /// <summary>预览模型是本类自己 Instantiate 的（Resources），先销毁实例再让 UIModelManager 清目标表</summary>
    private void UnloadPreview()
    {
        if (previewLoadedCharId < 0) return;
        previewLoadedCharId = -1;
        if (previewRaw == null || UIModelManager.Instance == null) return;

        UIRenderToTexture renderToTexture = previewRaw.GetComponent<UIRenderToTexture>();
        if (renderToTexture != null && renderToTexture.Targets != null)
        {
            foreach (var pair in renderToTexture.Targets)
            {
                GameObject go = pair.Value.gameObject;
                if (go != null) Destroy(go);
            }
        }
        UIModelManager.Instance.UnLoadModelByRawImage(previewRaw, false);
    }

    // ──────────────────────────────
    // 刷新入口
    // ──────────────────────────────

    private void RefreshAll()
    {
        RefreshHeroMeta();
        RefreshAttributes();
        RefreshWeaponPage();
        RefreshMenu();

        // 物品配置表（名称/图标/稀有度）可能比装备数据晚到
        if (pickerOpen) RefreshWeaponGrid();
    }

    /// <summary>顶部身份行、名称、星级、等级经验、简介</summary>
    private void RefreshHeroMeta()
    {
        BattleHeroCatalog.HeroInfo hero = BattleHeroCatalog.Get(selectedCharId);

        if (identityText != null) identityText.SetText($"{hero.ElementName} / {hero.DisplayName}");
        if (characterNameText != null) characterNameText.SetText(hero.DisplayName);
        if (descText != null) descText.SetText(hero.Description ?? string.Empty);

        if (levelText != null)
        {
            levelText.SetText(hero.MaxLevel > 0
                ? $"等级<size=118%>{hero.Level}</size><size=76%><color=#C8BBA0> / {hero.MaxLevel}</color></size>"
                : $"等级{hero.Level}");
        }
        if (expText != null) expText.SetText(hero.ExpMax > 0 ? $"{hero.Exp}/{hero.ExpMax}" : string.Empty);
        if (expBarFill != null)
        {
            expBarFill.fillAmount = hero.ExpMax > 0 ? Mathf.Clamp01((float)hero.Exp / hero.ExpMax) : 0f;
        }

        if (starRow != null)
        {
            for (int i = 0; i < starRow.childCount; i++)
            {
                Image star = starRow.GetChild(i).GetComponent<Image>();
                if (star != null) star.color = i < hero.Rarity ? Color.white : new Color(1f, 1f, 1f, 0.26f);
            }
        }
    }

    private void RefreshAttributes()
    {
        CharacterStatsData stats = CharacterStats();
        float baseAttack = stats != null ? stats.baseAttack : 0f;
        float baseDefense = stats != null ? stats.baseDefense : 0f;
        float baseMaxHp = stats != null ? stats.baseMaxHP : 0f;

        EquipAttrTotals bonus = EquipmentManager.Instance != null
            ? EquipmentManager.Instance.GetEquipAttrBonus(selectedCharId)
            : default;

        SetFlatAttr(attrAttackText, baseAttack, bonus.Attack);
        SetFlatAttr(attrDefenseText, baseDefense, bonus.Defense);
        SetRatioAttr(attrDamageUpText, bonus.DamageUp);
        SetRatioAttr(attrDamageDownText, bonus.DamageDown);
        SetRatioAttr(attrMoveSpeedText, bonus.MoveSpeed);
        if (attrMaxHpText != null) attrMaxHpText.SetText($"{baseMaxHp:0}");
    }

    /// <summary>白值行：合计为主、装备加成绿色括注（无加成时只显示基础）</summary>
    private static void SetFlatAttr(TMP_Text text, float baseValue, float bonus)
    {
        if (text == null) return;
        string main = $"{baseValue + bonus:0}";
        if (Mathf.Approximately(bonus, 0f))
        {
            text.SetText(main);
            return;
        }
        string signedBonus = bonus > 0f ? $"+{bonus:0}" : $"{bonus:0}";
        text.SetText($"{main}<size=70%><color=#{ColorUtility.ToHtmlStringRGB(Green)}> ({signedBonus})</color></size>");
    }

    /// <summary>系数行：百分比显示，无加成时灰白 0.0%</summary>
    private static void SetRatioAttr(TMP_Text text, float ratio)
    {
        if (text == null) return;
        if (Mathf.Approximately(ratio, 0f))
        {
            text.SetText("0.0%");
            return;
        }
        string color = ratio > 0f ? ColorUtility.ToHtmlStringRGB(Green) : ColorUtility.ToHtmlStringRGB(Red);
        text.SetText($"<color=#{color}>{(ratio > 0f ? "+" : string.Empty)}{ratio * 100f:0.0}%</color>");
    }

    private EquipInstanceInfo GetEquippedWeapon()
        => EquipmentManager.Instance?.GetEquipped(selectedCharId, EquipmentManager.SlotWeapon);

    private CharacterStatsData CharacterStats()
    {
        if (heroStats == null || selectedCharId < 0 || selectedCharId >= heroStats.Length) return null;
        return heroStats[selectedCharId] != null ? heroStats[selectedCharId].statsData : null;
    }

    // ──────────────────────────────
    // 武器详情页
    // ──────────────────────────────

    /// <summary>右侧正在展示的武器：选武器时看选中的，否则看当前角色穿着的</summary>
    private EquipInstanceInfo DisplayedWeapon()
    {
        EquipmentManager manager = EquipmentManager.Instance;
        if (manager == null) return null;
        if (pickerOpen && selectedWeaponId > 0) return manager.GetInstance(selectedWeaponId);
        return GetEquippedWeapon();
    }

    private void RefreshWeaponPage()
    {
        EquipmentManager manager = EquipmentManager.Instance;
        EquipInstanceInfo weapon = DisplayedWeapon();
        ItemConfigInfo item = weapon != null ? BagManager.Instance?.GetConfig(weapon.ItemId) : null;
        List<EquipAttrEntry> entries = weapon != null && manager != null ? manager.GetAttrEntries(weapon.ItemId) : null;

        if (weaponNameText != null)
        {
            weaponNameText.SetText(weapon == null
                ? "未装备武器"
                : (item != null ? item.Name : $"#{weapon.ItemId}"));
        }
        if (weaponTypeText != null)
        {
            weaponTypeText.SetText(weapon == null
                ? "点「替换」选择一件武器"
                : WeaponTypeName(manager?.GetConfig(weapon.ItemId)?.WeaponType ?? 0));
        }

        // 空槽：只留标题与提示，属性区整块收起
        if (weapon == null)
        {
            if (weaponStatRow0 != null) weaponStatRow0.SetActive(false);
            if (weaponStatRow1 != null) weaponStatRow1.SetActive(false);
            if (weaponStarRow != null) weaponStarRow.gameObject.SetActive(false);
            if (weaponLevelText != null) weaponLevelText.gameObject.SetActive(false);
            if (weaponRefineRow != null) weaponRefineRow.gameObject.SetActive(false);
            if (weaponEffectTitleText != null) weaponEffectTitleText.gameObject.SetActive(false);
            if (weaponEffectText != null) weaponEffectText.gameObject.SetActive(false);
            if (weaponCompareText != null) weaponCompareText.gameObject.SetActive(false);
            if (unequipButton != null) unequipButton.gameObject.SetActive(false);
            if (weaponFlavorText != null)
            {
                weaponFlavorText.gameObject.SetActive(true);
                weaponFlavorText.SetText("还没有装备武器。点「替换」进入武器选择页，挑一件适合的武器。");
            }
            return;
        }

        if (weaponStatRow0 != null) weaponStatRow0.SetActive(true);
        if (weaponStarRow != null) weaponStarRow.gameObject.SetActive(true);
        if (weaponLevelText != null) weaponLevelText.gameObject.SetActive(true);
        if (weaponRefineRow != null) weaponRefineRow.gameObject.SetActive(true);
        if (weaponEffectTitleText != null) weaponEffectTitleText.gameObject.SetActive(true);
        if (weaponEffectText != null) weaponEffectText.gameObject.SetActive(true);
        if (weaponFlavorText != null) weaponFlavorText.gameObject.SetActive(true);

        float attack = 0f;
        bool hasSecond = false;
        AttrType secondType = AttrType.Attack;
        float secondValue = 0f;
        if (entries != null)
        {
            foreach (EquipAttrEntry entry in entries)
            {
                AttrType type = (AttrType)entry.type;
                if (type == AttrType.Attack)
                {
                    attack += entry.value;
                    continue;
                }
                if (hasSecond) continue;
                secondType = type;
                secondValue = entry.value;
                hasSecond = true;
            }
        }

        if (weaponStatLabel0 != null) weaponStatLabel0.SetText("基础攻击力");
        if (weaponStatValue0 != null) weaponStatValue0.SetText(weapon != null ? $"{attack:0}" : "--");
        if (weaponStatRow1 != null) weaponStatRow1.SetActive(hasSecond);
        if (hasSecond)
        {
            if (weaponStatLabel1 != null) weaponStatLabel1.SetText(AttrLabel(secondType));
            if (weaponStatValue1 != null)
            {
                weaponStatValue1.SetText(AttrTypeUtil.IsRatio(secondType)
                    ? $"{secondValue * 100f:0.0}%"
                    : $"{secondValue:0}");
            }
        }

        int rarity = item != null ? item.Rarity : 0;
        if (weaponStarRow != null)
        {
            for (int i = 0; i < weaponStarRow.childCount; i++)
            {
                Image star = weaponStarRow.GetChild(i).GetComponent<Image>();
                if (star == null) continue;
                star.color = weapon != null && i < rarity
                    ? new Color(1f, 0.94f, 0.78f)
                    : new Color(1f, 1f, 1f, 0.20f);
            }
        }

        int enhance = weapon != null ? weapon.EnhanceLevel : 0;
        if (weaponLevelText != null) weaponLevelText.SetText(weapon != null ? $"强化 Lv.{enhance}" : string.Empty);
        if (weaponRefineRow != null)
        {
            int lit = Mathf.Clamp(enhance, 0, weaponRefineRow.childCount);
            for (int i = 0; i < weaponRefineRow.childCount; i++)
            {
                Image diamond = weaponRefineRow.GetChild(i).GetComponent<Image>();
                if (diamond == null) continue;
                diamond.color = i < lit
                    ? new Color(1f, 0.93f, 0.74f, 0.95f)
                    : new Color(1f, 1f, 1f, 0.22f);
            }
        }

        if (weaponEffectTitleText != null) weaponEffectTitleText.SetText("属性加成");
        if (weaponEffectText != null) weaponEffectText.SetText(BuildEffectLines(entries));
        if (weaponFlavorText != null) weaponFlavorText.SetText(item != null ? item.Desc : string.Empty);

        bool showCompare = pickerOpen && compareVisible && weapon != null;
        if (weaponCompareText != null)
        {
            weaponCompareText.gameObject.SetActive(showCompare);
            if (showCompare) weaponCompareText.SetText(BuildCompareText(weapon));
        }

        EquipInstanceInfo equipped = GetEquippedWeapon();
        if (unequipButton != null) unequipButton.gameObject.SetActive(equipped != null);
    }

    /// <summary>属性加成逐行列出（参考图里的效果段）</summary>
    private static string BuildEffectLines(List<EquipAttrEntry> entries)
    {
        if (entries == null || entries.Count == 0) return "<color=#C8BBA0>该武器没有额外属性</color>";

        var builder = new System.Text.StringBuilder();
        foreach (EquipAttrEntry entry in entries)
        {
            AttrType type = (AttrType)entry.type;
            bool ratio = AttrTypeUtil.IsRatio(type);
            float value = entry.value;
            string text = ratio ? $"{Mathf.Abs(value) * 100f:0.0}%" : $"{Mathf.Abs(value):0}";
            if (builder.Length > 0) builder.Append('\n');
            builder.Append($"· {AttrLabel(type)} {(value < 0f ? "-" : "+")}{text}");
        }
        return builder.ToString();
    }

    // ──────────────────────────────
    // 武器选择页
    // ──────────────────────────────

    /// <summary>当前列表数据：品质顺序走服务端默认排序，强化顺序按强化等级倒序</summary>
    private List<EquipInstanceInfo> WeaponList()
    {
        List<EquipInstanceInfo> list = EquipmentManager.Instance != null
            ? EquipmentManager.Instance.GetWeaponInstances()
            : new List<EquipInstanceInfo>();
        if (sortMode == 1) list.Sort((left, right) => right.EnhanceLevel.CompareTo(left.EnhanceLevel));
        return list;
    }

    private void RefreshWeaponGrid()
    {
        if (weaponGridContent == null || weaponCardTemplate == null) return;

        for (int i = weaponGridContent.childCount - 1; i >= 0; i--)
        {
            Transform child = weaponGridContent.GetChild(i);
            if (child == weaponCardTemplate) continue;
            Destroy(child.gameObject);
        }

        List<EquipInstanceInfo> weapons = WeaponList();
        for (int i = 0; i < weapons.Count; i++)
        {
            RectTransform card = Instantiate(weaponCardTemplate, weaponGridContent);
            card.gameObject.SetActive(true);
            BindWeaponCard(card, weapons[i]);
        }

        if (pickerEmptyText != null) pickerEmptyText.gameObject.SetActive(weapons.Count == 0);
    }

    private void BindWeaponCard(RectTransform card, EquipInstanceInfo weapon)
    {
        ItemConfigInfo item = BagManager.Instance?.GetConfig(weapon.ItemId);
        int rarity = item != null ? item.Rarity : 1;
        bool selected = weapon.InstanceId == selectedWeaponId;

        Image background = card.GetComponent<Image>();
        if (background != null) background.color = RarityCardColor(rarity, selected);

        Image icon = card.Find("Icon")?.GetComponent<Image>();
        if (icon != null)
        {
            icon.sprite = item != null ? ItemIconLoader.Get(item.IconKey) : null;
            icon.enabled = icon.sprite != null;
        }

        Transform stars = card.Find("StarRow");
        if (stars != null)
        {
            for (int i = 0; i < stars.childCount; i++)
            {
                Image star = stars.GetChild(i).GetComponent<Image>();
                if (star == null) continue;
                star.color = i < rarity ? new Color(1f, 0.94f, 0.78f) : new Color(1f, 1f, 1f, 0.25f);
            }
        }

        TMP_Text level = card.Find("LevelText")?.GetComponent<TMP_Text>();
        if (level != null) level.SetText($"Lv.{weapon.EnhanceLevel}");

        Transform mark = card.Find("Selected");
        if (mark != null) mark.gameObject.SetActive(selected);

        Button button = card.GetComponent<Button>();
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            long instanceId = weapon.InstanceId;
            button.onClick.AddListener(() => SelectWeapon(instanceId));
        }
    }

    /// <summary>卡片底色按稀有度区分（5 金 / 4 紫 / 3 蓝 / 其它灰），选中提亮</summary>
    private static Color RarityCardColor(int rarity, bool selected)
    {
        Color color = rarity >= 5 ? new Color(0.70f, 0.53f, 0.23f, 0.88f)
            : rarity == 4 ? new Color(0.46f, 0.34f, 0.61f, 0.88f)
            : rarity == 3 ? new Color(0.29f, 0.44f, 0.61f, 0.88f)
            : new Color(0.40f, 0.38f, 0.34f, 0.82f);
        if (selected) color = Color.Lerp(color, Color.white, 0.18f);
        return color;
    }

    private void SelectWeapon(long instanceId)
    {
        selectedWeaponId = instanceId;
        RefreshWeaponGrid();
        RefreshWeaponPage();
    }

    private void ToggleSort()
    {
        sortMode = sortMode == 0 ? 1 : 0;
        if (sortLabel != null) sortLabel.SetText(sortMode == 0 ? "品质顺序" : "强化顺序");
        RefreshWeaponGrid();
    }

    private void ToggleCompare()
    {
        compareVisible = !compareVisible;
        if (compareLabel != null) compareLabel.color = compareVisible ? Color.white : Cream;
        RefreshWeaponPage();
    }

    private void OnReplaceClicked()
    {
        if (pickerOpen)
        {
            EquipSelectedWeapon();
            return;
        }
        OpenPicker();
    }

    // ──────────────────────────────
    // 穿脱
    // ──────────────────────────────

    private void EquipSelectedWeapon()
    {
        EquipmentManager manager = EquipmentManager.Instance;
        if (manager == null || selectedWeaponId <= 0) return;

        EquipInstanceInfo weapon = manager.GetInstance(selectedWeaponId);
        if (weapon == null) return;

        // 同一件武器正被另一个角色穿着：先二次确认，确认后由服务端做原子转移
        if (weapon.EquippedChar >= 0 && weapon.EquippedChar != selectedCharId)
        {
            if (confirmMessageText != null)
            {
                confirmMessageText.SetText(
                    $"该武器正被{BattleHeroCatalog.GetDisplayName(weapon.EquippedChar)}装备，是否卸下并装备？");
            }
            if (confirmRoot != null) confirmRoot.gameObject.SetActive(true);
            return;
        }

        manager.Equip(selectedCharId, EquipmentManager.SlotWeapon, selectedWeaponId);
    }

    private void OnConfirmEquip()
    {
        CloseConfirm();
        EquipmentManager.Instance?.Equip(selectedCharId, EquipmentManager.SlotWeapon, selectedWeaponId);
    }

    private void CloseConfirm()
    {
        if (confirmRoot != null) confirmRoot.gameObject.SetActive(false);
    }

    private void OnUnequipClicked()
    {
        if (GetEquippedWeapon() == null) return;
        EquipmentManager.Instance?.Unequip(selectedCharId, EquipmentManager.SlotWeapon);
    }

    /// <summary>穿戴结算：成功就收回选择页并停在武器页签（失败留在原地看提示）</summary>
    private void HandleEquipResult(bool success, string message)
    {
        if (!success) return;
        if (pickerOpen)
        {
            pickerOpen = false;
            weaponTabActive = true;
            selectedWeaponId = 0;
            ApplyPageVisibility();
        }
        RefreshAll();
    }

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(UIType.CharacterView).Forget();
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content ?? string.Empty);
    }

    // ──────────────────────────────
    // 文案
    // ──────────────────────────────

    /// <summary>与当前武器逐项对比：↑绿 / ↓红 / 持平灰</summary>
    private string BuildCompareText(EquipInstanceInfo candidate)
    {
        EquipmentManager manager = EquipmentManager.Instance;
        if (manager == null || candidate == null) return string.Empty;

        EquipInstanceInfo current = GetEquippedWeapon();
        if (current == null)
        {
            return "<color=#C8BBA0>当前未装备武器</color>";
        }

        int attackDelta = Mathf.RoundToInt(
            manager.GetAttrValue(candidate.ItemId, AttrType.Attack) - manager.GetAttrValue(current.ItemId, AttrType.Attack));
        float damageDelta = manager.GetAttrValue(candidate.ItemId, AttrType.DamageUp)
                            - manager.GetAttrValue(current.ItemId, AttrType.DamageUp);
        float speedDelta = manager.GetAttrValue(candidate.ItemId, AttrType.MoveSpeed)
                           - manager.GetAttrValue(current.ItemId, AttrType.MoveSpeed);

        var builder = new System.Text.StringBuilder();
        AppendDelta(builder, "攻击", attackDelta, "0");
        AppendDelta(builder, "增伤", damageDelta * 100f, "0.0");
        AppendDelta(builder, "移速", speedDelta * 100f, "0.0");
        return builder.Length == 0 ? "<color=#C8BBA0>与当前武器持平</color>" : builder.ToString();
    }

    private static void AppendDelta(System.Text.StringBuilder builder, string label, float delta, string format)
    {
        if (Mathf.Approximately(delta, 0f)) return;
        string sign = delta > 0f ? "↑" : "↓";
        string color = delta > 0f ? ColorUtility.ToHtmlStringRGB(Green) : ColorUtility.ToHtmlStringRGB(Red);
        float value = Mathf.Abs(delta);
        if (builder.Length > 0) builder.Append("  ");
        builder.Append($"<color=#{color}>{label} {sign}{value.ToString(format)}</color>");
    }

    /// <summary>属性摘要："攻击 +44  增伤 +10.0%"（空列表返回"无属性加成"）</summary>
    private static string BuildAttrSummary(List<EquipAttrEntry> entries)
    {
        if (entries == null || entries.Count == 0) return "无属性加成";

        var builder = new System.Text.StringBuilder();
        foreach (EquipAttrEntry entry in entries)
        {
            if (builder.Length > 0) builder.Append("  ");
            string label = AttrLabel((AttrType)entry.type);
            bool ratio = AttrTypeUtil.IsRatio((AttrType)entry.type);
            builder.Append(ratio
                ? $"{label} {(entry.value > 0f ? "+" : string.Empty)}{entry.value * 100f:0.0}%"
                : $"{label} {(entry.value > 0f ? "+" : string.Empty)}{entry.value:0}");
        }
        return builder.ToString();
    }

    private static string AttrLabel(AttrType type) => type switch
    {
        AttrType.Attack => "攻击力",
        AttrType.Defense => "防御力",
        AttrType.DamageUp => "增伤",
        AttrType.DamageDown => "减伤",
        AttrType.MoveSpeed => "移速",
        _ => type.ToString(),
    };

    private static string WeaponTypeName(int weaponType) => weaponType switch
    {
        1 => "长刃",
        2 => "迅刀",
        3 => "佩枪",
        4 => "臂铠",
        5 => "音感仪",
        _ => "武器",
    };
}
