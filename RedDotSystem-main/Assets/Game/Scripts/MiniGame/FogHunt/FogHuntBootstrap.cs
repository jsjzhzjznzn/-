using TMPro;
using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>
    /// 场景入口：挂在 fog_hunt.scene 的根对象上，运行时构建整个小游戏。
    /// seed = 0 时使用「每日种子」，同一天进来是同一张图。
    /// </summary>
    public sealed class FogHuntBootstrap : MonoBehaviour
    {
        [Header("字体（中文 HUD）")]
        public TMP_FontAsset font;

        [Header("关卡参数")]
        public int width = 40;
        public int height = 40;
        [Tooltip("0 = 每日种子；其它值 = 固定种子")]
        public int seed = 0;
        public int beacons = 3;
        public int guards = 4;
        public int playerSight = 6;
        public int guardSight = 5;
        public int maxTurns = 120;

        private void Awake()
        {
            int finalSeed = seed != 0 ? seed : DailySeed();

            var options = new FogHuntGame.Options
            {
                Width = Mathf.Max(12, width),
                Height = Mathf.Max(12, height),
                Seed = finalSeed,
                Beacons = Mathf.Max(1, beacons),
                Guards = Mathf.Max(1, guards),
                PlayerSight = Mathf.Max(2, playerSight),
                GuardSight = Mathf.Max(2, guardSight),
                MaxTurns = Mathf.Max(20, maxTurns),
            };

            var game = gameObject.AddComponent<FogHuntGame>();
            game.Init(options, font);
        }

        private static int DailySeed()
        {
            var now = System.DateTime.Now;
            return now.Year * 10000 + now.Month * 100 + now.Day;
        }
    }
}
