namespace Game.Save
{
    using System.Threading.Tasks;

    using Common.Network;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 场景存档模块公共服务：拉取 / 排队上报 / 冲刷
    /// 只在事件点写库（与用户确认过的取舍：崩溃最多丢最后一段位移）
    /// </summary>
    public interface ISceneSaveService : IGameService
    {
        /// <summary>当前是否具备存档条件（已连接且已登录）</summary>
        bool IsAvailable { get; }

        /// <summary>最近一次成功响应里服务端写入的时间（排障用）</summary>
        long LastServerUpdatedAtMs { get; }

        /// <summary>拉取指定场景的存档快照（超时返回 null）</summary>
        Task<SceneSaveSnapshot> PullAsync(string sceneName, float timeoutSeconds = 2f);

        /// <summary>排队上报快照（覆盖式，最新一份生效）</summary>
        void QueuePush(SceneSaveSnapshot snapshot);

        /// <summary>立刻冲刷待发快照（事件点离场前调用）</summary>
        Task<bool> FlushAsync(float timeoutSeconds = 1f);
    }
}