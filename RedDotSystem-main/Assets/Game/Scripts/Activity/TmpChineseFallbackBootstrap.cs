using UnityEngine;
using TMPro;

namespace Game.Activity
{
    /// <summary>
    /// TMP 中文回退兜底：桥的 TMP Essentials 自动导入会把 TMP Settings 的 fallbackFontAssets 清空，
    /// 这里在进入游戏前把 Resources 里的 NotoSansSC 追加回回退列表，避免活动界面中文变方块。
    /// 幂等：已存在就不重复添加。
    /// </summary>
    public static class TmpChineseFallbackBootstrap
    {
        public const string FontResourcePath = "Fonts/NotoSansSC-Regular SDF";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnsureFallback()
        {
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>(FontResourcePath);
            if (font == null)
            {
                Debug.LogWarning("[TMP] 未找到中文回退字体：" + FontResourcePath);
                return;
            }

            var fallbacks = TMP_Settings.fallbackFontAssets;
            if (fallbacks == null) return;
            if (fallbacks.Contains(font)) return;

            fallbacks.Add(font);
            Debug.Log("[TMP] 已注入中文回退字体：" + font.name);
        }
    }
}