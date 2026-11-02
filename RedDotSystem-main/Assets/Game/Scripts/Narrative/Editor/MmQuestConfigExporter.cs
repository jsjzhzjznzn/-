using System.Collections.Generic;
using System.IO;
using Miemie.DialogSystem.Quest;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Miemie.DialogSystem.Quest.Editor
{
    /// <summary>
    /// 叙事任务配置导出工具
    /// 把项目内全部 QuestData 资产导出为服务端权威引擎的配置 JSON
    ///（competeserves/Server/Config/mm_quest_config.json），服务端启动时幂等种子到 mm_quest_config 表；
    /// 正确流程：改任务 SO → 执行本导出 → 重启服务端。
    /// 对话型目标以"对话图资产名 + eventKey"为服务端匹配键（服务端不引用 SO 资产）
    /// </summary>
    public static class MmQuestConfigExporter
    {
        /// <summary>导出目标（服务端仓库相对项目根的固定路径）</summary>
        private const string ExportPath = "../competeserves/Server/Config/mm_quest_config.json";

        [MenuItem("Tools/Mm叙事任务/导出配置到服务器")]
        public static void ExportAll()
        {
            // 扫描项目内全部 QuestData 资产（类型搜索）
            string[] guids = AssetDatabase.FindAssets("t:QuestData");
            if (guids.Length == 0)
            {
                UnityEngine.Debug.LogWarning("[Mm导出] 项目内没有 QuestData 资产，无事可导出");
                return;
            }

            ExportFile export = new ExportFile();
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                QuestData quest = AssetDatabase.LoadAssetAtPath<QuestData>(assetPath);
                if (quest == null) continue;
                if (quest.QuestId <= 0)
                {
                    UnityEngine.Debug.LogWarning($"[Mm导出] 跳过 {quest.name}：questId 未配置（={quest.QuestId}）");
                    continue;
                }

                ExportQuest row = new ExportQuest
                {
                    QuestId = quest.QuestId,
                    Category = (int)quest.Category,
                    Title = quest.Title,
                    Description = quest.Description,
                    AcceptMode = (int)quest.AcceptMode,
                    TimeLimitSec = quest.TimeLimit,
                    PrereqIds = new List<int>(quest.PrerequisiteIdList),
                };

                foreach (QuestRewardConfig reward in quest.Rewards)
                {
                    if (reward == null || reward.itemId <= 0 || reward.count <= 0) continue;
                    row.Rewards.Add(new ExportReward { ItemId = reward.itemId, Count = reward.count });
                }

                foreach (QuestGoal goal in quest.GetGoals())
                {
                    if (goal == null) continue;
                    row.Goals.Add(new ExportGoal
                    {
                        Type = (int)goal.type,
                        TargetKey = goal.targetKey ?? string.Empty,
                        Count = goal.count,
                        GraphName = goal.dialogueGraph != null ? goal.dialogueGraph.name : string.Empty,
                        EventKey = goal.dialogueEventKey ?? string.Empty,
                        Description = goal.description ?? string.Empty,
                    });
                }

                export.Quests.Add(row);
            }

            string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ExportPath));
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(fullPath, JsonConvert.SerializeObject(export, Formatting.Indented));
            UnityEngine.Debug.Log($"[Mm导出] 已导出 {export.Quests.Count} 条任务配置 → {fullPath}（重启服务端生效）");
        }

        // ──────────────────────────────
        // 导出 JSON 形状（与服务端 NarrativeQuestRepository.LoadAndSeedConfigs 的反序列化结构保持一致，
        // 大小写不敏感由服务端 JsonSerializerOptions.PropertyNameCaseInsensitive 兜底）
        // ──────────────────────────────

        private sealed class ExportFile
        {
            public List<ExportQuest> Quests = new();
        }

        private sealed class ExportQuest
        {
            public int QuestId;
            public int Category;
            public string Title = string.Empty;
            public string Description = string.Empty;
            public int AcceptMode;
            public float TimeLimitSec;
            public List<int> PrereqIds = new();
            public List<ExportGoal> Goals = new();
            public List<ExportReward> Rewards = new();
        }

        private sealed class ExportGoal
        {
            public int Type;
            public string TargetKey = string.Empty;
            public int Count = 1;
            public string GraphName = string.Empty;
            public string EventKey = string.Empty;
            public string Description = string.Empty;
        }

        private sealed class ExportReward
        {
            public int ItemId;
            public int Count;
        }
    }
}
