using System;

using DG.Tweening;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 阿福面板的 DOTween 动画层：只做表现，不含任何协议/数据逻辑。
/// 全部补间 SetUpdate(true)（unscaled）——与项目"UI 表现不随时间缩放冻结"的暂停口径一致，
/// 也让面板在 timeScale=0（暂停/对话冻结）时照常开合。
///
/// 池化对象（气泡）使用约定：换绑前调用方先 Kill 复位（见 AgentUIController.BindRow），
/// 回收时由 VirtualScrollList.RecycleItem 再 Kill 一次，防补间跑到池里的旧对象上。
/// </summary>
public static class AgentTweenFx
{
    /// <summary>面板展开时长</summary>
    public const float PanelOpenDuration = 0.22f;

    /// <summary>面板收起时长（比展开短：收起要"干脆"）</summary>
    public const float PanelCloseDuration = 0.16f;

    /// <summary>气泡入场时长</summary>
    public const float BubbleInDuration = 0.26f;

    /// <summary>多个新气泡同批到达时的错帧步长</summary>
    public const float BubbleStaggerStep = 0.045f;

    /// <summary>错帧上限（一次来十条也只错开这么多，别让最后一条等太久）</summary>
    public const float BubbleStaggerMax = 0.2f;

    /// <summary>面板收起时相对静止位的偏移（从屏幕右缘往里滑）</summary>
    public const float PanelSlideOffset = 16f;

    /// <summary>状态文案停留多久后开始淡出</summary>
    public const float StatusHoldSeconds = 2.6f;

    /// <summary>状态文案淡出时长</summary>
    public const float StatusFadeSeconds = 0.6f;

    /// <summary>思考点弹跳周期</summary>
    public const float DotBounceDuration = 0.3f;

    /// <summary>思考点之间的相位差</summary>
    public const float DotPhaseStep = 0.12f;

    // ──────────────────────────────
    // 面板开合
    // ──────────────────────────────

    /// <summary>面板展开：淡入 + 0.97→1 回弹 + 从右缘滑入；结束回调用于解除"动画中"锁。
    /// restScale = 玩家当前的缩放档位（补间只在 restScale*0.97 → restScale 之间动，缩放不会被复位成 1）。</summary>
    public static void ShowPanel(RectTransform rect, CanvasGroup group, Vector2 restPos, float restScale, Action onDone)
    {
        if (rect == null || group == null) { onDone?.Invoke(); return; }

        float target = restScale > 0.01f ? restScale : 1f;

        rect.DOKill();
        group.DOKill();
        group.alpha = 0f;
        rect.localScale = Vector3.one * (target * 0.97f);
        rect.anchoredPosition = restPos + new Vector2(PanelSlideOffset, 0f);

        Sequence sequence = DOTween.Sequence().SetUpdate(true);
        sequence.Join(group.DOFade(1f, PanelOpenDuration));
        sequence.Join(rect.DOScale(target, PanelOpenDuration + 0.04f).SetEase(Ease.OutBack, 0.55f));
        sequence.Join(rect.DOAnchorPos(restPos, PanelOpenDuration).SetEase(Ease.OutCubic));
        sequence.OnComplete(() => onDone?.Invoke());
    }

    /// <summary>面板收起：淡出 + 缩小 + 往右缘滑走；结束回调里复位（下次展开从干净状态起手）。</summary>
    public static void HidePanel(RectTransform rect, CanvasGroup group, Vector2 restPos, float restScale, Action onDone)
    {
        if (rect == null || group == null) { onDone?.Invoke(); return; }

        float target = restScale > 0.01f ? restScale : 1f;

        rect.DOKill();
        group.DOKill();

        Sequence sequence = DOTween.Sequence().SetUpdate(true);
        sequence.Join(group.DOFade(0f, PanelCloseDuration));
        sequence.Join(rect.DOScale(target * 0.97f, PanelCloseDuration).SetEase(Ease.InCubic));
        sequence.Join(rect.DOAnchorPos(restPos + new Vector2(PanelSlideOffset * 0.6f, 0f), PanelCloseDuration).SetEase(Ease.InCubic));
        sequence.OnComplete(() =>
        {
            group.alpha = 1f;
            rect.localScale = Vector3.one * target;
            rect.anchoredPosition = restPos;
            onDone?.Invoke();
        });
    }

    // ──────────────────────────────
    // 气泡入场
    // ──────────────────────────────

    /// <summary>
    /// 气泡入场：0.94→1 回弹 + 淡入。
    /// 只动 localScale / CanvasGroup.alpha —— 不动 anchoredPosition，
    /// 因为 VirtualScrollList 在 BindItem 返回后会写入 y，位置补间会被布局覆写（踩过同类）。
    /// </summary>
    public static void BubbleIn(RectTransform item, CanvasGroup group, float delay)
    {
        if (item == null) return;

        item.DOKill();
        item.localScale = Vector3.one * 0.94f;
        if (group != null)
        {
            group.DOKill();
            group.alpha = 0f;
        }

        Sequence sequence = DOTween.Sequence().SetUpdate(true).SetDelay(delay);
        sequence.Join(item.DOScale(1f, BubbleInDuration).SetEase(Ease.OutBack, 0.7f));
        if (group != null) sequence.Join(group.DOFade(1f, BubbleInDuration * 0.65f));
    }

