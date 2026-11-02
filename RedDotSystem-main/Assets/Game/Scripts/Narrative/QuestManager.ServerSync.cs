using System.Collections.Generic;

namespace Miemie.DialogSystem.Quest
{
    /// <summary>
    /// QuestManager 的服务器同步扩展（partial）
    /// 服务端权威模式下 QuestManager 退化为"服务端状态的本地镜像"：本文件提供快照应用与防串号重建入口，
    /// 网络收发由同物体上的 MmQuestServerSync 桥驱动（见 Assets/Game/Scripts/Narrative/MmQuestServerSync.cs）；
    /// 本地逻辑（ListenGameEvents/Accept/TrySubmit 等）保留给离线 Demo 场景（DemoScene 无桥组件）
    /// </summary>
    public partial class QuestManager
    {

        /// <summary>
        /// 应用服务器拉回的状态快照（与框架 LoadQuests 同语义，服务端权威模式下镜像专用）：
        /// 清空活跃列表与限时计时 → 逐条回填状态/进度 → 执行中任务回列表并重启限时 → 刷新可接取。
        /// 注意：系统派发任务的接取不再由本地做（本地接取不入库、不权威）——
        /// 空快照（新玩家）时由 MmQuestServerSync 以 AcceptServer 语义逐个走服务端接取
        /// </summary>
        /// <param name="saveFile">参数名：saveFile，类型：QuestSaveFile，用途：服务器返回的全量状态快照</param>
        public void ApplyServerSave(QuestSaveFile saveFile)
        {
            activeQuestList.Clear();
            StopAllTimeLimit();

            if (saveFile?.questDataList != null)
            {
                foreach (var saveData in saveFile.questDataList)
                {
                    if (saveData == null) continue;
                    if (!stateDict.TryGetValue(saveData.questId, out var runtime)) continue;

                    runtime.ApplySaveData(saveData);

                    if (runtime.eQuestState != EQuestState.执行中) continue;

                    activeQuestList.Add(runtime);
                    if (runtime.questData.HasTimeLimit)
                        StartTimeLimit(runtime.questData.QuestId, saveData.remainSeconds);
                }
            }

            RefreshAvailable();
        }

        /// <summary>
        /// 换账号/重登时重建干净的运行态（防串号：先回到配置初始状态，再由服务器拉取回填）
        /// </summary>
        public void ResetForAccountSwitch()
        {
            StopAllTimeLimit();
            BuildRuntimeState();
            RefreshAvailable();
        }
    }
}
