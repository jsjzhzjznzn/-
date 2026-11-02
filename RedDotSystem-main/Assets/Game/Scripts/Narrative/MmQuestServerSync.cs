using System;
using System.Collections.Generic;
using Common.Network;
using MiMieEventBus;
using UnityEngine;

namespace Miemie.DialogSystem.Quest
{
    /// <summary>
    /// 叙事任务服务器桥（挂在 Mm 框架 QuestManager 同一物体上，存在即启用服务端权威模式）
    /// 鸣潮式分工：客户端只报"发生了什么"（玩法事件 2705 上报）与发起接取/提交（2706/2708），
    /// 全部任务规则由服务端裁决；客户端 QuestManager 退化为服务端状态的"镜像"（供 UI/红点读取）。
    /// 数据流：
    ///   登录成功 → 2701 拉取全量快照 → ApplyServerSave 整表镜像；
    ///   总线玩法事件 → 2705 转发（本地不推进）→ 服务端匹配目标 → 2710 推全量快照 → 再次镜像；
    ///   接取/提交请求 → 回执自带全量快照 → 镜像。
    /// 权威在服务端：对本地镜像的任何篡改（含离线 Demo 的本地接口）都会在下一次快照到达时被覆盖。
    /// </summary>
    [RequireComponent(typeof(QuestManager))]
    [DisallowMultipleComponent]
    public class MmQuestServerSync : MonoBehaviour
    {
        /// <summary>事件转发订阅令牌（击杀/收集/到达/对话）</summary>
        private IDisposable enemyKilledSub;
        private IDisposable itemCollectedSub;
        private IDisposable zoneEnteredSub;
        private IDisposable dialogueTriggeredSub;

        /// <summary>镜像已应用（快照拉取 / 接取 / 提交 / 放弃 / 推送；UI 据此重渲染）</summary>
        public event Action SnapshotApplied;

        /// <summary>服务端提示推送（提交成功后的文案，交给提示条展示）</summary>
        public event Action<NarrativeQuestToastNotify> ToastReceived;

        private void OnEnable()
        {
            var net = ClientNetworkManager.Instance;
            if (net == null)
            {
                // Bootstrap(BeforeSceneLoad) 通常先于场景加载, 但在个别进 Play 时序下(如关闭 Domain Reload)Instance 尚未就绪:
                // 延迟重试避免 NRE 中断组件初始化(此前该 NRE 会触发 Console 的 Error Pause 挂起整个 Play 会话)
                StartCoroutine(SubscribeWhenNetReady());
                return;
            }

            SubscribeNetwork(net);
        }

        private System.Collections.IEnumerator SubscribeWhenNetReady()
        {
            for (var i = 0; i < 60 && ClientNetworkManager.Instance == null; i++)
                yield return null;

            var net = ClientNetworkManager.Instance;
            if (net == null)
            {
                Debug.LogWarning("[MmQuestServerSync] ClientNetworkManager 未就绪, 叙事任务服务器同步本次不生效");
                yield break;
            }

            SubscribeNetwork(net);
        }

        private void SubscribeNetwork(ClientNetworkManager net)
        {
            net.LoginResponseReceived += OnLoginResponse;
            net.NarrativeQuestSyncResponseReceived += OnSnapshotResponse;
            net.NarrativeQuestAcceptResponseReceived += OnAcceptResponse;
            net.NarrativeQuestSubmitResponseReceived += OnSubmitResponse;
            net.NarrativeQuestStateNotifyReceived += OnStateNotify;
            net.NarrativeQuestAbandonResponseReceived += OnAbandonResponse;
            net.NarrativeQuestToastNotifyReceived += OnToastNotify;
            net.ConnectionBroken += OnConnectionBroken;

            var bus = NarrativeEventBus.NarrytiveBus;
            enemyKilledSub = bus.Subscribe(NarrativeEventKeys.EnemyKilled, OnEnemyKilled);
            itemCollectedSub = bus.Subscribe(NarrativeEventKeys.ItemCollected, OnItemCollected);
            zoneEnteredSub = bus.Subscribe(NarrativeEventKeys.ZoneEntered, OnZoneEntered);
            dialogueTriggeredSub = bus.Subscribe(NarrativeEventKeys.DialogueTriggered, OnDialogueTriggered);

            // zhuye 流程：登录发生在 LOGIN 场景（本组件尚未激活），进场景后要补一次拉取
            if (net.MyPlayerId > 0)
                RequestSync();
        }

