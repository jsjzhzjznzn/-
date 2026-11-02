#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MH
{
    // [CreateAssetMenu(fileName = "RedDotEditorSettings", menuName = "RedDot/RedDotEditorSettings")]

    /// <summary>
    /// 编辑器红点工具设置（Editor 专用，不会打进包体）：
    /// 记录两个核心配置资产的路径，供 RedDotKeyWindow / RedDotConfigWindow 打开时自动定位加载。
    /// 资产固定存放在 Assets/Game/Resources/RedDotEditorSettings.asset。
    /// 注意：移动了 RedDotKeyAsset/RedDotConfigAsset 资产位置时，需同步更新这里的路径。
    /// </summary>
    public class RedDotEditorSettings : ScriptableObject
    {
        // 红点Key表（RedDotKeyAsset）的资产路径
        public string redDotKeyAssetPath = "Assets/Game/Configs/RedDot/RedDotKeyAsset.asset";
        // 红点关系配置表（RedDotConfigAsset）的资产路径
        public string redDotConfigAssetPath = "Assets/Game/Configs/RedDot/RedDotConfigAsset.asset";

        private static RedDotEditorSettings instance;

        /// <summary>
        /// 编辑器单例：优先从 Resources 加载；
        /// 不存在时自动在 Assets/Game/Resources 下创建该资产并保存。
        /// </summary>
        public static RedDotEditorSettings Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = Resources.Load<RedDotEditorSettings>("RedDotEditorSettings");
                    if (instance == null)
                    {
                        instance = CreateInstance<RedDotEditorSettings>();
                        // Resources 目录不存在则先创建
                        if (!AssetDatabase.IsValidFolder("Assets/Game/Resources"))
                        {
                            AssetDatabase.CreateFolder("Assets", "Resources");
                        }

                        AssetDatabase.CreateAsset(instance, "Assets/Game/Resources/RedDotEditorSettings.asset");
                        AssetDatabase.SaveAssets();
                    }
                }

                return instance;
            }
        }
    }
}
#endif
