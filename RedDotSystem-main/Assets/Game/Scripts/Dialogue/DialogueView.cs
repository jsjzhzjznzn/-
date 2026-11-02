using System.Collections.Generic;
using DG.Tweening;
using Miemie.DialogSystem;
using MiMieMVVM;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 原神式对话面板（UI 框架登记视图，UIType.DialogueView）
///
/// 结构：全屏透明根 + 底部渐变对话条（名牌/正文/继续箭头）+ 右侧竖排选项。
/// 动效仍由视图自己的 DOTween 驱动（默认吃 scaled 时间，暂停时随世界冻结），
/// 不使用框架 UIViewAnim —— 避免"暂停中打开面板卡在动画首帧"。
///
/// 与旧 GenshinDialogView 的差别只有生命周期：改由 UIManager 开合，
/// 因此必须在 OnOpen 里重置状态（框架会缓存复用实例），在 OnClose 里解绑并清理补间。
/// </summary>
public class DialogueView : UIView, IView
{
    [Header("面板")]
    [ControlBinding("DialogPanel")] public GameObject panelRoot;
    [ControlBinding("DialogPanel")] public CanvasGroup panelCanvasGroup;
    [ControlBinding("SpeakerName")] public TMP_Text speakerNameText;
    [ControlBinding] public TMP_Text contentText;
    [ControlBinding] public CanvasGroup continueArrow;

    [Header("选项")]
    [ControlBinding] public GameObject optionRoot;
    [ControlBinding] public Button[] optionButtons;

    [Header("顶部功能按钮")]
    [ControlBinding] public CanvasGroup rootGroup;
    [ControlBinding] public Button autoButton;
    [ControlBinding("AutoButton/Label")] public TMP_Text autoLabel;
    [ControlBinding] public Button skipButton;
    [ControlBinding] public Button hideButton;

    [Header("参数")]
    [SerializeField] private float typewriterSpeed = 30f;
    [SerializeField] private float fadeInDuration = 0.25f;
    [Tooltip("面板滑入距离")]
    [SerializeField] private float panelSlide = 46f;
    [Tooltip("选项逐条出现间隔")]
    [SerializeField] private float optionStagger = 0.06f;

    [Header("配色")]
    [SerializeField] private Color npcNameColor = new Color(0.91f, 0.77f, 0.42f, 1f);
    [SerializeField] private Color playerNameColor = new Color(0.77f, 0.88f, 0.95f, 1f);
    [SerializeField] private Color narratorNameColor = new Color(0.95f, 0.93f, 0.89f, 1f);

    private DialogueViewModel vm;
    private Tweener typewriterTween;
    private int lastNodeFrame = -1;
    private bool typing;

    /// <summary>自动播放开关（打完字停一会儿自动下一句；遇到选项节点停下等玩家选）</summary>
    private bool autoPlay;

    /// <summary>自动播放的停留计时（不缩放时间：暂停不该继续计时）</summary>
    private float autoTimer;

    /// <summary>UI 已隐藏（只看剧情画面；点击任意处恢复）</summary>
    private bool uiHidden;

    private Coroutine skipRoutine;

    // 动效基准值（首次 OnInit / OnOpen 缓存，避免重复播放后漂移）
    private Vector2 panelBasePos;
    private Vector2 arrowBasePos;
    private Vector2[] optionBasePos;
    private Vector3 speakerBaseScale = Vector3.one;
    private bool baseCached;

    public IViewModel ViewModel => vm;

    /// <summary>测试钩子：打字机是否进行中（暂停冻结时保持 true）</summary>
    public bool IsTyping => typing;

    /// <summary>测试钩子：当前已显示的正文（暂停冻结时长度不变）</summary>
    public string CurrentShownText => contentText != null ? contentText.text : string.Empty;

    /// <summary>测试钩子：本面板当前绑定到哪个 ViewModel</summary>
    public bool IsBound => vm != null;

    #region 框架生命周期

    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        CacheBaseTransforms();

        if (optionButtons != null)
        {
            for (int i = 0; i < optionButtons.Length; i++)
            {
                int captured = i;
                optionButtons[i].onClick.AddListener(() => SelectOption(captured));
            }
        }

        if (autoButton != null) autoButton.onClick.AddListener(ToggleAutoPlay);
        if (skipButton != null) skipButton.onClick.AddListener(OnSkipClicked);
        if (hideButton != null) hideButton.onClick.AddListener(OnHideClicked);

        ResetUi();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        ResetUi();