    /// <summary>复位气泡表现（换绑/回收时调用，防池化对象带着旧补间或旧缩放复活）。</summary>
    public static void ResetBubble(RectTransform item)
    {
        if (item == null) return;
        item.DOKill();
        item.localScale = Vector3.one;
        CanvasGroup group = item.GetComponent<CanvasGroup>();
        if (group != null)
        {
            group.DOKill();
            group.alpha = 1f;
        }
    }

    // ──────────────────────────────
    // 思考中三点指示器
    // ──────────────────────────────

    /// <summary>三个点错相呼吸弹跳（无限循环，unscaled）。</summary>
    public static void StartThinkingDots(Transform dotsRoot)
    {
        if (dotsRoot == null) return;

        int index = 0;
        foreach (Transform dot in dotsRoot)
        {
            dot.DOKill();
            dot.localScale = Vector3.one;
            dot.DOScale(1.5f, DotBounceDuration)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .SetDelay(index * DotPhaseStep)
                .SetUpdate(true);
            index++;
        }
    }

    /// <summary>停掉思考点并复位缩放。</summary>
    public static void StopThinkingDots(Transform dotsRoot)
    {
        if (dotsRoot == null) return;
        foreach (Transform dot in dotsRoot)
        {
            dot.DOKill();
            dot.localScale = Vector3.one;
        }
    }

    // ──────────────────────────────
    // 提示与反馈
    // ──────────────────────────────

    /// <summary>状态文案淡出（到点自动清空，修掉"提示不自动清除"）。</summary>
    public static Tween FadeStatus(TMP_Text text)
    {
        if (text == null) return null;
        text.DOKill();
        Color color = text.color;
        color.a = 1f;
        text.color = color;
        return text.DOFade(0f, StatusFadeSeconds)
            .SetDelay(StatusHoldSeconds)
            .SetEase(Ease.InQuad)
            .SetUpdate(true);
    }

    /// <summary>左右抖动（校验失败/服务端拒绝时给"被驳回"的体感）。</summary>
    public static void Shake(RectTransform target, Action onRestore = null)
    {
        if (target == null) return;
        target.DOKill();
        Vector2 rest = target.anchoredPosition;
        target.DOShakeAnchorPos(0.3f, new Vector2(8f, 0f), 14, 90f, false, true)
            .SetUpdate(true)
            .OnComplete(() =>
            {
                if (target != null) target.anchoredPosition = rest;
                onRestore?.Invoke();
            });
    }

    /// <summary>入口按钮脉动（关闭状态下阿福回话了：轻拍三下提示）。</summary>
    public static Tween PulseEntry(Transform entry)
    {
        if (entry == null) return null;
        entry.DOKill();
        entry.localScale = Vector3.one;
        return entry.DOPunchScale(new Vector3(0.08f, 0.08f, 0f), 0.42f, 5, 0.7f)
            .SetLoops(3, LoopType.Restart)
            .SetUpdate(true);
    }

    /// <summary>停下入口脉动并复位缩放。</summary>
    public static void StopEntryPulse(Transform entry)
    {
        if (entry == null) return;
        entry.DOKill();
        entry.localScale = Vector3.one;
    }

    // ──────────────────────────────
    // 平滑滚到底
    // ──────────────────────────────

    /// <summary>
    /// 平滑滚到最新一条：按"要走的像素距离"折算出时长（贴底时直接就位不空转）。
    /// 位置读写走调用方给的 getter/setter——调用方要在 setter 里打标记，
    /// 才能把自己驱动的滚动与"用户手滚"区分开（用户一上手就掐掉自动滚动）。
    /// onUpdate 里由调用方 RefreshView（虚拟列表要跟着滚动位置换绑）。
    /// </summary>
    public static Tween SmoothScrollToBottom(Func<float> getter, Action<float> setter, float contentHeight, Action onUpdate)
    {
        if (getter == null || setter == null) return null;

        Canvas.ForceUpdateCanvases();   // 内容尺寸刚变过，先让 ScrollRect bounds 跟上，否则赋值空操作
        float start = getter();
        if (start <= 0.001f)
        {
            setter(0f);
            onUpdate?.Invoke();
            return null;
        }

        float distance = Mathf.Max(0f, contentHeight) * Mathf.Clamp01(start);
        float duration = Mathf.Clamp(distance / 2600f, 0.12f, 0.3f);
        return DOTween.To(() => getter(),
                          value => setter(value),
                          0f,
                          duration)
            .SetEase(Ease.OutCubic)
            .SetUpdate(true)
            .OnUpdate(() => onUpdate?.Invoke());
    }
}
