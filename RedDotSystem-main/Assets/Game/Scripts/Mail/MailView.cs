using System;
using System.Collections.Generic;

using Common.Network;

using Cysharp.Threading.Tasks;

using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 邮件界面控制器（youjian Canvas）
/// 布局照任务面板：深色扁平 HUD——左上标题、右上红色关闭键、顶/底分隔线，
/// 左列为邮件列表（VirtualScrollList 固定高 56 行虚拟列表，只实例化视口内的行），
/// 右列为邮件详情（发件人/时间/正文/附件清单）+ 领取附件/删除/测试邮件（GM 联调键），底部状态栏
/// 列表行：未读标题亮色、已读置灰；有附件的行在状态位显示"礼"标记
/// 数据全部来自 MailManager（服务端权威快照），本类只做渲染与交互转发
/// </summary>
public class MailView : UIView
{
    // ──────────────────────────────
    // 序列化引用（编辑器搭建脚本注入）
    // ──────────────────────────────

    [Header("面板")]
    [ControlBinding]
    public Button closeButton;

    [Header("顶部")]
    [ControlBinding]
    public TMP_Text titleText;

    [Header("左列：邮件列表")]
    [ControlBinding]
    public ScrollRect mailScroll;

    [ControlBinding("MailScroll/Viewport/Content")]
    public RectTransform listContent;

    [ControlBinding("MailScroll/Viewport/Content/RowTemplate")]
    public RectTransform rowTemplate;

    [Header("右列：邮件详情")]
    [ControlBinding]
    public TMP_Text detailTitle;

    [ControlBinding]
    public TMP_Text detailMeta;

    [ControlBinding]
    public TMP_Text detailBody;

    [ControlBinding]
    public TMP_Text attachText;

    [ControlBinding]
    public Button claimButton;

    [ControlBinding]
    public Button deleteButton;

    [ControlBinding]
    public Button testMailButton;

    [Header("底部提示")]
    [ControlBinding]
    public TMP_Text statusText;

    [Header("视觉参数")]

    /// <summary>列表行高（与 RowTemplate 尺寸一致）</summary>
    public float rowHeight = 56f;

    /// <summary>列表行宽（与 RowTemplate 尺寸一致）</summary>
    public float rowWidth = 310f;

    /// <summary>行底色（选中时换成 rowSelectedColor）</summary>
    public Color rowColor = new(0.180f, 0.200f, 0.239f, 1f);

    /// <summary>选中行底色（略亮）</summary>
    public Color rowSelectedColor = new(0.227f, 0.259f, 0.314f, 1f);

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>列表虚拟组件</summary>
    private VirtualScrollList mailVirtual;

    /// <summary>
    /// 行子引用缓存：建行时 Find/GetComponent 一次，换绑直接复用
    /// 模板缺失的子节点解析为 null，绑定侧判空语义与原逐次 Find 一致
    /// </summary>
    private sealed class MailRowRefs : MonoBehaviour
    {
        public Image Background;

        public Button RowButton;

        public TMP_Text Title;

        public TMP_Text State;

        public TMP_Text Meta;

        public RectTransform Separator;

        public void Resolve(RectTransform row)
        {
            Background = row.GetComponent<Image>();
            RowButton = row.GetComponent<Button>();
            Title = row.Find("TitleText")?.GetComponent<TMP_Text>();
            State = row.Find("StateText")?.GetComponent<TMP_Text>();
            Meta = row.Find("MetaText")?.GetComponent<TMP_Text>();
            Separator = row.Find("Separator") as RectTransform;
        }
    }

    /// <summary>取行引用缓存（理论上建行时已挂；缺失则补解析一次，之后复用）。</summary>
    private static MailRowRefs GetRowRefs(RectTransform row)
    {
        MailRowRefs refs = row.GetComponent<MailRowRefs>();
        if (refs == null)
        {
            refs = row.gameObject.AddComponent<MailRowRefs>();
            refs.Resolve(row);
        }
        return refs;
    }

