using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
namespace Game.MapGen
{
    /// <summary>
    /// 柏林噪声地图生成：噪声值按阈值映射成七种地貌，逐格画到 Graphics 上；滑杆实时调参、按钮打印参数。
    /// 与 pcg-master 的 Script/Map/MapManager.ts 对应，只把 Cocos 的节点创建换成场景里预先搭好的 Stage / Slider。
    /// </summary>
    public class MapManager : MonoBehaviour
    {
        private const int TILE_WIDTH = 4;
        private const int TILE_HEIGHT = 4;
        private const int TILE_MAP_WIDTH = 960;
        private const int TILE_MAP_HEIGHT = 640;
        private const int TILE_WIDTH_COUNT = TILE_MAP_WIDTH / TILE_WIDTH;
        private const int TILE_HEIGHT_COUNT = TILE_MAP_HEIGHT / TILE_HEIGHT;

        private enum TileEnum
        {
            Dirt,   // 污垢
            Forest, // 森林
            Grass,  // 草地
            Soil,   // 土壤
            Sand,   // 沙滩
            Sea,    // 海洋
            Vortex, // 涡流
        }

        [SerializeField] private RectTransform stage;
        [SerializeField] private RectTransform tileMap;

        [Header("地貌颜色")]
        [SerializeField] private Color dirt = new Color(0x4D / 255f, 0x3D / 255f, 0x35 / 255f);
        [SerializeField] private Color forest = new Color(0x3D / 255f, 0x61 / 255f, 0x12 / 255f);
        [SerializeField] private Color grass = new Color(0x51 / 255f, 0x8A / 255f, 0x14 / 255f);
        [SerializeField] private Color soil = new Color(0xB2 / 255f, 0x82 / 255f, 0x4E / 255f);
        [SerializeField] private Color sand = new Color(0xE5 / 255f, 0xD8 / 255f, 0xB8 / 255f);
        [SerializeField] private Color sea = new Color(0x00 / 255f, 0xA0 / 255f, 0xEE / 255f);
        [SerializeField] private Color vortex = new Color(0x00 / 255f, 0x63 / 255f, 0xC7 / 255f);

        [Header("地图参数")]
        [SerializeField] private int seed = 1;
        [SerializeField] private float scale = 40;
        [SerializeField] private int octaves = 5;
        [SerializeField] private float persistance = 0.5f;
        [SerializeField] private float lacunarity = 2;
        [SerializeField] private float offsetX = 0;
        [SerializeField] private float offsetY = 0;

        [Header("交互")]
        [SerializeField] private Button saveDataButton;
        [SerializeField] private InputField seedInput;
        [SerializeField] private Button randomSeedButton;

        [Header("资源点")]
        [SerializeField] private int resourceCount = 100;
        [SerializeField] private Color resourceColor = new Color(0xA6 / 255f, 0x6F / 255f, 0xE8 / 255f);

        // 阈值从高到低排列：噪声值 >= value 的第一项即该格地貌
        private readonly float[] thresholdValues = { 0.85f, 0.6f, 0.5f, 0.45f, 0.4f, 0.15f, 0f };
        private readonly TileEnum[] thresholdTypes =
        {
            TileEnum.Dirt, TileEnum.Forest, TileEnum.Grass, TileEnum.Soil,
            TileEnum.Sand, TileEnum.Sea, TileEnum.Vortex,
        };
        private readonly Color[] thresholdColors = new Color[7];

        private TileRectPainter painter;
        private readonly List<TileRectPainter> tileChunks = new List<TileRectPainter>();
        private static readonly int ChunkCellCount = TileRectPainter.MaxVerticesPerMesh / 4;
        private static readonly int ChunkRowCount = Mathf.Max(1, ChunkCellCount / TILE_HEIGHT_COUNT);
        private float[,] noiseMap;
        private TileEnum[,] tiles;
        private Vector2[] resourcePoints = new Vector2[0];
        private TileRectPainter resourceLayer;
        private bool resourceRebuildPending;

        private void Awake()
        {
            thresholdColors[(int)TileEnum.Dirt] = dirt;
            thresholdColors[(int)TileEnum.Forest] = forest;
            thresholdColors[(int)TileEnum.Grass] = grass;
            thresholdColors[(int)TileEnum.Soil] = soil;
            thresholdColors[(int)TileEnum.Sand] = sand;
            thresholdColors[(int)TileEnum.Sea] = sea;
            thresholdColors[(int)TileEnum.Vortex] = vortex;
        }

