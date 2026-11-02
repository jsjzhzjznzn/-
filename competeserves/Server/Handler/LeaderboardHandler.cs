using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Entity;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 排行榜消息处理类（自 MessageHandler 拆出）
    /// 负责挑战成绩上报与排行榜查询的消息处理
    /// 身份一律取自会话绑定（session.PlayerId），客户端无法伪造他人成绩
    /// </summary>
    public static class LeaderboardHandler
    {
        /// <summary>
        /// 成绩上报处理方法
        /// 身份取自会话（客户端无法伪造他人成绩）；按"只保留最高分"入账 MySQL，Valkey 实时榜同步刷新
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的上报请求消息体</param>
        public static void HandleScoreSubmit(ClientSession session, ReadOnlySpan<byte> body)
        {
            ScoreSubmitRequest request = ScoreSubmitRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.ScoreSubmitResponse,
                    new ScoreSubmitResponse { Success = false, Message = "请先登录" });
                return;
            }
            if (!LeaderboardRepository.IsValidChallengeId(request.ChallengeId))
            {
                _ = session.Send(MessageID.ScoreSubmitResponse,
                    new ScoreSubmitResponse { Success = false, Message = "挑战ID非法" });
                return;
            }
            if (request.Score < 0 || request.Score > LeaderboardRepository.MaxScore)
            {
                _ = session.Send(MessageID.ScoreSubmitResponse,
                    new ScoreSubmitResponse { Success = false, Message = "成绩非法" });
                return;
            }

            (bool success, string message, long best, long rank, bool newRecord) =
                new LeaderboardRepository().SubmitScore(session.PlayerId, request.ChallengeId, request.Score);

            _ = session.Send(MessageID.ScoreSubmitResponse, new ScoreSubmitResponse
            {
                Success = success,
                Message = message,
                BestScore = best,
                MyRank = rank,
                NewRecord = newRecord,
            });

            // 挑战成绩 → 任务进度推进 + 生涯数据累计（挑战次数/历史最高分）+ 成就重算
            // 击杀竞速榜的分数是“反转后的耗时”，不能计入旧挑战次数/最高分任务。
            if (success && request.ChallengeId == LeaderboardRepository.DefaultChallengeId)
            {
                QuestHandler.RaiseChallengeScore(session.PlayerId, request.ChallengeId, (int)request.Score);
            }
        }

        /// <summary>
        /// 排行榜查询处理方法
        /// TopN 从 Valkey 实时榜读取（缺失时服务端自动用 MySQL 重建）；条目玩家名在此层从账号库补齐
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的查询请求消息体</param>
        public static void HandleLeaderboard(ClientSession session, ReadOnlySpan<byte> body)
        {
            LeaderboardRequest request = LeaderboardRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.LeaderboardResponse,
                    new LeaderboardResponse { Success = false, Message = "请先登录" });
                return;
            }

            int challengeId = request.ChallengeId > 0 ? request.ChallengeId : LeaderboardRepository.DefaultChallengeId;
            if (!LeaderboardRepository.IsValidChallengeId(challengeId))
            {
                // 未知挑战ID 不进仓储：否则每次请求都会"缓存缺失 → 全表扫 → 建不出键"地空转一遍
                _ = session.Send(MessageID.LeaderboardResponse,
                    new LeaderboardResponse { Success = false, Message = "挑战ID非法" });
                return;
            }

            (bool success, string message, List<(long Rank, int PlayerId, long Score)> rows, long myRank, long myScore) =
                new LeaderboardRepository().GetLeaderboard(challengeId, request.TopN, session.PlayerId);

            LeaderboardResponse response = new()
            {
                Success = success,
                Message = message,
                ChallengeId = challengeId,
                MyRank = myRank,
                MyScore = myScore,
            };

            if (success)
            {
                // 一次 IN 查询批量取回全部玩家名（Top100 = 1 次往返，替代逐行 QueryPlayer 的 N+1）
                Dictionary<int, string> names = new AccountRepository().GetNamesByIds(
                    rows.Select(row => row.PlayerId));
                foreach ((long rank, int playerId, long score) in rows)
                {
                    response.Entries.Add(new LeaderboardEntry
                    {
                        Rank = rank,
                        PlayerId = playerId,
                        Name = names.TryGetValue(playerId, out string? name) && !string.IsNullOrEmpty(name)
                            ? name
                            : $"玩家{playerId}",
                        Score = score,
                    });
                }
            }

            Console.WriteLine($"排行榜返回:挑战{challengeId} {response.Entries.Count}条 | 我的排名={myRank} 我的分数={myScore}");
            _ = session.Send(MessageID.LeaderboardResponse, response);
        }
    }
}
