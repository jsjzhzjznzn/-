// 命名空间：CompeteServer.Manager
// 服务端各单例/工具管理器所在命名空间
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace CompeteServer.Manager
{
    /// <summary>
    /// 违禁词过滤器（聊天文本打码）
    /// 服务端权威：消息落库前先打码，回执 / 推送 / Mongo 历史 / 控制台日志里全部是打码后文本——
    /// 改包客户端也无法看到原词；客户端乐观消息先原样显示，Echo 回执到达后被现有校准逻辑替换成打码文本
    /// 词库来自 Config/banned_words.json，启动时加载一次（改词重启生效）；无词库 = 不打码、消息原样通过
    ///
    /// 匹配在"折叠文本"上做，折叠顺序：单字符兼容折算 → 同形字表 → 剥干扰符 → 逐字符小写。
    /// 于是 全角ｆｕｃｋ / 带圈ⓕⓤⒸⓚ / "傻 逼" / "傻.逼" / 西里尔 ѕ 写的 sb 都能命中同一词形
    ///
    /// 自动机：加载时把词库编成 Aho-Corasick，正文只扫一遍（O(正文长 + 命中数)，与词数无关）
    /// 线程模型：会话消息在线程池并发处理——快照不可变 + Volatile 原子发布，读侧一次取引用；
    /// 工作缓冲按线程复用（ThreadStatic），无命中的常规路径零堆分配
    /// </summary>
    public static class BannedWordFilter
    {
        /// <summary>打码字符：命中几个原文字符就打几个星，总长度不变（前面的长度校验因此依旧成立）</summary>
        private const char StarChar = '*';

        // ──────────────────────────────
        // 词库 JSON 模型（字段名大小写不敏感，便于手改文件）
        // ──────────────────────────────

        private sealed class BannedWordConfig
        {
            /// <summary>违禁词表</summary>
            public List<string> BannedWords { get; set; } = new();

            /// <summary>干扰符表：折叠后被剥掉，拦"傻 逼""傻.逼"这类插字符绕过</summary>
            public List<string> InterferenceChars { get; set; } = new();

            /// <summary>同形字表：Unicode 正规化折不成拉丁的西里尔/希腊字母，只能点名映射（如 { "From": "ѕ", "To": "s" }）</summary>
            public List<HomoglyphEntry> Homoglyphs { get; set; } = new();
        }

        private sealed class HomoglyphEntry
        {
            public string From { get; set; } = string.Empty;

            public string To { get; set; } = string.Empty;
        }

        // ──────────────────────────────
        // 不可变匹配快照（原子引用发布）
        // ──────────────────────────────

        private sealed class WordSnapshot
        {
            /// <summary>词库自动机；null = 词库为空，不过滤</summary>
            public AhoAutomaton? Automaton { get; init; }

            /// <summary>干扰字符集（已含小写与全角折算形态）</summary>
            public HashSet<char> Interference { get; init; } = new();

            /// <summary>同形字映射（键 = 原文字符）</summary>
            public Dictionary<char, string> Folds { get; init; } = new();
        }

        /// <summary>当前快照（原子发布）</summary>
        private static WordSnapshot snapshot = new();

        /// <summary>当前词库词数（启动横幅 / 排查用）</summary>
        public static int WordCount => Volatile.Read(ref snapshot).Automaton?.PatternCount ?? 0;

        // ──────────────────────────────
        // 按线程复用的工作缓冲（线程池线程会长期存活，容量取历史最大消息即可，不额外回收）
        // ──────────────────────────────

        [ThreadStatic] private static StringBuilder? foldedBuffer;

        [ThreadStatic] private static List<int>? indexBuffer;

        [ThreadStatic] private static bool[]? maskFlags;

        [ThreadStatic] private static char[]? resultChars;

        // ──────────────────────────────
        // 加载
        // ──────────────────────────────

        /// <summary>
        /// 加载词库的方法（启动时调用一次）
        /// 文件不存在时先写一份带示例词的初始词库（幂等种子，便于手工接着改）
        /// </summary>
        /// <param name="jsonPath">参数名：jsonPath，类型：string，用途：词库 JSON 文件路径（Config/banned_words.json）</param>
        public static void Load(string jsonPath)
        {
            if (!File.Exists(jsonPath))
            {
                string? directory = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                BannedWordConfig seed = BuildSeed();
                // UnsafeRelaxedJsonEscaping：中文按字面写出（默认会全部转义成 \uXXXX，手改词库没法看）
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(seed, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                Console.WriteLine($"违禁词词库不存在，已生成初始词库: {jsonPath}");
            }

            BannedWordConfig? config = JsonSerializer.Deserialize<BannedWordConfig>(
                File.ReadAllText(jsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            config ??= new BannedWordConfig();

            HashSet<char> interference = BuildInterferenceSet(config.InterferenceChars);
            Dictionary<char, string> folds = BuildFoldTable(config.Homoglyphs);

            // 违禁词与正文走同一套折叠，保证"词怎么写都能命中"
            StringBuilder wordBuffer = new(32);
            List<string> words = new();
            foreach (string? word in config.BannedWords)
            {
                if (string.IsNullOrEmpty(word)) continue;
                wordBuffer.Clear();
                Fold(wordBuffer, word, null, interference, folds);
                string foldedWord = wordBuffer.ToString();
                if (foldedWord.Length > 0 && !words.Contains(foldedWord)) words.Add(foldedWord);
            }

            AhoAutomaton? automaton = null;
            if (words.Count > 0)
            {
                automaton = new AhoAutomaton();
                foreach (string foldedWord in words) automaton.Add(foldedWord);
                automaton.Build();
            }

            Volatile.Write(ref snapshot, new WordSnapshot
            {
                Automaton = automaton,
                Interference = interference,
                Folds = folds,
            });
            Console.WriteLine($"违禁词词库加载完成: {automaton?.PatternCount ?? 0} 个词，干扰符 {interference.Count} 个，" +
                              $"同形字 {folds.Count} 条" + (automaton == null ? "（词库为空，不过滤）" : "，已编成 Aho-Corasick") + $"（{jsonPath}）");
        }

        /// <summary>初始词库（示例）：干扰符含空格与常见分隔符，同形字含西里尔/希腊常用假冒位</summary>
        private static BannedWordConfig BuildSeed() => new()
        {
            BannedWords = new List<string> { "傻逼", "妈的", "fuck", "shit", "沙比", "nmsl" },
            InterferenceChars = new List<string> { " ", ".", "-", "_", "*", "#", "@" },
            Homoglyphs = new List<HomoglyphEntry>
            {
                new() { From = "ѕ", To = "s" },   // 西里尔 small dze
                new() { From = "с", To = "c" },   // 西里尔 es
                new() { From = "а", To = "a" },   // 西里尔 a
                new() { From = "е", To = "e" },   // 西里尔 ie
                new() { From = "о", To = "o" },   // 西里尔 o
                new() { From = "р", To = "p" },   // 西里尔 er
                new() { From = "х", To = "x" },   // 西里尔 ha
                new() { From = "м", To = "m" },   // 西里尔 em
                new() { From = "у", To = "y" },   // 西里尔 u
                new() { From = "і", To = "i" },   // 西里尔 Ukrainian i
                new() { From = "ν", To = "v" },   // 希腊 nu
                new() { From = "α", To = "a" },   // 希腊 alpha
            },
        };

        /// <summary>干扰符表：每条配置只认第一个字符（写多字符属于笔误，取首字符避免语义含混）</summary>
        private static HashSet<char> BuildInterferenceSet(List<string>? entries)
        {
            HashSet<char> interference = new();
            foreach (string? text in entries ?? new List<string>())
            {
                if (string.IsNullOrEmpty(text)) continue;
                char raw = text[0];
                interference.Add(raw);
                interference.Add(char.ToLowerInvariant(raw));
                // 配置里写全角"＿"也要能吃掉半角"_"：干扰符同样按全角折算一次
                if (raw >= 0xFF01 && raw <= 0xFF5E) interference.Add((char)(raw - 0xFEE0));
            }
            return interference;
        }

        /// <summary>同形字表：To 为空串 = 这个字符完全作废（装饰符）；表中条目优先于兼容分解</summary>
        private static Dictionary<char, string> BuildFoldTable(List<HomoglyphEntry>? entries)
        {
            Dictionary<char, string> folds = new();
            foreach (HomoglyphEntry? entry in entries ?? new List<HomoglyphEntry>())
            {
                if (entry == null || string.IsNullOrEmpty(entry.From)) continue;
                folds[entry.From[0]] = entry.To;
            }
            return folds;
        }

        // ──────────────────────────────
        // 打码
        // ──────────────────────────────

        /// <summary>
        /// 打码的方法
        /// 先在折叠文本上扫出命中，再把折叠区间映射回原文整段替换成 *：
        /// 命中几个原文字符就打几个星、区间内的干扰符一并打码（避免漏出词结构），总长度与原文一致
        /// </summary>
        /// <param name="content">参数名：content，类型：string，用途：聊天正文（已通过长度校验）</param>
        /// <returns>返回值类型：string，用途：打码后文本；无命中返回原文同一实例（零分配）</returns>
        public static string Mask(string content)
        {
            WordSnapshot current = Volatile.Read(ref snapshot);
            AhoAutomaton? automaton = current.Automaton;
            if (automaton == null || string.IsNullOrEmpty(content)) return content;

            StringBuilder folded = foldedBuffer ??= new StringBuilder(256);
            List<int> indexMap = indexBuffer ??= new List<int>(256);
            folded.Clear();
            indexMap.Clear();
            Fold(folded, content, indexMap, current.Interference, current.Folds);
            if (folded.Length == 0) return content;

            bool[] masked = TakeFlags(content.Length);
            if (!automaton.CollectHits(folded, indexMap, masked)) return content;

            char[] result = TakeChars(content.Length);
            content.CopyTo(0, result, 0, content.Length);
            for (int i = 0; i < content.Length; i++)
            {
                if (masked[i]) result[i] = StarChar;
            }
            return new string(result, 0, content.Length);
        }

        /// <summary>取命中标记表：新建即全 false，复用才需要清（清零与扫描同阶，不做增量清理）</summary>
        private static bool[] TakeFlags(int count)
        {
            bool[]? flags = maskFlags;
            if (flags == null || flags.Length < count)
            {
                flags = new bool[Math.Max(count, 256)];
                maskFlags = flags;
                return flags;
            }
            Array.Clear(flags, 0, count);
            return flags;
        }

        /// <summary>取结果字符缓冲（每次都会被整段覆写，无需清零）</summary>
        private static char[] TakeChars(int count)
        {
            char[]? chars = resultChars;
            if (chars == null || chars.Length < count)
            {
                chars = new char[Math.Max(count, 256)];
                resultChars = chars;
            }
            return chars;
        }

        // ──────────────────────────────
        // 折叠
        // ──────────────────────────────

        /// <summary>
        /// 折叠文本的对外入口（供同进程的其它文本分析器复用，如二级贝叶斯审核）
        /// 两级必须看到同一词形：绕过手法（全角 / 同形字 / 干扰符）在折叠层统一失效，而不是各写一套规则
        /// 只做折叠不做匹配；折叠缓冲由调用方提供并复用（需要原文位置映射时传 indexMap）
        /// </summary>
        /// <param name="text">参数名：text，类型：string，用途：待折叠的原文本</param>
        /// <param name="into">参数名：into，类型：StringBuilder，用途：折叠结果输出（调用方负责 Clear）</param>
        /// <param name="indexMap">参数名：indexMap，类型：List&lt;int&gt;?，用途：折叠索引→原文索引（不需要映射时传 null）</param>
        public static void FoldText(string text, StringBuilder into, List<int>? indexMap = null)
        {
            WordSnapshot current = Volatile.Read(ref snapshot);
            Fold(into, text, indexMap, current.Interference, current.Folds);
        }

        /// <summary>
        /// 折叠文本的方法：逐字符兼容折算 + 同形字映射 → 剥干扰符 → 逐字符小写
        /// 一个原文字符可以折出多个字符（ⓕ→f、ﬁ→fi），所以 indexMap 按"每个产出字符"追加原索引，
        /// 折叠索引 → 原文索引 的映射始终成立，打码位置才不会错位
        /// 注意：只对可疑区段做兼容分解——逐字符调 Normalize 会让每条中文消息白白分配上百个临时 string
        /// </summary>
        /// <param name="into">参数名：into，类型：StringBuilder，用途：折叠结果输出（调用方负责 Clear）</param>
        /// <param name="indexMap">参数名：indexMap，类型：List&lt;int&gt;?，用途：折叠索引→原文索引（量词时传 null）</param>
        private static void Fold(StringBuilder into, string text, List<int>? indexMap,
            HashSet<char> interference, Dictionary<char, string> folds)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (TryFoldChar(c, folds, out string? foldedText))
                {
                    // 折出的每个字符都要记回同一个原文位置，命中区间才能整段（含分隔符）打码
                    for (int k = 0; k < foldedText.Length; k++) Emit(into, indexMap, i, foldedText[k], interference);
                }
                else
                {
                    Emit(into, indexMap, i, c, interference);
                }
            }
        }

        /// <summary>落一个字符：干扰符在此丢弃（折叠之后判，才吃得到全角空格折算出的半角空格），再逐字符小写</summary>
        private static void Emit(StringBuilder into, List<int>? indexMap, int sourceIndex, char c, HashSet<char> interference)
        {
            char lowered = char.ToLowerInvariant(c);
            if (interference.Contains(lowered)) return;
            into.Append(lowered);
            indexMap?.Add(sourceIndex);
        }

        /// <summary>
        /// 单字符兼容折算的方法：返回 true = 折成了另一段文本（空串 = 整字符作废）
        /// 覆盖三类绕过：全角 ASCII（U+FF01–FF5E）、全角空格、带圈/连字/罗马数字等兼容字符；
        /// 同形字表在此之后、兼容分解之前查——表是运营点名，优先于自动分解
        /// </summary>
        private static bool TryFoldChar(char c, Dictionary<char, string> folds,
            [NotNullWhen(true)] out string? foldedText)
        {
            foldedText = null;

            // 半角 ASCII 原样（占绝大多数流量，不做任何查表与分配）
            if (c >= '!' && c <= '~') return false;

            // 全角 → 半角（算术折算比查表快，且这一段没有例外）
            if (c >= 0xFF01 && c <= 0xFF5E)
            {
                foldedText = ((char)(c - 0xFEE0)).ToString();
                return true;
            }
            if (c == 0x3000)
            {
                foldedText = " ";
                return true;
            }

            if (folds.TryGetValue(c, out string? mapped))
            {
                foldedText = mapped;
                return true;
            }

            // 可疑区段才走 Unicode 兼容分解（带圈字母数字 ①ⓕ、拉丁连字 ﬁﬂ、罗马数字、兼容标点）
            if ((c >= 0x2460 && c <= 0x24FF) ||
                (c >= 0xFB00 && c <= 0xFB17) ||
                (c >= 0x2150 && c <= 0x218F) ||
                (c >= 0x3248 && c <= 0x32FF) ||
                (c >= 0xFE30 && c <= 0xFE4F))
            {
                string source = c.ToString();
                string decomposed = source.Normalize(NormalizationForm.FormKD);
                if (decomposed != source)
                {
                    foldedText = decomposed;
                    return true;
                }
            }

            return false;
        }

        // ──────────────────────────────
        // Aho-Corasick（加载时构建，构建后只读；字符集不限，故邻接用字典而非定长数组）
        // ──────────────────────────────

        private sealed class AhoAutomaton
        {
            private readonly List<Dictionary<char, int>> children = new();

            private readonly List<int> fail = new();

            /// <summary>以此节点为尾的词长（0 = 不是词尾）</summary>
            private readonly List<int> ownLength = new();

            /// <summary>fail 链上最近的词尾节点（-1 = 无）：命中后沿它继续收同位置的其它词，不必回退重扫</summary>
            private readonly List<int> dictSuffix = new();

            /// <summary>已登记的模式数（同一词重复登记只算一个）</summary>
            public int PatternCount { get; private set; }

            public AhoAutomaton()
            {
                AddNode();   // 根节点
            }

            private int AddNode()
            {
                children.Add(new Dictionary<char, int>());
                fail.Add(0);
                ownLength.Add(0);
                dictSuffix.Add(-1);
                return children.Count - 1;
            }

            /// <summary>插入一个（已折叠的）词</summary>
            public void Add(string word)
            {
                int node = 0;
                for (int i = 0; i < word.Length; i++)
                {
                    if (!children[node].TryGetValue(word[i], out int next))
                    {
                        next = AddNode();
                        children[node][word[i]] = next;
                    }
                    node = next;
                }
                if (ownLength[node] == 0) PatternCount++;
                ownLength[node] = word.Length;
            }

            /// <summary>BFS 建 fail 与词尾后缀链（必须在全部 Add 之后调用一次）</summary>
            public void Build()
            {
                Queue<int> queue = new();
                foreach (int depthOne in children[0].Values)
                {
                    fail[depthOne] = 0;
                    queue.Enqueue(depthOne);
                }

                while (queue.Count > 0)
                {
                    int node = queue.Dequeue();
                    foreach ((char edge, int next) in children[node])
                    {
                        int f = fail[node];
                        while (f != 0 && !children[f].ContainsKey(edge)) f = fail[f];
                        if (f != node && children[f].TryGetValue(edge, out int candidate)) fail[next] = candidate;

                        int failTarget = fail[next];
                        dictSuffix[next] = ownLength[failTarget] > 0 ? failTarget : dictSuffix[failTarget];
                        queue.Enqueue(next);
                    }
                }
            }

            /// <summary>
            /// 扫一遍折叠文本，把命中映射回原文并置标记；返回是否存在命中
            /// 复杂度 O(折叠文本长 + 命中数)，与词数无关（旧实现是每个词各扫一遍正文）
            /// </summary>
            public bool CollectHits(StringBuilder text, List<int> indexMap, bool[] masked)
            {
                int node = 0;
                bool anyHit = false;

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    while (node != 0 && !children[node].ContainsKey(c)) node = fail[node];
                    if (!children[node].TryGetValue(c, out int next)) continue;
                    node = next;

                    for (int tail = node; tail > 0; tail = dictSuffix[tail])
                    {
                        int length = ownLength[tail];
                        if (length == 0) continue;

                        // 折叠区间 [i-length+1, i] → 原文区间 [map[起], map[止]]，整段（含夹在中间的分隔符）打码
                        int from = indexMap[i - length + 1];
                        int to = indexMap[i];
                        for (int k = from; k <= to; k++) masked[k] = true;
                        anyHit = true;
                    }
                }

                return anyHit;
            }
        }
    }
}
