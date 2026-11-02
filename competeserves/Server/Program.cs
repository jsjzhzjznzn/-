// 命名空间：CompeteServer
// 属于服务端程序的根命名空间
using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer
{
    /// <summary>
    /// 程序入口类
    /// 服务端应用程序的启动点
    /// </summary>
    internal class Program
    {
        /// <summary>
        /// 主方法，程序执行的入口点
        /// 负责数据库初始化检查并引导服务器启动流程
        /// </summary>
        /// <param name="args">参数名：args，类型：string[]，用途：命令行参数数组（当前未使用）</param>
        static async Task Main(string[] args)
        {
            try
            {
                // 表结构由使用者提前建好（见 Migrations\001_InitAccounts.sql），启动时只做连接健康检查
                await MySqlManager.Instance.CheckHealthAsync();
                Console.WriteLine($"数据库连接成功: {MySqlManager.Instance.DisplayEndpoint}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"数据库启动检查失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("Socket 服务未启动。请先执行 Migrations\\001_InitAccounts.sql 建好 compete 库与 accounts 表，并检查 CS_DB_* 环境变量与 MySQL 状态。");
                return;
            }

            // ──────────────────────────────
            // MongoDB 健康检查（聊天记录等文档型数据用）
            // 失败仅告警、不阻止启动：登录/好友走 MySQL，不受 Mongo 影响
            // ──────────────────────────────

            try
            {
                await MongoManager.Instance.CheckHealthAsync();
                Console.WriteLine($"MongoDB 连接成功: {MongoManager.Instance.DisplayEndpoint}");

                // 建立聊天消息的复合索引 { conversationId: 1, sentAt: -1 }（幂等，已存在则无副作用）
                new ChatRepository().EnsureIndexes();
                Console.WriteLine("聊天消息索引就绪");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"MongoDB 启动检查失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("聊天记录等 MongoDB 功能将不可用，服务继续启动。请检查 CS_MONGO_* 环境变量与 MongoDB 状态。");
            }

            // ──────────────────────────────
            // 违禁词词库（聊天文本打码：服务端权威，词库 Config/banned_words.json，改词重启生效）
            // ──────────────────────────────

            try
            {
                BannedWordFilter.Load(Path.Combine("Config", "banned_words.json"));
                Console.WriteLine($"违禁词过滤就绪（{BannedWordFilter.WordCount} 个词）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"违禁词词库加载失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("聊天将不经过违禁词过滤（消息原样落库）。请检查 Config/banned_words.json 格式。");
            }

            // ──────────────────────────────
            // 违禁词二级审核（朴素贝叶斯：词库外的新词 / 变体 / 广告话术）
            // 语料 + 配置在 Config/bayes_filter.json，改语料重启生效；失败仅告警，不影响一级打码
            // ──────────────────────────────

            try
            {
                NaiveBayesFilter.Load(Path.Combine("Config", "bayes_filter.json"));
                Console.WriteLine($"违禁词二级审核就绪（{NaiveBayesFilter.Summary}）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"违禁词二级审核加载失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("二级审核关闭（一级违禁词打码不受影响）。请检查 Config/bayes_filter.json 格式。");
            }

            // ──────────────────────────────
            // 排行榜建表（MySQL 权威，与 Valkey 可用性无关；只有建表失败才影响功能）
            // ──────────────────────────────

            try
            {
                LeaderboardRepository leaderboard = new();
                leaderboard.EnsureTable();   // 幂等建表（与 Migrations\003_AddScores.sql 等价，已有表无副作用）
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"排行榜建表失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("排行榜功能将不可用（不影响登录/好友/聊天/背包）。");
            }

            // ──────────────────────────────
            // 背包建表与物品配置种子（MySQL 权威，无外部缓存依赖）
            // ──────────────────────────────

            try
            {
                BagRepository bag = new();
                bag.EnsureTable();        // 幂等建表（与 Migrations\004_AddBag.sql 等价）
                bag.SeedItemConfigs();    // 幂等种子（INSERT IGNORE，已存在的物品不覆盖，便于手工改库）
                Console.WriteLine("背包就绪（item_config 配置表 + player_items 槽位表）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"背包初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("背包功能将不可用（不影响登录/好友/聊天/排行榜）。");
            }

            // ──────────────────────────────
            // 装备建表、配置种子与历史武器回填（依赖 item_config / player_items，必须在背包之后）
            // ──────────────────────────────

            try
            {
                EquipmentRepository equipment = new();
                equipment.EnsureTable();        // 幂等建表（与 Migrations\018_AddEquipment.sql 等价）
                equipment.SeedConfigs();        // 幂等种子（7 件首批武器，已存在不覆盖）
                equipment.BackfillInstances();  // 历史武器补建实例（只处理 instance_id IS NULL 的行）
                Console.WriteLine("装备系统就绪（equipment_config 配置 + equipment_instance 实例 + player_equipment 槽位）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"装备初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("装备功能将不可用（不影响登录/好友/聊天/背包）。");
            }

            // ──────────────────────────────
            // 任务建表与任务配置种子（MySQL 权威，无外部缓存依赖）
            // ──────────────────────────────

            try
            {
                QuestRepository quest = new();
                quest.EnsureTable();        // 幂等建表（生涯/成就查询仍依赖这些表；旧 25xx 任务已下线，不再写种子）
                new CareerRepository().EnsureTable();   // 生涯数据表（成就进度来源）

                // 独立成就系统：建表 → 种子 → 旧任务成就迁移（category=3）
                // 单独 try/catch：成就初始化失败不能阻断后续叙事任务/邮件等系统启动
                try
                {
                    AchievementRepository achievement = new();
                    achievement.EnsureTable();
                    achievement.SeedConfigs();
                    achievement.MigrateLegacyAchievements();
                    Console.WriteLine("成就系统就绪（achievement_config 定义 + player_achievements 进度/领取）");
                }
                catch (Exception achievementException)
                {
                    Console.Error.WriteLine(
                        $"成就系统初始化失败 [{achievementException.GetType().Name}]: {achievementException.Message}");
                    Console.Error.WriteLine("成就功能将不可用（不影响任务/邮件/背包等其它系统）。");
                }

                NarrativeQuestRepository narrative = new();
                narrative.EnsureTable();     // 叙事任务：进度表补列 + 配置表（与 Migrations\009/010 等价）
                narrative.LoadAndSeedConfigs(Path.Combine("Config", "mm_quest_config.json"));   // 从导出工具的 JSON 幂等种子（改配置=导出+重启）
                Console.WriteLine("任务系统就绪（quest_config 配置表 + quest_reward 奖励表 + player_quests 进度表 + career_scores 生涯表）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"任务初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("任务功能将不可用（不影响登录/好友/聊天/排行榜/背包）。");
            }

            // ──────────────────────────────
            // 邮件建表（MySQL 权威，本期仅系统邮件）
            // ──────────────────────────────

            try
            {
                new MailRepository().EnsureTable();   // 幂等建表（与 Migrations\007_AddMail.sql 等价）
                Console.WriteLine("邮件系统就绪（mails 玩家邮件表）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"邮件初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("邮件功能将不可用（不影响登录/好友/聊天/排行榜/背包/任务）。");
            }

            // ──────────────────────────────
            // 自动登录令牌建表（remember me：库里只存哈希，明文令牌只下发一次）
            // ──────────────────────────────

            try
            {
                new AutoLoginRepository().EnsureTable();   // 幂等建表（与 Migrations\013_AddAutoLogin.sql 等价）
                Console.WriteLine($"自动登录就绪（auto_login_tokens 令牌表，有效期{AutoLoginRepository.TokenTtlSeconds}s 滑动续期）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"自动登录初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("自动登录功能将不可用（不影响密码登录等其余功能）。");
            }

            // ──────────────────────────────
            // 副本建表与掉落规则种子（MySQL 权威：客户端只报击杀，掉什么由规则表裁决）
            // ──────────────────────────────

            try
            {
                DungeonRepository dungeon = new();
                dungeon.EnsureTable();      // 幂等建表（与 Migrations\011_AddDungeon.sql 等价）
                dungeon.SeedDropRules();    // 幂等种子（INSERT IGNORE，已存在不覆盖，便于手工改库调概率）
                Console.WriteLine("副本掉落系统就绪（dungeon_drop_rule 掉落规则表）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"副本掉落初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("副本掉落功能将不可用（不影响登录/好友/聊天/排行榜/背包/任务/邮件）。");
            }

            // ──────────────────────────────
            // Valkey 健康检查 + 实时榜重建（失败仅告警、不阻止启动：榜单查询降级直查 MySQL）
            // ──────────────────────────────
            try
            {
                await ValkeyManager.Instance.CheckHealthAsync();
                Console.WriteLine($"Valkey 连接成功: {ValkeyManager.Instance.DisplayEndpoint}");

                new LeaderboardRepository().RebuildValkey(LeaderboardRepository.DefaultChallengeId);   // 以 MySQL 真相重建实时榜
                Console.WriteLine("排行榜实时层就绪（Valkey ZSET）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Valkey 启动检查失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("排行榜实时层降级（榜单查询降级直查 MySQL）。建议启动 Valkey 以获得实时榜性能。");
            }

            // ──────────────────────────────
            // 活动唤取建表与种子（MySQL 权威：排期/卡池全在表里，换活动零代码改动）
            // ──────────────────────────────

            try
            {
                CompeteServer.Database.ActivityRepository activity = new();
                activity.EnsureTable();        // 幂等建表（与 Migrations\012_AddActivity.sql 等价）
                activity.SeedActivityConfigs();   // 幂等种子（活动A/B 排期 + 签到活动3 + 双卡池 + 唤取媒介物品行）
                Console.WriteLine("活动唤取系统就绪（activity_config 排期表 + banner_config 卡池表 + gacha_records 流水表）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"活动初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("活动唤取功能将不可用（不影响登录/好友/聊天/背包/任务/邮件/副本）。");
            }

            // ──────────────────────────────
            // 签到活动（鸣潮式 7 天累计签到，activity_config 的 type=2 排期活动）
            // ──────────────────────────────

            try
            {
                CompeteServer.Database.SignInRepository signIn = new();
                signIn.EnsureTable();          // 幂等建表（与 Migrations\014_AddSignIn.sql 等价）
                signIn.SeedSignConfigs();      // 幂等种子（活动3 的 7 天奖励）
                Console.WriteLine("签到活动系统就绪（sign_config 奖励表 + player_sign_records 记录表）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"签到活动初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("签到活动功能将不可用（不影响其它系统）。");
            }

            // ──────────────────────────────
            // 场景存档（「随时退出、回来还在原处」：位置/相机/对话节点）
            // ──────────────────────────────

            try
            {
                CompeteServer.Database.SceneSaveRepository sceneSave = new();
                sceneSave.EnsureTable();       // 幂等建表（与 Migrations\015_AddSceneSaves.sql 等价）
                Console.WriteLine("场景存档系统就绪（player_scene_saves 存档表：duihua / fuben）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"场景存档初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("场景存档功能将不可用（进入场景走默认开局，不影响其它系统）。");
            }

            // ──────────────────────────────
            // 新手引导进度（「退出后回来还在同一步」：步骤下标 + 完成标记）
            // ──────────────────────────────

            try
            {
                CompeteServer.Database.GuideProgressRepository guide = new();
                guide.EnsureTable();           // 幂等建表（与 Migrations\019_AddGuideProgress.sql 等价）
                Console.WriteLine("新手引导系统就绪（player_guide_progress 进度表）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"新手引导初始化失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("引导进度将退回本机缓存（不影响其它系统）。");
            }

            // ──────────────────────────────
            // 活动热更内容静态服务（YooAsset 联机模式的资源 CDN，端口 8899）
            // 失败仅告警：文件服务挂了只影响活动包下载/更新，不影响游戏端口 8888 的功能
            // ──────────────────────────────

            try
            {
                CompeteServer.Network.HotContentServer.Start();
                Console.WriteLine($"活动热更内容服务就绪（{CompeteServer.Network.HotContentServer.HostPrefix}:{CompeteServer.Network.HotContentServer.Port}/ → {CompeteServer.Network.HotContentServer.RootDirectory}）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"活动热更内容服务启动失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("活动热更包下载将不可用（不影响登录/好友/聊天/背包/任务/邮件/副本）。");
            }

            // ──────────────────────────────
            // AI Agent（阿福）内部数据 API：给 Python Agent 服务回查玩家真实数据（仅本机回环）
            // ──────────────────────────────

            try
            {
                CompeteServer.Network.InternalAgentApiServer.Start();
                Console.WriteLine($"Agent 内部数据 API 就绪（http://127.0.0.1:{CompeteServer.Network.InternalAgentApiServer.Port}/internal/agent/player/{{id}}/{{profile|bag|quests}} → Python: {CompeteServer.ServerConfig.AgentPythonBaseUrl}）");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Agent 内部数据 API 启动失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("Agent 工具将查不到真实玩家数据（不影响对话转发降级路径）。");
            }

            // ──────────────────────────────
            // AI Agent（阿福）主动提醒服务（悬浮球 3107）：事件触发 → Python /agent/proactive → 推送
            // ──────────────────────────────

            try
            {
                CompeteServer.Manager.AgentReminderService.Start();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"阿福主动提醒服务启动失败 [{exception.GetType().Name}]: {exception.Message}");
                Console.Error.WriteLine("主动提醒将不可用（不影响对话/简报/历史）。");
            }

            // ──────────────────────────────
            // 对战好友邀请：过期巡检（纯内存注册表，无外部依赖，失败不影响主流程）
            // ──────────────────────────────

            try
            {
                CompeteServer.Handler.BattleHandler.StartExpirySweeper();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"对战邀请服务启动失败 [{exception.GetType().Name}]: {exception.Message}");
            }

            // ──────────────────────────────
            // 显示服务器启动横幅
            // ──────────────────────────────

            Console.WriteLine("=================================");
            Console.WriteLine("      Compete Game Server        ");
            Console.WriteLine("=================================");

            // ──────────────────────────────
            // 启动服务器核心组件
            // ──────────────────────────────

            ServerBootstrap.Instance.Start();

            // ──────────────────────────────
            // 保持服务器运行
            // ──────────────────────────────

            Console.WriteLine("Server Running...");

            // 阻塞等待停机信号（Ctrl+C 或进程退出），随后执行优雅关闭
            await RunUntilShutdownAsync();
            Console.WriteLine("收到停止信号，正在优雅关闭服务器…");

            // 尽力关闭 TCP 监听器：Stop 会让 AcceptLoop 抛 ObjectDisposedException 并退出
            try
            {
                SocketServer.Instance.Stop();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"关闭监听器失败 [{exception.GetType().Name}]: {exception.Message}");
            }

            // 关闭全部会话（ClientSession.Close 私有，SessionManager 仅解除引用，连接随进程退出回收）
            try
            {
                SessionManager.Instance.CloseAll();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"关闭会话失败 [{exception.GetType().Name}]: {exception.Message}");
            }

            Console.WriteLine("服务器已停止。");
        }

        /// <summary>
        /// 等待停机信号：接管 Ctrl+C（Console.CancelKeyPress）与进程退出（AppDomain.ProcessExit）
        /// 收到任一信号即取消 token，使下方 await 返回，交由 Main 执行清理
        /// </summary>
        private static async Task RunUntilShutdownAsync()
        {
            using CancellationTokenSource cts = new();
            TaskCompletionSource<bool> stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration _ = cts.Token.Register(() => stopSignal.TrySetResult(true));

            ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
            {
                // 取消默认立即终止行为，保证 Main 有机会跑完优雅关闭逻辑
                eventArgs.Cancel = true;
                cts.Cancel();
            };
            EventHandler onExit = (_, _) => cts.Cancel();

            Console.CancelKeyPress += onCancel;
            AppDomain.CurrentDomain.ProcessExit += onExit;
            try
            {
                await stopSignal.Task;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                AppDomain.CurrentDomain.ProcessExit -= onExit;
            }
        }
    }
}
