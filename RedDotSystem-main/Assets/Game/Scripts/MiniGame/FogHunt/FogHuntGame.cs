using System.Collections;
using System.Collections.Generic;
using Common.Network;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace Game.MiniGame.FogHunt
{
    /// <summary>
    /// 《迷雾信标》主流程：生成地图 → 回合推进 → 迷雾重算 → 守卫 AI → 判定胜负。
    /// 迷雾规则见 FogGrid；表现见 FogHuntView；UI 见 FogHuntHud。
    /// </summary>
    public sealed class FogHuntGame : MonoBehaviour
    {
        public struct Options
        {
            public int Width;
            public int Height;
            public int Seed;
            public int Beacons;
            public int Guards;
            public int PlayerSight;
            public int GuardSight;
            public int MaxTurns;
        }

        public const int StoneRange = 6;

        private const float DetectLimit = 5f;
        private const float GuardFovCos = 0.5f;
        private const int StoneNoiseRange = 8;
        private const float BaseOrtho = 7.6f;
        private const float IntroSeconds = 3f;

        private sealed class Guard
        {
            public Vector2Int Pos;
            public Vector2Int Facing = Vector2Int.right;
            public List<Vector2Int> Path;
            public int PathIndex;
            public Vector2Int? Noise;
            public bool Alerted;
        }

        private Options _opt;
        private TMP_FontAsset _font;
        private FogGrid _grid;
        private bool[] _isRoom;
        private FogHuntView _view;
        private FogHuntHud _hud;
        private FogHuntMinimap _minimap;

        private Vector2Int _player;
        private Vector2Int _exit;
        private Vector2Int _lastDir = Vector2Int.right;
        private int _turns;
        private int _collected;
        private float _detect;
        private float _detectPeak;
        private int _phase = 1;
        private float _detectMul = 1f;
        private bool _over;
        private bool _paused;
        private bool _crouch;
        private bool _stoneMode;
        private bool _started;
        private float _introAt;
        private Camera _camera;
        private Vector3 _camVelocity;

        private readonly List<Guard> _guards = new List<Guard>();
        private readonly List<Vector2Int> _beacons = new List<Vector2Int>();
        private readonly List<bool> _taken = new List<bool>();
        private readonly List<bool> _seen = new List<bool>();

        // ── 初始化 ─────────────────────────────────────────────

        public void Init(Options options, TMP_FontAsset font)
        {
            _opt = options;
            _font = font;
            if (_opt.Seed == 0) _opt.Seed = System.Environment.TickCount;

            BuildMap();
            BuildCamera();
            BuildView();
            BuildHud();

            _introAt = Time.unscaledTime;
            _hud.ShowIntro();
            ResolveTurn(false);
        }

        private void BuildMap()
        {
            var map = FogHuntMapGenerator.Generate(_opt.Width, _opt.Height, _opt.Seed, _opt.Beacons, _opt.Guards);
            _grid = map.Grid;
            _isRoom = map.IsRoom;
            _player = map.Start;
            _exit = map.Start;
            _beacons.AddRange(map.Beacons);

            _taken.Clear();
            _seen.Clear();
            for (int i = 0; i < _beacons.Count; i++)
            {
                _taken.Add(false);
                _seen.Add(false);
            }

            var rng = new System.Random(_opt.Seed * 31 + 7);
            for (int i = 0; i < map.Guards.Count; i++)
            {
                _guards.Add(new Guard
                {
                    Pos = map.Guards[i],
                    Path = BuildPatrolPath(map.Guards[i], rng),
                });
            }

            _minimap = new FogHuntMinimap();
            _minimap.Build(_grid, _isRoom);
        }

        private List<Vector2Int> BuildPatrolPath(Vector2Int origin, System.Random rng)
        {
            var path = new List<Vector2Int> { origin };
            var cursor = origin;
            for (int step = 0; step < 3; step++)
            {
                for (int tries = 0; tries < 60; tries++)
                {
                    var candidate = new Vector2Int(
                        origin.x + rng.Next(-4, 5),
                        origin.y + rng.Next(-4, 5));
                    if (!_grid.InBounds(candidate.x, candidate.y)) continue;
                    if (_grid.IsBlocked(candidate.x, candidate.y)) continue;
                    if (candidate == cursor) continue;
                    path.Add(candidate);
                    cursor = candidate;
                    break;
                }
            }
            return path;
        }

        private void BuildCamera()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                var go = new GameObject("FogHunt Camera");
                go.tag = "MainCamera";
                _camera = go.AddComponent<Camera>();
            }
            _camera.orthographic = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.020f, 0.027f, 0.043f, 1f);
            _camera.transform.rotation = Quaternion.identity;
            _camera.orthographicSize = BaseOrtho;
            _camera.transform.position = new Vector3(_player.x + 0.5f, _player.y + 0.5f, -10f);
        }

        private void BuildView()
        {
            var guardCells = new List<Vector2Int>(_guards.Count);
            for (int i = 0; i < _guards.Count; i++) guardCells.Add(_guards[i].Pos);

            _view = new FogHuntView();
            _view.Build(_grid, _isRoom, _opt.Seed, transform, _player, _exit, _beacons, guardCells);
        }

        private void BuildHud()
        {
            var hudGo = new GameObject("FogHuntHud");
            hudGo.transform.SetParent(transform, false);
            _hud = hudGo.AddComponent<FogHuntHud>();
            _hud.Init(this, _font, _minimap.Texture, Restart, ExitToHome);
        }

        private void OnDestroy()
        {
            _view?.Destroy();
            _view = null;
            _minimap?.Destroy();
            _minimap = null;
        }

        private static Vector3 CellCenter(Vector2Int cell) => new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0f);

        // ── 输入入口（键盘 + HUD 触屏按钮）────────────────────

        public void StartGame()
        {
            if (_started) return;
            _started = true;
            _hud.HideIntro();
            _hud.Refresh(BuildState());
        }

        public void UiMove(int dx, int dy)
        {
            if (!_started || _over || _paused) return;
            TryMove(dx, dy);
        }

        public void UiToggleCrouch()
        {
            if (!_started || _over || _paused) return;
            _crouch = !_crouch;
            _hud.Refresh(BuildState());
        }

        public void UiToggleStone()
        {
            if (!_started || _over || _paused) return;
            _stoneMode = !_stoneMode;
            _hud.Refresh(BuildState());
        }

        public void UiTogglePause()
        {
            if (!_started || _over) return;
            _paused = !_paused;
            _hud.SetPaused(_paused);
        }

        /// <summary>投石到指定格（HUD 触屏模式下由点击格子触发）。</summary>
        public void UiThrowStoneAt(Vector2Int cell)
        {
            if (!_started || _over || _paused) return;
            ThrowStone(cell);
        }

        public bool StoneMode => _stoneMode;
        public bool Paused => _paused;
        public bool Over => _over;
        public bool Started => _started;

        private void Update()
        {
            _view?.Tick(Time.time);

            if (!_started)
            {
                // 进门那一次点击会落在新场景的第一帧上，开场卡至少停留 0.6 秒才接受输入
                float elapsed = Time.unscaledTime - _introAt;
                if (elapsed >= IntroSeconds || (elapsed >= 0.6f && Input.anyKeyDown)) StartGame();
                return;
            }

            if (_over) return;

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                UiTogglePause();
                return;
            }
            if (_paused) return;

            if (Input.GetKeyDown(KeyCode.Q)) UiToggleCrouch();
            if (Input.GetKeyDown(KeyCode.E)) UiToggleStone();

            _view?.SetStonePreview(_stoneMode, _player,
                _stoneMode ? ScreenToCell(Input.mousePosition) : _player);

            if (_stoneMode && Input.GetMouseButtonDown(0))
            {
                bool overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                if (!overUi)
                {
                    ThrowStone(ScreenToCell(Input.mousePosition));
                    return;
                }
            }

            int dx = 0, dy = 0;
            if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)) dy = 1;
            else if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow)) dy = -1;
            else if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) dx = -1;
            else if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) dx = 1;

            if (dx != 0 || dy != 0) TryMove(dx, dy);
        }

        private void LateUpdate()
        {
            if (_camera == null) return;

            float targetSize = _stoneMode ? BaseOrtho * 1.08f : BaseOrtho;
            _camera.orthographicSize = Mathf.Lerp(_camera.orthographicSize, targetSize,
                1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));

            var target = new Vector3(_player.x + 0.5f, _player.y + 0.5f, -10f);
            _camera.transform.position = Vector3.SmoothDamp(_camera.transform.position, target,
                ref _camVelocity, 0.12f);
        }

        private Vector2Int ScreenToCell(Vector3 screen)
        {
            Vector3 world = _camera.ScreenToWorldPoint(screen);
            return new Vector2Int(Mathf.FloorToInt(world.x), Mathf.FloorToInt(world.y));
        }

        // ── 回合 ───────────────────────────────────────────────

        private void TryMove(int dx, int dy)
        {
            var target = new Vector2Int(_player.x + dx, _player.y + dy);
            if (!_grid.InBounds(target.x, target.y) || _grid.IsBlocked(target.x, target.y) || IsGuardAt(target))
            {
                _view?.BumpPlayer(new Vector2Int(dx, dy));
                return;
            }

            _lastDir = new Vector2Int(dx, dy);
            _player = target;
            _view?.PlayStep(_player);
            ResolveTurn(true);
        }

        private void ThrowStone(Vector2Int cell)
        {
            _stoneMode = false;
            if (!_grid.InBounds(cell.x, cell.y)) return;
            if (Manhattan(cell, _player) > StoneRange)
            {
                _hud.Refresh(BuildState());
                return;
            }

            for (int i = 0; i < _guards.Count; i++)
            {
                if (Manhattan(_guards[i].Pos, cell) <= StoneNoiseRange)
                    _guards[i].Noise = cell;
            }

            ResolveTurn(true);
        }

        private void ResolveTurn(bool guardsAct)
        {
            if (guardsAct) _turns++;

            if (guardsAct) GuardsAct();

            _grid.BeginTurn();
            _grid.RevealCircle(_player.x, _player.y, _opt.PlayerSight);

            UpdateDetection();
            SyncView();
            _hud.Refresh(BuildState());

            if (guardsAct) CheckEnd();
        }

        private void GuardsAct()
        {
            for (int i = 0; i < _guards.Count; i++)
            {
                var guard = _guards[i];
                Vector2Int target;

                if (guard.Noise.HasValue)
                {
                    target = guard.Noise.Value;
                    if (guard.Pos == target)
                    {
                        guard.Noise = null;
                        continue;
                    }
                }
                else
                {
                    if (guard.Path.Count == 0) continue;
                    target = guard.Path[guard.PathIndex];
                    if (guard.Pos == target)
                    {
                        guard.PathIndex = (guard.PathIndex + 1) % guard.Path.Count;
                        target = guard.Path[guard.PathIndex];
                    }
                }

                var next = StepToward(guard.Pos, target, i);
                if (next != guard.Pos)
                {
                    var delta = next - guard.Pos;
                    if (delta.x != 0) guard.Facing = new Vector2Int(delta.x > 0 ? 1 : -1, 0);
                    else if (delta.y != 0) guard.Facing = new Vector2Int(0, delta.y > 0 ? 1 : -1);
                    guard.Pos = next;
                }
            }
        }

        private Vector2Int StepToward(Vector2Int from, Vector2Int to, int selfIndex)
        {
            int dx = Mathf.Abs(to.x - from.x);
            int dy = Mathf.Abs(to.y - from.y);

            var candidates = new List<Vector2Int>(2);
            if (dx >= dy)
            {
                if (dx != 0) candidates.Add(new Vector2Int(from.x + (to.x > from.x ? 1 : -1), from.y));
                if (dy != 0) candidates.Add(new Vector2Int(from.x, from.y + (to.y > from.y ? 1 : -1)));
            }
            else
            {
                if (dy != 0) candidates.Add(new Vector2Int(from.x, from.y + (to.y > from.y ? 1 : -1)));
                if (dx != 0) candidates.Add(new Vector2Int(from.x + (to.x > from.x ? 1 : -1), from.y));
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!_grid.InBounds(c.x, c.y) || _grid.IsBlocked(c.x, c.y)) continue;
                if (c == _player) continue;
                if (IsGuardAt(c, selfIndex)) continue;
                return c;
            }
            return from;
        }

        private bool IsGuardAt(Vector2Int cell, int ignoreIndex = -1)
        {
            for (int i = 0; i < _guards.Count; i++)
            {
                if (i == ignoreIndex) continue;
                if (_guards[i].Pos == cell) return true;
            }
            return false;
        }

        // ── 视野与暴露 ─────────────────────────────────────────

        private int GuardSight => _opt.GuardSight + (_phase >= 2 ? 1 : 0);

        private void UpdateDetection()
        {
            CollectBeacons();

            bool anySeen = false;
            for (int i = 0; i < _guards.Count; i++)
            {
                bool sees = CanSeePlayer(_guards[i]);
                _guards[i].Alerted = sees;
                if (sees) anySeen = true;
            }

            if (anySeen) _detect = Mathf.Min(DetectLimit, _detect + (_crouch ? 0.5f : 1f) * _detectMul);
            else _detect = Mathf.Max(0f, _detect - 1f);

            _detectPeak = Mathf.Max(_detectPeak, _detect);
        }

        private bool CanSeePlayer(Guard guard)
        {
            if (_grid.IsCover(_player.x, _player.y) && Manhattan(guard.Pos, _player) > 1) return false;

            int dx = _player.x - guard.Pos.x;
            int dy = _player.y - guard.Pos.y;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len > GuardSight) return false;
            if (len > 0.001f)
            {
                float dot = (dx * guard.Facing.x + dy * guard.Facing.y) / len;
                if (dot < GuardFovCos) return false;
            }
            return _grid.HasLineOfSight(guard.Pos.x, guard.Pos.y, _player.x, _player.y);
        }

        private void CollectBeacons()
        {
            bool picked = false;
            for (int i = 0; i < _beacons.Count; i++)
            {
                if (_taken[i] || _beacons[i] != _player) continue;
                _taken[i] = true;
                _seen[i] = true;
                _collected++;
                picked = true;
                _view?.PlayPickup(_beacons[i]);
                _hud.FlashPickup();
            }

            if (!picked) return;

            if (_collected == 1 && _phase < 2)
            {
                _phase = 2;
                _detectMul = 1.25f;
                _hud.Toast("守卫察觉到了什么");
            }
            else if (_collected >= _beacons.Count && _phase < 3)
            {
                _phase = 3;
                _detectMul = 1.5f;
                for (int i = 0; i < _guards.Count; i++) _guards[i].Noise = _exit;
                _hud.Toast("带着信标回去");
            }
            else
            {
                _hud.Toast("信标 " + _collected + "/" + _beacons.Count);
            }
        }

        private void SyncView()
        {
            _view.SetPlayer(_player, _lastDir);
            _view.SetExit(_exit, _collected >= _beacons.Count);

            for (int i = 0; i < _beacons.Count; i++)
            {
                bool visible = !_taken[i] && _grid.IsVisible(_beacons[i].x, _beacons[i].y);
                if (visible) _seen[i] = true;
                _view.SetBeacon(i, _beacons[i], _taken[i], visible);
            }

            for (int i = 0; i < _guards.Count; i++)
            {
                var guard = _guards[i];
                bool visible = _grid.IsVisible(guard.Pos.x, guard.Pos.y);
                _view.SetGuard(i, guard.Pos, guard.Facing, visible, guard.Alerted, GuardSight);
            }

            _view.RefreshFog(_player);
            _minimap.Refresh(_player, _exit, _beacons, _taken, _seen);
        }

        // ── 结算 ───────────────────────────────────────────────

        private void CheckEnd()
        {
            if (_collected >= _beacons.Count && _player == _exit)
            {
                Finish(true);
                return;
            }
            if (_detect >= DetectLimit)
            {
                Finish(false);
                return;
            }
            if (_turns >= _opt.MaxTurns) Finish(false);
        }

        private void Finish(bool win)
        {
            _over = true;

            string rating = "C";
            int ratingScore = 100;
            if (win)
            {
                if (_turns <= 45 && _detect <= 0f) rating = "S";
                else if (_turns <= 70) rating = "A";
                else rating = "B";
                ratingScore = rating == "S" ? 1000 : rating == "A" ? 800 : 600;
            }

            int remainTurns = Mathf.Max(0, _opt.MaxTurns - _turns);
            int explored = ExplorePercent();
            long score = ratingScore * 100L + remainTurns + explored;

            _hud.ShowResult(win, rating, _turns, _collected, _beacons.Count, _detectPeak,
                "评级 " + ratingScore * 100 + " + 回合 " + remainTurns + " + 探索 " + explored + " = " + score,
                _phase);
            SubmitScore(score);
        }

        /// <summary>结算上报：分数越高越好，服务端按 challengeId=3 排行。</summary>
        private void SubmitScore(long score)
        {
            var network = ClientNetworkManager.Instance;
            if (network == null || network.MyPlayerId <= 0) return;

            network.ScoreSubmitResponseReceived += OnScoreSubmitResponse;
            _ = network.SendScoreSubmit(3, score);
        }

        private int ExplorePercent()
        {
            if (_grid == null) return 0;
            int explored = 0;
            for (int y = 0; y < _grid.Height; y++)
            for (int x = 0; x < _grid.Width; x++)
                if (_grid.IsExplored(x, y)) explored++;
            return Mathf.RoundToInt(explored * 100f / (_grid.Width * _grid.Height));
        }

        private void OnScoreSubmitResponse(ScoreSubmitResponse response)
        {
            var network = ClientNetworkManager.Instance;
            if (network != null) network.ScoreSubmitResponseReceived -= OnScoreSubmitResponse;
            if (response == null) return;

            _hud.SetRankInfo(response.Success
                ? (response.NewRecord ? "新纪录！" : "") + "当前排名 " + response.MyRank + "（最高 " + response.BestScore + "）"
                : "成绩上报失败：" + response.Message);
        }

        private void Restart()
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }

        private void ExitToHome()
        {
            SceneLoader.LoadAsync("zhuye").Forget();
        }

        // ── HUD 数据 ───────────────────────────────────────────

        private FogHuntHud.HudState BuildState()
        {
            return new FogHuntHud.HudState
            {
                Turn = _turns,
                MaxTurn = _opt.MaxTurns,
                Collected = _collected,
                Total = _beacons.Count,
                Phase = _phase,
                Detect = _detect,
                DetectMax = DetectLimit,
                Crouch = _crouch,
                Stone = _stoneMode,
                BeaconHint = BuildBeaconHint(),
            };
        }

        private string BuildBeaconHint()
        {
            int best = -1;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < _beacons.Count; i++)
            {
                if (_taken[i]) continue;
                int distance = Manhattan(_beacons[i], _player);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            if (best < 0 || bestDistance > 8) return string.Empty;
            return "信标 · " + DirectionOf(_beacons[best] - _player) + " " + bestDistance + " 格";
        }

        private static string DirectionOf(Vector2Int delta)
        {
            string vertical = delta.y > 0 ? "北" : delta.y < 0 ? "南" : string.Empty;
            string horizontal = delta.x > 0 ? "东" : delta.x < 0 ? "西" : string.Empty;
            if (vertical.Length > 0 && horizontal.Length > 0) return horizontal + vertical;
            if (vertical.Length > 0) return vertical;
            if (horizontal.Length > 0) return horizontal;
            return "脚下";
        }

        private static int Manhattan(Vector2Int a, Vector2Int b) =>
            Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
    }
}
