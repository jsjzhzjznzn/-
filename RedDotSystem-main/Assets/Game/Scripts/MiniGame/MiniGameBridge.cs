using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

namespace Game.MiniGame
{
    /// <summary>
    /// 活动内容页 → 小游戏场景的桥（供 Lua 侧调用）。
    /// 只负责次数校验 + 关面板 + 走统一的 SceneLoader（自带加载条）。
    /// 次数目前记在本机（按日期），服务端权威次数待协议补齐后替换。
    /// </summary>
    public static class MiniGameBridge
    {
        public const string FogHuntScene = "fog_hunt";

        /// <summary>每日次数上限</summary>
        public const int FogHuntDailyLimit = 3;

        private static string CountKey(int activityId)
        {
            return "foghunt.runs." + activityId + "." + System.DateTime.Now.ToString("yyyyMMdd");
        }

        /// <summary>今日剩余次数（activityId 默认 4 = 宁静信标活动）。</summary>
        public static int GetRemainRuns(int activityId = 4)
        {
            int used = PlayerPrefs.GetInt(CountKey(activityId), 0);
            return Mathf.Max(0, FogHuntDailyLimit - used);
        }

        /// <summary>进入《迷雾信标》。返回 false 表示次数用完或正在切场景。</summary>
        public static bool EnterFogHunt(int activityId = 4)
        {
            if (SceneLoader.IsLoading) return false;
            if (GetRemainRuns(activityId) <= 0) return false;

            PlayerPrefs.SetInt(CountKey(activityId), PlayerPrefs.GetInt(CountKey(activityId), 0) + 1);
            PlayerPrefs.Save();

            EnterFogHuntAsync().Forget();
            return true;
        }

        /// <summary>
        /// 先收起所有"主页侧"界面再切场景。
        /// 主页入口栏（HomeView）是登记过的常驻 UI，会跨场景保留 —— 不关的话
        /// 进 fog_hunt 后它还盖在小游戏 HUD 上（全屏背景 + 入口按钮 + 遮罩拦点击）。
        /// </summary>
        private static async UniTaskVoid EnterFogHuntAsync()
        {
            var ui = UIManager.Instance;
            if (ui != null)
            {
                if (ui.IsOpen(UIType.ActivityView))
                    await ui.Close(UIType.ActivityView);
                if (ui.IsOpen(UIType.HomeView))
                    await ui.Close(UIType.HomeView);
            }

            await SceneLoader.LoadAsync(FogHuntScene);
        }

        /// <summary>从活动页判断当前是否已经在小游戏里。</summary>
        public static bool IsInFogHunt()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return scene.name == FogHuntScene;
        }
    }
}
