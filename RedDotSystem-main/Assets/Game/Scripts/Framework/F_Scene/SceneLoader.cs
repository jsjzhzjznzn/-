using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 场景加载统一入口：唯一的异步切换通道（NGO 网络场景除外，见 RoomState 注释）。
/// 职责：加载界面 + 真实进度 + 防重入 + 失败兜底 + 加载完成回调。
/// 与 UIManager 无耦合：加载界面自建 Canvas，任何场景（含 UI 框架未就绪的 LOGIN）都能用。
/// </summary>
public static class SceneLoader
{
    /// <summary>是否有一次加载正在进行</summary>
    public static bool IsLoading { get; private set; }

    /// <summary>
    /// 异步切换场景。加载中重复调用会被忽略（只打警告），不会叠加两次加载。
    /// </summary>
    /// <param name="sceneName">场景名（必须在 Build Settings 里）</param>
    /// <param name="onLoaded">新场景激活后回调；用于必须在新场景里才生效的提示（如被顶号提示）</param>
    /// <param name="mode">默认 Single；Additive 也支持</param>
    public static async UniTask LoadAsync(string sceneName, Action onLoaded = null,
                                          LoadSceneMode mode = LoadSceneMode.Single)
    {
        if (IsLoading)
        {
            Debug.LogWarning("[SceneLoader] 已有加载在进行中，忽略这次请求：" + sceneName);
            return;
        }

        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("[SceneLoader] 场景名为空");
            return;
        }

        IsLoading = true;
        LoadingScreen screen = null;
        bool succeeded = true;

        try
        {
            screen = LoadingScreen.Show(sceneName);
            await UniTask.Yield();   // 先让遮罩渲染一帧，再开始加载

            AsyncOperation operation = SceneManager.LoadSceneAsync(sceneName, mode);
            if (operation == null)
            {
                // 场景没进 Build Settings 时 Unity 直接返回 null
                Debug.LogError("[SceneLoader] 无法加载场景（不在 Build Settings 里？）：" + sceneName);
                succeeded = false;
            }
            else
            {
                operation.allowSceneActivation = false;

                // progress 上限是 0.9（激活前），映射成 0~1 才能当进度条用
                while (operation.progress < 0.9f)
                {
                    screen.SetProgress(operation.progress / 0.9f);
                    await UniTask.Yield();
                }

                screen.SetProgress(1f);
                await screen.WaitMinimumStayAsync();

                operation.allowSceneActivation = true;
                while (!operation.isDone)
                    await UniTask.Yield();
            }
        }
        catch (Exception exception)
        {
            Debug.LogError("[SceneLoader] 加载异常：" + exception);
            succeeded = false;
        }

        if (screen != null)
            await screen.HideAsync();

        IsLoading = false;

        if (!succeeded || onLoaded == null)
            return;

        try
        {
            onLoaded();
        }
        catch (Exception exception)
        {
            Debug.LogError("[SceneLoader] onLoaded 回调异常：" + exception);
        }
    }
}
