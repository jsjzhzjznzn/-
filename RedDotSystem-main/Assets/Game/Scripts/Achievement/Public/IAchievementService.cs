namespace Game.Achievement
{
    using System.Collections.Generic;

    using Common.Network;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 成就模块公共服务：只读查询 + 命令
    /// 服务端权威，领取与进度都由服务端回执驱动
    /// </summary>
    public interface IAchievementService : IGameService
    {
        /// <summary>是否已经拿到过列表数据</summary>
        bool IsLoaded { get; }

        /// <summary>是否正在请求列表</summary>
        bool IsLoading { get; }

        /// <summary>基础汇总（总数/已解锁/可领取/成就点）</summary>
        AchievementSummary Summary { get; }

        /// <summary>成就配置（服务端排序，UI 直接使用）</summary>
        IReadOnlyList<AchievementCfgInfo> Configs { get; }

        /// <summary>可领取数量</summary>
        int ClaimableCount { get; }

        /// <summary>成就状态（0 未解锁等，与服务端一致）</summary>
        int StateOf(int achievementId);

        /// <summary>是否正在领取该成就</summary>
        bool IsClaiming(int achievementId);

        /// <summary>展示用进度</summary>
        int DisplayProgress(int achievementId);

        /// <summary>目标值</summary>
        int TargetOf(int achievementId);

        /// <summary>请求成就列表</summary>
        void RequestAchievements();

        /// <summary>领取单个成就奖励</summary>
        void Claim(int achievementId);

        /// <summary>一键领取</summary>
        void ClaimAll();
    }
}
