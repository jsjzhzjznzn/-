using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 新手引导全屏遮罩（自定义 Graphic）：
/// - 视觉：GuideMask.shader 按 _HoleRect 挖洞（SDF 圆角矩形 + 呼吸描边）；
/// - 输入：重写 Raycast——洞内不命中（点击穿透给下层真按钮，原生按压反馈保留），
///         洞外命中（挡住引导期间的一切误触）；
///         blockAll=true 时洞内也命中（讲解步：高亮但不放行点击）。
/// holeLocalRect 与 shader 的 _HoleRect 由 GuideManager 每帧写入同一份数据，视觉与命中像素级一致。
///
/// 注意：Tuanjie 的 ugui fork 里 Graphic 基类只 RequireComponent(RectTransform)，
/// 不像标准 Unity 会自动带 CanvasRenderer——必须本类自声明，否则不渲染且 GraphicRaycaster 报错。
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class GuideMaskGraphic : MaskableGraphic
{
    /// <summary>洞在遮罩本地坐标系的矩形（GuideManager 每帧写入）</summary>
    public Rect holeLocalRect;

    /// <summary>信息步：无洞全遮罩、全屏挡输入</summary>
    public bool fullDim;

    /// <summary>讲解步：照常画洞，但洞内也拦截点击（只高亮，不让玩家误开面板）</summary>
    public bool blockAll;

    public override Texture mainTexture => Texture2D.whiteTexture;

    public override bool Raycast(Vector2 sp, Camera eventCamera)
    {
        if (fullDim) return true;
        if (blockAll) return true;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform, sp, eventCamera, out Vector2 local))
        {
            return true;
        }
        return !holeLocalRect.Contains(local);
    }
}