        private void Start()
        {
            GenerateNode();
            GenerateMap();
            BindSliders();
            BindSeedControls();
            if (saveDataButton != null)
            {
                saveDataButton.onClick.AddListener(SaveData);
            }
        }

        /// <summary>
        /// Seed 输入框：手填数字回车即换图；随机按钮每次摇一个新 seed
        /// </summary>
        private void BindSeedControls()
        {
            if (seedInput != null)
            {
                seedInput.text = seed.ToString();
                seedInput.onEndEdit.AddListener(OnSeedInputEndEdit);
            }

            if (randomSeedButton != null)
            {
                randomSeedButton.onClick.AddListener(RandomizeSeed);
            }
        }

        private void OnSeedInputEndEdit(string text)
        {
            if (!int.TryParse(text, out int value))
            {
                // 输入不是整数时回填当前值，避免界面与地图状态不一致
                seedInput.text = seed.ToString();
                return;
            }

            seed = value;
            GenerateMap();
        }

        private void RandomizeSeed()
        {
            seed = UnityEngine.Random.Range(0, 100000);
            if (seedInput != null)
            {
                seedInput.text = seed.ToString();
            }
            GenerateMap();
        }

        private void GenerateNode()
        {
            if (tileMap == null)
            {
                var go = new GameObject("TileMap");
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(stage != null ? stage : transform as RectTransform, false);
                rt.sizeDelta = new Vector2(TILE_MAP_WIDTH, TILE_MAP_HEIGHT);
                // 坐标以画布中心为原点，与 fillRect 的居中算法一致
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                tileMap = rt;
            }

            // 程序建出来的对象默认是 Default 层，框架 UI 相机只渲染 UI 层
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0)
            {
                tileMap.gameObject.layer = uiLayer;
            }

            painter = tileMap.GetComponent<TileRectPainter>();
            if (painter == null)
            {
                // 本工程 uGUI 分叉的 Graphic 基类不声明 RequireComponent(CanvasRenderer)，程序化添加时要自己补
                if (tileMap.GetComponent<CanvasRenderer>() == null)
                {
                    tileMap.gameObject.AddComponent<CanvasRenderer>();
                }
                            painter = tileMap.gameObject.AddComponent<TileRectPainter>();
                    }

                    painter.Clear();
                    EnsureTileChunks();
                }

                /// <summary>
                /// uGUI 单个网格最多 65000 个顶点（每格 4 个），所以地图按块拆成若干张同尺寸子画布叠加提交，
                /// 全部用完仍未满一张时不留多余画布。
                /// </summary>
                private void EnsureTileChunks()
                {
                    int totalCells = TILE_WIDTH_COUNT * TILE_HEIGHT_COUNT;
                    int needCount = Mathf.CeilToInt(totalCells / (float)ChunkCellCount);

                    int uiLayer = LayerMask.NameToLayer("UI");
                    for (int i = 0; i < needCount; i++)
                    {
                        TileRectPainter chunk;
                        if (i == 0)
                        {
                            chunk = painter;
                        }
                        else
                        {
                            if (i < tileChunks.Count)
                            {
                                chunk = tileChunks[i];
                            }
                            else
                            {
                                var go = new GameObject("TileMap_" + i, typeof(RectTransform));
                                var rt = go.GetComponent<RectTransform>();
                                rt.SetParent(tileMap, false);
                                rt.anchorMin = Vector2.zero;
                                rt.anchorMax = Vector2.one;
                                rt.offsetMin = Vector2.zero;
                                rt.offsetMax = Vector2.zero;
                                if (uiLayer >= 0)
                                {
                                    go.layer = uiLayer;
                                }
                                go.AddComponent<CanvasRenderer>();
                                chunk = go.AddComponent<TileRectPainter>();
                                tileChunks.Add(chunk);
                            }
                        }
                        chunk.Clear();
                    }

                    for (int i = needCount; i < tileChunks.Count; i++)
                    {
                        tileChunks[i].Clear();
                        tileChunks[i].gameObject.SetActive(false);
                    }
                }

