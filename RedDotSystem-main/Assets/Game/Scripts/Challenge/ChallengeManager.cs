using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 挑战（击杀竞速）场景总控。
///
/// 职责：
///   - 开局打开挑战 HUD，用配置血量初始化玩家/怪物，开始计时
///   - 每帧把计时与怪物血量推给 HUD（HUD 显示的就是 HealthModel 的真实血量，不再另存一份）
///   - 怪物死亡 = 挑战成功（显示击杀用时）；玩家死亡 = 挑战失败
///
/// 挂在 tiaozhan.scene 的 ChallengeManager 物体上，player / monster 在 Inspector 里接。
/// 说明：这个类在 2026-10-21 的一次 git reset 中丢失，此处按场景里遗留的序列化字段
/// （player / playerHealth / monster / monsterHealth / playerMaxHP / monsterMaxHP）重建。
/// </summary>
public class ChallengeManager : MonoBehaviour
{
    [Header("玩家")]
    public Player player;
    public HealthModel playerHealth;

    [Header("怪物")]
    public GameObject monster;
    public HealthModel monsterHealth;

    [Header("血量配置")]
    public float playerMaxHP = 1000f;
    public float monsterMaxHP = 300f;

    /// <summary>挑战是否进行中（结束后不再刷新 HUD）</summary>
    private bool running;
    private bool opened;
    private float startTime;
    private float elapsedMs;

    private void Start()
    {
        ResolveRefs();

        if (playerHealth != null) playerHealth.InitStats(playerMaxHP);
        if (monsterHealth != null) monsterHealth.InitStats(monsterMaxHP);

        startTime = Time.time;
        running = true;

        UIManager.Instance.Open(UIType.ChallengeHudView).Forget();
        opened = true;
        Debug.Log($"[挑战] 开始：玩家 {playerMaxHP:F0} HP，怪物 {monsterMaxHP:F0} HP");
    }

    private void ResolveRefs()
    {
        if (player == null) player = FindObjectOfType<Player>();
        if (playerHealth == null && player != null) playerHealth = player.GetComponent<HealthModel>();

        if (monster == null)
        {
            var monsterCtrl = FindObjectOfType<ChallengeMonster>();
            if (monsterCtrl != null) monster = monsterCtrl.gameObject;
        }
        if (monsterHealth == null && monster != null) monsterHealth = monster.GetComponent<HealthModel>();

        if (playerHealth == null) Debug.LogWarning("[挑战] 没接玩家 HealthModel，玩家血量不会受控");
        if (monsterHealth == null) Debug.LogWarning("[挑战] 没接怪物 HealthModel，怪物血条不会显示");
    }

    private void Update()
    {
        var hud = GetHud();
        if (hud == null) return;

        if (running)
        {
            elapsedMs = (Time.time - startTime) * 1000f;

            // 怪物血条：直接读 HealthModel，保证与真实血量一致（此前"HUD 血条对不上"就是两套数据）
            if (monsterHealth != null)
                hud.SetMonsterHp(monsterHealth.CurrentHP.Value, monsterHealth.MaxHP.Value);

            hud.SetTimer((long)elapsedMs);

            if (monsterHealth != null && !monsterHealth.IsAlive)
                Finish(true);
            else if (playerHealth != null && !playerHealth.IsAlive)
                Finish(false);

            return;
        }

        // 结束后血条保持最终值
        if (monsterHealth != null)
            hud.SetMonsterHp(monsterHealth.CurrentHP.Value, monsterHealth.MaxHP.Value);
    }

    /// <summary>一次性的开局准备（直接 Play 该场景时也保证 HUD 打开）</summary>
    private static ChallengeHudView GetHud()
    {
        if (!UIManager.Instance.IsOpen(UIType.ChallengeHudView))
        {
            if (!warned) { warned = true; Debug.LogWarning("[挑战] ChallengeHudView 未打开，计时/血条不会刷新"); }
            return null;
        }
        return UIManager.Instance.GetView<ChallengeHudView>(UIType.ChallengeHudView);
    }
    private static bool warned;

    private void Finish(bool success)
    {
        if (!running) return;
        running = false;

        var hud = GetHud();
        if (hud == null) return;

        hud.ShowResult(success, (long)elapsedMs);
        hud.SetRankText(success
            ? $"击杀用时 {(long)elapsedMs / 1000f:F2} 秒"
            : "再试一次，缩短击杀时间");
        Debug.Log($"[挑战] {(success ? "成功" : "失败")}，用时 {elapsedMs:F0} ms");
    }

    private void OnDestroy()
    {
        if (opened && UIManager.Instance != null && UIManager.Instance.IsOpen(UIType.ChallengeHudView))
            UIManager.Instance.Close(UIType.ChallengeHudView).Forget();
        warned = false;
    }
}
