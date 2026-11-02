namespace MieMieFrameWork
{
    using System;
    using System.Threading;
    using Cysharp.Threading.Tasks;
    using SkierFramework;

    /// <summary>
    /// 启动阶段一：资源后端就绪（YooAsset）
    /// 失败只告警不中断启动，避免"资源没配好游戏就完全起不来"
    /// </summary>
    public partial class ModuleHub
    {
        private async UniTask BootResourceAsync(CancellationToken cancellationToken)
        {
            if (!initializeResource)
            {
                FrameLog.Warn("已关闭资源初始化，按路径加载的音频将不可用", "Module");
                return;
            }

            try
            {
                FrameLog.Info("初始化资源后端", "Module");
                await ResourceManager.Instance.InitializeAsync(cancellationToken);
                FrameLog.Info("资源后端就绪", "Module");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception Exception)
            {
                FrameLog.Error("资源后端初始化失败，框架继续启动（按路径加载的资源会失败）：" + Exception.Message, "Module");
            }
        }
    }
}
