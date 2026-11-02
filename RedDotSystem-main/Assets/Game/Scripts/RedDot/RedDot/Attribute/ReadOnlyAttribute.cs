using UnityEditor;
using UnityEngine;

namespace MH
{
    /// <summary>
    /// 字段只读特性：让字段在 Inspector 中置灰、仅供查看不可编辑。
    /// 用法：[SerializeField, ReadOnly]，配合下方 ReadOnlyDrawer 绘制生效。
    /// 红点配置数据用它防止在 Inspector 里绕过编辑窗口误改。
    /// </summary>
    public class ReadOnlyAttribute : PropertyAttribute
    {
    }

#if UNITY_EDITOR
    /// <summary>ReadOnly 特性的 Inspector 绘制器：绘制期间禁用 GUI 交互，实现"只读展示"</summary>
    [CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
    public class ReadOnlyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            GUI.enabled = false;  // 禁用交互 → 只读
            EditorGUI.PropertyField(position, property, label, true);
            GUI.enabled = true;  // 恢复交互
        }
    }
#endif
}
