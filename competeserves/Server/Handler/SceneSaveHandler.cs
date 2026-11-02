using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 场景存档消息处理类（自 MessageHandler 路由转发）
    /// 「随时退出、回来还在原处」的服务端账本：进入场景拉取（3301/3302），
    /// 事件点覆盖式上报（3303/3304）。身份一律取自会话绑定（session.PlayerId）。
    /// 位置/相机只做范围护栏，对话节点只做存储；玩法裁决仍由各自系统负责
    /// （对话选项推进叙事任务走 2705，本模块不参与）。
    /// </summary>
    public static class SceneSaveHandler
    {
        // ──────────────────────────────
        // 消息处理
        // ──────────────────────────────

        /// <summary>
        /// 存档拉取处理方法
        /// 进入场景时调用；无档返回 has_save=false（客户端走场景默认开局，不视为失败）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：ReadOnlySpan&lt;byte&gt;，用途：protobuf 序列化的请求消息体</param>
        public static void HandlePull(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.SaveStatePullResponse,
                    new SaveStatePullResponse { Success = false, Message = "请先登录" });
                return;
            }

            SaveStatePullRequest request = SaveStatePullRequest.Parser.ParseFrom(body);
            string sceneName = request.SceneName.Trim();
            if (!SceneSaveRepository.IsAllowedScene(sceneName))
            {
                _ = session.Send(MessageID.SaveStatePullResponse,
                    new SaveStatePullResponse { Success = false, Message = "不支持的场景" });
                return;
            }

            SceneSaveRepository repository = new();
            SceneSaveRow? row = repository.Get(session.PlayerId, sceneName);

            SaveStatePullResponse response = new()
            {
                Success = true,
                Message = "OK",
                HasSave = row != null,
            };
            if (row != null) response.Snapshot = ToSnapshot(row);

            Console.WriteLine($"场景存档拉取:玩家{session.PlayerId} 场景{sceneName} → {(row != null ? $"有档(对话={(row.DialogueActive ? 1 : 0)})" : "无档")}");
            _ = session.Send(MessageID.SaveStatePullResponse, response);
        }

        /// <summary>
        /// 存档上报处理方法
        /// 覆盖式写入；JSON 附件非法时丢弃为 null（位置等核心字段仍然保存）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：ReadOnlySpan&lt;byte&gt;，用途：protobuf 序列化的请求消息体</param>
        public static void HandlePush(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.SaveStatePushResponse,
                    new SaveStatePushResponse { Success = false, Message = "请先登录" });
                return;
            }

            SaveStatePushRequest request = SaveStatePushRequest.Parser.ParseFrom(body);
            SceneSaveSnapshot snapshot = request.Snapshot ?? new SceneSaveSnapshot();

            // JSON 附件：超长直接拒绝（可见信号），短但非法的内容才降级丢弃（位置等核心字段仍保存）
            if (snapshot.DialogueVarsJson.Length > SceneSaveRepository.MaxJsonLength)
            {
                SendPushResult(session, false, "对话变量超长", 0);
                return;
            }
            if (snapshot.DialogueFiredJson.Length > SceneSaveRepository.MaxJsonLength)
            {
                SendPushResult(session, false, "对话事件列表超长", 0);
                return;
            }

            SceneSaveRow row = new()
            {
                PlayerId = session.PlayerId,
                SceneName = snapshot.SceneName.Trim(),
                PosX = snapshot.PosX,
                PosY = snapshot.PosY,
                PosZ = snapshot.PosZ,
                RotY = snapshot.RotY,
                CamYaw = snapshot.CamYaw,
                CamPitch = snapshot.CamPitch,
                CamDistance = snapshot.CamDistance,
                DialogueActive = snapshot.DialogueActive,
                DialogueGraph = snapshot.DialogueGraph,
                DialogueNodeId = snapshot.DialogueNodeId,
                DialogueVarsJson = SceneSaveRepository.SanitizeJson(snapshot.DialogueVarsJson),
                DialogueFiredJson = SceneSaveRepository.SanitizeJson(snapshot.DialogueFiredJson),
                SaveVersion = SceneSaveRepository.CurrentSaveVersion,
            };

            SceneSaveRepository repository = new();
            (bool success, string message, long updatedAtMs) = repository.Upsert(row);

            if (!success)
            {
                Console.WriteLine($"场景存档写入失败:玩家{session.PlayerId} 场景{row.SceneName} → {message}");
            }

            _ = session.Send(MessageID.SaveStatePushResponse, new SaveStatePushResponse
            {
                Success = success,
                Message = message,
                ServerUpdatedAtMs = updatedAtMs,
            });
        }

        // ──────────────────────────────
        // 工具
        // ──────────────────────────────

        /// <summary>统一回执方法（成功/失败都走这里，避免每处重复构造）</summary>
        private static void SendPushResult(ClientSession session, bool success, string message, long updatedAtMs)
        {
            _ = session.Send(MessageID.SaveStatePushResponse, new SaveStatePushResponse
            {
                Success = success,
                Message = message,
                ServerUpdatedAtMs = updatedAtMs,
            });
        }

        /// <summary>
        /// 仓储行 → 协议快照（客户端据此重建位置/相机/对话节点）
        /// </summary>
        private static SceneSaveSnapshot ToSnapshot(SceneSaveRow row)
        {
            return new SceneSaveSnapshot
            {
                SceneName = row.SceneName,
                PosX = (float)row.PosX,
                PosY = (float)row.PosY,
                PosZ = (float)row.PosZ,
                RotY = (float)row.RotY,
                CamYaw = (float)row.CamYaw,
                CamPitch = (float)row.CamPitch,
                CamDistance = (float)row.CamDistance,
                DialogueActive = row.DialogueActive,
                DialogueGraph = row.DialogueGraph,
                DialogueNodeId = row.DialogueNodeId,
                DialogueVarsJson = row.DialogueVarsJson ?? string.Empty,
                DialogueFiredJson = row.DialogueFiredJson ?? string.Empty,
                UpdatedAtMs = row.UpdatedAtMs,
            };
        }
    }
}
