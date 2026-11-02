using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 登录界面（框架视图）。把原本"场景里直接实例化"的 LoginPanel 纳入 UIManager：
///   - 预制体路径仍由 UIConfig.json 指向 Assets/Game/UI/Login/LoginPanel.prefab，
///     依旧从 ui 包（YooAsset）加载，热更语义不变；
///   - 界面逻辑仍在 LoginSceneController / SignupController 上，本类只负责框架生命周期转发：
///     OnOpen → 开始登录会话（接线/恢复勾选/自动登录），OnClose → 结束会话；
///   - 登录成功后自动关闭自己（面板实例常驻 DDOL，不关会跟到主城）。
///
/// 挂在 LOGIN 场景里由 LoginSceneBootstrap 调 UIManager.Open(UIType.LoginView) 打开。
/// </summary>
public class LoginView : UIView
{
    [Tooltip("登录逻辑控制器；留空则运行时从子树里自动查找")]
    [SerializeField] private LoginSceneController loginController;

    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        if (loginController == null)
            loginController = GetComponentInChildren<LoginSceneController>(true);

        if (loginController == null)
        {
            Debug.LogError("[LoginView] 预制体里找不到 LoginSceneController，登录按钮不会生效");
            return;
        }

        loginController.loginSucceeded = OnLoginSucceeded;
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        loginController?.BeginSession();
    }

    public override void OnClose()
    {
        loginController?.EndSession();
        base.OnClose();
    }

    /// <summary>登录成功：收起登录界面（切场景由 LoginSceneController 负责）</summary>
    private void OnLoginSucceeded()
    {
        if (UIManager.Instance != null && UIManager.Instance.IsOpen(UIType.LoginView))
            UIManager.Instance.Close(UIType.LoginView).Forget();
    }
}
