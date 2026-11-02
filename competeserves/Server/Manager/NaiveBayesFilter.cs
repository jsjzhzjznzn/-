// 命名空间：CompeteServer.Manager
// 服务端各单例/工具管理器所在命名空间
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace CompeteServer.Manager
{
    /// <summary>
    /// 违禁文本二级审核（朴素贝叶斯分类器）
    ///
    /// 定位：一级是 <see cref="BannedWordFilter"/> 的 AC 自动机——词库里登记过的词精确打码，
    /// 可定位、几乎零误伤，但"词库外的新词 / 变体 / 广告话术"必然漏，只能靠人工加词补救。
    /// 二级补的正是这块：不看"命中了哪个词"，而看"整段文本的特征分布像不像违规"，
    /// 于是 煞笔 / 傻批 / 憨批 这类没登记、但与已登记词共享大量字形的说法也能被泛化命中。
    ///
    /// 模型：多项式朴素贝叶斯，对数空间 + 拉普拉斯平滑
    ///   P(违规|文本) ∝ P(违规) · Π P(特征|违规)
    /// 特征 = 折叠后文本的字符 unigram + bigram（bigram 判别力更强，配 BigramWeight 加权）。
    /// 打分用"长度归一化的平均对数似然比"： Σ w·log[P(t|违规)/P(t|正常)] / Σ w
    ///   除以总权重是为了消除朴素贝叶斯的经典长度偏置——不除的话，长消息必然累积出高分。
    ///   先验 P(违规) 不参与打分：聊天场景里违规消息的绝对占比极低且随运营漂移，
    ///   把它平摊进长度归一化后的分数只会扭曲阈值语义；要更保守，调高 Threshold 即可。
    ///
    /// 对抗绕过：与一级共用同一套折叠（<see cref="BannedWordFilter.FoldText"/>）——
    /// 全角 / 同形字 / 干扰符归一化之后，"煞·笔""傻 批"与语料里的词形一致，两级同时生效。
    ///
    /// 可解释性：概率分类天生"说不清为什么"，日志会补上贡献最大的 top-3 特征，
    /// 并把挨着阈值的判定也记一行，便于运营按真实流量调阈值 / 补语料。
    ///
    /// 线程模型：与一级一致——启动时训练一次，快照不可变 + Volatile 原子发布，
    /// 读侧无锁、按线程复用缓冲，未命中零堆分配。
    /// </summary>
    public static class NaiveBayesFilter
    {
        /// <summary>二级审核的处置决策</summary>
        public enum Decision
        {
            /// <summary>放行：未启用、特征不足，或分数未达阈值</summary>
            Pass = 0,

            /// <summary>整条打码后放行（Mode = mask）</summary>
            Mask = 1,

            /// <summary>拒绝发送、不落库不计数（Mode = reject）</summary>
            Reject = 2,
        }

        /// <summary>打码字符：与一级同字符，Mask 模式整条替换成等长星号</summary>
        private const char StarChar = '*';

        /// <summary>bigram 键的高位标记：与 unigram 键（0..0xFFFF 的字符值）不重叠</summary>
        private const long BigramKeyBase = 0x1_0000_0000L;

        /// <summary>日志里回显的贡献特征个数</summary>
        private const int TopFeatureCount = 3;

        /// <summary>观察日志门槛：分数 ≥ 阈值×该值 时记一行"接近阈值"，便于按真实流量调参</summary>
        private const double ObserveRatio = 0.5;

        // ──────────────────────────────
        // 语料 + 配置（JSON 模型，字段名大小写不敏感，便于手改文件）
        // ──────────────────────────────

        private sealed class BayesConfig
        {
            /// <summary>处置方式：off / log / mask / reject（无法识别的值一律按 off，宁可放行也不误拦）</summary>
            public string Mode { get; set; } = "log";

            /// <summary>命中阈值（平均对数似然比）：≤0 表示"用留一自检的建议值"；越保守调越高</summary>
            public double Threshold { get; set; }

            /// <summary>折叠后文本长度下限：短文本特征太少、概率不可靠，不足此长度不判</summary>
            public int MinLength { get; set; } = 4;

            /// <summary>bigram 特征权重（相对 unigram 的倍数）</summary>
            public double BigramWeight { get; set; } = 2.0;

            /// <summary>拉普拉斯平滑系数：避免"语料里没出现过的特征"概率为 0</summary>
            public double Alpha { get; set; } = 1.0;

            /// <summary>违规语料（辱骂 / 变体 / 广告话术）</summary>
            public List<string> Violating { get; set; } = new();

            /// <summary>正常语料（玩家日常聊天），有对照才谈得上"判别力"</summary>
            public List<string> Normal { get; set; } = new();
        }

        // ──────────────────────────────
        // 不可变匹配快照（原子引用发布）
        // ──────────────────────────────

        private sealed class BayesSnapshot
        {
            /// <summary>是否启用；false 时 Screen 直接放行</summary>
            public bool Enabled { get; init; }

            /// <summary>处置方式：log / mask / reject</summary>
            public string Mode { get; init; } = "off";

            /// <summary>命中阈值（平均对数似然比）</summary>
            public double Threshold { get; init; }

            /// <summary>折叠后文本长度下限</summary>
            public int MinLength { get; init; }

            /// <summary>bigram 特征权重</summary>
            public double BigramWeight { get; init; }

            /// <summary>特征 → 对数似然比 log[P(t|违规)/P(t|正常)]</summary>
            public Dictionary<long, double> LogRatio { get; init; } = new();

            /// <summary>两类都没出现过的特征的对数似然比（平滑常数决定，通常极小）</summary>
            public double UnknownLogRatio { get; init; }

            /// <summary>违规语料条数（启动横幅用）</summary>
            public int ViolatingCount { get; init; }

            /// <summary>正常语料条数（启动横幅用）</summary>
            public int NormalCount { get; init; }

            /// <summary>特征个数（启动横幅用）</summary>
            public int FeatureCount { get; init; }
        }

        /// <summary>当前快照（原子发布）</summary>
        private static BayesSnapshot snapshot = new();

        /// <summary>按线程复用的折叠缓冲（线程池线程长期存活，容量取历史最大消息即可）</summary>
        [ThreadStatic] private static StringBuilder? foldBuffer;

        /// <summary>启动横幅 / 排查用的摘要</summary>
        public static string Summary
        {
            get
            {
                BayesSnapshot current = Volatile.Read(ref snapshot);
                return current.Enabled
                    ? $"模式={current.Mode} 阈值={current.Threshold:F3} 特征={current.FeatureCount} 语料={current.ViolatingCount}+{current.NormalCount}"
                    : "未启用";
            }
        }

        // ──────────────────────────────
        // 训练中间态
        // ──────────────────────────────

        /// <summary>一条语料样本提取出的特征（留一自检要"把这条摘出去"，所以整条留着）</summary>
        private sealed class Sample
        {
            public long[] Keys { get; init; } = Array.Empty<long>();

            public double[] Weights { get; init; } = Array.Empty<double>();

            /// <summary>该样本的特征权重总和（= 打分时分母的一部分）</summary>
            public double WeightSum { get; init; }

            /// <summary>原始语料文本：自检日志要回显"最低/最高分是哪一条"（语料是配置，不是用户内容）</summary>
            public string Text { get; init; } = string.Empty;
        }

        /// <summary>训练中间态：正负两类的特征计数 + 平滑/归一化参数</summary>
        private sealed class Trainer
        {
            public Dictionary<long, double> ViolatingCounts { get; } = new();

            public Dictionary<long, double> NormalCounts { get; } = new();

            public double ViolatingTotal { get; set; }

            public double NormalTotal { get; set; }

            public int VocabSize { get; set; }

            public double Alpha { get; set; }

            /// <summary>朴素贝叶斯分母：该类总特征量 + 平滑量（未出现过的特征也不会拿到 0 概率）</summary>
            public double Denominator(double total) => total + Alpha * VocabSize;

            /// <summary>某个特征的对数似然比（训练集视角，不含留一的扣减）</summary>
            public double RatioOf(long key)
            {
                ViolatingCounts.TryGetValue(key, out double violatingCount);
                NormalCounts.TryGetValue(key, out double normalCount);
                double pViolating = (violatingCount + Alpha) / Denominator(ViolatingTotal);
                double pNormal = (normalCount + Alpha) / Denominator(NormalTotal);
                return Math.Log(pViolating) - Math.Log(pNormal);
            }
        }

        // ──────────────────────────────
        // 加载 / 训练
        // ──────────────────────────────

        /// <summary>
        /// 加载语料并训练的方法（启动时调用一次）
        /// 文件不存在时写一份示例语料（幂等种子，便于手工接着改）；任何异常都发布"未启用"快照，
        /// 二级审核绝不阻塞启动，也不影响一级违禁词打码
        /// </summary>
        /// <param name="jsonPath">参数名：jsonPath，类型：string，用途：语料+配置 JSON 路径（Config/bayes_filter.json）</param>
        public static void Load(string jsonPath)
        {
            if (!File.Exists(jsonPath))
            {
                string? directory = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(BuildSeed(), new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                Console.WriteLine($"二级审核语料不存在，已生成示例语料: {jsonPath}");
            }

            BayesConfig? config = JsonSerializer.Deserialize<BayesConfig>(
                File.ReadAllText(jsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            config ??= new BayesConfig();

            string mode = NormalizeMode(config.Mode);
            if (mode == "off")
            {
                PublishDisabled();
                Console.WriteLine("二级审核已关闭（Mode = off），聊天只走一级违禁词打码");
                return;
            }

            // 正负语料缺一不可：只有一类样本时"似然比"没有参照，算出来的分数没有意义
            if (config.Violating.Count == 0 || config.Normal.Count == 0)
            {
                PublishDisabled();
                Console.Error.WriteLine("二级审核语料不完整（Violating / Normal 需要同时提供），二级审核不予启用");
                return;
            }

            double bigramWeight = config.BigramWeight > 0 ? config.BigramWeight : 2.0;
            double alpha = config.Alpha > 0 ? config.Alpha : 1.0;
            int minLength = config.MinLength > 0 ? config.MinLength : 4;

            Trainer trainer = new() { Alpha = alpha };
            List<Sample> violatingSamples = new();
            List<Sample> normalSamples = new();
            StringBuilder buffer = new(128);
            List<long> keys = new(128);
            List<double> weights = new(128);

            trainer.ViolatingTotal = CountCorpus(config.Violating, bigramWeight, buffer, keys, weights,
                trainer.ViolatingCounts, violatingSamples);
            trainer.NormalTotal = CountCorpus(config.Normal, bigramWeight, buffer, keys, weights,
                trainer.NormalCounts, normalSamples);

            if (trainer.ViolatingTotal <= 0 || trainer.NormalTotal <= 0)
            {
                PublishDisabled();
                Console.Error.WriteLine("二级审核语料没有可用特征（全是空白串），二级审核不予启用");
                return;
            }

            HashSet<long> vocabulary = new(trainer.ViolatingCounts.Keys);
            vocabulary.UnionWith(trainer.NormalCounts.Keys);
            trainer.VocabSize = vocabulary.Count;

            // 两类都没出现过的特征：概率同为平滑值，比值是个常数（不为 0，因为两边总特征量未必相等）
            double unknownRatio =
                Math.Log(trainer.Denominator(trainer.NormalTotal)) - Math.Log(trainer.Denominator(trainer.ViolatingTotal));

            // 留一自检：给出无偏的分数分布（阈值定标依据），同时体检"语料是否可分"
            (double minViolating, string minViolatingText, double maxNormal, string maxNormalText) =
                LeaveOneOutCheck(trainer, violatingSamples, normalSamples);

            // 建议阈值取"违规样本最低分"的六成：
            // 正常侧最高分因留一扣掉了该条样本自身的特征而系统性偏高（悲观偏差），
            // 拿两侧中点当分界会保守到不可用——真实正常消息的分数远低于这里的 maxNormal
            double suggestedThreshold = minViolating * 0.6;
            if (!double.IsFinite(suggestedThreshold) || suggestedThreshold <= 0) suggestedThreshold = 0.5;

            double configured = config.Threshold;
            double threshold = configured > 0 && double.IsFinite(configured) ? configured : suggestedThreshold;

            Dictionary<long, double> logRatio = new(trainer.VocabSize);
            foreach (long key in vocabulary) logRatio[key] = trainer.RatioOf(key);

            Volatile.Write(ref snapshot, new BayesSnapshot
            {
                Enabled = true,
                Mode = mode,
                Threshold = threshold,
                MinLength = minLength,
                BigramWeight = bigramWeight,
                LogRatio = logRatio,
                UnknownLogRatio = unknownRatio,
                ViolatingCount = violatingSamples.Count,
                NormalCount = normalSamples.Count,
                FeatureCount = trainer.VocabSize,
            });

            Console.WriteLine($"二级审核语料就绪: 违规 {violatingSamples.Count} 条 / 正常 {normalSamples.Count} 条，" +
                              $"特征 {trainer.VocabSize} 个，bigram 权重 {bigramWeight:0.##}，平滑 {alpha:0.##}");
            Console.WriteLine($"二级审核留一自检: 违规样本最低分 {minViolating:F3}（「{minViolatingText}」）/ " +
                              $"正常样本最高分 {maxNormal:F3}（「{maxNormalText}」）→ 建议阈值 {suggestedThreshold:F3}" +
                              $"（本次采用 {threshold:F3}" + (config.Threshold > 0 ? "，来自配置）" : "，来自自检）"));
            if (maxNormal > suggestedThreshold)
            {
                Console.WriteLine("注意：正常侧最高分高于建议阈值（留一偏悲观所致），启用拦截前请用真实正常消息复核");
            }
        }

        /// <summary>发布"未启用"快照（任何一条失败路径都收敛到这里）</summary>
        private static void PublishDisabled() => Volatile.Write(ref snapshot, new BayesSnapshot());

        /// <summary>规范化处置方式；无法识别的值一律按 off</summary>
        private static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
        {
            "reject" => "reject",
            "mask" => "mask",
            "log" => "log",
            _ => "off",
        };

        /// <summary>
        /// 统计一类语料的特征计数
        /// 训练与推理共用同一套提特征（ExtractFeatures），两边规则不会漂移
        /// </summary>
        /// <param name="corpus">参数名：corpus，类型：List&lt;string&gt;，用途：该类语料</param>
        /// <param name="bigramWeight">参数名：bigramWeight，类型：double，用途：bigram 权重</param>
        /// <param name="buffer">参数名：buffer，类型：StringBuilder，用途：折叠缓冲（复用）</param>
        /// <param name="keys">参数名：keys，类型：List&lt;long&gt;，用途：特征键缓冲（复用）</param>
        /// <param name="weights">参数名：weights，类型：List&lt;double&gt;，用途：特征权重缓冲（复用）</param>
        /// <param name="counts">参数名：counts，类型：Dictionary&lt;long, double&gt;，用途：输出该类的特征计数</param>
        /// <param name="samples">参数名：samples，类型：List&lt;Sample&gt;，用途：输出逐条样本特征（留一自检用）</param>
        /// <returns>返回值类型：double，用途：该类特征权重总和（打分分母）</returns>
        private static double CountCorpus(List<string> corpus, double bigramWeight, StringBuilder buffer,
            List<long> keys, List<double> weights, Dictionary<long, double> counts, List<Sample> samples)
        {
            double total = 0;
            foreach (string text in corpus)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;

                keys.Clear();
                weights.Clear();
                ExtractFeatures(text, bigramWeight, buffer, keys, weights);
                if (keys.Count == 0) continue;

                double sampleTotal = 0;
                for (int i = 0; i < keys.Count; i++)
                {
                    counts.TryGetValue(keys[i], out double existing);
                    counts[keys[i]] = existing + weights[i];
                    sampleTotal += weights[i];
                }
                total += sampleTotal;

                samples.Add(new Sample
                {
                    Keys = keys.ToArray(),
                    Weights = weights.ToArray(),
                    WeightSum = sampleTotal,
                    Text = text,
                });
            }
            return total;
        }

        /// <summary>
        /// 提特征的方法：折叠（与一级同一套归一化，绕过手法对两级同时失效）→ 字符 unigram + bigram
        /// </summary>
        /// <param name="text">参数名：text，类型：string，用途：原文本</param>
        /// <param name="bigramWeight">参数名：bigramWeight，类型：double，用途：bigram 权重</param>
        /// <param name="buffer">参数名：buffer，类型：StringBuilder，用途：折叠缓冲（复用）</param>
        /// <param name="keys">参数名：keys，类型：List&lt;long&gt;，用途：输出特征键</param>
        /// <param name="weights">参数名：weights，类型：List&lt;double&gt;，用途：输出特征权重</param>
        private static void ExtractFeatures(string text, double bigramWeight, StringBuilder buffer,
            List<long> keys, List<double> weights)
        {
            buffer.Clear();
            BannedWordFilter.FoldText(text, buffer, null);

            for (int i = 0; i < buffer.Length; i++)
            {
                keys.Add(buffer[i]);
                weights.Add(1.0);

                if (i == 0) continue;
                keys.Add(BigramKeyBase | ((long)buffer[i - 1] << 16) | buffer[i]);
                weights.Add(bigramWeight);
            }
        }

        /// <summary>
        /// 留一交叉验证：每条样本打分时先把它自己那份计数摘掉（相当于"没见过这条"再判它）
        /// 返回违规样本的最低分与正常样本的最高分——两者不相交，才存在能分开它们的单一阈值
        /// </summary>
        /// <param name="trainer">参数名：trainer，类型：Trainer，用途：训练中间态</param>
        /// <param name="violatingSamples">参数名：violatingSamples，类型：List&lt;Sample&gt;，用途：违规样本</param>
        /// <param name="normalSamples">参数名：normalSamples，类型：List&lt;Sample&gt;，用途：正常样本</param>
        /// <returns>返回值类型：(double, string, double, string)，用途：违规最低分及其样本 / 正常最高分及其样本</returns>
        private static (double MinViolating, string MinViolatingText, double MaxNormal, string MaxNormalText)
            LeaveOneOutCheck(Trainer trainer, List<Sample> violatingSamples, List<Sample> normalSamples)
        {
            double minViolating = double.MaxValue;
            string minViolatingText = string.Empty;
            foreach (Sample sample in violatingSamples)
            {
                double score = ScoreSample(sample, trainer, isViolating: true, excludeSelf: true);
                if (score < minViolating)
                {
                    minViolating = score;
                    minViolatingText = sample.Text;
                }
            }

            double maxNormal = double.MinValue;
            string maxNormalText = string.Empty;
            foreach (Sample sample in normalSamples)
            {
                double score = ScoreSample(sample, trainer, isViolating: false, excludeSelf: true);
                if (score > maxNormal)
                {
                    maxNormal = score;
                    maxNormalText = sample.Text;
                }
            }

            if (violatingSamples.Count == 0) minViolating = 0;
            if (normalSamples.Count == 0) maxNormal = 0;
            return (minViolating, minViolatingText, maxNormal, maxNormalText);
        }

        /// <summary>
        /// 给一条样本打分（长度归一化的平均对数似然比）
        /// excludeSelf = true 即留一：把这条样本自身对计数的贡献先扣掉，不许"偷看答案"
        /// </summary>
        /// <param name="sample">参数名：sample，类型：Sample，用途：待打分样本</param>
        /// <param name="trainer">参数名：trainer，类型：Trainer，用途：训练中间态</param>
        /// <param name="isViolating">参数名：isViolating，类型：bool，用途：该样本属于哪一类</param>
        /// <param name="excludeSelf">参数名：excludeSelf，类型：bool，用途：是否扣掉样本自身贡献</param>
        /// <returns>返回值类型：double，用途：平均对数似然比</returns>
        private static double ScoreSample(Sample sample, Trainer trainer, bool isViolating, bool excludeSelf)
        {
            double violatingTotal = trainer.ViolatingTotal;
            double normalTotal = trainer.NormalTotal;
            if (excludeSelf)
            {
                if (isViolating) violatingTotal -= sample.WeightSum;
                else normalTotal -= sample.WeightSum;
            }

            double violatingDenominator = trainer.Denominator(violatingTotal);
            double normalDenominator = trainer.Denominator(normalTotal);

            double sum = 0;
            double weightSum = 0;
            for (int i = 0; i < sample.Keys.Length; i++)
            {
                long key = sample.Keys[i];
                double weight = sample.Weights[i];
                weightSum += weight;

                trainer.ViolatingCounts.TryGetValue(key, out double violatingCount);
                trainer.NormalCounts.TryGetValue(key, out double normalCount);
                if (excludeSelf)
                {
                    if (isViolating) violatingCount -= weight;
                    else normalCount -= weight;
                }

                double pViolating = (violatingCount + trainer.Alpha) / violatingDenominator;
                double pNormal = (normalCount + trainer.Alpha) / normalDenominator;
                sum += weight * (Math.Log(pViolating) - Math.Log(pNormal));
            }

            return weightSum > 0 ? sum / weightSum : 0;
        }

        // ──────────────────────────────
        // 审核
        // ──────────────────────────────

        /// <summary>
        /// 二级审核的方法：对一级零命中的文本给出决策
        /// 未启用 / 特征不足 / 分数未达阈值都返回 Pass；Mask 与 Reject 由配置的 Mode 决定
        /// 用**未打码的原文**提特征：星号是打码产物，进特征会把分布带偏
        /// </summary>
        /// <param name="content">参数名：content，类型：string，用途：原始聊天正文</param>
        /// <param name="processed">输出参数：Mask 模式下为等长打码文本，其余情况等于 content</param>
        /// <returns>返回值类型：Decision，用途：放行 / 打码 / 拒绝</returns>
        public static Decision Screen(string content, out string processed)
        {
            processed = content;

            BayesSnapshot current = Volatile.Read(ref snapshot);
            if (!current.Enabled || string.IsNullOrEmpty(content)) return Decision.Pass;

            StringBuilder buffer = foldBuffer ??= new StringBuilder(128);
            buffer.Clear();
            BannedWordFilter.FoldText(content, buffer, null);

            // 折叠后太短：特征太少、概率不可靠（短文本误判的代价却不小），不判
            if (buffer.Length < current.MinLength) return Decision.Pass;

            double sum = 0;
            double weightSum = 0;
            int featureCount = 0;
            Span<long> topKeys = stackalloc long[TopFeatureCount];
            Span<double> topValues = stackalloc double[TopFeatureCount];
            int topCount = 0;

            for (int i = 0; i < buffer.Length; i++)
            {
                char character = buffer[i];

                // unigram：单字也有判别力（"傻""逼"），权重 1
                weightSum += 1.0;
                featureCount++;
                double unigramContribution = RatioOf(current, character);
                sum += unigramContribution;
                if (unigramContribution > 0) PushTop(character, unigramContribution, topKeys, topValues, ref topCount);

                // bigram：相邻两字，判别力更强，故加权；也顺带压掉"随机拼字"的噪声
                if (i == 0) continue;
                long bigramKey = BigramKeyBase | ((long)buffer[i - 1] << 16) | character;
                weightSum += current.BigramWeight;
                featureCount++;
                double bigramContribution = current.BigramWeight * RatioOf(current, bigramKey);
                sum += bigramContribution;
                if (bigramContribution > 0) PushTop(bigramKey, bigramContribution, topKeys, topValues, ref topCount);
            }

            double score = weightSum > 0 ? sum / weightSum : 0;
            if (score < current.Threshold)
            {
                // 未达阈值但挨得近：记一行观察日志，便于按真实流量判断阈值松紧
                if (score >= current.Threshold * ObserveRatio)
                {
                    Console.WriteLine($"[二级贝叶斯] 观察 分数={score:F3} 阈值={current.Threshold:F3} " +
                                      $"特征={featureCount} 贡献={DescribeTop(topKeys, topValues, topCount)}");
                }
                return Decision.Pass;
            }

            Decision decision = current.Mode switch
            {
                "reject" => Decision.Reject,
                "mask" => Decision.Mask,
                _ => Decision.Pass,   // log：只记录、不处置
            };

            // 只记分数与贡献特征，不回显原文：违规内容不该被复制进服务端日志
            Console.WriteLine($"[二级贝叶斯] {DescribeDecision(decision)} 分数={score:F3} 阈值={current.Threshold:F3} " +
                              $"特征={featureCount} 贡献={DescribeTop(topKeys, topValues, topCount)}");

            if (decision == Decision.Mask) processed = MaskWhole(content);
            return decision;
        }

        /// <summary>取特征的对数似然比；未见特征用常数项（两类平滑概率之比）</summary>
        private static double RatioOf(BayesSnapshot snapshot, long key) =>
            snapshot.LogRatio.TryGetValue(key, out double known) ? known : snapshot.UnknownLogRatio;

        /// <summary>整条打码：等长星号（与一级"打码不改长度"的约定一致）</summary>
        private static string MaskWhole(string content) => new string(StarChar, content.Length);

        /// <summary>把正贡献特征插入 top-N（N 很小，直接插入排序，不引入堆）</summary>
        private static void PushTop(long key, double value, Span<long> keys, Span<double> values, ref int count)
        {
            if (count < keys.Length)
            {
                int position = count++;
                while (position > 0 && values[position - 1] < value)
                {
                    values[position] = values[position - 1];
                    keys[position] = keys[position - 1];
                    position--;
                }
                values[position] = value;
                keys[position] = key;
                return;
            }

            if (value <= values[keys.Length - 1]) return;
            int index = keys.Length - 1;
            while (index > 0 && values[index - 1] < value)
            {
                values[index] = values[index - 1];
                keys[index] = keys[index - 1];
                index--;
            }
            values[index] = value;
            keys[index] = key;
        }

        /// <summary>把特征键还原成可读文字（bigram 两字，unigram 一字）</summary>
        private static string DescribeKey(long key)
        {
            if (key >= BigramKeyBase)
            {
                long value = key - BigramKeyBase;
                return $"“{(char)(value >> 16)}{(char)(value & 0xFFFF)}”";
            }
            return $"“{(char)key}”";
        }

        /// <summary>回显 top-N 贡献特征（只在日志路径分配，可接受）</summary>
        private static string DescribeTop(Span<long> keys, Span<double> values, int count)
        {
            if (count == 0) return "无";
            StringBuilder text = new();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text.Append('/');
                text.Append(DescribeKey(keys[i])).Append(':').Append(values[i].ToString("F2"));
            }
            return text.ToString();
        }

        /// <summary>决策的中文名（日志用）</summary>
        private static string DescribeDecision(Decision decision) => decision switch
        {
            Decision.Mask => "打码",
            Decision.Reject => "拒绝",
            _ => "放行",
        };

        /// <summary>
        /// 示例语料：文件丢失时也能起来（兜底）
        /// 真实语料请直接改 Config/bayes_filter.json——语料是这套审核的唯一真相，改完重启生效
        /// </summary>
        private static BayesConfig BuildSeed() => new()
        {
            Mode = "log",
            Threshold = 0,
            MinLength = 4,
            BigramWeight = 2,
            Violating = new List<string>
            {
                "煞笔玩意 滚远点",
                "你个脑瘫 会不会玩",
                "草泥马 会不会打",
                "死全家 别惹我",
                "憨批东西 别丢人",
                "加微信领取免费福利",
                "代练上分 便宜 加我私聊",
                "免费领取皮肤 点击链接",
            },
            Normal = new List<string>
            {
                "老哥 组队打副本吗",
                "谢谢老板",
                "这个boss怎么打",
                "我来拉怪 你输出",
                "任务做完了吗",
                "加个好友吧",
                "有空再一起玩",
                "版本更新了 看看公告",
            },
        };
    }
}
