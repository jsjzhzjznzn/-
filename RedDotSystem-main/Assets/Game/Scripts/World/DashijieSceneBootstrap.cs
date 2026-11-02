using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

/// <summary>
/// dashijie 场景入口：开场打开大世界 HUD，场景卸载时关闭 HUD 并清掉输入门禁与暂停账本。
/// 换角色时把新的 Player 交给存档控制器，保证「返回主页再进来」写的是当前角色。
/// </summary>
public class DashijieSceneBootstrap : MonoBehaviour
{
    [SerializeField] private DashijiePlayerSwitcher switcher;
    [SerializeField] private DashijieSceneSaveController saveController;

    private void Awake()
    {
        if (switcher == null) switcher = FindObjectOfType<DashijiePlayerSwitcher>();
        if (saveController == null) saveController = FindObjectOfType<DashijieSceneSaveController>();

        if (switcher != null) switcher.PlayerChanged += OnPlayerChanged;
    }

    private void Start()
    {
        if (UIManager.Instance == null) return;
        UIManager.Instance.Open(UIType.DashijieHudView, switcher).Forget();
    }

    private void OnDestroy()
    {
        if (switcher != null) switcher.PlayerChanged -= OnPlayerChanged;

        // 静态门禁在场景之间是活的：这里必须清空，否则下一个场景的角色会被锁住
        PlayerInputGate.Clear();

        if (UIManager.Instance != null && UIManager.Instance.IsOpen(UIType.DashijieHudView))
            UIManager.Instance.Close(UIType.DashijieHudView).Forget();
    }

    private void OnPlayerChanged(Player player)
    {
        if (saveController != null) saveController.BindPlayer(player);
    }
}
