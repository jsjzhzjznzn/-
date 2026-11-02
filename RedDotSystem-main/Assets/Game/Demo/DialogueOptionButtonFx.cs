using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 对话选项按钮的悬停/按下反馈（由 DialogueUiBuilder 自动挂载并绑定描边/左侧强调条）。
/// 只做表现层，不改变按钮的点击逻辑。
/// </summary>
public class DialogueOptionButtonFx : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    [SerializeField] private Image border;
    [SerializeField] private RectTransform accent;

    [Header("手感")]
    [SerializeField] private float hoverScale = 1.025f;
    [SerializeField] private float duration = 0.12f;

    private static readonly Color BorderNormal = new Color(0.91f, 0.77f, 0.42f, 0.30f);
    private static readonly Color BorderHover = new Color(0.91f, 0.77f, 0.42f, 0.95f);

    private Vector3 baseScale = Vector3.one;
    private float accentWidth;
    private bool hovering;

    private void Awake()
    {
        baseScale = transform.localScale;
        if (accent != null) accentWidth = accent.sizeDelta.x;
        if (border != null) border.color = BorderNormal;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        hovering = true;
        transform.DOKill();
        transform.DOScale(baseScale * hoverScale, duration).SetEase(Ease.OutQuad);
        if (border != null) border.DOColor(BorderHover, duration);
        if (accent != null) accent.DOSizeDelta(new Vector2(accentWidth + 3f, accent.sizeDelta.y), duration);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        hovering = false;
        transform.DOKill();
        transform.DOScale(baseScale, duration).SetEase(Ease.OutQuad);
        if (border != null) border.DOColor(BorderNormal, duration);
        if (accent != null) accent.DOSizeDelta(new Vector2(accentWidth, accent.sizeDelta.y), duration);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        transform.DOKill();
        transform.DOScale(baseScale * 0.985f, 0.06f).SetEase(Ease.OutQuad);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        transform.DOKill();
        transform.DOScale(baseScale * (hovering ? hoverScale : 1f), 0.08f).SetEase(Ease.OutQuad);
    }

    private void OnDisable()
    {
        transform.DOKill();
        transform.localScale = baseScale;
        if (border != null) border.color = BorderNormal;
        if (accent != null) accent.sizeDelta = new Vector2(accentWidth, accent.sizeDelta.y);
    }
}
