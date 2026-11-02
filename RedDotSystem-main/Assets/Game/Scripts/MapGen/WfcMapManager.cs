using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Game.MapGen
{
    /// <summary>
    /// WFC 结构化地图生成器：按邻接约束把房间/走廊/路口拼成一张连通的地牢图，
    /// 再用最佳候选在可通行区域撒敌人与道具。与噪声方案互补：噪声决定「长什么样」，WFC 保证「走得通」。
    /// </summary>
    public class WfcMapManager : MonoBehaviour
    {
        private const int CELL_SIZE = 16;
        private const int MAP_WIDTH = 32;
        private const int MAP_HEIGHT = 20;

        [SerializeField] private RectTransform stage;
        [SerializeField] private Button regenerateButton;
        [SerializeField] private InputField seedInput;
        [SerializeField] private Button randomSeedButton;
        [SerializeField] private Text statusText;

        [Header("生成参数")]
        [SerializeField] private int seed = 1;
        [SerializeField] private int maxAttempts = 20;
        [SerializeField] private bool allowBacktrack = true;
        [SerializeField] private int enemyCount = 12;
        [SerializeField] private int itemCount = 18;

        [Header("配色")]
        [SerializeField] private Color solidColor = new Color(0.10f, 0.10f, 0.12f);
        [SerializeField] private Color enemyColor = new Color(0.85f, 0.28f, 0.24f);
        [SerializeField] private Color itemColor = new Color(0.95f, 0.80f, 0.30f);
        [SerializeField] private Color waterColor = new Color(0.16f, 0.32f, 0.48f);

        [Header("地貌（噪声层）")]
        [Tooltip("关闭则整张图都是可建区，退化为纯 WFC 地牢")]
        [SerializeField] private bool useTerrainMask = true;
        [SerializeField] private int terrainSeed = 7;
        [SerializeField] private float terrainScale = 8f;
        [SerializeField] private int terrainOctaves = 4;
        [SerializeField] private float terrainPersistance = 0.5f;
        [SerializeField] private float terrainLacunarity = 2f;
        [Tooltip("噪声值低于该阈值的格子视为水域，不可建造")]
        [SerializeField] private float waterThreshold = 0.35f;
        [Tooltip("陆地少于该比例时自动降低水位重算，避免整张图被水吞掉")]
        [SerializeField] private float minLandRatio = 0.45f;
        [Tooltip("修剪孤岛后至少要有这么多可通行格，否则继续降低水位")]
        [SerializeField] private int minWalkableCells = 140;

        private RectTransform tileMap;
        private readonly List<TileRectPainter> chunks = new List<TileRectPainter>();
        private TileRectPainter overlayLayer;

        private List<WfcTile> tileset;
        private int[] resultTiles;
        private float[,] terrainNoise;
        /// <summary>本次生成实际使用的水位（自适应后的值）</summary>
        private float effectiveWaterThreshold;
        private bool[] buildableMask;
        private int walkBeforeRetry;
        private List<Vector2Int> enemyCells = new List<Vector2Int>();
        private List<Vector2Int> itemCells = new List<Vector2Int>();

        private static int ChunkCellCount => TileRectPainter.MaxVerticesPerMesh / 4;

        public int LastAttempts { get; private set; }
        public bool LastSucceeded { get; private set; }
        public IReadOnlyList<Vector2Int> EnemyCells => enemyCells;
        public IReadOnlyList<Vector2Int> ItemCells => itemCells;
        public int Width => MAP_WIDTH;
        public int Height => MAP_HEIGHT;

        private void Start()
        {
            tileset = WfcTileset.Create();
            BuildCanvas();
            BindControls();
            Generate();
        }

        private void BindControls()
        {
            if (regenerateButton != null)
            {
                regenerateButton.onClick.AddListener(Generate);
            }
            if (seedInput != null)
            {
                seedInput.text = seed.ToString();
                seedInput.onEndEdit.AddListener(text =>
                {
                    if (!int.TryParse(text, out int v))
                    {
                        seedInput.text = seed.ToString();
                        return;
                    }
                    seed = v;
                    Generate();
                });
            }
            if (randomSeedButton != null)
            {
                randomSeedButton.onClick.AddListener(() =>
                {
                    seed = Random.Range(0, 100000);
                    // 地貌种子一起换，否则地图轮廓永远一样、只有内部结构在变
                    terrainSeed = Random.Range(0, 100000);
                    if (seedInput != null) seedInput.text = seed.ToString();
                    Generate();
                });
            }
        }

        private void BuildCanvas()
        {
            if (tileMap == null)
            {
                var go = new GameObject("WfcTileMap");
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(stage != null ? stage : transform as RectTransform, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(MAP_WIDTH * CELL_SIZE, MAP_HEIGHT * CELL_SIZE);
                tileMap = rt;
            }

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0)
            {
                tileMap.gameObject.layer = uiLayer;
            }

            int totalCells = MAP_WIDTH * MAP_HEIGHT;
            int need = Mathf.CeilToInt(totalCells / (float)ChunkCellCount);
            for (int i = 0; i < need; i++)
            {
                if (i < chunks.Count)
                {
                    continue;
                }
                var go = new GameObject("WfcChunk_" + i, typeof(RectTransform));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(tileMap, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                if (uiLayer >= 0) go.layer = uiLayer;
                go.AddComponent<CanvasRenderer>();
                chunks.Add(go.AddComponent<TileRectPainter>());
            }

            if (overlayLayer == null)
            {
                var go = new GameObject("WfcOverlay", typeof(RectTransform));
                var rt = go.GetComponent<RectTransform>();
                rt.SetParent(tileMap, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                if (uiLayer >= 0) go.layer = uiLayer;
                go.AddComponent<CanvasRenderer>();
                overlayLayer = go.AddComponent<TileRectPainter>();
            }
        }

        /// <summary>生成一张新地图：噪声定建区 → WFC 拼结构 → 最佳候选撒内容</summary>
        public void Generate()
        {
            // 第一层：噪声决定哪里是水（不可建）、哪里是陆地
            bool[] buildable = BuildTerrainMask();
            buildableMask = buildable;

            // 第二层：只在陆地上做 WFC 拼接，水域格直接成为墙。
            // WFC 的碰撞结果同样有随机性：偶尔会把陆地拆得很碎，此时换一个子种子重拼，
            // 比单纯降低水位有效（水位只影响陆地总量，碎不碎是拼接造成的）。
            WfcSolver solver = null;
            int attempts = 0;
            for (int pass = 0; pass < 8; pass++)
            {
                int localSeed = seed + pass * 7919;
                solver = new WfcSolver(MAP_WIDTH, MAP_HEIGHT, tileset, localSeed, buildableMask);
                LastSucceeded = solver.Solve(maxAttempts, allowBacktrack, out attempts);
                LastAttempts = attempts;
                if (!LastSucceeded)
                {
                    break;
                }

                CollectTiles(solver);
                TrimDisconnectedRegions();
                walkBeforeRetry = CountWalkable();
                if (walkBeforeRetry >= minWalkableCells)
                {
                    break;
                }

                // 陆地本身就少：整体降一档水位再试
                if (pass % 2 == 1)
                {
                    LowerWaterOneStep();
                    RebuildMaskFromNoise();
                }
            }

            if (!LastSucceeded)
            {
                ClearAll();
                SetStatus($"生成失败（尝试 {attempts} 次），可换一个 Seed 或关闭回溯");
                return;
            }

            CollectTiles(solver);

            // WFC 只保证「局部邻接合法」，不保证全局连通：孤立的小空腔会被填回墙体
            int filled = TrimDisconnectedRegions();
            DrawTiles();
            PlaceActors();
            SetStatus($"Seed {seed} · 尝试 {attempts} 次 · 水域 {CountWater()} 格 · 可通行 {CountWalkable()} · 敌人 {enemyCells.Count} · 道具 {itemCells.Count}" +
                      (filled > 0 ? $" · 填平孤岛 {filled} 格" : ""));
        }

        /// <summary>把求解结果搬进 resultTiles</summary>
        private void CollectTiles(WfcSolver solver)
        {
            resultTiles = new int[MAP_WIDTH * MAP_HEIGHT];
            for (int y = 0; y < MAP_HEIGHT; y++)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    resultTiles[x + y * MAP_WIDTH] = solver.GetTileIndex(x, y);
                }
            }
        }

        /// <summary>水位整体下调一档</summary>
        private void LowerWaterOneStep()
        {
            effectiveWaterThreshold -= 0.04f;
        }

        /// <summary>按当前水位重算建区掩码</summary>
        private bool[] RebuildMaskFromNoise()
        {
            var mask = new bool[MAP_WIDTH * MAP_HEIGHT];
            if (terrainNoise == null)
            {
                for (int i = 0; i < mask.Length; i++) mask[i] = true;
                buildableMask = mask;
                return mask;
            }
            for (int y = 0; y < MAP_HEIGHT; y++)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    mask[x + y * MAP_WIDTH] = terrainNoise[x, y] >= effectiveWaterThreshold;
                }
            }
            buildableMask = mask;
            return mask;
        }

        /// <summary>
        /// 第一层：用柏林噪声给每个格子判定「陆地 / 水域」，返回建区掩码。
        /// 噪声负责大尺度地貌，WFC 只在其上拼建筑结构，二者在这里对接。
        /// </summary>
        private bool[] BuildTerrainMask()
        {
            var mask = new bool[MAP_WIDTH * MAP_HEIGHT];
            if (!useTerrainMask)
            {
                for (int i = 0; i < mask.Length; i++) mask[i] = true;
                terrainNoise = null;
                return mask;
            }

            terrainNoise = NoiseMapGenerator.GenerateNoiseMap(
                MAP_WIDTH, MAP_HEIGHT, terrainSeed, terrainScale,
                terrainOctaves, terrainPersistance, terrainLacunarity, 0f, 0f);

            // 噪声天生波动：同一个种子也可能整图偏水。陆地不足时逐步降低水位再算。
            // 用局部变量，不回写配置字段，保证每次生成的起点一致。
            float threshold = waterThreshold;
            for (int pass = 0; pass < 8; pass++)
            {
                int land = 0;
                for (int y = 0; y < MAP_HEIGHT; y++)
                    for (int x = 0; x < MAP_WIDTH; x++)
                        if (terrainNoise[x, y] >= threshold) land++;

                if (land >= minLandRatio * mask.Length)
                {
                    break;
                }
                threshold -= 0.04f;
            }
            effectiveWaterThreshold = threshold;

            for (int y = 0; y < MAP_HEIGHT; y++)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    mask[x + y * MAP_WIDTH] = terrainNoise[x, y] >= effectiveWaterThreshold;
                }
            }
            return mask;
        }

        /// <summary>某个格子是不是水域（噪声层判定），供断言与绘制使用</summary>
        public bool IsWater(int x, int y)
        {
            if (terrainNoise == null) return false;
            if (x < 0 || y < 0 || x >= MAP_WIDTH || y >= MAP_HEIGHT) return false;
            return terrainNoise[x, y] < effectiveWaterThreshold;
        }

        /// <summary>水域格数量</summary>
        public int CountWater()
        {
            if (terrainNoise == null) return 0;
            int n = 0;
            for (int y = 0; y < MAP_HEIGHT; y++)
                for (int x = 0; x < MAP_WIDTH; x++)
                    if (terrainNoise[x, y] < effectiveWaterThreshold) n++;
            return n;
        }

        /// <summary>
        /// 把所有可通行格按四连通分块，只保留面积最大的一块，其余填成墙体。
        /// WFC 只约束相邻格是否合法，不保证全局只有一个连通的空腔。
        /// 返回被填平的格子数。
        /// </summary>
        private int TrimDisconnectedRegions()
        {
            int total = MAP_WIDTH * MAP_HEIGHT;
            var region = new int[total];
            for (int i = 0; i < total; i++) region[i] = -1;

            var sizes = new List<int>();
            var queue = new Queue<int>();
            var sides = new[] { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0) };

            for (int start = 0; start < total; start++)
            {
                if (region[start] >= 0 || !IsWalkableIndex(start)) continue;

                int id = sizes.Count;
                int size = 0;
                region[start] = id;
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    size++;
                    int cx = cur % MAP_WIDTH;
                    int cy = cur / MAP_WIDTH;
                    foreach (var d in sides)
                    {
                        int nx = cx + d.x;
                        int ny = cy + d.y;
                        if (nx < 0 || ny < 0 || nx >= MAP_WIDTH || ny >= MAP_HEIGHT) continue;
                        int ni = nx + ny * MAP_WIDTH;
                        if (region[ni] >= 0 || !IsWalkableIndex(ni)) continue;
                        region[ni] = id;
                        queue.Enqueue(ni);
                    }
                }
                sizes.Add(size);
            }

            if (sizes.Count <= 1)
            {
                return 0;
            }

            // 保留最大的一块，其余填成墙
            int keep = 0;
            for (int i = 1; i < sizes.Count; i++)
            {
                if (sizes[i] > sizes[keep]) keep = i;
            }

            int filled = 0;
            for (int i = 0; i < total; i++)
            {
                if (region[i] >= 0 && region[i] != keep)
                {
                    resultTiles[i] = WfcTileset.Solid;
                    filled++;
                }
            }
            return filled;
        }

        private bool IsWalkableIndex(int index)
        {
            int idx = resultTiles[index];
            return idx >= 0 && WfcTileset.IsWalkable(tileset[idx]);
        }

        private void DrawTiles()
        {
            foreach (var c in chunks)
            {
                c.Clear();
            }

            var chunk = chunks[0];
            for (int y = 0; y < MAP_HEIGHT; y++)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    int idx = resultTiles[x + y * MAP_WIDTH];
                    Color color;
                    if (IsWater(x, y))
                    {
                        // 噪声判定的水域：画成水色，与「可建造但没被选中」的墙区分开
                        color = waterColor;
                    }
                    else
                    {
                        color = idx == WfcTileset.Solid ? solidColor : tileset[idx].Color;
                    }
                    chunk.AddRect(x * CELL_SIZE, y * CELL_SIZE, CELL_SIZE, CELL_SIZE, color);
                }
            }
            chunk.Commit();
        }

        /// <summary>在可通行格里用最佳候选撒敌人与道具，二者各自均匀且互不重叠</summary>
        private void PlaceActors()
        {
            overlayLayer.Clear();
            enemyCells.Clear();
            itemCells.Clear();

            var candidates = new List<Vector2Int>();
            for (int y = 0; y < MAP_HEIGHT; y++)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    int idx = resultTiles[x + y * MAP_WIDTH];
                    if (idx >= 0 && WfcTileset.IsWalkable(tileset[idx]))
                    {
                        candidates.Add(new Vector2Int(x, y));
                    }
                }
            }
            if (candidates.Count == 0)
            {
                overlayLayer.Commit();
                return;
            }

            // 最佳候选按整张网格撒点，再用可通行格过滤
            var points = BestCandidate.Generate(
                MAP_WIDTH, MAP_HEIGHT, enemyCount + itemCount,
                p =>
                {
                    int x = Mathf.FloorToInt(p.x);
                    int y = Mathf.FloorToInt(p.y);
                    int idx = x + y * MAP_WIDTH;
                    return idx >= 0 && idx < resultTiles.Length && resultTiles[idx] >= 0
                           && WfcTileset.IsWalkable(tileset[resultTiles[idx]]);
                });

            for (int i = 0; i < points.Length; i++)
            {
                var cell = new Vector2Int(Mathf.FloorToInt(points[i].x), Mathf.FloorToInt(points[i].y));
                bool isEnemy = i % 2 == 0 && enemyCells.Count < enemyCount;
                if (!isEnemy && itemCells.Count >= itemCount)
                {
                    isEnemy = true;
                }
                if (isEnemy && enemyCells.Count >= enemyCount)
                {
                    continue;
                }

                float cx = cell.x * CELL_SIZE + CELL_SIZE * 0.5f;
                float cy = cell.y * CELL_SIZE + CELL_SIZE * 0.5f;

                if (isEnemy)
                {
                    enemyCells.Add(cell);
                    overlayLayer.AddRect(cx - 5f, cy - 5f, 10f, 10f, Color.white);
                    overlayLayer.AddRect(cx - 4f, cy - 4f, 8f, 8f, enemyColor);
                }
                else
                {
                    itemCells.Add(cell);
                    overlayLayer.AddRect(cx - 4f, cy - 4f, 8f, 8f, itemColor);
                }
            }
            overlayLayer.Commit();
        }

        private void ClearAll()
        {
            foreach (var c in chunks) c.Clear();
            overlayLayer.Clear();
            overlayLayer.Commit();
            resultTiles = null;
        }

        private void SetStatus(string text)
        {
            if (statusText != null)
            {
                statusText.text = text;
            }
        }

        /// <summary>调试用：把当前地图导成字符画（# 实心，其余为通道）</summary>
        public string ToAscii()
        {
            if (resultTiles == null) return "";
            var sb = new StringBuilder();
            for (int y = MAP_HEIGHT - 1; y >= 0; y--)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    sb.Append(resultTiles[x + y * MAP_WIDTH] == WfcTileset.Solid ? '#' : '.');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>当前地图可通行格的连通块检查，用于验收「WFC 保证连通」</summary>
        public int CountReachable(int startX, int startY)
        {
            if (resultTiles == null) return 0;
            bool IsWalk(int x, int y)
            {
                if (x < 0 || y < 0 || x >= MAP_WIDTH || y >= MAP_HEIGHT) return false;
                int idx = resultTiles[x + y * MAP_WIDTH];
                return idx >= 0 && WfcTileset.IsWalkable(tileset[idx]);
            }

            if (!IsWalk(startX, startY)) return 0;

            var visited = new bool[MAP_WIDTH * MAP_HEIGHT];
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(new Vector2Int(startX, startY));
            visited[startX + startY * MAP_WIDTH] = true;
            int count = 0;

            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                count++;
                for (int d = 0; d < 4; d++)
                {
                    int nx = c.x + (d == 1 ? 1 : d == 3 ? -1 : 0);
                    int ny = c.y + (d == 0 ? 1 : d == 2 ? -1 : 0);
                    if (!IsWalk(nx, ny)) continue;
                    int i = nx + ny * MAP_WIDTH;
                    if (visited[i]) continue;
                    visited[i] = true;
                    queue.Enqueue(new Vector2Int(nx, ny));
                }
            }
            return count;
        }

        /// <summary>可通行格总数</summary>
        public int CountWalkable()
        {
            if (resultTiles == null) return 0;
            int n = 0;
            for (int i = 0; i < resultTiles.Length; i++)
            {
                if (resultTiles[i] >= 0 && WfcTileset.IsWalkable(tileset[resultTiles[i]])) n++;
            }
            return n;
        }

        /// <summary>取某格模块名，用于断言</summary>
        public string GetTileName(int x, int y)
        {
            if (resultTiles == null) return null;
            int idx = resultTiles[x + y * MAP_WIDTH];
            return idx < 0 ? null : tileset[idx].Name;
        }
    }
}
