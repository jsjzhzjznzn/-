using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.MapGen
{
    /// <summary>
    /// 元气骑士式地牢的展示层：按房间类型给地面上色，画走廊与房门，
    /// 再按房间语义撒敌人（战斗房）与道具（宝箱/商店房）。算法侧看 RoguelikeGenerator。
    /// </summary>
    public class RoguelikeMapManager : MonoBehaviour
    {
        private const int CELL_SIZE = 8;
        private const int MAP_WIDTH = 80;
        private const int MAP_HEIGHT = 56;
        private const int SLOT_COLS = 5;
        private const int SLOT_ROWS = 4;

        [SerializeField] private RectTransform stage;
        [SerializeField] private InputField seedInput;
        [SerializeField] private Button randomSeedButton;
        [SerializeField] private Button regenerateButton;
        [SerializeField] private Text statusText;

        [Header("生成参数")]
        [SerializeField] private int seed = 1;
        [SerializeField] private int extraLinks = 3;
        [Tooltip("每个格子槽被跳过的概率，越高房间越少")]
        [SerializeField] private float roomSkipChance = 0.28f;

        [Header("配色")]
        [SerializeField] private Color wallColor = new Color(0.07f, 0.07f, 0.09f);
        [SerializeField] private Color corridorColor = new Color(0.36f, 0.33f, 0.30f);
        [SerializeField] private Color normalColor = new Color(0.60f, 0.54f, 0.46f);
        [SerializeField] private Color startColor = new Color(0.35f, 0.62f, 0.40f);
        [SerializeField] private Color treasureColor = new Color(0.85f, 0.74f, 0.35f);
        [SerializeField] private Color shopColor = new Color(0.40f, 0.52f, 0.78f);
        [SerializeField] private Color bossColor = new Color(0.66f, 0.30f, 0.32f);
        [SerializeField] private Color doorColor = new Color(0.30f, 0.75f, 0.80f);
        [SerializeField] private Color enemyColor = new Color(0.88f, 0.24f, 0.20f);
        [SerializeField] private Color itemColor = new Color(0.98f, 0.86f, 0.30f);

        private RectTransform tileMap;
        private TileRectPainter floorLayer;
        private TileRectPainter overlayLayer;

        private RoguelikeResult result;
        private readonly List<Vector2Int> enemyCells = new List<Vector2Int>();
        private readonly List<Vector2Int> itemCells = new List<Vector2Int>();

        public int LastRoomCount => result != null ? result.Rooms.Count : 0;
        public int LastLinkCount => result != null ? result.Links.Count : 0;
        public IReadOnlyList<Vector2Int> EnemyCells => enemyCells;
        public IReadOnlyList<Vector2Int> ItemCells => itemCells;
        public RoguelikeResult Result => result;

        private void Start()
        {
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
                    if (seedInput != null) seedInput.text = seed.ToString();
                    Generate();
                });
            }
        }

        private void BuildCanvas()
        {
            if (tileMap == null)
            {
                var go = new GameObject("RoguelikeMap");
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

            floorLayer = EnsureLayer("RoguelikeFloor", tileMap, uiLayer, floorLayer);
            overlayLayer = EnsureLayer("RoguelikeOverlay", tileMap, uiLayer, overlayLayer);
        }

        private TileRectPainter EnsureLayer(string name, RectTransform parent, int uiLayer, TileRectPainter existing)
        {
            if (existing != null)
            {
                return existing;
            }

            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            if (uiLayer >= 0)
            {
                go.layer = uiLayer;
            }
            go.AddComponent<CanvasRenderer>();
            return go.AddComponent<TileRectPainter>();
        }

        /// <summary>生成一张新地牢并画出来</summary>
        public void Generate()
        {
            result = RoguelikeGenerator.Generate(
                MAP_WIDTH, MAP_HEIGHT, SLOT_COLS, SLOT_ROWS, seed, roomSkipChance, extraLinks, 2);

            if (result.Rooms.Count < 2)
            {
                floorLayer.Clear();
                floorLayer.Commit();
                overlayLayer.Clear();
                overlayLayer.Commit();
                SetStatus($"Seed {seed} · 房间过少（{result.Rooms.Count}），请换一个 Seed");
                return;
            }

            DrawMap();
            PlaceActors(seed);

            int[] hops = result.RoomHops();
            SetStatus($"Seed {seed} · 房间 {result.Rooms.Count}（通道 {result.Links.Count} 条）· 起始房→BOSS房 {hops[result.BossRoomIndex]} 跳 · 敌人 {enemyCells.Count} · 道具 {itemCells.Count}");
        }

        private void DrawMap()
        {
            floorLayer.Clear();

            for (int y = 0; y < MAP_HEIGHT; y++)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    float px = x * CELL_SIZE;
                    float py = y * CELL_SIZE;

                    if (result.IsDoor(x, y))
                    {
                        // 房门：走廊与房间交界处，单独给个醒目的青色
                        floorLayer.AddRect(px, py, CELL_SIZE, CELL_SIZE, doorColor);
                        continue;
                    }

                    if (!result.IsFloor(x, y))
                    {
                        continue;   // 墙体由底板承担，不逐格画
                    }

                    int roomIndex = result.RoomAt(x, y);
                    Color color;
                    if (roomIndex < 0)
                    {
                        color = corridorColor;
                    }
                    else
                    {
                        color = ColorForType(result.Rooms[roomIndex].Type);
                    }
                    floorLayer.AddRect(px, py, CELL_SIZE, CELL_SIZE, color);
                }
            }

            floorLayer.Commit();
        }

        private Color ColorForType(RoguelikeRoomType type)
        {
            switch (type)
            {
                case RoguelikeRoomType.Start: return startColor;
                case RoguelikeRoomType.Treasure: return treasureColor;
                case RoguelikeRoomType.Shop: return shopColor;
                case RoguelikeRoomType.Boss: return bossColor;
                default: return normalColor;
            }
        }

        /// <summary>
        /// 按房间语义撒内容：战斗房撒敌人、宝箱房撒道具堆、商店房撒货架、BOSS 房一个大标记。
        /// 房间内的点仍由最佳候选生成，保证同一房间里的敌人不会挤在一处。
        /// </summary>
        private void PlaceActors(int runSeed)
        {
            overlayLayer.Clear();
            enemyCells.Clear();
            itemCells.Clear();

            foreach (var room in result.Rooms)
            {
                switch (room.Type)
                {
                    case RoguelikeRoomType.Start:
                        break;      // 起始房空着

                    case RoguelikeRoomType.Boss:
                        PlaceBoss(room);
                        break;

                    case RoguelikeRoomType.Treasure:
                        PlaceInRoom(room, Mathf.Max(2, room.Width * room.Height / 26), false, true);
                        break;

                    case RoguelikeRoomType.Shop:
                        PlaceInRoom(room, Mathf.Max(2, room.Width / 3), false, true);
                        break;

                    default:
                        PlaceInRoom(room, Mathf.Max(1, room.Width * room.Height / 16), true, false);
                        break;
                }
            }

            overlayLayer.Commit();
        }

        private void PlaceBoss(RoguelikeRoom room)
        {
            var marker = new Vector2Int(room.CenterX, room.CenterY);
            enemyCells.Add(marker);
            DrawMarker(marker, 14f, new Color(0.55f, 0.10f, 0.12f), true);

            // BOSS 房角落再放两个道具
            PlaceInRoom(room, 2, false, true, true);
        }

        private void PlaceInRoom(RoguelikeRoom room, int count, bool enemies, bool items, bool skipCenter = false)
        {
            if (count <= 0) return;

            var points = BestCandidate.Generate(
                room.Width, room.Height, count,
                p => result.IsFloor(room.X + Mathf.FloorToInt(p.x), room.Y + Mathf.FloorToInt(p.y)));

            foreach (var p in points)
            {
                var cell = new Vector2Int(room.X + Mathf.FloorToInt(p.x), room.Y + Mathf.FloorToInt(p.y));
                if (skipCenter && cell.x == room.CenterX && cell.y == room.CenterY) continue;

                if (enemies)
                {
                    enemyCells.Add(cell);
                    DrawMarker(cell, 8f, enemyColor, true);
                }
                else if (items)
                {
                    itemCells.Add(cell);
                    DrawMarker(cell, 7f, itemColor, false);
                }
            }
        }

        private void DrawMarker(Vector2Int cell, float size, Color color, bool outline)
        {
            float cx = cell.x * CELL_SIZE + CELL_SIZE * 0.5f;
            float cy = cell.y * CELL_SIZE + CELL_SIZE * 0.5f;
            if (outline)
            {
                overlayLayer.AddRect(cx - size / 2f - 1f, cy - size / 2f - 1f, size + 2f, size + 2f, Color.white);
            }
            overlayLayer.AddRect(cx - size / 2f, cy - size / 2f, size, size, color);
        }

        private void SetStatus(string text)
        {
            if (statusText != null)
            {
                statusText.text = text;
            }
        }

        /// <summary>调试用：字符画。S 起始 / B BOSS / T 宝箱 / P 商店 / . 战斗房 / + 走廊 / # 墙</summary>
        public string ToAscii()
        {
            if (result == null) return "";
            var sb = new System.Text.StringBuilder();
            for (int y = MAP_HEIGHT - 1; y >= 0; y--)
            {
                for (int x = 0; x < MAP_WIDTH; x++)
                {
                    int ri = result.RoomAt(x, y);
                    if (ri < 0)
                    {
                        sb.Append(result.IsFloor(x, y) ? '+' : '#');
                    }
                    else
                    {
                        switch (result.Rooms[ri].Type)
                        {
                            case RoguelikeRoomType.Start: sb.Append('S'); break;
                            case RoguelikeRoomType.Boss: sb.Append('B'); break;
                            case RoguelikeRoomType.Treasure: sb.Append('T'); break;
                            case RoguelikeRoomType.Shop: sb.Append('P'); break;
                            default: sb.Append('.'); break;
                        }
                    }
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>从起始房洪水填充能到达的房间数，用于验收连通性</summary>
        public int CountReachableRooms()
        {
            if (result == null || result.Rooms.Count == 0) return 0;

            var start = result.Rooms[result.StartRoomIndex];
            var visited = new bool[MAP_WIDTH * MAP_HEIGHT];
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(new Vector2Int(start.CenterX, start.CenterY));
            visited[start.CenterX + start.CenterY * MAP_WIDTH] = true;

            var dirs = new[] { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0) };
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var d in dirs)
                {
                    int nx = c.x + d.x;
                    int ny = c.y + d.y;
                    if (!result.IsFloor(nx, ny)) continue;
                    int i = nx + ny * MAP_WIDTH;
                    if (visited[i]) continue;
                    visited[i] = true;
                    queue.Enqueue(new Vector2Int(nx, ny));
                }
            }

            int reachable = 0;
            foreach (var room in result.Rooms)
            {
                if (visited[room.CenterX + room.CenterY * MAP_WIDTH]) reachable++;
            }
            return reachable;
        }
    }
}