        DialogueRunner runner = userData as DialogueRunner;
        if (runner != null) Bind(runner.ViewModel);
    }

    public override void OnClose()
    {
        Unbind();
        KillAllTweens();
        ResetUi();

        base.OnClose();
    }

    public override void OnRelease()
    {
        Unbind();
        KillAllTweens();
        base.OnRelease();
    }

    private void OnDestroy()
    {
        Unbind();
        KillAllTweens();
    }

    #endregion

    private void Update()
    {
        // UI 隐藏状态：任意点击/空格恢复显示，不推进对话
        if (uiHidden)
        {
            if (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space)) SetUiHidden(false);
            return;
        }

        if (vm == null || vm.CurrentNode == null) return;

        // 暂停中不接受推进/选项输入（点击暂停面板不能误推进对话；对话本体随世界冻结）
        if (PauseManager.IsPaused) return;

        // 节点出现的当帧不响应推进（防止触发对话的 F 键同帧连跳第一句）
        if (Time.frameCount == lastNodeFrame) return;

        var node = vm.CurrentNode;
        if (node.IsOptionNode)
        {
            var choices = vm.RuntimeModel.AvailableChoiceList;
            for (int i = 0; i < optionButtons.Length && i < choices.Count; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    SelectOption(i);
            }
            return;
        }

        // 自动播放：文字打完停留 1.4 秒自动下一句
        if (autoPlay)
        {
            if (typing)
            {
                autoTimer = 0f;
            }
            else
            {
                autoTimer += Time.unscaledDeltaTime;
                if (autoTimer >= 1.4f)
                {
                    autoTimer = 0f;
                    vm.GoNext();
                    return;
                }
            }
        }

        bool advancePressed = Input.GetMouseButtonDown(0)
                              || Input.GetKeyDown(KeyCode.Space)
                              || Input.GetKeyDown(KeyCode.F);
        if (!advancePressed) return;

        if (typing)
        {
            typewriterTween?.Complete();
            return;
        }

        vm.GoNext();
    }

    // ──────────────────────────────
    // 顶部功能按钮
    // ──────────────────────────────

    /// <summary>自动播放：开/关（选项节点永不自动选，避免替玩家做决定）</summary>
    private void ToggleAutoPlay()
    {
        autoPlay = !autoPlay;
        autoTimer = 0f;
        RefreshAutoLabel();
    }

    private void RefreshAutoLabel()
    {
        if (autoLabel != null) autoLabel.SetText(autoPlay ? "自动中" : "自动");
    }

    /// <summary>跳过：连续推进直到选项节点或对话结束（走正常 GoNext，结束时照常发整图结束事件）</summary>
    private void OnSkipClicked()
    {
        if (vm == null) return;
        if (skipRoutine != null) StopCoroutine(skipRoutine);
        skipRoutine = StartCoroutine(SkipRoutine());
    }

    private System.Collections.IEnumerator SkipRoutine()
    {
        int guard = 0;
        while (vm != null && vm.CurrentNode != null && !vm.CurrentNode.IsOptionNode && guard++ < 200)
        {
            typewriterTween?.Complete();
            vm.GoNext();
            yield return new WaitForSeconds(0.05f);
        }
        skipRoutine = null;
    }

    /// <summary>隐藏 UI（看纯剧情画面），再点一下恢复</summary>
    private void OnHideClicked() => SetUiHidden(true);

    private void SetUiHidden(bool hidden)
    {
        uiHidden = hidden;
        if (rootGroup == null) return;

        rootGroup.alpha = hidden ? 0f : 1f;
        rootGroup.blocksRaycasts = !hidden;
        rootGroup.interactable = !hidden;
    }

    #region 绑定

    public void Bind(IViewModel viewModel)
    {
        Unbind();

        vm = viewModel as DialogueViewModel;
        if (vm == null) return;

        vm.NodeChanged += OnNodeChanged;
        vm.OptionsChanged += OnOptionsChanged;
        vm.DialogEnded += OnDialogEnded;
    }

    public void Unbind()
    {
        if (vm == null) return;

        vm.NodeChanged -= OnNodeChanged;
        vm.OptionsChanged -= OnOptionsChanged;
        vm.DialogEnded -= OnDialogEnded;
        vm = null;
    }

    #endregion

    #region VM 事件

    private void OnNodeChanged(DialogueNodeData node)
    {
        lastNodeFrame = Time.frameCount;

        ShowPanel();

        if (speakerNameText != null)
        {
            speakerNameText.text = node.SpeakerName;
            speakerNameText.color = node.SpeakType switch
            {
                SpeakEnums.NPC => npcNameColor,
                SpeakEnums.Player => playerNameColor,
                _ => narratorNameColor,
            };
            PopSpeakerName();
        }

        // 选项节点：正文清空不出继续箭头，等选项列表出现
        if (node.IsOptionNode)
        {
            contentText.text = string.Empty;
            typing = false;
            typewriterTween?.Kill();
            StopArrowLoops();
            continueArrow.gameObject.SetActive(false);
            return;
        }

        contentText.text = string.Empty;

        if (optionRoot != null) optionRoot.SetActive(false);
        StopArrowLoops();
        continueArrow.gameObject.SetActive(false);

        string text = node.DialogText ?? string.Empty;
        typing = text.Length > 0;
        typewriterTween?.Kill();
        if (text.Length == 0)
        {
            ShowContinueArrow();
            return;
        }

        float duration = text.Length / Mathf.Max(typewriterSpeed, 1f);
        typewriterTween = contentText
            .DOText(text, duration, true)
            .SetEase(Ease.Linear)
            .OnComplete(ShowContinueArrow);
    }

    private void OnOptionsChanged(IReadOnlyList<DialogueTransLineData> choices)
    {
        StopArrowLoops();
        continueArrow.DOKill();
        continueArrow.gameObject.SetActive(false);

        if (choices == null || choices.Count == 0)
        {
            if (optionRoot != null) optionRoot.SetActive(false);
            return;
        }

        if (optionRoot != null) optionRoot.SetActive(true);
        for (int i = 0; i < optionButtons.Length; i++)
        {
            bool show = i < choices.Count;
            var button = optionButtons[i];
            button.gameObject.SetActive(show);
            if (!show) continue;

            button.GetComponentInChildren<TMP_Text>().text = choices[i].labelText;

            var rt = (RectTransform)button.transform;
            var cg = button.GetComponent<CanvasGroup>();
            if (cg == null) cg = button.gameObject.AddComponent<CanvasGroup>();
            if (optionBasePos != null && i < optionBasePos.Length)
                rt.anchoredPosition = optionBasePos[i];

            // 逐条从右侧滑入 + 淡入
            rt.DOKill();
            cg.DOKill();
            cg.alpha = 0f;
            float delay = i * optionStagger;
            rt.anchoredPosition = optionBasePos[i] + new Vector2(70f, 0f);
            cg.DOFade(1f, 0.2f).SetDelay(delay).SetEase(Ease.OutQuad);
            rt.DOAnchorPos(optionBasePos[i], 0.24f).SetDelay(delay).SetEase(Ease.OutCubic);
        }
    }

    private void OnDialogEnded()
    {
        HidePanel(false);
    }

    private void SelectOption(int index)
    {
        if (vm == null) return;
        var choices = vm.RuntimeModel.AvailableChoiceList;
        if (index < 0 || index >= choices.Count) return;

        if (optionRoot != null) optionRoot.SetActive(false);
        vm.SelectOption(index);
    }

    #endregion

    #region 面板显隐

    private void ShowPanel()
    {
        if (panelRoot == null || panelCanvasGroup == null) return;

        panelRoot.SetActive(true);

        var rt = (RectTransform)panelRoot.transform;
        panelCanvasGroup.DOKill();
        rt.DOKill();

        panelCanvasGroup.alpha = 0f;
        rt.anchoredPosition = panelBasePos - new Vector2(0f, panelSlide);
        panelCanvasGroup.DOFade(1f, fadeInDuration).SetEase(Ease.OutCubic);
        rt.DOAnchorPos(panelBasePos, fadeInDuration).SetEase(Ease.OutCubic);
    }

    /// <summary>隐藏面板（immediate=true 立刻收起，false 播下滑淡出）</summary>
    public void HidePanel(bool immediate)
    {
        typewriterTween?.Kill();
        typing = false;

        StopArrowLoops();
        if (continueArrow != null) continueArrow.gameObject.SetActive(false);
        if (optionRoot != null) optionRoot.SetActive(false);

        if (panelRoot == null || panelCanvasGroup == null) return;

        if (immediate)
        {
            panelCanvasGroup.DOKill();
            ((RectTransform)panelRoot.transform).DOKill();
            panelCanvasGroup.alpha = 0;
            panelRoot.SetActive(false);
            return;
        }

        var rt = (RectTransform)panelRoot.transform;
        panelCanvasGroup.DOKill();
        rt.DOKill();

        panelCanvasGroup.DOFade(0f, fadeInDuration).SetEase(Ease.InCubic);
        rt.DOAnchorPos(panelBasePos - new Vector2(0f, panelSlide * 0.6f), fadeInDuration)
          .SetEase(Ease.InCubic)
          .OnComplete(() =>
          {
              if (panelRoot != null && panelCanvasGroup != null && panelCanvasGroup.alpha <= 0.01f)
                  panelRoot.SetActive(false);
          });
    }

    #endregion

    #region 动效 / 状态

    private void CacheBaseTransforms()
    {
        if (baseCached) return;
        if (panelRoot != null) panelBasePos = ((RectTransform)panelRoot.transform).anchoredPosition;
        if (continueArrow != null) arrowBasePos = ((RectTransform)continueArrow.transform).anchoredPosition;
        if (speakerNameText != null) speakerBaseScale = speakerNameText.transform.localScale;

        if (optionButtons != null)
        {
            optionBasePos = new Vector2[optionButtons.Length];
            for (int i = 0; i < optionButtons.Length; i++)
                if (optionButtons[i] != null)
                    optionBasePos[i] = ((RectTransform)optionButtons[i].transform).anchoredPosition;
        }

        baseCached = true;
    }

    /// <summary>复位到"未显示"状态（框架缓存复用，每次 OnOpen 都要干净）</summary>
    private void ResetUi()
    {
        typewriterTween?.Kill();
        typing = false;
        lastNodeFrame = -1;

        // 顶部按钮的状态每次开面板都要干净（框架会复用实例）
        autoPlay = false;
        autoTimer = 0f;
        RefreshAutoLabel();
        SetUiHidden(false);
        if (skipRoutine != null)
        {
            StopCoroutine(skipRoutine);
            skipRoutine = null;
        }

        if (contentText != null) contentText.text = string.Empty;
        if (speakerNameText != null) speakerNameText.text = string.Empty;
        if (optionRoot != null) optionRoot.SetActive(false);
        if (optionButtons != null)
        {
            foreach (var button in optionButtons)
            {
                if (button != null) button.gameObject.SetActive(false);
            }
        }

        if (continueArrow != null)
        {
            continueArrow.DOKill();
            continueArrow.gameObject.SetActive(false);
        }

        if (panelRoot != null && panelCanvasGroup != null)
        {
            panelCanvasGroup.DOKill();
            ((RectTransform)panelRoot.transform).DOKill();
            panelCanvasGroup.alpha = 0f;
            ((RectTransform)panelRoot.transform).anchoredPosition = panelBasePos;
            panelRoot.SetActive(false);
        }
    }

    private void KillAllTweens()
    {
        typewriterTween?.Kill();
        if (skipRoutine != null)
        {
            StopCoroutine(skipRoutine);
            skipRoutine = null;
        }
        if (continueArrow != null)
        {
            continueArrow.DOKill();
            continueArrow.transform.DOKill();
        }
        if (panelRoot != null)
        {
            if (panelCanvasGroup != null) panelCanvasGroup.DOKill();
            panelRoot.transform.DOKill();
        }
        if (speakerNameText != null) speakerNameText.transform.DOKill();
        if (optionButtons != null)
        {
            foreach (var button in optionButtons)
            {
                if (button == null) continue;
                button.transform.DOKill();
                var cg = button.GetComponent<CanvasGroup>();
                if (cg != null) cg.DOKill();
            }
        }
    }

    private void PopSpeakerName()
    {
        if (speakerNameText == null) return;

        var t = speakerNameText.transform;
        t.DOKill();
        t.localScale = speakerBaseScale * 0.92f;
        t.DOScale(speakerBaseScale, 0.2f).SetEase(Ease.OutBack);
    }

    private void ShowContinueArrow()
    {
        typing = false;
        if (continueArrow == null) return;

        continueArrow.gameObject.SetActive(true);
        continueArrow.alpha = 1f;

        var rt = (RectTransform)continueArrow.transform;
        rt.DOKill();
        continueArrow.DOKill();

        rt.anchoredPosition = arrowBasePos;
        rt.DOAnchorPos(arrowBasePos + new Vector2(0f, -7f), 0.55f)
          .SetEase(Ease.InOutSine)
          .SetLoops(-1, LoopType.Yoyo);

        continueArrow.alpha = 1f;
        continueArrow.DOFade(0.35f, 0.7f)
          .SetEase(Ease.InOutSine)
          .SetLoops(-1, LoopType.Yoyo);
    }

    private void StopArrowLoops()
    {
        if (continueArrow == null) return;
        continueArrow.DOKill();
        var rt = (RectTransform)continueArrow.transform;
        rt.DOKill();
        rt.anchoredPosition = arrowBasePos;
        continueArrow.alpha = 1f;
    }

    #endregion
}
