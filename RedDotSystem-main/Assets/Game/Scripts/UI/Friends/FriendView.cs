using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 好友/聊天面板的框架视图（P8-1）。
///
/// 迁移策略：逻辑仍由 `FriendUIController` 承担（1400 行微信式好友 + 聊天 + 表情 + 红点），
/// 这里只做「框架适配」——把面板被打开/关闭的生命周期接到 SkierFramework，
/// 这样预制体里的 `UIControlData` 按字段名就能把 33 个控件绑回原有字段，不需要重写业务代码。
///
/// 注意：旧场景实例（zhuye/FriendCanvas/FriendUIRoot 上的 FriendUIController）暂时保留，
/// 等入口按钮切到 `UIManager.Open(UIType.FriendView)` 并回归通过后再删除。
/// </summary>
public class FriendView : FriendUIController
{
    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        // 每次打开回到好友列表页，并拉一次最新好友列表（与原 OnEntryClicked 的行为一致）
        ShowListPage();
        ResetChat();

        if (ClientNetworkManager.Instance != null && ClientNetworkManager.Instance.IsConnected)
        {
            _ = ClientNetworkManager.Instance.SendFriendListRequest();
            _ = ClientNetworkManager.Instance.SendFriendRequestsRequest();
        }
    }

    public override void OnClose()
    {
        // 关面板 = 离开会话：之后来的消息才计未读红点
        ResetChat();
        base.OnClose();
    }

    /// <summary>面板右上角关闭键：框架版走 UIManager.Close（场景版仍是隐藏 panelRoot）</summary>
    protected override void OnCloseClicked()
    {
        ResetChat();
        UIManager.Instance.Close(UIType.FriendView).Forget();
    }

    /// <summary>供入口按钮/外部代码调用（等价于点右上角"好友"）</summary>
    public static void Open()
    {
        UIManager.Instance.Open(UIType.FriendView).Forget();
    }
}
