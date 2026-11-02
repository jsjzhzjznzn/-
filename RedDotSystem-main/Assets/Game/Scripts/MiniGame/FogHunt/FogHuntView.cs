using System.Collections.Generic;
using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>
    /// 表现层：地形、迷雾遮罩、提灯、单位与光效。
    /// 只负责好看；判定永远读 FogGrid（见 FogGrid / FogHuntGame）。
    /// </summary>
    public sealed class FogHuntView
    {
        private const float MoveDuration = 0.12f;
        private const float LampRadius = 6.5f;

        private sealed class Tween
        {
            public Transform Target;
            public Vector3 From;
            public Vector3 To;
            public float Start;
            public float Duration;
        }

        private sealed class Fx
        {
            public Transform Transform;
            public SpriteRenderer Renderer;
            public Vector3 From;
            public Vector3 Velocity;
            public Vector3 FromScale;
            public Vector3 ToScale;
            public float Start;
            public float Duration;
            public float Alpha;
        }

        private sealed class UnitView
        {
            public Transform Root;
            public SpriteRenderer Shadow;
            public SpriteRenderer Body;
            public SpriteRenderer Extra;
        }

        private static readonly Color LampColor = new Color(1f, 0.74f, 0.38f, 0.14f);
        private static readonly Color GuardIdle = new Color(1f, 0.38f, 0.34f, 0.16f);
        private static readonly Color GuardAlert = new Color(1f, 0.28f, 0.22f, 0.30f);

        private Transform _root;
        private FogHuntFogMask _fog;
        private Texture2D _terrainTexture;

        private SpriteRenderer _lamp;
        private UnitView _player;
        private SpriteRenderer _playerFacing;
        private UnitView _exit;
        private SpriteRenderer _stoneRing;
        private SpriteRenderer _stoneCursor;

        private readonly List<UnitView> _beacons = new List<UnitView>();
        private readonly List<UnitView> _guards = new List<UnitView>();
        private readonly List<Tween> _tweens = new List<Tween>();
        private readonly List<Fx> _fx = new List<Fx>();
        private readonly List<Texture2D> _ownedTexture = new List<Texture2D>();
        private readonly List<Sprite> _ownedSprite = new List<Sprite>();

        private Sprite _coneSprite;
        private Sprite _ringSprite;
        private Sprite _dotSprite;
        private Vector3 _bumpDir;
        private float _bumpUntil;
        private bool _exitHighlighted;

        public void Build(FogGrid grid, bool[] isRoom, int seed, Transform parent, Vector2Int playerStart,
            Vector2Int exit, List<Vector2Int> beacons, List<Vector2Int> guards)
        {
            _root = new GameObject("FogHuntWorld").transform;
            _root.SetParent(parent, false);

            BuildTerrain(grid, isRoom, seed);
            _fog = new FogHuntFogMask();
            _fog.Build(grid, _root);

            BuildLamp(playerStart);
            BuildPlayer(playerStart);
            BuildExit(exit);
            BuildBeacons(beacons);
            BuildGuards(guards);
            BuildStonePreview();
        }

        // ── 构建 ───────────────────────────────────────────────

        private void BuildTerrain(FogGrid grid, bool[] isRoom, int seed)
        {
            _terrainTexture = FogHuntTerrain.Build(grid, isRoom, seed);
            _ownedTexture.Add(_terrainTexture);

            int tw = grid.Width * FogHuntTerrain.CellSize;
            int th = grid.Height * FogHuntTerrain.CellSize;
            var sprite = Sprite.Create(_terrainTexture, new Rect(0, 0, tw, th), Vector2.zero,
                FogHuntTerrain.CellSize, 0, SpriteMeshType.FullRect);
            _ownedSprite.Add(sprite);
            NewRenderer("FogHuntTerrain", sprite, 0, _root);
        }

        private void BuildLamp(Vector2Int cell)
        {
            var sprite = Track(FogHuntArt.Radial(Color.white, 128));
            _lamp = NewRenderer("Lamp", sprite, 30, _root);
            _lamp.color = LampColor;
            _lamp.transform.position = CellCenter(cell);
            _lamp.transform.localScale = Vector3.one * LampRadius;
        }

        private void BuildPlayer(Vector2Int cell)
        {
            _player = NewUnit("Player", Track(FogHuntArt.Circle(new Color(0.36f, 0.86f, 1f), 48)),
                0.62f, 12, true);
            _player.Root.position = CellCenter(cell);

            _playerFacing = NewRenderer("PlayerFacing",
                Track(FogHuntArt.Triangle(new Color(0.72f, 0.96f, 1f), 24)), 13, _player.Root);
            _playerFacing.transform.localScale = new Vector3(0.30f, 0.34f, 1f);
        }

        private void BuildExit(Vector2Int cell)
        {
            var sprite = Track(FogHuntArt.RingOutline(new Color(0.55f, 1f, 0.62f, 0.95f), 96, 4f));
            _exit = new UnitView { Root = new GameObject("Exit").transform };
            _exit.Root.SetParent(_root, false);
            _exit.Root.position = CellCenter(cell);
            _exit.Body = NewRenderer("ExitRing", sprite, 11, _exit.Root);
            _exit.Body.transform.localScale = Vector3.one * 1.5f;
            _exit.Shadow = NewRenderer("ExitGlow", Track(FogHuntArt.Radial(Color.white, 64)), 10, _exit.Root);
            _exit.Shadow.color = new Color(0.45f, 1f, 0.6f, 0.10f);
            _exit.Shadow.transform.localScale = Vector3.one * 2.2f;
        }

        private void BuildBeacons(List<Vector2Int> cells)
        {
            var bodySprite = Track(FogHuntArt.Diamond(new Color(1f, 0.82f, 0.28f), 48));
            var pillarSprite = Track(FogHuntArt.Pillar(Color.white, 32, 128));

            for (int i = 0; i < cells.Count; i++)
            {
                var unit = new UnitView { Root = new GameObject("Beacon").transform };
                unit.Root.SetParent(_root, false);
                unit.Root.position = CellCenter(cells[i]);

                var pillar = NewRenderer("BeaconPillar", pillarSprite, 8, unit.Root);
                pillar.color = new Color(1f, 0.82f, 0.34f, 0.22f);
                pillar.transform.localPosition = new Vector3(0f, 0.55f, 0f);
                pillar.transform.localScale = new Vector3(0.55f, 1.6f, 1f);
                unit.Extra = pillar;

                unit.Body = NewRenderer("BeaconBody", bodySprite, 12, unit.Root);
                unit.Body.transform.localScale = Vector3.one * 0.55f;
                _beacons.Add(unit);
            }
        }

        private void BuildGuards(List<Vector2Int> cells)
        {
            var bodySprite = Track(FogHuntArt.Triangle(new Color(1f, 0.40f, 0.36f), 48));
            _coneSprite = Track(FogHuntArt.Cone(Color.white, 0.5f, 64));

            for (int i = 0; i < cells.Count; i++)
            {
                var unit = NewUnit("Guard", bodySprite, 0.62f, 12, true);
                unit.Root.position = CellCenter(cells[i]);
                unit.Extra = NewRenderer("GuardCone", _coneSprite, 9, unit.Root);
                unit.Extra.color = GuardIdle;
                unit.Extra.enabled = false;
                unit.Body.enabled = false;
                if (unit.Shadow != null) unit.Shadow.enabled = false;
                _guards.Add(unit);
            }
        }

        private void BuildStonePreview()
        {
            _ringSprite = Track(FogHuntArt.RingOutline(Color.white, 64, 3f));
            _dotSprite = Track(FogHuntArt.Circle(Color.white, 8));

            _stoneRing = NewRenderer("StoneRing", Track(FogHuntArt.RingOutline(new Color(0.45f, 0.9f, 1f, 0.35f), 128, 2f)), 14, _root);
            _stoneRing.enabled = false;

            _stoneCursor = NewRenderer("StoneCursor", Track(FogHuntArt.RingOutline(new Color(0.6f, 1f, 1f, 0.9f), 64, 3f)), 15, _root);
            _stoneCursor.enabled = false;
        }

        // ── 回合同步 ───────────────────────────────────────────

        public void SetPlayer(Vector2Int cell, Vector2Int facing)
        {
            TweenTo(_player.Root, CellCenter(cell));
            _lamp.transform.position = CellCenter(cell);
            ApplyFacing(_playerFacing, facing, 0.22f);
        }

        public void SetGuard(int index, Vector2Int cell, Vector2Int facing, bool visible, bool alerted, int sight)
        {
            if (index < 0 || index >= _guards.Count) return;
            var unit = _guards[index];
            TweenTo(unit.Root, CellCenter(cell));

            unit.Body.enabled = visible;
            if (unit.Shadow != null) unit.Shadow.enabled = visible;
            unit.Extra.enabled = visible;
            if (!visible) return;

            ApplyFacing(unit.Body, facing, 0f);
            unit.Extra.color = alerted ? GuardAlert : GuardIdle;
            float angle = Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg;
            unit.Extra.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            unit.Extra.transform.localScale = Vector3.one * (sight * 2f);
        }

        public void SetBeacon(int index, Vector2Int cell, bool taken, bool visible)
        {
            if (index < 0 || index >= _beacons.Count) return;
            var unit = _beacons[index];
            unit.Root.position = CellCenter(cell);
            bool show = visible && !taken;
            unit.Body.enabled = show;
            unit.Extra.enabled = show;
        }

        public void SetExit(Vector2Int cell, bool highlighted)
        {
            _exit.Root.position = CellCenter(cell);
            _exitHighlighted = highlighted;
            var color = highlighted ? new Color(1f, 0.86f, 0.45f, 1f) : new Color(0.55f, 1f, 0.62f, 0.95f);
            _exit.Body.color = color;
        }

        public void SetStonePreview(bool on, Vector2Int player, Vector2Int hover)
        {
            _stoneRing.enabled = on;
            _stoneCursor.enabled = on;
            if (!on) return;

            _stoneRing.transform.position = CellCenter(player);
            _stoneRing.transform.localScale = Vector3.one * (FogHuntGame.StoneRange * 2f + 1f);
            _stoneCursor.transform.position = CellCenter(hover);
            _stoneCursor.transform.localScale = Vector3.one;
        }

        public void BumpPlayer(Vector2Int dir)
        {
            _bumpDir = new Vector3(dir.x, dir.y, 0f) * 0.07f;
            _bumpUntil = Time.time + 0.14f;
        }

        public void RefreshFog(Vector2Int player)
        {
            _fog.Refresh(player);
        }

        /// <summary>落地尘环：一格大小的柔环由小扩到 1.4 格再消失。</summary>
        public void PlayStep(Vector2Int cell)
        {
            SpawnRing(CellCenter(cell), new Color(0.92f, 0.90f, 0.82f, 0.45f), 0.55f, 1.40f, 0.28f);
        }

        /// <summary>拾取信标：金色尘环 + 一圈四散的小方块。</summary>
        public void PlayPickup(Vector2Int cell)
        {
            var center = CellCenter(cell);
            SpawnRing(center, new Color(1f, 0.84f, 0.35f, 0.75f), 0.70f, 2.40f, 0.48f);

            var sprite = _dotSprite;
            for (int i = 0; i < 10; i++)
            {
                float angle = i * Mathf.PI * 2f / 10f + (i % 3) * 0.21f;
                float speed = 1.6f + (i % 4) * 0.45f;
                SpawnFx(sprite, center, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * speed,
                    new Vector3(0.20f, 0.20f, 1f), Vector3.one * 0.05f, 0.46f,
                    new Color(1f, 0.82f, 0.32f, 1f), 20);
            }
        }

        private void SpawnRing(Vector3 center, Color color, float from, float to, float duration)
        {
            SpawnFx(_ringSprite, center, Vector3.zero,
                Vector3.one * from, Vector3.one * to, duration, color, 19);
        }

        private void SpawnFx(Sprite sprite, Vector3 position, Vector3 velocity, Vector3 fromScale, Vector3 toScale,
            float duration, Color color, int order)
        {
            var go = new GameObject("Fx");
            go.transform.SetParent(_root, false);
            go.transform.position = position;
            go.transform.localScale = fromScale;

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = color;
            renderer.sortingOrder = order;

            _fx.Add(new Fx
            {
                Transform = go.transform,
                Renderer = renderer,
                From = position,
                Velocity = velocity,
                FromScale = fromScale,
                ToScale = toScale,
                Start = Time.time,
                Duration = duration,
                Alpha = color.a,
            });
        }

        public void Tick(float time)
        {
            for (int i = _tweens.Count - 1; i >= 0; i--)
            {
                var tween = _tweens[i];
                if (tween.Target == null)
                {
                    _tweens.RemoveAt(i);
                    continue;
                }

                float t = tween.Duration <= 0f ? 1f : Mathf.Clamp01((time - tween.Start) / tween.Duration);
                tween.Target.position = Vector3.Lerp(tween.From, tween.To, Mathf.SmoothStep(0f, 1f, t));
                if (t >= 1f) _tweens.RemoveAt(i);
            }

            float remain = _bumpUntil - time;
            float bump = remain > 0f ? Mathf.Sin(Mathf.Clamp01(remain / 0.14f) * Mathf.PI) : 0f;
            _player.Body.transform.localPosition = _bumpDir * bump;

            UpdateFx(time);

            float breath = 1f + 0.06f * Mathf.Sin(time * 2.6f);
            _lamp.transform.localScale = Vector3.one * (LampRadius * breath);
            _lamp.color = new Color(LampColor.r, LampColor.g, LampColor.b, LampColor.a * breath);

            for (int i = 0; i < _beacons.Count; i++)
            {
                var unit = _beacons[i];
                if (!unit.Body.enabled) continue;
                float pulse = 1f + 0.07f * Mathf.Sin(time * 2.1f + i * 1.7f);
                unit.Body.transform.localScale = Vector3.one * (0.55f * pulse);
            }

            float exitPulse = _exitHighlighted
                ? 1.5f + 0.12f * Mathf.Sin(time * 4.2f)
                : 1.5f;
            _exit.Body.transform.localScale = Vector3.one * exitPulse;
        }

        private void UpdateFx(float time)
        {
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var fx = _fx[i];
                float t = fx.Duration <= 0f ? 1f : Mathf.Clamp01((time - fx.Start) / fx.Duration);

                fx.Transform.position = fx.From + fx.Velocity * (t * fx.Duration);
                fx.Transform.localScale = Vector3.Lerp(fx.FromScale, fx.ToScale, t);
                var color = fx.Renderer.color;
                fx.Renderer.color = new Color(color.r, color.g, color.b, fx.Alpha * (1f - t));

                if (t >= 1f)
                {
                    Object.Destroy(fx.Transform.gameObject);
                    _fx.RemoveAt(i);
                }
            }
        }

        public void Destroy()
        {            for (int i = 0; i < _ownedSprite.Count; i++)
                if (_ownedSprite[i] != null) Object.Destroy(_ownedSprite[i]);
            for (int i = 0; i < _ownedTexture.Count; i++)
                if (_ownedTexture[i] != null) Object.Destroy(_ownedTexture[i]);
            _ownedSprite.Clear();
            _ownedTexture.Clear();
            _fog?.Destroy();
            _fog = null;
            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
        }

        // ── 工具 ───────────────────────────────────────────────

        private UnitView NewUnit(string name, Sprite body, float bodyScale, int order, bool withShadow)
        {
            var unit = new UnitView { Root = new GameObject(name).transform };
            unit.Root.SetParent(_root, false);

            if (withShadow)
            {
                unit.Shadow = NewRenderer(name + "_Shadow", Track(FogHuntArt.SoftEllipse(new Color(0f, 0f, 0f, 0.45f))),
                    order - 1, unit.Root);
                unit.Shadow.transform.localPosition = new Vector3(0f, -0.2f, 0f);
                unit.Shadow.transform.localScale = new Vector3(0.62f, 0.26f, 1f);
            }

            unit.Body = NewRenderer(name + "_Body", body, order, unit.Root);
            unit.Body.transform.localScale = Vector3.one * bodyScale;
            return unit;
        }

        private SpriteRenderer NewRenderer(string name, Sprite sprite, int order, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingOrder = order;
            return renderer;
        }

        private static void ApplyFacing(SpriteRenderer body, Vector2Int facing, float offset)
        {
            if (facing == Vector2Int.zero) return;
            float angle = Mathf.Atan2(facing.y, facing.x) * Mathf.Rad2Deg - 90f;
            body.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            if (offset > 0f)
                body.transform.localPosition = new Vector3(facing.x, facing.y, 0f).normalized * offset;
        }

        private void TweenTo(Transform target, Vector3 to)
        {
            float now = Time.time;
            for (int i = 0; i < _tweens.Count; i++)
            {
                if (_tweens[i].Target != target) continue;
                _tweens[i].From = target.position;
                _tweens[i].To = to;
                _tweens[i].Start = now;
                _tweens[i].Duration = MoveDuration;
                return;
            }

            _tweens.Add(new Tween
            {
                Target = target,
                From = target.position,
                To = to,
                Start = now,
                Duration = MoveDuration,
            });
        }

        private Sprite Track(Sprite sprite)
        {
            if (sprite != null && sprite.texture != null) _ownedTexture.Add(sprite.texture);
            if (sprite != null) _ownedSprite.Add(sprite);
            return sprite;
        }

        private static Vector3 CellCenter(Vector2Int cell) => new Vector3(cell.x + 0.5f, cell.y + 0.5f, 0f);
    }
}
