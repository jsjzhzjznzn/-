using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 新手引导进度消息处理类（由 MessageHandler 路由转发）
    /// 「退出后回来还在同一步」的服务端账本：进主页拉取（3601/3602），每步覆盖式上报（3603/3604）。
    /// 身份一律取自会话绑定（session.PlayerId）；本模块只存进度，不参与引导内容与表现。
    /// </summary>
    public static class GuideHandler
    {
        /// <summary>
        /// 进度拉取处理方法
        /// 无记录返回 has_record=false（客户端按新号/老号规则决定是否自动开场，不视为失败）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：ReadOnlySpan&lt;byte&gt;，用途：protobuf 序列化的请求消息</param>
        public static void HandlePull(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.GuidePullResponse,
                    new GuidePullResponse { Success = false, Message = "请先登录" });
                return;
            }

            GuidePullRequest request = GuidePullRequest.Parser.ParseFrom(body);
            string guideKey = (request.GuideKey ?? string.Empty).Trim();
            if (!GuideProgressRepository.IsValidKey(guideKey))
            {
                _ = session.Send(MessageID.GuidePullResponse,
                    new GuidePullResponse { Success = false, Message = "引导标识非法" });
                return;
            }

            GuideProgressRepository repository = new();
            GuideProgressRow? row = repository.Get(session.PlayerId, guideKey);

            GuidePullResponse response = new()
            {
                Success = true,
                Message = "OK",
                HasRecord = row != null,
                Step = row?.Step ?? 0,
                Completed = row?.Completed ?? false,
            };

            Console.WriteLine(
                $"引导进度拉取:玩家{session.PlayerId} 引导{guideKey} → {(row == null ? "无记录" : $"step={row.Step} completed={(row.Completed ? 1 : 0)}")}");
            _ = session.Send(MessageID.GuidePullResponse, response);
        }

        /// <summary>
        /// 进度上报处理方法
        /// 覆盖式写入；步骤下标由仓库钳制到 0..MaxStep
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：ReadOnlySpan&lt;byte&gt;，用途：protobuf 序列化的请求消息</param>
        public static void HandlePush(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.GuidePushResponse,
                    new GuidePushResponse { Success = false, Message = "请先登录" });
                return;
            }

            GuidePushRequest request = GuidePushRequest.Parser.ParseFrom(body);
            string guideKey = (request.GuideKey ?? string.Empty).Trim();

            GuideProgressRepository repository = new();
            var result = repository.Upsert(session.PlayerId, guideKey, request.Step, request.Completed);

            GuidePushResponse response = new()
            {
                Success = result.Success,
                Message = result.Message,
                Step = result.Step,
                Completed = result.Completed,
                ServerUpdatedAtMs = result.UpdatedAtMs,
            };

            Console.WriteLine(
                $"引导进度上报:玩家{session.PlayerId} 引导{guideKey} step={request.Step} completed={(request.Completed ? 1 : 0)} → {(result.Success ? "OK" : result.Message)}");
            _ = session.Send(MessageID.GuidePushResponse, response);
        }
    }
}
