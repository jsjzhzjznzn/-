namespace Game.Narrative.Quest
{
    using System.Collections.Generic;

    using MieMieFrameWork.Business;

    using Miemie.DialogSystem.Quest;

    /// <summary>
    /// 叙事任务模块公共服务：只读查询 + 命令
    /// 权威在服务端，本地只是 Mm QuestManager 的镜像
    /// 说明：QuestData 属于 Mm 叙事框架（ScriptableObject），整套 QuestSystem 尚未抽成独立程序集，
    /// 因此本接口暂与实现同处 Assembly-CSharp；抽离时接口随模型一起搬迁，调用方无需改动。
    /// </summary>
    public interface IQuestService : IGameService
    {
        /// <summary>全部任务（按任务ID升序）</summary>
        IReadOnlyList<QuestData> AllQuests { get; }

        /// <summary>当前追踪的任务（0 = 未追踪）</summary>
        int TrackedQuestId { get; }

        /// <summary>可接取数量</summary>
        int AcceptableCount { get; }

        /// <summary>可提交数量</summary>
        int SubmittableCount { get; }

        /// <summary>某分类下的任务（按任务ID升序）</summary>
        List<QuestData> QuestsOf(EQuestCategory category);

        /// <summary>任务状态（未注册返回未激活）</summary>
        EQuestState StateOf(int questId);

        /// <summary>目标数量</summary>
        int GoalCountOf(int questId);

        /// <summary>第 index 个目标的进度</summary>
        int GoalProgressOf(int questId, int index);

        /// <summary>当前目标下标（全部达标返回 -1）</summary>
        int CurrentGoalIndex(int questId);

        /// <summary>当前目标（全部达标返回 null）</summary>
        QuestGoal CurrentGoal(int questId);

        /// <summary>任务配置</summary>
        QuestData ConfigOf(int questId);

        /// <summary>是否可接取</summary>
        bool IsAcceptable(int questId);

        /// <summary>是否可提交</summary>
        bool IsSubmittable(int questId);

        /// <summary>追踪任务（0 = 取消追踪）</summary>
        void Track(int questId);

        /// <summary>解析当前应追踪的任务</summary>
        int ResolveTrackedQuest();

        /// <summary>向服务端请求一次镜像同步</summary>
        void RequestSync();

        /// <summary>接取任务</summary>
        void Accept(int questId);

        /// <summary>提交任务</summary>
        void Submit(int questId);

        /// <summary>放弃任务</summary>
        void Abandon(int questId);
    }
}
