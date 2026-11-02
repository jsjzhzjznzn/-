using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// LOGIN 场景启动器：把登录界面交给 UI 框架加载。
///
/// 历史：这里原先自己用 ResourceManager.InstantiateAsync 实例化 LoginPanel，
/// 属于"场景直连"路线；现在统一到 UIManager（UIConfig 的 LoginView 条目），
/// 好处是登录面板与其他面板同一套生命周期/层级/排序，且能被 UI 编辑器框架管理。
///
/// 热更语义不变：UIConfig 里的 path 仍是 Assets/Game/UI/Login/LoginPanel.prefab，
/// UIManager 同样通过 ResourceManager 从 ui 包（YooAsset）实例化，不会打进场景。
///
/// 生命周期：进入 LOGIN 打开，离开 LOGIN 关闭（面板实例由框架缓存、常驻 DDOL，
/// 不显式关闭会残留到主城/挑战场景）。
/// </summary>
public class LoginSceneBootstrap : MonoBehaviour
{
    private async void Start()
    {
        EnsureEventSystem();

        if (UIManager.Instance == null)
        {
            Debug.LogError("[LoginSceneBootstrap] UIManager 不存在，登录界面无法打开");
            return;
        }

        await UIManager.Instance.Open(UIType.LoginView);
    }

    private void OnDestroy()
    {
        // 场景切换/销毁时收起登录界面（未打开过就跳过，避免无谓的加载）
        if (UIManager.Instance == null) return;
        if (!UIManager.Instance.IsOpen(UIType.LoginView)) return;
        UIManager.Instance.Close(UIType.LoginView).Forget();
    }

    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null)
            return;

        GameObject go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
        Debug.Log("[LoginSceneBootstrap] 场景缺少 EventSystem，已自动补建");
    }
}
