using UnityEngine;
using TMPro;

/// <summary>
/// TMP 输入框高度自适应示例（实验用）
/// 监听文字变化，按文字实际 preferredHeight 撑开背景框，超过 maxHeight 后封顶不再拉伸
/// 用法：挂在 InputField(TMP) 根物体（带 Image 背景的那个）上
/// 提示：轴心(pivot)Y=0 时向上长高（聊天框常用），Y=1 时向下长高
/// </summary>
[RequireComponent(typeof(TMP_InputField))]
public class TMPInputAutoSize : MonoBehaviour
{
    [Header("上下内边距（文字到框边的留白）")]
    public float paddingTop = 7;

    public float paddingBottom = 7;

    [Header("最小高度（空文本时的框高）")]
    public float minHeight = 40;

    [Header("最大高度，超过不再拉伸（配合 TextViewport 裁剪滚动）")]
    public float maxHeight = 220;

    private TMP_InputField inputField;
    private RectTransform bgRect;

    private void Awake()
    {
        inputField = GetComponent<TMP_InputField>();
        bgRect = (RectTransform)transform;

        // 监听文本变化
        inputField.onValueChanged.AddListener(OnTextChange);

        // 初始化时也量一次（处理场景里预填了文字的情况）
        OnTextChange(inputField.text);
    }

    private void OnTextChange(string text)
    {
        // 关键：强制重建文本网格，否则 preferredHeight 是上一次的旧值
        inputField.textComponent.ForceMeshUpdate();
        float textPreferredH = inputField.textComponent.preferredHeight;

        // 文字实际高度 + 上下边距，并夹在 [min, max] 区间
        float newH = textPreferredH + paddingTop + paddingBottom;
        newH = Mathf.Clamp(newH, minHeight, maxHeight);

        // SetSizeWithCurrentAnchors：不管锚点怎么设，都按"高度=newH"处理（比直接写 sizeDelta 稳）
        bgRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, newH);
    }
}