        private void BindSliders()
        {
            // 场景里的滑杆按名字找，name 与参数一一对应（与 pcg-master 一致）
            foreach (var slider in FindObjectsOfType<Slider>())
            {
                slider.onValueChanged.AddListener(_ => OnSliderChange(slider));
            }
        }

        private void GenerateMap()
        {
            GenerateNoise();
            GenerateTile();
            GenerateResource();
        }

        /// <summary>
        /// 拖动滑杆时的重生成：立即重画地貌，资源点留到下一次采样再算。
        /// 最佳候选是 O(已选点 × 候选数) 的重计算，跟着每一个滑杆回调跑会让拖动明显卡顿。
        /// </summary>
        private void GenerateMapImmediate()
        {
            GenerateNoise();
            GenerateTile();

            if (resourceRebuildPending)
            {
                return;
            }
            resourceRebuildPending = true;
            StartCoroutine(ResourceRebuildNextFrame());
        }

        private System.Collections.IEnumerator ResourceRebuildNextFrame()
        {
            yield return null;
            yield return null;
            resourceRebuildPending = false;
            GenerateResource();
        }

        private void GenerateNoise()
        {
            noiseMap = NoiseMapGenerator.GenerateNoiseMap(
                TILE_WIDTH_COUNT,
                TILE_HEIGHT_COUNT,
                seed,
                scale,
                octaves,
                persistance,
                lacunarity,
                offsetX,
                offsetY);
        }

        private void GenerateTile()
        {
            painter.Clear();
            tiles = new TileEnum[TILE_WIDTH_COUNT, TILE_HEIGHT_COUNT];

            // 按行切成若干块，每块负责一段连续的行（每张画布顶点数不超过 MaxVerticesPerMesh）
            for (int chunk = 0; chunk < tileChunks.Count; chunk++)
            {
                int rowStart = chunk * ChunkRowCount;
                int rowEnd = Mathf.Min(rowStart + ChunkRowCount, TILE_WIDTH_COUNT);
                var chunkPainter = tileChunks[chunk];
                chunkPainter.Clear();
                if (rowStart >= rowEnd)
                {
                    chunkPainter.Commit();
                    continue;
                }

                for (int x = rowStart; x < rowEnd; x++)
                {
                    for (int y = 0; y < TILE_HEIGHT_COUNT; y++)
                    {
                        float noiseValue = noiseMap[x, y];
                        int target = -1;
                        for (int i = 0; i < thresholdValues.Length; i++)
                        {
                            if (noiseValue >= thresholdValues[i])
                            {
                                target = i;
                                break;
                            }
                        }
                        if (target < 0)
                        {
                            continue;
                        }

                        TileEnum type = thresholdTypes[target];
                        tiles[x, y] = type;

                        float posX = TILE_WIDTH * x - TILE_MAP_WIDTH / 2f;
                        float posY = TILE_HEIGHT * y - TILE_MAP_HEIGHT / 2f;
                        chunkPainter.AddRect(posX, posY, TILE_WIDTH, TILE_HEIGHT, thresholdColors[(int)type]);
                    }
                }
                chunkPainter.Commit();
            }
        }

        /// <summary>
        /// 在陆地上用米切尔最佳候选撒资源点：先排除海洋/涡流，再用最佳候选保证点之间尽量均匀。
        /// 资源点单独占一张画布，否则追加到已满 65000 顶点的地形画布会整张都不渲染。
        /// </summary>
        private void GenerateResource()
        {
            EnsureResourceLayer();
            resourceLayer.Clear();

            resourcePoints = BestCandidate.Generate(
                TILE_WIDTH_COUNT,
                TILE_HEIGHT_COUNT,
                resourceCount,
                p => IsLand(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)));