        private void OnDisable()
        {
            var net = ClientNetworkManager.Instance;
            if (net != null)
            {
                net.LoginResponseReceived -= OnLoginResponse;
                net.NarrativeQuestSyncResponseReceived -= OnSnapshotResponse;
                net.NarrativeQuestAcceptResponseReceived -= OnAcceptResponse;
                net.NarrativeQuestSubmitResponseReceived -= OnSubmitResponse;
                net.NarrativeQuestStateNotifyReceived -= OnStateNotify;
                net.NarrativeQuestAbandonResponseReceived -= OnAbandonResponse;
                net.NarrativeQuestToastNotifyReceived -= OnToastNotify;
                net.ConnectionBroken -= OnConnectionBroken;
            }

            enemyKilledSub?.Dispose();
            itemCollectedSub?.Dispose();
            zoneEnteredSub?.Dispose();
            dialogueTriggeredSub?.Dispose();
            enemyKilledSub = null;
            itemCollectedSub = null;
            zoneEnteredSub = null;
            dialogueTriggeredSub = null;
        }

        // ──────────────────────────────
        // 对外 API（UI / E2E 用；服务端权威下接取与提交都必须走这里）
        // ──────────────────────────────

        /// <summary>向服务端发起接取任务（本地不推进，回执快照后镜像更新）</summary>
        public void AcceptServer(int questId)
        {
            var net = ClientNetworkManager.Instance;
            if (net == null || !net.IsConnected || net.MyPlayerId <= 0) return;
            _ = net.SendNarrativeQuestAccept(questId);
        }

        /// <summary>向服务端发起提交任务（服务端校验达标，回执快照后镜像更新）</summary>
        public void SubmitServer(int questId)
        {
            var net = ClientNetworkManager.Instance;
            if (net == null || !net.IsConnected || net.MyPlayerId <= 0) return;
            _ = net.SendNarrativeQuestSubmit(questId);
        }

        /// <summary>向服务端发起放弃任务（状态回到未激活、进度清零，回执快照后镜像更新）</summary>
        public void AbandonServer(int questId)
        {
            var net = ClientNetworkManager.Instance;
            if (net == null || !net.IsConnected || net.MyPlayerId <= 0) return;
            _ = net.SendNarrativeQuestAbandon(questId);
        }

        // ──────────────────────────────
        // 登录 / 拉取 / 镜像
        // ──────────────────────────────

        /// <summary>登录成功：重建干净镜像（防串号）后拉取服务端快照</summary>
        private void OnLoginResponse(LoginResponse response)
        {
            if (response == null || !response.Success) return;

            if (QuestManager.Instance != null)
                QuestManager.Instance.ResetForAccountSwitch();

            RequestSync();
        }

        /// <summary>发送拉取请求（2701）；连接可用才发（UI 打开面板时也可主动触发）</summary>
        public void RequestSync()
        {
            var net = ClientNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            _ = net.SendNarrativeQuestSyncRequest();
        }

        /// <summary>拉取回执（2702）：整表镜像服务端状态；空快照（新玩家）时系统派发任务走服务端接取</summary>
        private void OnSnapshotResponse(NarrativeQuestSyncResponse response)
        {
            if (response == null || !response.Success)
            {
                Debug.LogWarning($"[MmQuestSync] 拉取失败: {response?.Message ?? "空响应"}");
                return;
            }

            ApplySnapshot(response.Quests, "拉取");

            // 服务端无存档（新玩家）：系统派发型任务由桥逐个向服务端接取
            //（本地 AcceptSystemQuests 不入库——服务端权威下本地接取只是无意义的镜像污染）
            if (response.Quests.Count == 0 && QuestManager.Instance != null)
            {
                foreach (var quest in QuestManager.Instance.RegisteredQuests)
                {
                    if (quest == null) continue;
                    if (quest.AcceptMode != EQuestAcceptMode.系统派发) continue;
                    if (QuestManager.Instance.GetState(quest.QuestId) != EQuestState.可用) continue;
                    AcceptServer(quest.QuestId);
                }
            }
        }

