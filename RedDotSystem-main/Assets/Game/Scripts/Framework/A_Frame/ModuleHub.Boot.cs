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
                GameLog.Warn("[Module] 已关闭资源初始化，按路径加载的音频将不可用");
                return;
            }

            try
            {
                GameLog.Info("[Module] 初始化资源后端");
                await ResourceManager.Instance.InitializeAsync(cancellationToken);
                GameLog.Info("[Module] 资源后端就绪");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception Exception)
            {
                GameLog.Error("[Module] 资源后端初始化失败，框架继续启动（按路径加载的资源会失败）：" + Exception.Message);
            }
        }
    }
}
