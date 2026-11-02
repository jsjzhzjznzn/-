using System;
using System.Collections.Generic;
using MiMieMVVM;
using UnityEngine;

namespace Miemie.DialogSystem.Quest
{
  /// <summary>
  /// 任务配置数据类
  /// 这里定义了一个任务最基本的配置信息 且包含了任务的生命周期方法
  /// 具体的任务逻辑 需要在外部实现
  /// </summary>
  [CreateAssetMenu(fileName = "Quest", menuName = "Quest System/Quest")]
  public class QuestData : ScriptableObject, IQuestBehaviour, IModelConfig
  {
    /// <summary>
    /// 任务ID
    /// </summary>
    [SerializeField]
    private int questId;

    /// <summary>
    /// 任务分类
    /// </summary>
    [SerializeField]
    private EQuestCategory category = EQuestCategory.支线任务;

    /// <summary>
    /// 任务标题
    /// </summary>
    [SerializeField]
    private string title;

    /// <summary>
    /// 任务描述
    /// </summary>
    [SerializeField]
    [TextArea]
    private string description;

    /// <summary>
    /// 任务接受模式
    /// </summary>
    [SerializeField]
    private EQuestAcceptMode acceptMode = EQuestAcceptMode.手动接受;

    /// <summary>
    /// 任务时间限制
    /// </summary>
    [SerializeField]
    private float timeLimit;

    /// <summary>
    /// 任务目标组
    /// </summary>
    [SerializeField]
    private List<QuestGoal> goalList = new();

    /// <summary>
    /// 前置任务ID列表
    /// </summary>
    [SerializeField]
    private List<int> prerequisiteIdList = new();

    /// <summary>
    /// 提交奖励（服务端在提交的同一事务内发放，背包满转邮件；
    /// 任务面板的奖励区读同一份，保证"看到的"与"拿到的"一致）
    /// </summary>
    [SerializeField]
    private List<QuestRewardConfig> rewardList = new();

    /// <summary>
    /// 任务目标 NPC 标识（可选，与场景里 WorldDialogueNpc.npcKey 对应）
    /// 只用于"地图上该标谁"，不参与目标匹配；为空表示该任务没有地图标记
    /// </summary>
    [SerializeField]
    private string npcKey;

    // 属性
    public int QuestId => questId;
    public int ConfigId => questId;
    public string Name => title;
    public EQuestCategory Category => category;
    public string Title => title;
    public string Description => description;
    public EQuestAcceptMode AcceptMode => acceptMode;
    public float TimeLimit => timeLimit;
    public IReadOnlyList<int> PrerequisiteIdList => prerequisiteIdList;
    public bool HasTimeLimit => timeLimit > 0f;

    /// <summary>提交奖励列表</summary>
    public IReadOnlyList<QuestRewardConfig> Rewards => rewardList;

    /// <summary>任务目标 NPC 标识（空 = 无地图标记）</summary>
    public string NpcKey => npcKey;


    /// <summary>
    /// 获取目标列表
    /// </summary>
    public IReadOnlyList<QuestGoal> GetGoals() => goalList;

    /// <summary>
    /// 接受任务
    /// </summary>
    public virtual void OnAccepted(QuestStateContext context)
    {
    }

    /// <summary>
    /// 任务进度变化
    /// </summary>
    public virtual void OnProgressChanged(QuestStateContext context)
    {
    }

    /// <summary>
    /// 完成任务
    /// </summary>
    public virtual void OnCompleted(QuestStateContext context)
    {
    }

    /// <summary>
    /// 任务失败
    /// </summary>
    public virtual void OnFailed(QuestStateContext context)
    {
    }

  }

  /// <summary> 
  /// 任务目标
  /// </summary>
  [Serializable]
  public class QuestGoal
  {
    /// <summary> 任务目标类型 </summary>
    public EQuestGoalType type;
    /// <summary> 匹配Key </summary>
    public string targetKey;
    /// <summary> 对话图 </summary>
    public DialogueGraph dialogueGraph;
    /// <summary> 对话事件Key </summary>
    public string dialogueEventKey = NarrativeEventKeys.DialogueGraphFinishedKey;
    /// <summary> 目标数量 </summary>
    public int count = 1;
    /// <summary> 目标描述 </summary>
    public string description;
  }

  /// <summary>
  /// 任务奖励条目（物品ID + 数量）
  /// 物品名称/图标不在这里：一律查 item_config（与背包格子同源）
  /// </summary>
  [Serializable]
  public class QuestRewardConfig
  {
    /// <summary> 物品ID（item_config.item_id） </summary>
    public int itemId;
    /// <summary> 数量（≤0 视为不发） </summary>
    public int count = 1;
  }

}

