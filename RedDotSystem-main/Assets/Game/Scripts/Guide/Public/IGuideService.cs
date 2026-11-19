namespace Game.Guide
{
    using MieMieFrameWork.Business;

    /// <summary>
    /// 新手引导模块公共服务：运行时状态 + 命令
    /// 进度存服务端（3601~3604），本地只做断网兜底
    /// </summary>
    public interface IGuideService : IGameService
    {
        /// <summary>引导是否正在播放</summary>
        bool IsRunning { get; }

        /// <summary>当前步下标</summary>
        int CurrentStepIndex { get; }

        /// <summary>当前步定义（未播放时为 null）</summary>
        GuideStep CurrentStep { get; }

        /// <summary>服务端记录到的进度步</summary>
        int SavedStep { get; }

        /// <summary>服务端记录是否已完成</summary>
        bool SavedCompleted { get; }

        /// <summary>从头强制播放（无视已完成记录）</summary>
        void ForceStart();

        /// <summary>设置面板里的"重看新手引导"</summary>
        void ReplayFromStart();
    }
}
