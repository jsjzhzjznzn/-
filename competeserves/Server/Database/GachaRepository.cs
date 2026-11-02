// 命名空间：CompeteServer.Database
// 唤取仓库：卡池配置（banner_config，10 秒 TTL 静态缓存）+ 分层随机 + 双保底（5★计数 / 大保底 / 十连4★）+ 幂等 + 流水
// 权威口径：单事务 + 玩家主行锁内完成 幂等命中 → 校验媒介 → roll → 扣媒介 → 入包（满包转邮件）→ 保底状态落库 → 写流水/幂等快照，一次提交
//
// 设计要点（对照“抽卡系统四原则”）：
//   1. 随机分两步：先按 star_rates_json 定星级，再在星级内按物品权重 + up_rate 定物品（旧池无星级配置时回退整池权重）
//   2. 保底分两件事：pity5_count（距上次 5★ 的抽数）+ guaranteed_up（下一次 5★ 是否必 UP）
//   3. 十连逐抽结算：第 10 抽 roll 之前判定“前 9 抽是否已有 4★+”，不再事后替换
//   4. 幂等：同一 request_id 只结算一次，重试直接重放首次快照（不扣媒介、不重 roll）
using System.Text.Json;
using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 唤取数据仓库类
    /// 用途：武器唤取的服务端权威裁决（roll 概率/保底/入包/流水全部在服务器，客户端只展示结果）
    /// </summary>
    public class GachaRepository
    {
        /// <summary>卡池物品条目（pool_json 反序列化）</summary>
        public sealed record PoolEntry(int ItemId, int Star, int Weight, int IsUp);

        /// <summary>星级权重（star_rates_json 反序列化）</summary>
        public sealed record StarRate(int Star, int Weight);

        /// <summary>
        /// 卡池配置（静态数据，读路径走 TTL 缓存）
        /// PityGroup：0=本池独立；&gt;0=同组共享保底
        /// StarRates：空=兼容旧的整池物品权重 roll
        /// </summary>
        public sealed record BannerConfig(
            int BannerId, string Title, int CostItemId, int CostSingle, int CostTen,
            int Pity5Threshold, List<PoolEntry> Pool,
            int PityGroup, List<StarRate> StarRates, int UpRate5, int UpRate4);

        /// <summary>单次唤取的结果条目（协议 GachaResultItem 的数据源）</summary>
        /// <param name="Star">本次 roll 出的卡池星级（与物品稀有度 Rarity 分开；成就统计用）</param>
        public sealed record DrawResultItem(
            int ItemId, string Name, int Rarity, string IconKey, bool IsUp, bool IsPity, bool IsNew, int PityKind,
            int Star);

        /// <summary>保底状态快照（gacha_pity 的读模型；缺失时从流水惰性反推）</summary>
        public sealed record PitySnapshot(int Pity5Count, bool GuaranteedUp, int TotalDraws);

        /// <summary>唤取流水行（详单 2906 数据源）</summary>
        public sealed record HistoryRow(int BannerId, int ItemId, bool IsPity, int PityKind, long CreatedAtMs);

        /// <summary>一次唤取的完整结算结果（Handler 直接映射协议）</summary>
        public sealed record DrawOutcome(
            bool Success, string Message,
            List<DrawResultItem> Results,
            int Pity5Count, int Pity5Threshold, int TotalDraws, bool GuaranteedUp, bool Replayed,
            List<(int SlotIndex, int ItemId, int Count, long InstanceId)> Slots,
            List<(int ItemId, string Name, int ItemType, int Rarity, string IconKey, string Description, int MaxStack)> Configs)
        {
            /// <summary>本次实际入包的物品（重放路径为空；成就/任务统计只对首次结算生效）</summary>
            public List<(int ItemId, int Count)> GrantedItems { get; } = new();
        }

        // ──────────────────────────────
        // 卡池配置缓存（原子引用发布 + 不可变快照，空结果不缓存——与 Dungeon/Quest 同口径）
        // ──────────────────────────────

        private static Dictionary<int, BannerConfig>? bannerCache;
        private static long bannerCacheTick;
        private const long BannerCacheTtlMs = 10_000;

        /// <summary>取卡池配置（命中缓存直接返回；查不到返回 null）。</summary>
        public static BannerConfig? GetBanner(int bannerId)
        {
            Dictionary<int, BannerConfig>? cache = bannerCache;
            long tick = Interlocked.Read(ref bannerCacheTick);
            if (cache == null || tick <= 0 || Environment.TickCount64 - tick >= BannerCacheTtlMs)
            {
                cache = LoadBanners();
                if (cache.Count > 0)
                {
                    bannerCache = cache;
                    Interlocked.Exchange(ref bannerCacheTick, Environment.TickCount64);
                }
            }
            return cache.GetValueOrDefault(bannerId);
        }

        /// <summary>取全部卡池配置（Agent 简报/get_gacha_status 工具用；与 GetBanner 同缓存同口径）。</summary>
        public static List<BannerConfig> GetBanners()
        {
            _ = GetBanner(int.MinValue);   // 借一次查询触发缓存装载（任何 id 都行，装载读的是全表）
            return bannerCache?.Values.OrderBy(b => b.BannerId).ToList() ?? new List<BannerConfig>();
        }

        /// <summary>卡池缓存失效（种子写入/手工改库后调用）。</summary>
        public static void InvalidateBannerCache()
        {
            bannerCache = null;
            Interlocked.Exchange(ref bannerCacheTick, 0);
        }

        /// <summary>保底分组键：0 视为本池独立（用 banner_id 作键）。</summary>
        private static int PityGroupOf(BannerConfig banner) => banner.PityGroup > 0 ? banner.PityGroup : banner.BannerId;

        /// <summary>同保底组的全部卡池（用于从流水反推共享保底 / 计算组内累计）。</summary>
        private static List<BannerConfig> BannersInGroup(BannerConfig banner)
        {
            int group = PityGroupOf(banner);
            List<BannerConfig> list = GetBanners().Where(b => PityGroupOf(b) == group).ToList();
            if (list.Count == 0) list.Add(banner);
            return list;
        }

        /// <summary>读全表卡池配置并解析 pool_json / star_rates_json（解析失败的池跳过该行，不让单行脏数据拖垮整个读路径）。</summary>
        private static Dictionary<int, BannerConfig> LoadBanners()
        {
            var result = new Dictionary<int, BannerConfig>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    SELECT `banner_id`, `title`, `cost_item_id`, `cost_single`, `cost_ten`, `pity5_threshold`, `pool_json`,
                           COALESCE(`pity_group`, 0), COALESCE(`star_rates_json`, ''), COALESCE(`up_rate_5`, 50), COALESCE(`up_rate_4`, 0)
                    FROM `banner_config`
                    """,
                    connection);
                using MySqlDataReader reader = command.ExecuteReader();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                while (reader.Read())
                {
                    try
                    {
                        // 注意：pool_json 的字段名是下划线风格（item_id/is_up），
                        // 大小写不敏感匹配不等于忽略下划线——必须显式映射，否则 ItemId 全解析成 0
                        List<PoolEntryDto>? dtos = JsonSerializer.Deserialize<List<PoolEntryDto>>(reader.GetString(6), options);
                        if (dtos == null || dtos.Count == 0) continue;
                        List<PoolEntry> pool = dtos
                            .Select(dto => new PoolEntry(dto.ItemId, dto.Star, dto.Weight, dto.IsUp))
                            .ToList();

                        List<StarRate> starRates = new();
                        string starJson = reader.GetString(8);
                        if (!string.IsNullOrWhiteSpace(starJson))
                        {
                            List<StarRateDto>? rates = JsonSerializer.Deserialize<List<StarRateDto>>(starJson, options);
                            if (rates != null)
                            {
                                starRates = rates
                                    .Where(r => r.Weight > 0)
                                    .Select(r => new StarRate(r.Star, r.Weight))
                                    .ToList();
                            }
                        }

                        result[reader.GetInt32(0)] = new BannerConfig(
                            reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2),
                            reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), pool,
                            reader.GetInt32(7), starRates, reader.GetInt32(9), reader.GetInt32(10));
                    }
                    catch (JsonException exception)
                    {
                        Console.WriteLine($"卡池 {reader.GetInt32(0)} 的 pool_json/star_rates_json 解析失败: {exception.Message}");
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"卡池配置读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return result;
        }

        /// <summary>pool_json 的反序列化 DTO（下划线字段名显式映射）。</summary>
        private sealed class PoolEntryDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("item_id")]
            public int ItemId { get; set; }
            public int Star { get; set; }
            public int Weight { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("is_up")]
            public int IsUp { get; set; }
        }

        /// <summary>star_rates_json 的反序列化 DTO。</summary>
        private sealed class StarRateDto
        {
            public int Star { get; set; }
            public int Weight { get; set; }
        }

        // ──────────────────────────────
        // 保底状态与流水
        // ──────────────────────────────

        /// <summary>
        /// 查保底状态（兼容旧签名：Total=该保底组累计抽数，Pity5Count=距上次 5★ 的抽数）
        /// </summary>
        public (int Total, int Pity5Count) GetPityState(int playerId, int bannerId)
        {
            PitySnapshot snapshot = GetPitySnapshot(playerId, bannerId);
            return (snapshot.TotalDraws, snapshot.Pity5Count);
        }

        /// <summary>
        /// 查保底状态快照（详单/首屏用；读不到 gacha_pity 行时从流水惰性反推，不写库）
        /// </summary>
        public PitySnapshot GetPitySnapshot(int playerId, int bannerId)
        {
            BannerConfig? banner = GetBanner(bannerId);
            if (banner == null) return new PitySnapshot(0, false, 0);

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            return LoadPityState(connection, null, playerId, banner);
        }

        /// <summary>
        /// 读保底状态（连接/事务由调用方决定）。
        /// 有 gacha_pity 行 → 直接读（事务内加 FOR UPDATE）；没有 → 从流水反推（老玩家迁移路径，不写库）。
        /// </summary>
        private static PitySnapshot LoadPityState(
            MySqlConnection connection, MySqlTransaction? transaction, int playerId, BannerConfig banner)
        {
            int group = PityGroupOf(banner);
            string sql = transaction != null
                ? "SELECT `pity5_count`, `guaranteed_up`, `total_draws` FROM `gacha_pity` WHERE `player_id` = @PlayerId AND `pity_group` = @Group FOR UPDATE"
                : "SELECT `pity5_count`, `guaranteed_up`, `total_draws` FROM `gacha_pity` WHERE `player_id` = @PlayerId AND `pity_group` = @Group";
            using (MySqlCommand command = new(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@Group", group);
                using MySqlDataReader reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return new PitySnapshot(reader.GetInt32(0), reader.GetInt32(1) != 0, reader.GetInt32(2));
                }
            }

            return ComputePityFromRecords(connection, transaction, playerId, banner);
        }

        /// <summary>
        /// 从流水反推保底（只在 gacha_pity 缺失时调用）：
        ///   pity5_count = 组内最后一条 5★ 之后的记录数；
        ///   guaranteed_up = 最后一条 5★ 是否为非 UP（歪过 → 下一次必 UP）。
        /// 只读、不写库；首次抽卡时会把结果 UPSERT 进 gacha_pity。
        /// </summary>
        private static PitySnapshot ComputePityFromRecords(
            MySqlConnection connection, MySqlTransaction? transaction, int playerId, BannerConfig banner)
        {
            List<BannerConfig> groupBanners = BannersInGroup(banner);
            Dictionary<int, BannerConfig> byId = groupBanners.ToDictionary(b => b.BannerId);
            string inList = string.Join(",", groupBanners.Select(b => b.BannerId));

            int total = 0;
            int pity = 0;
            bool guaranteed = false;
            using (MySqlCommand command = new(
                $"""
                SELECT `banner_id`, `item_id`, `star`
                FROM `gacha_records`
                WHERE `player_id` = @PlayerId AND `banner_id` IN ({inList})
                ORDER BY `created_at_ms`, `banner_id`, `draw_seq`
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    total++;
                    int bannerId = reader.GetInt32(0);
                    int itemId = reader.GetInt32(1);
                    int star = reader.GetInt32(2);
                    if (star != 5)
                    {
                        pity++;
                        continue;
                    }

                    pity = 0;
                    BannerConfig? recordBanner = byId.GetValueOrDefault(bannerId);
                    bool hasUp = recordBanner != null && recordBanner.Pool.Any(p => p.IsUp != 0);
                    PoolEntry? entry = recordBanner?.Pool.FirstOrDefault(p => p.ItemId == itemId);
                    guaranteed = hasUp && (entry == null || entry.IsUp == 0);
                }
            }
            return new PitySnapshot(pity, guaranteed, total);
        }

        /// <summary>
        /// 查唤取流水（详单协议 2906 数据源；时间倒序最近优先）
        /// </summary>
        public List<HistoryRow> GetHistory(int playerId, int bannerId, int limit = 200)
        {
            var result = new List<HistoryRow>();
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                $"""
                SELECT `banner_id`, `item_id`, `is_pity`, `created_at_ms`, COALESCE(`pity_kind`, 0)
                FROM `gacha_records`
                WHERE `player_id` = @PlayerId AND (`banner_id` = @BannerId OR @BannerId = 0)
                ORDER BY `created_at_ms` DESC, `banner_id` DESC, `draw_seq` DESC
                LIMIT {Math.Clamp(limit, 1, 1000)}
                """, connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@BannerId", bannerId);
            using MySqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new HistoryRow(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) != 0,
                    reader.GetInt32(4), reader.GetInt64(3)));
            }
            return result;
        }

        /// <summary>
        /// 全卡池合计抽数（详单 banner_id=0 时的 total_draws）
        /// </summary>
        public int GetTotalDraws(int playerId, int bannerId)
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                "SELECT COUNT(*) FROM `gacha_records` WHERE `player_id` = @PlayerId AND (`banner_id` = @BannerId OR @BannerId = 0)",
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@BannerId", bannerId);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        // ──────────────────────────────
        // 唤取主流程
        // ──────────────────────────────

        /// <summary>
        /// 唤取的方法（服务端权威全流程 + 请求幂等）
        /// 顺序：幂等命中 → 校验卡池/次数 → 单事务 + 玩家主行锁内：FOR UPDATE 读背包校验媒介 → 读保底 → 逐抽 roll（含双保底）→
        /// 扣媒介 → 逐件入包（满包转邮件）→ 保底状态 UPSERT → 写流水 + 幂等快照 → 一次提交。
        /// 媒介不足/卡池不存在时什么都不扣；任何一步失败整单回滚。
        /// requestId 为空 = 不做幂等（老客户端）；同一 requestId 重试 = 重放首次结果，不重复扣费。
        /// </summary>
        public DrawOutcome Draw(int playerId, int bannerId, int count, string? requestId)
        {
            List<DrawResultItem> emptyResults = new();
            List<(int, int, int, long)> emptySlots = new();
            List<(int, string, int, int, string, string, int)> emptyConfigs = new();

            string idempotencyKey = (requestId ?? string.Empty).Trim();
            if (idempotencyKey.Length > 64)
                return Failure("请求ID非法", bannerId, emptyResults, emptySlots, emptyConfigs);

            if (count != 1 && count != 10)
                return Failure("唤取次数仅支持 1 或 10", bannerId, emptyResults, emptySlots, emptyConfigs);

            BannerConfig? banner = GetBanner(bannerId);
            if (banner == null)
                return Failure("卡池不存在或未开放", bannerId, emptyResults, emptySlots, emptyConfigs);
            if (banner.Pool.Count == 0)
                return Failure("卡池为空", bannerId, emptyResults, emptySlots, emptyConfigs);

            int cost = count == 10 ? banner.CostTen : banner.CostSingle;
            bool useIdempotency = idempotencyKey.Length > 0;

            BagRepository bag = new();
            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);

            List<DrawResultItem> results = emptyResults;
            int total = 0;
            int pityAfter = 0;
            bool guaranteedAfter = false;
            var grantNotes = new List<string>();
            var grantedItems = new List<(int ItemId, int Count)>();

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                BagRepository.LockPlayerRow(connection, transaction, playerId);

                // ① 幂等：同一 requestId 已结算 → 直接重放首次快照（不扣媒介、不 roll、不写流水）
                if (useIdempotency)
                {
                    string? snapshotJson = LoadIdempotentSnapshot(connection, transaction, playerId, idempotencyKey);
                    if (!string.IsNullOrEmpty(snapshotJson))
                    {
                        transaction.Commit();
                        DrawOutcome replay = BuildReplay(snapshotJson, bag, banner, playerId);
                        Console.WriteLine($"唤取重放(幂等):玩家{playerId} 卡池{bannerId} 请求{idempotencyKey} → 未扣费、未重抽");
                        return replay;
                    }
                }

                // ② 锁内读背包：媒介持有量与"新获得"判定都以这份快照为准
                BagSlot[] slots = BagRepository.LoadSlotsForUpdate(connection, transaction, playerId);
                HashSet<int> ownedItems = new(slots.Where(s => s.Count > 0).Select(s => s.ItemId));
                int currencyOwned = BagRepository.CountInSlots(slots, banner.CostItemId);
                if (currencyOwned < cost)
                {
                    // 尚未写库，事务随 return 释放即回滚；回执带唤取前的槽位快照
                    return Failure($"唤取媒介不足（还差 {cost - currencyOwned} 枚）", banner,
                        emptyResults, ToSlotList(slots), emptyConfigs);
                }

                // ③ 读保底（缺失时从流水反推）+ 本池累计（draw_seq 口径，与共享保底分开）
                PitySnapshot pityState = LoadPityState(connection, transaction, playerId, banner);
                int bannerSeqBefore = QueryBannerDrawCount(connection, transaction, playerId, bannerId);

                // ④ 逐抽 roll（分层随机 + 5★计数保底 + 十连4★保底 + 大保底）
                (results, pityAfter, guaranteedAfter) = RollBatch(
                    banner, count, pityState.Pity5Count, pityState.GuaranteedUp, ownedItems);

                // ⑤ 扣媒介 + 逐件入包：只改内存快照，满包放不下的记入 overflow（同事务转邮箱）
                BagRepository.ConsumeFromSlots(slots, banner.CostItemId, cost);
                Dictionary<int, int> maxStacks = LoadMaxStacks();
                var overflow = new List<(int ItemId, int Count)>();
                foreach (DrawResultItem item in results)
                {
                    int maxStack = maxStacks.TryGetValue(item.ItemId, out int stack) ? Math.Max(1, stack) : 1;
                    int remaining = BagRepository.GrantIntoSlots(
                        connection, transaction, playerId, ref slots, item.ItemId, 1, maxStack);
                    if (remaining == 0)
                    {
                        grantedItems.Add((item.ItemId, 1));
                    }
                    else
                    {
                        overflow.Add((item.ItemId, remaining));
                        grantNotes.Add($"{item.Name}: 背包已满，转入邮箱");
                    }
                }
                BagRepository.SaveSlots(connection, transaction, playerId, slots);

                total = bannerSeqBefore + count;

                // ⑥ 流水（draw_seq 连续：bannerSeqBefore+1 .. bannerSeqBefore+count）+ 保底状态，与背包同事务
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using (MySqlCommand insert = new(
                    """
                    INSERT INTO `gacha_records` (`player_id`, `banner_id`, `draw_seq`, `item_id`, `star`, `is_pity`, `pity_kind`, `created_at_ms`)
                    VALUES (@PlayerId, @BannerId, @Seq, @ItemId, @Star, @IsPity, @PityKind, @CreatedAt)
                    """, connection, transaction))
                {
                    insert.Parameters.Add("@PlayerId", MySqlDbType.Int32);
                    insert.Parameters.Add("@BannerId", MySqlDbType.Int32);
                    insert.Parameters.Add("@Seq", MySqlDbType.Int32);
                    insert.Parameters.Add("@ItemId", MySqlDbType.Int32);
                    insert.Parameters.Add("@Star", MySqlDbType.Int32);
                    insert.Parameters.Add("@IsPity", MySqlDbType.Int32);
                    insert.Parameters.Add("@PityKind", MySqlDbType.Int32);
                    insert.Parameters.Add("@CreatedAt", MySqlDbType.Int64);
                    insert.Prepare();
                    for (int i = 0; i < results.Count; i++)
                    {
                        DrawResultItem item = results[i];
                        insert.Parameters["@PlayerId"].Value = playerId;
                        insert.Parameters["@BannerId"].Value = bannerId;
                        insert.Parameters["@Seq"].Value = bannerSeqBefore + i + 1;
                        insert.Parameters["@ItemId"].Value = item.ItemId;
                        insert.Parameters["@Star"].Value = item.Star;
                        insert.Parameters["@IsPity"].Value = item.IsPity ? 1 : 0;
                        insert.Parameters["@PityKind"].Value = item.PityKind;
                        insert.Parameters["@CreatedAt"].Value = now;
                        insert.ExecuteNonQuery();
                    }
                }

                SavePityState(connection, transaction, playerId, banner,
                    pityAfter, guaranteedAfter, pityState.TotalDraws + count, now);

                if (overflow.Count > 0)
                {
                    MailRepository.InsertOverflowMail(connection, transaction, playerId,
                        "背包已满：唤取奖励转入邮箱",
                        "背包空间不足，部分唤取奖励已转入邮箱，请清理背包后领取。",
                        overflow);
                }

                // ⑦ 幂等快照（与结算同事务：要么都生效，要么都不生效）
                if (useIdempotency)
                {
                    string message = grantNotes.Count > 0 ? $"唤取成功，但 {string.Join("；", grantNotes)}" : "唤取成功";
                    SaveIdempotentSnapshot(connection, transaction, playerId, idempotencyKey, bannerId, count,
                        BuildSnapshotJson(message, pityAfter, banner.Pity5Threshold, total, guaranteedAfter,
                            results, ToSlotList(slots)), now);
                }

                transaction.Commit();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"唤取失败 [{exception.GetType().Name}]: {exception.Message}（player={playerId} banner={bannerId}）");
                return Failure("唤取失败，请稍后再试", banner, emptyResults, bag.GetSlots(playerId), emptyConfigs);
            }

            // 回执数据：操作后的背包快照 + 涉及物品配置
            List<(int SlotIndex, int ItemId, int Count, long InstanceId)> postSlots = bag.GetSlots(playerId);
            List<(int ItemId, string Name, int ItemType, int Rarity, string IconKey, string Description, int MaxStack)> configs =
                ConfigsForResults(bag, results);

            string successMessage = grantNotes.Count > 0
                ? $"唤取成功，但 {string.Join("；", grantNotes)}"
                : "唤取成功";
            Console.WriteLine($"唤取:玩家{playerId} 卡池{bannerId}({banner.Title}) {count}连 → [{string.Join(",", results.Select(r => $"{r.Name}{(r.Rarity >= 5 ? "(5★)" : r.Rarity >= 4 ? "(4★)" : string.Empty)}"))}] 保底={pityAfter}/{banner.Pity5Threshold} 大保底={(guaranteedAfter ? "是" : "否")} 累计={total}{(useIdempotency ? $" 请求{idempotencyKey}" : string.Empty)}");

            MaybeCleanupIdempotency();
            var outcome = new DrawOutcome(true, successMessage, results, pityAfter, banner.Pity5Threshold,
                total, guaranteedAfter, false, postSlots, configs);
            outcome.GrantedItems.AddRange(grantedItems);
            return outcome;
        }

        /// <summary>
        /// 一批 roll 的方法（纯内存，入包在调用方）
        /// 逐抽顺序结算：
        ///   5★ 计数到阈值 → 本抽强制 5★（pity_kind=1）
        ///   第 10 抽且前 9 抽无 4★+ → 本抽星级下限抬到 4（pity_kind=2，抽到 5★ 时按 5★ 计）
        ///   大保底 → 本抽 5★ 强制 UP（pity_kind=3）
        /// 每次结算后立刻更新 pity 与 guaranteed_up，下一抽用它。
        /// </summary>
        private (List<DrawResultItem> Results, int PityAfter, bool GuaranteedAfter) RollBatch(
            BannerConfig banner, int count, int pityBefore, bool guaranteedBefore, HashSet<int> ownedItems)
        {
            // 物品名/图标：item_config 全表小，一次取齐
            Dictionary<int, (string Name, int Rarity, string IconKey)> itemInfo = new();
            foreach (var config in new BagRepository().GetAllConfigs())
                itemInfo[config.ItemId] = (config.Name, config.Rarity, config.IconKey);

            var results = new List<DrawResultItem>();
            HashSet<int> batchSeen = new();
            int pity = pityBefore;
            bool guaranteedUp = guaranteedBefore;
            bool has4Plus = false;

            for (int i = 0; i < count; i++)
            {
                bool forced5 = pity + 1 >= banner.Pity5Threshold;
                bool tenGuard = count == 10 && i == count - 1 && !has4Plus;

                // 先定星级（0 = 该池未配置星级权重，走旧整池权重）
                int star = 0;
                if (forced5) star = 5;
                else if (tenGuard) star = RollStarTier(banner, minStar: 4);
                else if (banner.StarRates.Count > 0) star = RollStarTier(banner, minStar: 0);

                // 再定物品（大保底只作用于 5★）
                bool forceUp = star == 5 && guaranteedUp;
                PoolEntry entry;
                bool wasUp;
                if (star > 0)
                {
                    (entry, wasUp) = RollItemInStar(banner, star, forceUp);
                }
                else if (tenGuard)
                {
                    // 旧池兼容（未配置 star_rates）：十连保底直接在 4★ 子池里 roll
                    entry = RollPoolStar(banner.Pool, 4);
                    wasUp = entry.IsUp != 0;
                }
                else
                {
                    entry = RollAll(banner.Pool);
                    wasUp = entry.IsUp != 0;
                }

                // 兜底：星级子池为空时可能落到别的星级，以实际条目为准
                int effectiveStar = entry.Star;

                int pityKind = 0;
                if (effectiveStar == 5)
                {
                    if (guaranteedUp && wasUp) pityKind = 3;   // 大保底生效
                    else if (forced5) pityKind = 1;             // 5★ 计数保底
                }
                else if (tenGuard && effectiveStar == 4)
                {
                    pityKind = 2;                               // 十连 4★ 保底
                }

                bool isNew = !ownedItems.Contains(entry.ItemId) && !batchSeen.Contains(entry.ItemId);
                batchSeen.Add(entry.ItemId);

                (string name, int rarity, string iconKey) = itemInfo.GetValueOrDefault(
                    entry.ItemId, ($"物品{entry.ItemId}", effectiveStar, string.Empty));
                results.Add(new DrawResultItem(entry.ItemId, name, rarity, iconKey,
                    wasUp, pityKind != 0, isNew, pityKind, effectiveStar));

                // 顺序推进状态（本抽结算完立刻生效）
                if (effectiveStar >= 4) has4Plus = true;
                pity = effectiveStar == 5 ? 0 : pity + 1;
                if (effectiveStar == 5)
                    guaranteedUp = TierHasUp(banner, 5) && !wasUp;
            }

            return (results, pity, guaranteedUp);
        }

        /// <summary>按星级权重 roll 星级；minStar=4 用于十连保底。返回 0 表示该池未配置星级权重。</summary>
        private static int RollStarTier(BannerConfig banner, int minStar)
        {
            List<StarRate> tiers = banner.StarRates.Where(t => t.Weight > 0 && t.Star >= minStar).ToList();
            if (tiers.Count == 0)
            {
                // 配置缺档（例如十连保底要求 4★ 但池里没有 4★）：取最高星级兜底并告警
                tiers = banner.StarRates.Where(t => t.Weight > 0)
                    .OrderByDescending(t => t.Star).Take(1).ToList();
                if (tiers.Count > 0)
                    Console.WriteLine($"卡池 {banner.BannerId} 缺少 {minStar}★+ 配置，回落最高星级 {tiers[0].Star}★");
            }
            if (tiers.Count == 0) return 0;

            int total = tiers.Sum(t => t.Weight);
            int roll = Random.Shared.Next(total);
            foreach (StarRate tier in tiers)
            {
                roll -= tier.Weight;
                if (roll < 0) return tier.Star;
            }
            return tiers[^1].Star;
        }

        /// <summary>
        /// 星级内 roll 物品：优先按 up_rate 决定是否走 UP 子池；大保底直接走 UP 子池。
        /// 该星级没有 UP 条目时按普通权重 roll。
        /// </summary>
        private static (PoolEntry Entry, bool WasUp) RollItemInStar(BannerConfig banner, int star, bool forceUp)
        {
            List<PoolEntry> tier = banner.Pool.Where(p => p.Star == star).ToList();
            if (tier.Count == 0)
            {
                Console.WriteLine($"卡池 {banner.BannerId} 缺少 {star}★ 物品，回退整池 roll（请检查 pool_json）");
                tier = banner.Pool;
            }

            List<PoolEntry> up = tier.Where(p => p.IsUp != 0).ToList();
            List<PoolEntry> normal = tier.Where(p => p.IsUp == 0).ToList();
            if (up.Count == 0)
            {
                PoolEntry plain = RollAll(tier);
                return (plain, plain.IsUp != 0);
            }

            int rate = star >= 5 ? banner.UpRate5 : star == 4 ? banner.UpRate4 : 0;
            bool takeUp = forceUp || (rate > 0 && Random.Shared.Next(100) < rate);
            if (takeUp)
            {
                PoolEntry upEntry = RollAll(up);
                return (upEntry, upEntry.IsUp != 0);
            }

            if (normal.Count > 0) return (RollAll(normal), false);
            return (RollAll(tier), false);
        }

        /// <summary>全池按权重 roll（旧池兼容 / 星级内子池 roll）。</summary>
        private static PoolEntry RollAll(List<PoolEntry> pool)
        {
            int totalWeight = pool.Sum(p => p.Weight);
            if (totalWeight <= 0) return pool[^1];
            int roll = Random.Shared.Next(totalWeight);
            foreach (PoolEntry entry in pool)
            {
                roll -= entry.Weight;
                if (roll < 0) return entry;
            }
            return pool[^1];   // 权重和为 0 等退化情况的兜底
        }

        /// <summary>指定星级的子池按权重 roll（旧池无 star_rates 时的保底用）。</summary>
        private static PoolEntry RollPoolStar(List<PoolEntry> pool, int star)
        {
            List<PoolEntry> subPool = pool.Where(p => p.Star == star).ToList();
            return subPool.Count > 0 ? RollAll(subPool) : RollAll(pool);
        }

        /// <summary>该星级是否有 UP 条目（决定大保底状态是否有意义）。</summary>
        private static bool TierHasUp(BannerConfig banner, int star) =>
            banner.Pool.Any(p => p.Star == star && p.IsUp != 0);

        /// <summary>本池累计抽数（draw_seq 最大值；与共享保底组的累计分开）。</summary>
        private static int QueryBannerDrawCount(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, int bannerId)
        {
            using MySqlCommand command = new(
                "SELECT COALESCE(MAX(`draw_seq`), 0) FROM `gacha_records` WHERE `player_id` = @PlayerId AND `banner_id` = @BannerId",
                connection, transaction);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@BannerId", bannerId);
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }

        /// <summary>保底状态落库（UPSERT：唯一真相）。</summary>
        private static void SavePityState(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, BannerConfig banner,
            int pity5Count, bool guaranteedUp, int totalDraws, long nowMs)
        {
            using MySqlCommand command = new(
                """
                INSERT INTO `gacha_pity` (`player_id`, `pity_group`, `pity5_count`, `guaranteed_up`, `total_draws`, `updated_at_ms`)
                VALUES (@PlayerId, @Group, @Pity, @Guaranteed, @Total, @Now)
                ON DUPLICATE KEY UPDATE
                  `pity5_count` = VALUES(`pity5_count`),
                  `guaranteed_up` = VALUES(`guaranteed_up`),
                  `total_draws` = VALUES(`total_draws`),
                  `updated_at_ms` = VALUES(`updated_at_ms`)
                """, connection, transaction);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@Group", PityGroupOf(banner));
            command.Parameters.AddWithValue("@Pity", pity5Count);
            command.Parameters.AddWithValue("@Guaranteed", guaranteedUp ? 1 : 0);
            command.Parameters.AddWithValue("@Total", totalDraws);
            command.Parameters.AddWithValue("@Now", nowMs);
            command.ExecuteNonQuery();
        }

        // ──────────────────────────────
        // 幂等快照
        // ──────────────────────────────

        private static string? LoadIdempotentSnapshot(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, string requestId)
        {
            using MySqlCommand command = new(
                "SELECT `response_json` FROM `gacha_requests` WHERE `player_id` = @PlayerId AND `request_id` = @RequestId FOR UPDATE",
                connection, transaction);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@RequestId", requestId);
            object? value = command.ExecuteScalar();
            return value as string;
        }

        private static void SaveIdempotentSnapshot(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, string requestId,
            int bannerId, int count, string responseJson, long nowMs)
        {
            using MySqlCommand command = new(
                """
                INSERT INTO `gacha_requests` (`player_id`, `request_id`, `banner_id`, `draw_count`, `response_json`, `created_at_ms`)
                VALUES (@PlayerId, @RequestId, @BannerId, @Count, @Json, @Now)
                """, connection, transaction);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@RequestId", requestId);
            command.Parameters.AddWithValue("@BannerId", bannerId);
            command.Parameters.AddWithValue("@Count", count);
            command.Parameters.AddWithValue("@Json", responseJson);
            command.Parameters.AddWithValue("@Now", nowMs);
            command.ExecuteNonQuery();
        }

        /// <summary>幂等快照 DTO（字段与回执语义一致；configs 不入快照，重放时按 item_id 现查静态表）。</summary>
        private sealed class StoredDraw
        {
            public string Message { get; set; } = string.Empty;
            public int Pity5Count { get; set; }
            public int Pity5Threshold { get; set; }
            public int TotalDraws { get; set; }
            public bool GuaranteedUp { get; set; }
            public List<StoredItem> Results { get; set; } = new();
            public List<StoredSlot> Slots { get; set; } = new();
        }

        private sealed class StoredItem
        {
            public int ItemId { get; set; }
            public string Name { get; set; } = string.Empty;
            public int Rarity { get; set; }
            public string IconKey { get; set; } = string.Empty;
            public bool IsUp { get; set; }
            public bool IsPity { get; set; }
            public bool IsNew { get; set; }
            public int PityKind { get; set; }
            public int Star { get; set; }
        }

        private sealed class StoredSlot
        {
            public int SlotIndex { get; set; }
            public int ItemId { get; set; }
            public int Count { get; set; }
            public long InstanceId { get; set; }
        }

        private static string BuildSnapshotJson(
            string message, int pity5Count, int pity5Threshold, int totalDraws, bool guaranteedUp,
            List<DrawResultItem> results, List<(int SlotIndex, int ItemId, int Count, long InstanceId)> slots)
        {
            var stored = new StoredDraw
            {
                Message = message,
                Pity5Count = pity5Count,
                Pity5Threshold = pity5Threshold,
                TotalDraws = totalDraws,
                GuaranteedUp = guaranteedUp,
                Results = results.Select(r => new StoredItem
                {
                    ItemId = r.ItemId, Name = r.Name, Rarity = r.Rarity, IconKey = r.IconKey,
                    IsUp = r.IsUp, IsPity = r.IsPity, IsNew = r.IsNew, PityKind = r.PityKind, Star = r.Star,
                }).ToList(),
                Slots = slots.Select(s => new StoredSlot
                {
                    SlotIndex = s.SlotIndex, ItemId = s.ItemId, Count = s.Count, InstanceId = s.InstanceId,
                }).ToList(),
            };
            return JsonSerializer.Serialize(stored);
        }

        /// <summary>把快照还原成回执（重放路径：不碰背包/保底/流水）。</summary>
        private static DrawOutcome BuildReplay(string snapshotJson, BagRepository bag, BannerConfig banner, int playerId)
        {
            try
            {
                StoredDraw? stored = JsonSerializer.Deserialize<StoredDraw>(snapshotJson);
                if (stored != null)
                {
                    List<DrawResultItem> results = stored.Results.Select(r => new DrawResultItem(
                        r.ItemId, r.Name, r.Rarity, r.IconKey, r.IsUp, r.IsPity, r.IsNew, r.PityKind, r.Star)).ToList();
                    List<(int SlotIndex, int ItemId, int Count, long InstanceId)> slots =
                        stored.Slots.Select(s => (s.SlotIndex, s.ItemId, s.Count, s.InstanceId)).ToList();
                    return new DrawOutcome(true, stored.Message, results,
                        stored.Pity5Count, stored.Pity5Threshold, stored.TotalDraws, stored.GuaranteedUp, true,
                        slots, ConfigsForResults(bag, results));
                }
            }
            catch (JsonException exception)
            {
                Console.WriteLine($"幂等快照解析失败（player={playerId}）: {exception.Message}");
            }

            // 快照损坏：明确失败，绝不静默重抽（避免重复扣费）
            return new DrawOutcome(false, "上次唤取结果读取失败，请联系客服核对", new List<DrawResultItem>(),
                0, banner.Pity5Threshold, 0, false, true, new List<(int, int, int, long)>(),
                new List<(int, string, int, int, string, string, int)>());
        }

        /// <summary>按结果 item_id 取静态配置（重放与新抽共用）。</summary>
        private static List<(int ItemId, string Name, int ItemType, int Rarity, string IconKey, string Description, int MaxStack)>
            ConfigsForResults(BagRepository bag, List<DrawResultItem> results)
        {
            HashSet<int> resultItemIds = new(results.Select(r => r.ItemId));
            return bag.GetAllConfigs().Where(c => resultItemIds.Contains(c.ItemId)).ToList();
        }

        /// <summary>幂等记录保留 7 天；抽卡路径以 1% 概率顺带清理（避免启动时长事务）。</summary>
        private static void MaybeCleanupIdempotency()
        {
            if (Random.Shared.Next(100) != 0) return;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                    using MySqlCommand command = new(
                        "DELETE FROM `gacha_requests` WHERE `created_at_ms` < @Cutoff LIMIT 1000", connection);
                    command.Parameters.AddWithValue("@Cutoff",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7L * 24 * 3600 * 1000);
                    int deleted = command.ExecuteNonQuery();
                    if (deleted > 0) Console.WriteLine($"抽卡幂等记录清理:{deleted} 条");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"抽卡幂等记录清理失败 [{exception.GetType().Name}]: {exception.Message}");
                }
            });
        }

        // ──────────────────────────────
        // 小工具
        // ──────────────────────────────

        private static DrawOutcome Failure(string message, int bannerId, List<DrawResultItem> results,
            List<(int, int, int, long)> slots, List<(int, string, int, int, string, string, int)> configs)
        {
            BannerConfig? banner = GetBanner(bannerId);
            return new DrawOutcome(false, message, results, 0, banner?.Pity5Threshold ?? 0, 0, false, false, slots, configs);
        }

        private static DrawOutcome Failure(string message, BannerConfig banner, List<DrawResultItem> results,
            List<(int, int, int, long)> slots, List<(int, string, int, int, string, string, int)> configs)
        {
            return new DrawOutcome(false, message, results, 0, banner.Pity5Threshold, 0, false, false, slots, configs);
        }

        /// <summary>把内存槽位快照转成回执用的非空槽列表（与 BagRepository.GetSlots 同口径）。</summary>
        private static List<(int SlotIndex, int ItemId, int Count, long InstanceId)> ToSlotList(BagSlot[] slots)
        {
            var rows = new List<(int, int, int, long)>();
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Count > 0) rows.Add((i, slots[i].ItemId, slots[i].Count, slots[i].InstanceId));
            }
            return rows;
        }

        /// <summary>读取全部物品的堆叠上限（入包拆堆用；走 BagRepository 统一配置缓存，与背包/邮件同规则）。</summary>
        private static Dictionary<int, int> LoadMaxStacks()
        {
            var stacks = new Dictionary<int, int>();
            foreach ((int itemId, BagRepository.ItemConfig config) in BagRepository.GetConfigMap())
            {
                stacks[itemId] = config.MaxStack;   // 缓存装载时已 Math.Max(1, …) 兜底
            }
            return stacks;
        }
    }
}
