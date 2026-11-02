using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.MiniGame.FogHunt
{
    /// <summary>
    /// 局内 HUD：状态条 / 小地图 / 方向键 / 功能键 / 开场卡 / 暂停 / 结算。
    /// 全部运行时构建，不依赖预制体；配色走「迷雾 + 暗金」一套，圆角用程序化 9 宫格贴图。
    /// </summary>
    public sealed class FogHuntHud : MonoBehaviour
    {
        /// <summary>一回合结束后的界面数据快照。</summary>
        public struct HudState
        {
            public int Turn;
            public int MaxTurn;
            public int Collected;
            public int Total;
            public int Phase;
            public float Detect;
            public float DetectMax;
            public bool Crouch;
            public bool Stone;
            public string BeaconHint;
        }

        // ── 调色板 ─────────────────────────────────────────────
        private static readonly Color Ink = new Color(0.047f, 0.067f, 0.098f, 0.86f);
        private static readonly Color InkSolid = new Color(0.043f, 0.060f, 0.086f, 0.96f);
        private static readonly Color Hairline = new Color(0.91f, 0.77f, 0.42f, 0.20f);
        private static readonly Color Gold = new Color(0.91f, 0.77f, 0.42f, 1f);
        private static readonly Color TextMain = new Color(0.91f, 0.93f, 0.96f, 1f);
        private static readonly Color TextDim = new Color(0.56f, 0.64f, 0.73f, 1f);
        private static readonly Color Danger = new Color(1f, 0.36f, 0.30f, 1f);
        private static readonly Color Warn = new Color(1f, 0.70f, 0.28f, 1f);
        private static readonly Color Calm = new Color(0.44f, 0.83f, 1f, 1f);
        private static readonly Color Ok = new Color(0.55f, 1f, 0.66f, 1f);
        private static readonly Color ButtonIdle = new Color(0.13f, 0.18f, 0.25f, 0.92f);
        private static readonly Color ButtonOn = new Color(0.62f, 0.44f, 0.14f, 0.95f);

        private TMP_FontAsset _font;
        private RectTransform _uiRoot;
        private FogHuntGame _game;

        private TextMeshProUGUI _turnText;
        private TextMeshProUGUI _beaconText;
        private TextMeshProUGUI _detectText;
        private Image _detectFill;
        private TextMeshProUGUI _beaconHint;
        private TextMeshProUGUI _hintText;

        private RawImage _vignette;
        private Texture2D _vignetteTexture;
        private Image _flash;
        private float _flashUntil;

        private TextMeshProUGUI _toastText;
        private GameObject _toastRoot;
        private float _toastUntil;

        private Image _crouchFill;
        private Image _stoneFill;

        private GameObject _introPanel;
        private GameObject _pausePanel;
        private GameObject _resultPanel;
        private TextMeshProUGUI _resultTitle;
        private Image _resultSeal;
        private TextMeshProUGUI _resultRating;
        private TextMeshProUGUI _resultStat1;
        private TextMeshProUGUI _resultStat2;
        private TextMeshProUGUI _resultStat3;
        private TextMeshProUGUI _resultScore;
        private TextMeshProUGUI _resultPhase;
        private TextMeshProUGUI _resultRank;

        private Action _onRestart;
        private Action _onExit;

        // ── 生命周期 ───────────────────────────────────────────

        public void Init(FogHuntGame game, TMP_FontAsset font, Texture2D minimap, Action onRestart, Action onExit)
        {
            _game = game;
            _font = font;
            _onRestart = onRestart;
            _onExit = onExit;

            EnsureEventSystem();
            BuildCanvas();
            BuildVignette();
            BuildStatus();
            BuildMinimap(minimap);
            BuildControls();
            BuildHint();
            BuildToast();
            BuildIntro();
            BuildPausePanel();
            BuildResultPanel();

            SetHints("WASD/方向键 移动 · Q 蹲伏 · E 投石（点目标格）· Esc 设置");
        }

        private void OnDestroy()
        {
            if (_vignetteTexture != null) Destroy(_vignetteTexture);
            if (_resultSeal != null && _resultSeal.sprite != null) Destroy(_resultSeal.sprite.texture);
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("FogHuntEventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
            DontDestroyOnLoad(go);
        }

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("FogHuntCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();
            _uiRoot = (RectTransform)canvasGo.transform;
        }

        // ── 屏幕红晕 ───────────────────────────────────────────

        private void BuildVignette()
        {
            const int w = 256, h = 144;
            _vignetteTexture = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "FogHuntVignette",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = (x / (float)(w - 1) - 0.5f) * 2f;
                    float dy = (y / (float)(h - 1) - 0.5f) * 2f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f;
                    float a = Mathf.Pow(Mathf.Clamp01((d - 0.68f) / 0.32f), 2f);
                    pixels[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            _vignetteTexture.SetPixels32(pixels);
            _vignetteTexture.Apply(false, false);

            var go = new GameObject("Vignette", typeof(RectTransform));
            go.transform.SetParent(_uiRoot, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _vignette = go.AddComponent<RawImage>();
            _vignette.texture = _vignetteTexture;
            _vignette.raycastTarget = false;
            _vignette.color = new Color(1f, 0.24f, 0.2f, 0f);

            var flashGo = new GameObject("PickupFlash", typeof(RectTransform));
            flashGo.transform.SetParent(_uiRoot, false);
            var flashRect = (RectTransform)flashGo.transform;
            flashRect.anchorMin = Vector2.zero;
            flashRect.anchorMax = Vector2.one;
            flashRect.offsetMin = Vector2.zero;
            flashRect.offsetMax = Vector2.zero;

            _flash = flashGo.AddComponent<Image>();
            _flash.sprite = White();
            _flash.raycastTarget = false;
            _flash.color = new Color(1f, 0.86f, 0.45f, 0f);
        }

        /// <summary>拾取信标时全屏一帧淡金闪。</summary>
        public void FlashPickup()
        {
            if (_flash == null) return;
            _flash.color = new Color(1f, 0.86f, 0.45f, FlashAlpha);
            _flashUntil = Time.unscaledTime + FlashSeconds;
        }

        private const float FlashAlpha = 0.02f;
        private const float FlashSeconds = 0.24f;

        // ── 左上状态条 ─────────────────────────────────────────

        private void BuildStatus()
        {
            var card = CreatePanel("StatusCard", new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(26f, -26f), new Vector2(388f, 118f), Ink, 14);

            CreateLabel(card, "TurnCap", 0f, 18f, -12f, 120f, 30f, 20, TextAlignmentOptions.Left, TextDim).text = "回合";
            _turnText = CreateLabel(card, "TurnValue", 1f, -18f, -10f, 220f, 34f, 26, TextAlignmentOptions.Right, TextMain);

            CreateLabel(card, "BeaconCap", 0f, 18f, -48f, 120f, 30f, 20, TextAlignmentOptions.Left, TextDim).text = "信标";
            _beaconText = CreateLabel(card, "BeaconValue", 1f, -18f, -46f, 220f, 34f, 26, TextAlignmentOptions.Right, Gold);

            var barRoot = CreateRect(card, "DetectBar");
            var barRect = (RectTransform)barRoot;
            barRect.anchorMin = new Vector2(0f, 1f);
            barRect.anchorMax = new Vector2(0f, 1f);
            barRect.pivot = new Vector2(0f, 1f);
            barRect.anchoredPosition = new Vector2(18f, -86f);
            barRect.sizeDelta = new Vector2(180f, 10f);

            var barBg = CreateImage(barRoot, "Bg", new Color(1f, 1f, 1f, 0.09f), 5);
            Stretch(barBg.rectTransform);

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(barRoot, false);
            _detectFill = fillGo.AddComponent<Image>();
            _detectFill.sprite = White();
            _detectFill.type = Image.Type.Filled;
            _detectFill.fillMethod = Image.FillMethod.Horizontal;
            _detectFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _detectFill.fillAmount = 0f;
            _detectFill.raycastTarget = false;
            Stretch(_detectFill.rectTransform);

            _detectText = CreateLabel(card, "DetectText", 1f, -18f, -78f, 168f, 26f, 19,
                TextAlignmentOptions.Right, Calm);

            _beaconHint = CreateLabel(_uiRoot, "BeaconHint", 0f, 26f, -154f, 460f, 28f, 20,
                TextAlignmentOptions.Left, Gold);
            _beaconHint.gameObject.SetActive(false);
        }

        // ── 右上小地图 ─────────────────────────────────────────

        private void BuildMinimap(Texture2D texture)
        {
            var panel = CreatePanel("Minimap", new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-26f, -26f), new Vector2(216f, 216f), Ink, 14);

            var go = new GameObject("Map", typeof(RectTransform));
            go.transform.SetParent(panel, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(12f, 12f);
            rect.offsetMax = new Vector2(-12f, -12f);
            var map = go.AddComponent<RawImage>();
            map.texture = texture;
            map.color = Color.white;
            map.raycastTarget = false;
        }

        // ── 左下方向键 / 右下功能键 ───────────────────────────

        private void BuildControls()
        {
            var pad = CreatePanel("MovePad", new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(30f, 30f), new Vector2(252f, 252f), Ink, 18);

            CreatePadButton(pad, "↑", new Vector2(0f, 72f), () => _game.UiMove(0, 1));
            CreatePadButton(pad, "←", new Vector2(-72f, 0f), () => _game.UiMove(-1, 0));
            CreatePadButton(pad, "↓", new Vector2(0f, -72f), () => _game.UiMove(0, -1));
            CreatePadButton(pad, "→", new Vector2(72f, 0f), () => _game.UiMove(1, 0));

            var bar = CreatePanel("ActionBar", new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-30f, 30f), new Vector2(168f, 252f), Ink, 18);

            _crouchFill = CreateActionButton(bar, "蹲伏", new Vector2(0f, 72f), () => _game.UiToggleCrouch());
            _stoneFill = CreateActionButton(bar, "投石", new Vector2(0f, 0f), () => _game.UiToggleStone());
            CreateActionButton(bar, "设置", new Vector2(0f, -72f), () => _game.UiTogglePause());
        }

        private Image CreateActionButton(Transform parent, string label, Vector2 pos, Action onClick)
        {
            var button = CreatePadButton(parent, label, pos, onClick, 132f, 60f, 26);
            return button.GetComponent<Image>();
        }

        private Button CreatePadButton(Transform parent, string label, Vector2 pos, Action onClick,
            float width = 66f, float height = 66f, int fontSize = 28)
        {
            var go = new GameObject("Pad_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(width, height);

            var image = go.AddComponent<Image>();
            image.sprite = Rounded(14);
            image.type = Image.Type.Sliced;
            image.color = ButtonIdle;

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ApplyButtonTint(button, image.color);
            button.onClick.AddListener(() => onClick?.Invoke());

            var text = CreateLabel(go.transform, "Label", 0.5f, 0f, 0f, width, height, fontSize,
                TextAlignmentOptions.Center, TextMain);
            text.text = label;
            Stretch(text.rectTransform);

            return button;
        }

        private static void ApplyButtonTint(Button button, Color _)
        {
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            colors.fadeDuration = 0.08f;
            colors.colorMultiplier = 1f;
            button.colors = colors;
        }

        // ── 底部提示 + Toast ──────────────────────────────────

        private void BuildHint()
        {
            var pill = CreatePanel("HintBar", new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 22f), new Vector2(860f, 42f), new Color(0.047f, 0.067f, 0.098f, 0.72f), 21);
            _hintText = CreateLabel(pill, "Hint", 0.5f, 0f, 0f, 820f, 40f, 20, TextAlignmentOptions.Center, TextDim);
            Stretch(_hintText.rectTransform);
        }

        private void BuildToast()
        {
            _toastRoot = CreatePanel("Toast", new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -104f), new Vector2(560f, 52f), new Color(0.78f, 0.42f, 0.16f, 0.94f), 26).gameObject;
            _toastText = CreateLabel(_toastRoot.transform, "ToastText", 0.5f, 0f, 0f, 520f, 48f, 23,
                TextAlignmentOptions.Center, Color.white);
            Stretch(_toastText.rectTransform);
            _toastRoot.SetActive(false);
        }

        // ── 开场卡 ────────────────────────────────────────────

        private void BuildIntro()
        {
            var root = CreateRect(_uiRoot, "Intro");
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var backdrop = root.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0.02f, 0.027f, 0.043f, 0.78f);
            backdrop.raycastTarget = true;

            var button = root.gameObject.AddComponent<Button>();
            button.targetGraphic = backdrop;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => _game.StartGame());

            var title = CreateCenterText(root, "IntroTitle", 120f, 900f, 80f, 54,
                TextAlignmentOptions.Center, Gold);
            title.text = "迷雾信标";

            var goal = CreateCenterText(root, "IntroGoal", 40f, 900f, 44f, 28,
                TextAlignmentOptions.Center, TextMain);
            goal.text = "找回 3 枚信标，回到入口";

            var sub = CreateCenterText(root, "IntroSub", 0f, 900f, 38f, 22,
                TextAlignmentOptions.Center, TextDim);
            sub.text = "你只能看见灯照到的地方";

            var start = CreateCenterText(root, "IntroStart", -100f, 900f, 38f, 22,
                TextAlignmentOptions.Center, new Color(0.58f, 0.80f, 1f));
            start.text = "按任意键 / 点击屏幕开始";

            _introPanel = root.gameObject;
        }

        public void ShowIntro()
        {
            if (_introPanel == null) return;
            _introPanel.transform.SetAsLastSibling();
            _introPanel.SetActive(true);
        }

        public void HideIntro()
        {
            if (_introPanel != null) _introPanel.SetActive(false);
        }

        // ── 暂停 ──────────────────────────────────────────────

        private void BuildPausePanel()
        {
            _pausePanel = CreatePanel("PausePanel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(500f, 400f), InkSolid, 22).gameObject;

            var title = CreateCenterText(_pausePanel.transform, "PauseTitle", 128f, 440f, 52f, 32,
                TextAlignmentOptions.Center, Gold);
            title.text = "已暂停";

            var sub = CreateCenterText(_pausePanel.transform, "PauseSub", 88f, 440f, 30f, 18,
                TextAlignmentOptions.Center, TextDim);
            sub.text = "按 Esc 继续";

            CreateWideButton(_pausePanel.transform, "继续", new Vector2(0f, 14f), () => _game.UiTogglePause());
            CreateWideButton(_pausePanel.transform, "重新开始", new Vector2(0f, -62f), () => _onRestart?.Invoke());
            CreateWideButton(_pausePanel.transform, "返回主页", new Vector2(0f, -138f), () => _onExit?.Invoke());
            _pausePanel.SetActive(false);
        }

        // ── 结算 ──────────────────────────────────────────────

        private void BuildResultPanel()
        {
            _resultPanel = CreatePanel("ResultPanel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(620f, 660f), InkSolid, 22).gameObject;

            var sealGo = new GameObject("Seal", typeof(RectTransform));
            sealGo.transform.SetParent(_resultPanel.transform, false);
            var sealRect = (RectTransform)sealGo.transform;
            sealRect.anchorMin = new Vector2(0.5f, 0.5f);
            sealRect.anchorMax = new Vector2(0.5f, 0.5f);
            sealRect.pivot = new Vector2(0.5f, 0.5f);
            sealRect.anchoredPosition = new Vector2(0f, 200f);
            sealRect.sizeDelta = new Vector2(150f, 150f);
            _resultSeal = sealGo.AddComponent<Image>();
            _resultSeal.sprite = FogHuntArt.RingOutline(Color.white, 128, 5f);
            _resultSeal.raycastTarget = false;

            _resultRating = CreateCenterText(_resultPanel.transform, "Rating", 200f, 200f, 90f, 64,
                TextAlignmentOptions.Center, TextMain);

            _resultTitle = CreateCenterText(_resultPanel.transform, "ResultTitle", 112f, 560f, 44f, 34,
                TextAlignmentOptions.Center, TextMain);

            _resultStat1 = CreateCenterText(_resultPanel.transform, "Stat1", 62f, 560f, 34f, 22,
                TextAlignmentOptions.Center, TextMain);
            _resultStat2 = CreateCenterText(_resultPanel.transform, "Stat2", 26f, 560f, 34f, 22,
                TextAlignmentOptions.Center, TextMain);
            _resultStat3 = CreateCenterText(_resultPanel.transform, "Stat3", -10f, 560f, 34f, 22,
                TextAlignmentOptions.Center, TextMain);

            var divider = CreateImage(_resultPanel.transform, "Divider", Hairline, 0);
            var dr = divider.rectTransform;
            dr.anchorMin = new Vector2(0.5f, 0.5f);
            dr.anchorMax = new Vector2(0.5f, 0.5f);
            dr.pivot = new Vector2(0.5f, 0.5f);
            dr.anchoredPosition = new Vector2(0f, -44f);
            dr.sizeDelta = new Vector2(520f, 1f);

            _resultScore = CreateCenterText(_resultPanel.transform, "Score", -70f, 560f, 30f, 20,
                TextAlignmentOptions.Center, TextDim);
            _resultRank = CreateCenterText(_resultPanel.transform, "Rank", -104f, 560f, 28f, 20,
                TextAlignmentOptions.Center, new Color(0.58f, 0.80f, 1f));
            _resultPhase = CreateCenterText(_resultPanel.transform, "Phase", -134f, 560f, 26f, 18,
                TextAlignmentOptions.Center, TextDim);

            CreateWideButton(_resultPanel.transform, "再来一局", new Vector2(0f, -196f), () => _onRestart?.Invoke());
            CreateWideButton(_resultPanel.transform, "返回主页", new Vector2(0f, -266f), () => _onExit?.Invoke());
            _resultPanel.SetActive(false);
        }

        // ── 数据刷新 ──────────────────────────────────────────

        public void Refresh(HudState state)
        {
            if (_turnText != null)
                _turnText.text = state.Turn + " <color=#6E7C8C>/ " + state.MaxTurn + "</color>";

            if (_beaconText != null)
            {
                _beaconText.text = state.Collected + " <color=#6E7C8C>/ " + state.Total + "</color>";
                _beaconText.color = state.Collected >= state.Total ? Ok : Gold;
            }

            float percent = state.DetectMax <= 0f ? 0f : Mathf.Clamp01(state.Detect / state.DetectMax);
            var color = DetectColor(percent);

            if (_detectFill != null)
            {
                _detectFill.fillAmount = percent;
                _detectFill.color = color;
            }
            if (_detectText != null)
            {
                _detectText.text = DetectLabel(percent);
                _detectText.color = color;
            }
            if (_vignette != null)
            {
                float alpha = percent < 0.6f ? 0f : Mathf.Lerp(0.04f, 0.11f, (percent - 0.6f) / 0.4f);
                _vignette.color = new Color(1f, 0.24f, 0.2f, alpha);
            }

            if (_crouchFill != null) _crouchFill.color = state.Crouch ? ButtonOn : ButtonIdle;
            if (_stoneFill != null) _stoneFill.color = state.Stone ? new Color(0.62f, 0.30f, 0.14f, 0.95f) : ButtonIdle;

            if (_beaconHint != null)
            {
                bool show = !string.IsNullOrEmpty(state.BeaconHint);
                _beaconHint.gameObject.SetActive(show);
                if (show) _beaconHint.text = state.BeaconHint;
            }
        }

        private static Color DetectColor(float percent)
        {
            if (percent < 0.2f) return Calm;
            return percent < 0.6f ? Warn : Danger;
        }

        private static string DetectLabel(float percent)
        {
            if (percent < 0.2f) return "潜行中";
            return percent < 0.6f ? "被怀疑" : "即将暴露";
        }

        public void SetHints(string hint)
        {
            if (_hintText != null) _hintText.text = hint;
        }

        public void Toast(string message)
        {
            if (_toastText == null) return;
            _toastText.text = message;
            _toastRoot.SetActive(true);
            _toastUntil = Time.unscaledTime + 2f;
        }

        public void SetPaused(bool paused)
        {
            if (_pausePanel == null) return;
            if (paused) _pausePanel.transform.SetAsLastSibling();
            _pausePanel.SetActive(paused);
        }

        public void SetRankInfo(string text)
        {
            if (_resultRank != null) _resultRank.text = text;
        }

        public void ShowResult(bool win, string rating, int turns, int collected, int total, float detectPeak,
            string scoreLine, int phase)
        {
            if (_resultPanel == null) return;
            if (_toastRoot != null) _toastRoot.SetActive(false);
            if (_introPanel != null) _introPanel.SetActive(false);
            _resultPanel.transform.SetAsLastSibling();
            _resultPanel.SetActive(true);

            var accent = win ? Gold : Danger;
            if (_resultSeal != null) _resultSeal.color = accent;
            if (_resultRating != null)
            {
                _resultRating.text = rating;
                _resultRating.color = accent;
            }

            _resultTitle.text = win ? "撤离成功" : "任务失败";
            _resultTitle.color = win ? Ok : Danger;

            _resultStat1.text = "用时　<b>" + turns + "</b> 回合";
            _resultStat2.text = "信标　<b>" + collected + "</b> / " + total;
            _resultStat3.text = "暴露度峰值　<b>" + detectPeak.ToString("0.#") + "</b> / 5";

            if (_resultScore != null) _resultScore.text = scoreLine;
            if (_resultPhase != null)
            {
                string name = phase >= 3 ? "撤离" : phase >= 2 ? "戒备" : "潜行";
                _resultPhase.text = "本局阶段　<color=#E8C46A>" + name + "</color>";
            }
            if (_resultRank != null) _resultRank.text = "正在上报成绩…";
        }

        private void Update()
        {
            if (_toastRoot != null && _toastRoot.activeSelf && Time.unscaledTime > _toastUntil)
                _toastRoot.SetActive(false);

            if (_flash != null && _flash.color.a > 0f)
            {
                float remain = Mathf.Clamp01((_flashUntil - Time.unscaledTime) / FlashSeconds);
                _flash.color = new Color(1f, 0.86f, 0.45f, FlashAlpha * remain);
            }
        }

        // ── 构建工具 ──────────────────────────────────────────

        /// <summary>圆角面板：底衬（描边）+ 主体，返回主体 RectTransform 作为内容父级。</summary>
        private RectTransform CreatePanel(string name, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size,
            Color color, int radius)
        {
            var root = CreateRect(_uiRoot, name);
            root.anchorMin = anchor;
            root.anchorMax = anchor;
            root.pivot = pivot;
            root.anchoredPosition = pos;
            root.sizeDelta = size;

            var edge = CreateImage(root, "Edge", Hairline, -1);
            var edgeRect = edge.rectTransform;
            edgeRect.anchorMin = Vector2.zero;
            edgeRect.anchorMax = Vector2.one;
            edgeRect.offsetMin = new Vector2(-1.5f, -1.5f);
            edgeRect.offsetMax = new Vector2(1.5f, 1.5f);
            edge.sprite = Rounded(radius);
            edge.type = Image.Type.Sliced;

            var body = CreateImage(root, "Body", color, -1);
            Stretch(body.rectTransform);
            body.sprite = Rounded(radius);
            body.type = Image.Type.Sliced;

            return root;
        }

        /// <summary>居中锚点的文本（面板内部用，posY 相对面板中心）。</summary>
        private TextMeshProUGUI CreateCenterText(Transform parent, string name, float posY, float width, float height,
            int fontSize, TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, posY);
            rect.sizeDelta = new Vector2(width, height);

            var text = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            text.richText = true;
            text.text = string.Empty;
            return text;
        }

        private TextMeshProUGUI CreateLabel(Transform parent, string name, float anchorX, float posX, float posY,
            float width, float height, int fontSize, TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(anchorX, 1f);
            rect.anchorMax = new Vector2(anchorX, 1f);
            rect.pivot = new Vector2(anchorX, 1f);
            rect.anchoredPosition = new Vector2(posX, posY);
            rect.sizeDelta = new Vector2(width, height);

            var text = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            text.richText = true;
            text.text = string.Empty;
            text.overflowMode = TextOverflowModes.Overflow;
            return text;
        }

        private Button CreateWideButton(Transform parent, string label, Vector2 pos, Action onClick)
        {
            var go = new GameObject("Button_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(300f, 60f);

            var image = go.AddComponent<Image>();
            image.sprite = Rounded(15);
            image.type = Image.Type.Sliced;
            image.color = new Color(0.16f, 0.24f, 0.33f, 0.96f);

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            ApplyButtonTint(button, image.color);
            button.onClick.AddListener(() => onClick?.Invoke());

            var text = CreateLabel(go.transform, "Label", 0.5f, 0f, 0f, 300f, 60f, 24,
                TextAlignmentOptions.Center, TextMain);
            text.text = label;
            Stretch(text.rectTransform);

            return button;
        }

        private static RectTransform CreateRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Image CreateImage(Transform parent, string name, Color color, int radius)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            if (radius > 0) { image.sprite = Rounded(radius); image.type = Image.Type.Sliced; }
            return image;
        }

        private static Sprite _whiteSprite;

        private static Sprite White()
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "FogHuntWhite" };
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            return _whiteSprite;
        }

        /// <summary>程序化圆角贴图（9 宫格，按半径缓存）。</summary>
        private static readonly Dictionary<int, Sprite> SharedRounded = new Dictionary<int, Sprite>();

        private static Sprite Rounded(int radius)
        {
            if (SharedRounded.TryGetValue(radius, out var cached) && cached != null) return cached;

            int size = radius * 4 + 8;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "FogHuntRound" + radius,
            };

            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            float inner = half - radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f - half;
                    float py = y + 0.5f - half;
                    float qx = Mathf.Abs(px) - inner;
                    float qy = Mathf.Abs(py) - inner;
                    float ax = Mathf.Max(qx, 0f), ay = Mathf.Max(qy, 0f);
                    float dist = Mathf.Sqrt(ax * ax + ay * ay) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    float alpha = Mathf.Clamp01(0.5f - dist);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            SharedRounded[radius] = sprite;
            return sprite;
        }
    }
}
