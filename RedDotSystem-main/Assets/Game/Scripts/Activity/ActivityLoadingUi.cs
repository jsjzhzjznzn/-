using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Activity
{
    /// <summary>
    /// 给 Lua 用的加载遮罩薄封装：复用工程 LoadingScreen（全屏进度条），
    /// 提供同步的 Show/SetProgress/Hide，避免 Lua 侧处理 UniTask。
    /// </summary>
    public static class ActivityLoadingUi
    {
        private static LoadingScreen _screen;

        /// <summary>显示加载遮罩（text 作为“场景名”显示在进度条上方）</summary>
        public static void Show(string text)
        {
            if (_screen != null) Hide();
            _screen = LoadingScreen.Show(string.IsNullOrEmpty(text) ? "加载中" : text);
            _screen.SetProgress(0f);
        }

        /// <summary>更新进度（0~1）</summary>
        public static void SetProgress(float value)
        {
            if (_screen == null) return;
            _screen.SetProgress(Mathf.Clamp01(value));
        }

        /// <summary>隐藏加载遮罩</summary>
        public static void Hide()
        {
            if (_screen == null) return;
            LoadingScreen screen = _screen;
            _screen = null;
            screen.HideAsync().Forget();
        }

        public static bool IsVisible => _screen != null;
    }
}
