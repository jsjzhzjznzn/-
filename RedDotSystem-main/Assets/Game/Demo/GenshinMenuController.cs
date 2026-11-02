using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 原神demo·菜单控制器：设置/背包两个暂停面板的开关 + 两条时间线的语义展示
/// 世界时钟吃 scaled 时间（暂停即冻结）；界面时钟/背包倒计时吃 unscaled 时间（暂停照走）
/// </summary>
public class GenshinMenuController : MonoBehaviour
{
    [Header("面板")]
    [SerializeField] private GameObject settingsPanel;
    [SerializeField] private GameObject bagPanel;

    [Header("入口")]
    [SerializeField] private Button settingsButton;
    [SerializeField] private Button bagButton;

    [Header("时钟")]
    [SerializeField] private TMP_Text hudWorldClock;        // HUD 世界时钟（scaled）
    [SerializeField] private TMP_Text settingsWorldClock;   // 设置面板内：世界时钟（读同一累计值）
    [SerializeField] private TMP_Text settingsUiClock;      // 设置面板内：界面时钟（unscaled）
    [SerializeField] private TMP_Text bagCountdown;         // 背包限时倒计时（unscaled）

    [Header("背包占位数据")]
    [SerializeField] private TMP_Text bagDetailText;
    [SerializeField] private Transform slotGridRoot;

    [Header("按键")]
    [SerializeField] private KeyCode settingsKey = KeyCode.Escape;
    [SerializeField] private KeyCode bagKey = KeyCode.B;
    [SerializeField] private float bagCountdownStart = 300f;

    /// <summary> 世界累计时间（scaled，暂停时冻结）——测试断言用 </summary>
    public float WorldElapsed { get; private set; }
    /// <summary> 界面累计时间（unscaled，暂停时照走）——测试断言用 </summary>
    public float UiElapsed { get; private set; }
    /// <summary> 背包倒计时剩余（unscaled）——测试断言用 </summary>
    public float BagCountdownRemaining { get; private set; }

    /// <summary> 占位背包物品（名字/数量/图标色），UI 构建脚本与槽位选择共用 </summary>
    public static readonly (string name, int count, Color icon)[] DemoItems =
    {
        ("铁矿", 12, new Color(0.62f, 0.55f, 0.48f)),
        ("铜矿", 8,  new Color(0.72f, 0.45f, 0.28f)),
        ("银矿", 3,  new Color(0.78f, 0.80f, 0.85f)),
        ("治疗药剂", 5, new Color(0.36f, 0.70f, 0.44f)),
        ("精制治疗药剂", 2, new Color(0.30f, 0.85f, 0.60f)),
        ("未名残页", 1, new Color(0.55f, 0.45f, 0.75f)),
    };

    private const int SlotTotal = 24;
    private const int SlotColumns = 4;

    private readonly List<Image> slotFrames = new List<Image>();

    private void Start()
    {
        BagCountdownRemaining = bagCountdownStart;

        if (settingsButton != null) settingsButton.onClick.AddListener(OpenSettings);
        if (bagButton != null) bagButton.onClick.AddListener(OpenBag);

        if (bagDetailText != null) bagDetailText.text = "选中物品后在此显示";

        // 槽位视觉由 UI 构建脚本生成（Slot_0..Slot_N），运行时发现并接线选中框与点击
        if (slotGridRoot != null)
        {
            foreach (Transform slot in slotGridRoot)
            {
                if (!slot.name.StartsWith("Slot_")) continue;
                int captured = int.Parse(slot.name.Substring("Slot_".Length));
                var frame = slot.Find("SelFrame") != null
                    ? slot.Find("SelFrame").GetComponent<Image>()
                    : null;
                if (frame != null) RegisterSlot(captured, frame);
                var btn = slot.GetComponent<Button>();
                if (btn != null) btn.onClick.AddListener(() => SelectSlot(captured));
            }
        }
    }

    private void Update()
    {
        // 两条时间线的核心：deltaTime 暂停时为 0（世界冻结），unscaledDeltaTime 照走（UI 继续）
        WorldElapsed += Time.deltaTime;
        UiElapsed += Time.unscaledDeltaTime;
        if (BagCountdownRemaining > 0f)
            BagCountdownRemaining -= Time.unscaledDeltaTime;

        if (Input.GetKeyDown(settingsKey))
        {
            if (settingsPanel.activeInHierarchy) CloseSettings();
            else OpenSettings();
        }
        if (Input.GetKeyDown(bagKey))
        {
            if (bagPanel.activeInHierarchy) CloseBag();
            else OpenBag();
        }

        RefreshClocks();
    }

    public void OpenSettings() => settingsPanel.SetActive(true);
    public void CloseSettings() => settingsPanel.SetActive(false);
    public void OpenBag() => bagPanel.SetActive(true);
    public void CloseBag() => bagPanel.SetActive(false);

    /// <summary> UI 构建脚本注册槽位选中框 </summary>
    public void RegisterSlot(int index, Image frame)
    {
        while (slotFrames.Count <= index) slotFrames.Add(null);
        slotFrames[index] = frame;
    }

    /// <summary> 槽位点击：更新详情与选中框——暂停中照常可交互（UI 逻辑不暂停的证明） </summary>
    public void SelectSlot(int index)
    {
        var item = DemoItems[index % DemoItems.Length];
        if (bagDetailText != null)
            bagDetailText.text = $"{item.name} ×{item.count}";

        for (int i = 0; i < slotFrames.Count; i++)
            if (slotFrames[i] != null)
                slotFrames[i].gameObject.SetActive(i == index);
    }

    private void RefreshClocks()
    {
        if (hudWorldClock != null) hudWorldClock.text = $"世界时间  {FormatClock(WorldElapsed)}";
        if (settingsWorldClock != null) settingsWorldClock.text = FormatClock(WorldElapsed);
        if (settingsUiClock != null) settingsUiClock.text = FormatClock(UiElapsed);
        if (bagCountdown != null) bagCountdown.text = $"限时补给  {FormatCountdown(Mathf.Max(0f, BagCountdownRemaining))}";
    }

    private static string FormatClock(float t) =>
        $"{(int)(t / 60f):00}:{(int)(t % 60f):00}.{(int)(t * 10f % 10f)}";

    private static string FormatCountdown(float t) =>
        $"{(int)(t / 60f):00}:{(int)(t % 60f):00}";
}