            foreach (var p in resourcePoints)
            {
                // 以点为中心画一个 12×12 的资源格，外加 1px 白色描边，缩小截图里也认得出来
                float centerX = TILE_WIDTH * p.x - TILE_MAP_WIDTH / 2f + TILE_WIDTH * 0.5f;
                float centerY = TILE_HEIGHT * p.y - TILE_MAP_HEIGHT / 2f + TILE_HEIGHT * 0.5f;
                float size = TILE_WIDTH * 3f;
                resourceLayer.AddRect(centerX - size / 2f - 1f, centerY - size / 2f - 1f, size + 2f, size + 2f, Color.white);
                resourceLayer.AddRect(centerX - size / 2f, centerY - size / 2f, size, size, resourceColor);
            }
            resourceLayer.Commit();
        }

        /// <summary>资源点画布：与地形画布同尺寸、挂在最后（最上层），只建一次</summary>
        private void EnsureResourceLayer()
        {
            if (resourceLayer != null)
            {
                return;
            }

            var go = new GameObject("TileMap_Resource", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(tileMap, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0)
            {
                go.layer = uiLayer;
            }
            go.AddComponent<CanvasRenderer>();
            resourceLayer = go.AddComponent<TileRectPainter>();
        }

        /// <summary>森林、草地、土壤、沙滩、污垢算可放置的陆地，海洋与涡流不可</summary>
        private bool IsLand(int x, int y)
        {
            if (tiles == null || x < 0 || y < 0 || x >= TILE_WIDTH_COUNT || y >= TILE_HEIGHT_COUNT)
            {
                return false;
            }
            return tiles[x, y] != TileEnum.Sea && tiles[x, y] != TileEnum.Vortex;
        }

        /// <summary>当前资源点数量与坐标，供断言/调试使用</summary>
        public int ResourceCount => resourcePoints.Length;
        public IReadOnlyList<Vector2> ResourcePoints => resourcePoints;

        private void OnSliderChange(Slider slider)
        {
            float value = slider.value;
            switch (slider.name)
            {
                case "Scale":
                    scale = Mathf.Lerp(10, 100, value);
                    break;
                case "Octaves":
                    octaves = (int)Mathf.Lerp(3, 7, value);
                    break;
                case "Persistance":
                    persistance = Mathf.Lerp(0.4f, 1, value);
                    break;
                case "Lacunarity":
                    lacunarity = Mathf.Lerp(2, 6, value);
                    break;
                case "OffsetX":
                    offsetX = value;
                    break;
                case "OffsetY":
                    offsetY = value;
                    break;
                default:
                    return;
            }

            GenerateMapImmediate();
        }

        private void SaveData()
        {
            var builder = new StringBuilder(noiseMap.Length * 6);
            builder.Append("{\"seed\":").Append(seed);
            builder.Append(",\"width\":").Append(TILE_WIDTH_COUNT);
            builder.Append(",\"height\":").Append(TILE_HEIGHT_COUNT);
            builder.Append(",\"scale\":").Append(scale.ToString("0.###"));
            builder.Append(",\"octaves\":").Append(octaves);
            builder.Append(",\"persistance\":").Append(persistance.ToString("0.###"));
            builder.Append(",\"lacunarity\":").Append(lacunarity.ToString("0.###"));
            builder.Append(",\"offsetX\":").Append(offsetX.ToString("0.###"));
            builder.Append(",\"offsetY\":").Append(offsetY.ToString("0.###"));
            builder.Append(",\"noiseMap\":[");
            for (int x = 0; x < TILE_WIDTH_COUNT; x++)
            {
                if (x > 0) builder.Append(',');
                builder.Append('[');
                for (int y = 0; y < TILE_HEIGHT_COUNT; y++)
                {
                    if (y > 0) builder.Append(',');
                    builder.Append(noiseMap[x, y].ToString("0.###"));
                }
                builder.Append(']');
            }
            builder.Append("]}");
            Debug.Log(builder.ToString());
        }

        /// <summary>当前参数快照，便于脚本/自动化直接读取（与 SaveData 打印的字段一致）</summary>
        public string GetParameterJson()
        {
            return $"{{\"seed\":{seed},\"scale\":{scale:0.###},\"octaves\":{octaves}," +
                   $"\"persistance\":{persistance:0.###},\"lacunarity\":{lacunarity:0.###}," +
                   $"\"offsetX\":{offsetX:0.###},\"offsetY\":{offsetY:0.###}}}";
        }

        /// <summary>按地标索引取地貌名，用于断言/调试</summary>
        public string GetTerrain(int x, int y)
        {
            if (tiles == null || x < 0 || y < 0 || x >= TILE_WIDTH_COUNT || y >= TILE_HEIGHT_COUNT)
            {
                return null;
            }
            return tiles[x, y].ToString();
        }
    }
}