        /// <summary>接取回执（2707）：整表镜像（无论成败都应用快照——失败时快照对齐服务端真相）</summary>
        private void OnAcceptResponse(NarrativeQuestAcceptResponse response)
        {
            if (response != null && !response.Success)
                Debug.LogWarning($"[MmQuestSync] 接取被服务端拒绝: {response.Message}");
            ApplySnapshot(response?.Quests, "接取回执");
        }

        /// <summary>提交回执（2709）：整表镜像（无论成败都应用快照——失败时快照对齐服务端真相）</summary>
        private void OnSubmitResponse(NarrativeQuestSubmitResponse response)
        {
            if (response != null && !response.Success)
                Debug.LogWarning($"[MmQuestSync] 提交被服务端拒绝: {response.Message}");
            ApplySnapshot(response?.Quests, "提交回执");
        }

        /// <summary>状态推送（2710）：服务端事件推进/限时到期后的权威快照，整表镜像覆盖本地</summary>
        private void OnStateNotify(NarrativeQuestStateNotify notify)
        {
            ApplySnapshot(notify?.Quests, "推送");
        }

        /// <summary>放弃回执（2712）：整表镜像（失败时快照对齐服务端真相）</summary>
        private void OnAbandonResponse(NarrativeQuestAbandonResponse response)
        {
            if (response != null && !response.Success)
                Debug.LogWarning($"[MmQuestSync] 放弃被服务端拒绝: {response.Message}");
            ApplySnapshot(response?.Quests, "放弃回执");
        }

        /// <summary>提示推送（2713）：提交成功后的文案，交给提示条（不影响镜像）</summary>
        private void OnToastNotify(NarrativeQuestToastNotify notify)
        {
            if (notify == null) return;
            ToastReceived?.Invoke(notify);
        }

        /// <summary>断线：镜像保留在内存，重连+重新登录后走拉取流程覆盖</summary>
        private void OnConnectionBroken()
        {
        }

        // ──────────────────────────────
        // 玩法事件转发（客户端只报事件，不推进任务）
        // ──────────────────────────────

        private void OnEnemyKilled(string enemyKey, int count) => ReportEvent(1, enemyKey, count);
        private void OnItemCollected(string itemKey, int count) => ReportEvent(2, itemKey, count);
        private void OnZoneEntered(string zoneKey) => ReportEvent(3, zoneKey, 1);

        private void OnDialogueTriggered(Miemie.DialogSystem.DialogueGraph graph, string eventKey)
        {
            // 对话目标以"对话图资产名 + eventKey"为匹配键（服务端不引用 SO 资产）
            string graphName = graph != null ? graph.name : string.Empty;
            ReportEvent(4, string.Empty, 1, graphName, eventKey);
        }

        /// <summary>转发玩法事件到服务端（2705，无回执；推进结果由 2710 推送确认）</summary>
        private void ReportEvent(int eventType, string targetKey, int count, string graphName = "", string eventKey = "")
        {
            var net = ClientNetworkManager.Instance;
            if (net == null || !net.IsConnected || net.MyPlayerId <= 0) return;   // 离线（DemoScene）不转发
            _ = net.SendNarrativeQuestEventReport(eventType, targetKey, count, graphName, eventKey);
        }

        // ──────────────────────────────
        // 快照应用（所有回执/推送共用一条镜像路径）
        // ──────────────────────────────

        /// <summary>把服务端全量快照整表应用到本地镜像（复用 QuestManager.ServerSync partial 的 ApplyServerSave）</summary>
        private void ApplySnapshot(IEnumerable<NarrativeQuestStateEntry> entries, string source)
        {
            if (entries == null || QuestManager.Instance == null) return;

            QuestSaveFile saveFile = new();
            foreach (var entry in entries)
            {
                saveFile.questDataList.Add(new QuestSaveData
                {
                    questId = entry.QuestId,
                    eQuestState = (EQuestState)entry.State,
                    progressList = new List<int>(entry.ProgressList),
                    acceptedAt = entry.AcceptedAt,
                    remainSeconds = entry.RemainSeconds,
                });
            }

            QuestManager.Instance.ApplyServerSave(saveFile);
            Debug.Log($"[MmQuestSync] 已镜像服务端任务快照({source}): {saveFile.questDataList.Count} 条");

            SnapshotApplied?.Invoke();
        }
    }
}