    /// <summary>当前选中邮件（0 = 未选中）</summary>
    private int selectedMailId;

    /// <summary>测试邮件按钮文案（按下后发一封带附件的系统邮件给自己，联调用）</summary>
    private const string TestMailTitle = "系统测试邮件";

    private const string TestMailBody = "这是一封带附件的测试邮件，用于联调邮箱功能。点击\"领取附件\"即可把物品收入背包。";

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        if (rowTemplate != null) rowTemplate.gameObject.SetActive(false);
    }

    /// <summary>绑定完成后的一次性初始化：订阅数据层 + 建立虚拟列表（实例常驻，只做一次）</summary>
    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        if (MailManager.Instance != null)
        {
            MailManager.Instance.MailsUpdated += RefreshAll;
            MailManager.Instance.StatusChanged += SetStatus;
        }

        SetupMailList();
    }

    /// <summary>每次打开重新接线（关闭时框架走 OnRemoveListener 摘掉）</summary>
    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (claimButton != null) claimButton.onClick.AddListener(OnClaimClicked);
        if (deleteButton != null) deleteButton.onClick.AddListener(OnDeleteClicked);
        if (testMailButton != null) testMailButton.onClick.AddListener(OnTestMailClicked);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (claimButton != null) claimButton.onClick.RemoveListener(OnClaimClicked);
        if (deleteButton != null) deleteButton.onClick.RemoveListener(OnDeleteClicked);
        if (testMailButton != null) testMailButton.onClick.RemoveListener(OnTestMailClicked);
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        SetStatus(string.Empty);
        RefreshAll();
        MailManager.Instance?.RequestMails();   // 打开即拉最新列表
    }

    private void OnDestroy()
    {
        if (MailManager.Instance == null) return;
        MailManager.Instance.MailsUpdated -= RefreshAll;
        MailManager.Instance.StatusChanged -= SetStatus;
    }

    // ──────────────────────────────
    // 面板开关
    // ──────────────────────────────

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(UIType.MailView).Forget();
    }

    // ──────────────────────────────
    // 虚拟列表接入
    // ──────────────────────────────

    /// <summary>
    /// 接入邮件列表虚拟列表的方法
    /// 固定高行（56）、单池；CreateItem 克隆 RowTemplate 到 Content，BindItem 填邮件内容
    /// </summary>
    private void SetupMailList()
    {
        if (mailScroll == null || listContent == null || rowTemplate == null) return;

        mailVirtual = mailScroll.GetComponent<VirtualScrollList>();
        if (mailVirtual == null) mailVirtual = mailScroll.gameObject.AddComponent<VirtualScrollList>();
        mailVirtual.Configure(8f, 8f, 4f);
        mailVirtual.GetItemCount = () => MailManager.Instance != null ? MailManager.Instance.Count : 0;
        mailVirtual.GetItemHeight = _ => rowHeight;
        mailVirtual.CreateItem = _ =>
        {
            RectTransform row = Instantiate(rowTemplate, listContent);
            row.gameObject.SetActive(true);
            // 建行时解析一次子引用，换绑热路径不再 Find+GetComponent
            row.gameObject.AddComponent<MailRowRefs>().Resolve(row);
            return row;
        };
        mailVirtual.BindItem = BindMailRow;
    }

    /// <summary>
    /// 行换绑：标题（未读亮色/已读置灰）+ 发件人·时间小字 + 状态位（未读/附件标记/已领取）
    /// 行是池化复用的，换绑时必须重置全部子物体状态
    /// </summary>
    private void BindMailRow(int index, RectTransform row)
    {
        MailManager manager = MailManager.Instance;
        if (manager == null) return;
        IReadOnlyList<MailInfo> mails = manager.Mails;
        if (index < 0 || index >= mails.Count) return;
        MailInfo mail = mails[index];

        row.sizeDelta = new Vector2(rowWidth, rowHeight);
        row.anchoredPosition = new Vector2(0f, row.anchoredPosition.y);

        // 子引用走行创建时缓存（缺失节点仍为 null，下方判空逻辑与原 Find 一致）
        MailRowRefs refs = GetRowRefs(row);
        Image background = refs.Background;
        Button button = refs.RowButton;
        TMP_Text title = refs.Title;
        TMP_Text state = refs.State;
        TMP_Text meta = refs.Meta;
        RectTransform separator = refs.Separator;

        if (background != null) background.color = mail.MailId == selectedMailId ? rowSelectedColor : rowColor;
        if (separator != null) separator.gameObject.SetActive(true);
        if (button != null)
        {
            button.enabled = true;
            int targetId = mail.MailId;   // 每次换绑都重挂点击（先清旧监听，否则会点到上一封邮件）
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => SelectMail(targetId));
        }

        if (title != null)
        {
            title.fontSize = 16;
            title.color = mail.Read
                ? new Color(0.541f, 0.561f, 0.588f, 1f)      // 已读：次文字色
                : new Color(0.878f, 0.878f, 0.878f, 1f);    // 未读：主文字色
            title.fontStyle = mail.Read ? FontStyles.Normal : FontStyles.Bold;
            title.SetText(mail.Title);
        }
        if (meta != null)
        {
            meta.fontSize = 13;
            meta.color = new Color(0.541f, 0.561f, 0.588f, 1f);
            meta.SetText($"{mail.SenderName} · {FormatTime(mail.CreateTimeMs)}");
        }
        if (state != null)
        {
            state.fontSize = 13;
            if (mail.Items.Count > 0 && !mail.Claimed)
            {
                // 有附件未领：钢蓝"礼"标记（最该处理的）
                state.color = new Color(0.498f, 0.698f, 0.851f, 1f);
                state.SetText("礼");
            }
            else if (!mail.Read)
            {
                state.color = new Color(0.498f, 0.698f, 0.851f, 1f);
                state.SetText("未读");
            }
            else if (mail.Claimed)
            {
                state.color = new Color(0.541f, 0.561f, 0.588f, 1f);
                state.SetText("已领取");
            }
            else
            {
                state.color = new Color(0.420f, 0.440f, 0.470f, 1f);
                state.SetText("已读");
            }
        }
    }

    // ──────────────────────────────
    // 渲染
    // ──────────────────────────────

    /// <summary>全量刷新：标题计数 + 重绑可见行 + 右列详情</summary>
    private void RefreshAll()
    {
        MailManager manager = MailManager.Instance;
        if (manager == null) return;

        if (titleText != null)
        {
            titleText.SetText(
                $"邮件　<size=15><color=#6F6757>未读 {manager.UnreadCount} · {manager.Count}/{(manager.Capacity > 0 ? manager.Capacity.ToString() : "-")} 封</color></size>");
        }

        // 选中项失效（被删除后）时退回第一封，避免详情区显示空
        if (selectedMailId != 0 && manager.GetMail(selectedMailId) == null)
        {
            selectedMailId = manager.Count > 0 ? manager.Mails[0].MailId : 0;
        }

        mailVirtual?.RefreshData();
        RefreshDetail();
    }

    /// <summary>
    /// 右列详情：标题（含未读标记）/发件人/时间/正文/附件清单 + 按邮件状态切换可用按钮
    /// </summary>
    private void RefreshDetail()
    {
        MailManager manager = MailManager.Instance;
        MailInfo mail = manager?.GetMail(selectedMailId);

        if (mail == null)
        {
            if (detailTitle != null) detailTitle.SetText(manager is { Count: > 0 } ? "请选择邮件" : "暂无邮件");
            if (detailMeta != null) detailMeta.SetText(string.Empty);
            if (detailBody != null) detailBody.SetText(string.Empty);
            if (attachText != null) attachText.SetText(string.Empty);
            SetButton(claimButton, false, null);
            SetButton(deleteButton, false, null);
            return;
        }

        if (detailTitle != null)
        {
            string unreadMark = mail.Read ? string.Empty : " <color=#C56A6A>●</color>";
            detailTitle.SetText($"{mail.Title}{unreadMark}");
        }
        if (detailMeta != null) detailMeta.SetText($"发件人：{mail.SenderName}　时间：{FormatTime(mail.CreateTimeMs)}");
        if (detailBody != null) detailBody.SetText(mail.Body);

        // 附件清单（已领取的整行置灰 + 尾注）
        if (attachText != null)
        {
            if (mail.Items.Count == 0)
            {
                attachText.SetText(string.Empty);
            }
            else
            {
                const string dim = "#8A8F96";
                var parts = new List<string>(mail.Items.Count);
                foreach (MailItemInfo item in mail.Items)
                {
                    parts.Add(string.IsNullOrEmpty(item.Name)
                        ? $"物品#{item.ItemId} x{item.Count}"
                        : $"{item.Name} x{item.Count}");
                }
                string suffix = mail.Claimed ? $" <color={dim}>（已领取）</color>" : string.Empty;
                attachText.SetText($"附件：{string.Join("、", parts)}{suffix}");
            }
        }

        // 按钮：领取只在"有附件且未领取"时可用；删除对任意有效邮件可用
        bool claimable = mail.Items.Count > 0 && !mail.Claimed;
        SetButton(claimButton, claimable, mail.Claimed ? "已领取" : "领取附件");
        SetButton(deleteButton, true, "删除邮件");
    }

    /// <summary>按钮显隐 + 文案（隐藏时同时禁用，避免残留可点状态）。</summary>
    private static void SetButton(Button button, bool visible, string label)
    {
        if (button == null) return;
        button.gameObject.SetActive(visible);
        if (label == null) return;
        TMP_Text text = button.transform.Find("Label")?.GetComponent<TMP_Text>();
        if (text != null) text.SetText(label);
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content ?? string.Empty);
    }

    // ──────────────────────────────
    // 交互
    // ──────────────────────────────

    /// <summary>点击列表行：选中并刷新详情；未读邮件顺带置已读（服务端幂等，红点即时熄灭）</summary>
    public void SelectMail(int mailId)
    {
        selectedMailId = mailId;
        MailManager manager = MailManager.Instance;
        if (manager != null && mailId > 0)
        {
            MailInfo mail = manager.GetMail(mailId);
            if (mail != null && !mail.Read) manager.MarkRead(mailId);
        }
        RefreshAll();
    }

    private void OnClaimClicked()
    {
        if (selectedMailId == 0) return;
        MailManager.Instance?.Claim(selectedMailId);
    }

    private void OnDeleteClicked()
    {
        if (selectedMailId == 0) return;

        // 附件未领取也允许删（作废与否由玩家斟酌）；删除结果由响应消息反馈
        MailManager.Instance?.Delete(selectedMailId);
    }

    /// <summary>测试邮件（GM 联调键）：发一封带附件的系统邮件给自己，验证收件→红点→领取全链路</summary>
    private void OnTestMailClicked()
    {
        MailManager.Instance?.SendGmMail(0, TestMailTitle, TestMailBody, new List<(int, int)>
        {
            (1001, 2),   // 治疗药剂 x2
            (1003, 5),   // 铁矿 x5
        });
        SetStatus("已发送测试邮件（发件人=自己）");
    }

    // ──────────────────────────────
    // 文案映射
    // ──────────────────────────────

    /// <summary>邮件时间显示（服务端 UTC 毫秒 → 本地日期时间短格式）。</summary>
    private static string FormatTime(long timeMs)
    {
        if (timeMs <= 0) return "-";
        return DateTimeOffset.FromUnixTimeMilliseconds(timeMs).LocalDateTime.ToString("MM-dd HH:mm");
    }
}
