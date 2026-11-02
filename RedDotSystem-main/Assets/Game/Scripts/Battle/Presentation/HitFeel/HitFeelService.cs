using System.Collections;
using System.Collections.Generic;

using Animancer;

using MieMieFrameWork;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 打击感统一入口（单例，运行时自举，无需挂场景）。
///
/// 一次命中要同时发生的事都收在这里，避免又散回各个状态/连招代码：
///   顿帧（局部 PauseGraph / 全局 timeScale） → 相机震屏 → FOV 冲击 → 命中特效 → 命中音效。
/// 受击方的闪白/抖动/击退由挂在角色上的 <see cref="HitReaction"/> 自己做（它订阅 E_OnDamage）。
///
/// 顿帧分两档（见方案文档 §4.1）：
///   · ≤ profile.localHitStopMax：只暂停攻方与受击方的 Animancer 动画图，
///     相机/UI/物理照常走——轻击手感"顺"，不会把整个世界卡住；
///   · 更长（重击/弹刀/极限闪避/击杀）：走 Player.HitStop → PauseManager 全局压低 timeScale，
///     逻辑与物理一起停，最"实"。
/// </summary>
[DisallowMultipleComponent]
public class HitFeelService : MonoBehaviour
{
    public static HitFeelService Instance { get; private set; }

    /// <summary>当前配置（可能在 Awake 之前被读到，所以允许为 null，调用方要兜底）</summary>
    public static HitFeelProfile Profile => Instance != null ? Instance.profile : null;

    [SerializeField] private HitFeelProfile profile;

    [Header("全屏闪光")]
    [Tooltip("闪光层排序（高于 UiFx 的 500，低于暂停面板）")]
    [SerializeField] private int flashSortingOrder = 520;

    /// <summary>调试/E2E：累计命中反馈次数（玩家打怪 + 怪打玩家）</summary>
    public int HitCount { get; private set; }

    /// <summary>调试/E2E：弹刀 / 极限闪避 / 击杀特写触发次数</summary>
    public int DeflectCount { get; private set; }
    public int PerfectDodgeCount { get; private set; }
    public int KillCamCount { get; private set; }

    /// <summary>调试/E2E：当前全屏闪光强度（0 = 没在闪）</summary>
    public float FlashAlpha => flashPeakAlpha;

    /// <summary>局部顿帧引用计数：同一个 Animancer 被多次冻结时只有归零才恢复</summary>
    private readonly Dictionary<AnimancerComponent, int> localPauses = new();

    // 全屏闪光（懒建：自举发生在 BeforeSceneLoad，那时 UICameraService 还没准备好）
    private Canvas flashCanvas;
    private Image flashImage;
    private Color flashColor;
    private float flashPeakAlpha;
    private float flashRemaining;
    private float flashDuration;

    private float nextDodgeTime;
    private float nextKillTime;
    private bool damageTextEnsured;

    // ──────────────────────────────
    // 单例
    // ──────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("HitFeelService");
        holder.AddComponent<HitFeelService>();
        DontDestroyOnLoad(holder);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // 自举建出来的对象没有序列化引用，用 Resources 兜底（与 VfxManager 同款做法）
        if (profile == null) profile = Resources.Load<HitFeelProfile>("Battle/HitFeelProfile");
        if (profile == null)
            Debug.LogWarning("[打击感] 找不到 Assets/Game/Resources/Battle/HitFeelProfile.asset，将使用内置默认值");

        // 极限闪避 / 击杀特写都靠事件驱动（挑战场景里没有别的系统订阅它们）
        EventCenter.MainInstance.AddListener<DamageData>(E_EventType.E_DamageBlocked, this, OnDamageBlocked);
        EventCenter.MainInstance.AddListener<DeathData>(E_EventType.E_OnDeath, this, OnDeath);
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        EventCenter.MainInstance.UnregisterTarget(this);
        Instance = null;
    }

    private void Start()
    {
        EnsureDamageText();
    }

    // ──────────────────────────────
    // 对外入口
    // ──────────────────────────────

    /// <summary>
    /// 玩家打中敌人：顿帧 + 震屏 + 命中特效 + 命中音。
    /// </summary>
    /// <param name="pauseSeconds">招式配置的顿帧时长（ComboData.pauseFrameTime，0 表示用兜底值）</param>
    /// <param name="shakeForce">招式配置的震屏力度（0 表示用兜底值）</param>
    public static void PlayerHitEnemy(Player player, HealthModel victim, Collider hitCollider,
        float pauseSeconds, float shakeForce, bool isHeavy, bool isCritical)
    {
        HitFeelService service = Instance;
        if (service == null || player == null || victim == null) return;

        HitFeelProfile p = service.profile;

        Vector3 hitPoint = ResolveHitPoint(hitCollider, victim.transform.position + Vector3.up * 0.9f);
        Vector3 attackDirection = victim.transform.position - player.transform.position;
        attackDirection.y = 0f;

        // 1) 顿帧（可关：玩家要求"打怪不要暂停感"，怪连续挂在受击状态里）
        bool allowHitStop = p == null || p.hitStopOnPlayerHit;

        if (allowHitStop)
        {
            float pause = pauseSeconds > 0f
                ? pauseSeconds
                : (isHeavy ? 0.12f : 0.05f);
            float localMax = p != null ? p.localHitStopMax : 0.09f;

            if (pause <= localMax)
            {
                AnimancerComponent enemyAnimancer = victim.GetComponentInParent<EnemyBrain>()?.Animancer;
                service.LocalHitStop(pause, player.characterAnimancer, enemyAnimancer);
            }
            else
            {
                float scale = p != null ? p.globalHitStopScale : 0.05f;
                player.HitStop(pause, scale);
            }
        }

        // 2) 相机震屏 + 每次命中的轻微 FOV 收紧（ZZZ 的"镜头跟着刀一顿"）
        float shake = shakeForce > 0f
            ? shakeForce
            : (isHeavy ? (p != null ? p.heavyHitShake : 0.55f) : (p != null ? p.lightHitShake : 0.18f));
        CameraHitFeel.ShakeAt(hitPoint, shake, attackDirection);
        CameraHitFeel.PunchFov(isHeavy ? -1.5f : -0.6f, 0.12f);

        // 3) 特效 + 4) 音效
        service.PlayHitVfx(hitPoint, isHeavy);
        service.PlayHitSound(victim, isHeavy);
        service.HitCount++;
    }

    /// <summary>
    /// BOSS 打中玩家：全局顿帧 + 震屏 + 受击特效/音效。
    /// </summary>
    public static void EnemyHitPlayer(EnemyBrain enemy, Player player, Vector3 hitPoint, bool isHeavy)
    {
        HitFeelService service = Instance;
        HitFeelProfile p = service != null ? service.profile : null;

        if (player != null)
        {
            float seconds = p != null ? p.enemyHitStopSeconds : 0.07f;
            float scale = p != null ? p.enemyHitStopScale : 0.06f;
            player.HitStop(seconds, scale);
        }

        float shake = p != null ? p.enemyHitShake : 0.32f;
        Vector3 direction = enemy != null ? hitPoint - enemy.transform.position : Vector3.down;
        CameraHitFeel.ShakeAt(hitPoint, shake, direction);

        service?.PlayHitVfx(hitPoint, isHeavy, player);
        service?.PlayHitSound(player, isHeavy);
        if (service != null) service.HitCount++;
    }

    // ──────────────────────────────
    // 高光时刻（ZZZ 式）
    // ──────────────────────────────

    /// <summary>
    /// 弹刀（拼刀成功）：双方冻结 → 黄色闪光 + 金属音 + 震屏 + FOV 收紧
    /// → 双方后退 → BOSS 短硬直、玩家不掉血、本次不结算伤害。
    /// </summary>
    public static void PlayerDeflect(Player player, EnemyBrain enemy, Vector3 hitPoint)
    {
        HitFeelService service = Instance;
        if (service == null || player == null || enemy == null) return;

        HitFeelProfile p = service.profile;
        service.DeflectCount++;

        // 1) 全局顿帧（弹刀必须"停住"才有拼刀的重量）
        float seconds = p != null ? p.deflectHitStopSeconds : 0.2f;
        float scale = p != null ? p.deflectHitStopScale : 0.02f;
        player.HitStop(seconds, scale);

        // 2) 震屏 + FOV 收紧 + 黄色全屏闪
        Vector3 direction = enemy.transform.position - player.transform.position;
        CameraHitFeel.ShakeAt(hitPoint, p != null ? p.deflectShake : 0.9f, direction);
        CameraHitFeel.PunchFov(-Mathf.Abs(p != null ? p.deflectFovKick : 3f), 0.22f);
        service.ScreenFlash(p != null ? p.deflectFlashColor : new Color(1f, 0.85f, 0.25f),
            0.45f, p != null ? p.deflectFlashSeconds : 0.16f);

        // 3) 特效 + 音效
        service.PlayVfx(VfxKeys.DeflectSpark, hitPoint, 1.2f);
        service.PlayDeflectSound(player);

        // 4) 双方后退 + BOSS 短硬直（硬直时长可配，比破防短）
        Vector3 away = player.transform.position - enemy.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.0001f)
        {
            away.Normalize();
            Vector3 push = -away * (p != null ? p.deflectKnockbackPlayer : 0.25f);
            var controller = player.GetComponent<CharacterController>();
            if (controller != null && controller.enabled) controller.Move(push);
            else player.transform.position += push;

            enemy.MoveAlong(away, p != null ? p.deflectKnockbackEnemy : 0.45f);
        }

        enemy.ForceStagger(p != null ? p.deflectStaggerSeconds : 0.6f);
    }

    /// <summary>
    /// 极限闪避：无敌窗拦截伤害（E_DamageBlocked）→ 子弹时间 + 镜头推近 + 冷色闪光。
    /// </summary>
    private void OnDamageBlocked(DamageData data)
    {
        if (data.target == null) return;
        if (Time.time < nextDodgeTime) return;

        var player = data.target.GetComponent<Player>();
        if (player == null || player.IsDead) return;

        nextDodgeTime = Time.time + 0.9f;
        PerfectDodgeCount++;

        HitFeelProfile p = profile;
        GlobalSlow(p != null ? p.dodgeSlowSeconds : 0.35f, p != null ? p.dodgeSlowScale : 0.25f);
        CameraHitFeel.PunchFov(-Mathf.Abs(p != null ? p.dodgeFovKick : 4f), 0.3f);
        ScreenFlash(p != null ? p.dodgeFlashColor : new Color(0.85f, 0.95f, 1f),
            p != null ? p.dodgeFlashAlpha : 0.22f, p != null ? p.dodgeFlashSeconds : 0.14f);
        player.ActorAudio?.PlayByStyle(SoundStyle.DodgeSound);
    }

    /// <summary>
    /// 击杀特写：慢镜 + FOV 推近 + 暖白闪 + 震屏。只对敌人（带 EnemyBrain）触发。
    /// </summary>
    private void OnDeath(DeathData data)
    {
        if (data.target == null) return;
        if (Time.time < nextKillTime) return;

        var brain = data.target.GetComponent<EnemyBrain>();
        if (brain == null) return;

        nextKillTime = Time.time + 1f;
        KillCamCount++;

        HitFeelProfile p = profile;
        GlobalSlow(p != null ? p.killSlowSeconds : 0.5f, p != null ? p.killSlowScale : 0.15f);
        CameraHitFeel.PunchFov(-Mathf.Abs(p != null ? p.killFovKick : 6f), 0.45f);
        CameraHitFeel.ShakeAt(data.target.transform.position, 0.45f, Vector3.down);
        ScreenFlash(p != null ? p.killFlashColor : new Color(1f, 0.95f, 0.8f),
            p != null ? p.killFlashAlpha : 0.3f, p != null ? p.killFlashSeconds : 0.22f);
    }

    /// <summary>统一的时间减速（走 PauseManager 的顿帧通道，与暂停/顿帧互不覆盖）</summary>
    public void GlobalSlow(float seconds, float scale)
    {
        if (seconds <= 0f) return;
        PauseManager.PushHitStop(Mathf.Clamp(scale, 0.01f, 1f));
        StartCoroutine(PopSlowAfter(seconds));
    }

    private IEnumerator PopSlowAfter(float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        PauseManager.PopHitStop();
    }

    // ──────────────────────────────
    // 全屏闪光（UI 叠层）
    // ──────────────────────────────

    /// <summary>全屏闪光：ZZZ 式的高光反馈，不依赖 URP Volume/后处理</summary>
    public void ScreenFlash(Color color, float alpha, float seconds)
    {
        if (alpha <= 0.001f) return;
        EnsureFlashCanvas();
        if (flashImage == null) return;

        // 同一帧内多次触发取更强的一次，避免互相覆盖
        if (alpha < flashPeakAlpha) return;

        flashColor = color;
        flashPeakAlpha = Mathf.Clamp01(alpha);
        flashDuration = Mathf.Max(0.02f, seconds);
        flashRemaining = flashDuration;

        Color c = flashColor;
        c.a = flashPeakAlpha;
        flashImage.color = c;
    }

    private void EnsureFlashCanvas()
    {
        if (flashCanvas != null) return;

        var canvasGo = new GameObject("HitFeelCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);
        flashCanvas = canvasGo.GetComponent<Canvas>();
        flashCanvas.sortingOrder = flashSortingOrder;
        // 纯展示层：不挂 GraphicRaycaster，绝不参与点击
        UICameraService.ConvertCanvas(flashCanvas, UICameraService.UICamera);

        var imageGo = new GameObject("Flash", typeof(RectTransform), typeof(Image));
        imageGo.transform.SetParent(canvasGo.transform, false);
        var rect = (RectTransform)imageGo.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        flashImage = imageGo.GetComponent<Image>();
        flashImage.raycastTarget = false;
        flashImage.color = new Color(1f, 1f, 1f, 0f);
    }

    private void Update()
    {
        if (flashImage == null || flashPeakAlpha <= 0f) return;

        flashRemaining -= Time.unscaledDeltaTime;
        float k = Mathf.Clamp01(flashRemaining / Mathf.Max(0.0001f, flashDuration));

        Color c = flashColor;
        c.a = k * flashPeakAlpha;
        flashImage.color = c;

        if (flashRemaining <= 0f) flashPeakAlpha = 0f;
    }

    // ──────────────────────────────
    // 顿帧
    // ──────────────────────────────

    /// <summary>
    /// 局部顿帧：暂停这些角色的动画图（相机/UI/物理不受影响）。
    /// 引用了同一角色的多个来源会累加计数，全部到期才恢复。
    /// </summary>
    public void LocalHitStop(float seconds, params AnimancerComponent[] targets)
    {
        if (seconds <= 0f || targets == null || targets.Length == 0) return;

        var paused = new List<AnimancerComponent>(targets.Length);
        for (int i = 0; i < targets.Length; i++)
        {
            AnimancerComponent animancer = targets[i];
            if (animancer == null || animancer.Animator == null) continue;

            localPauses.TryGetValue(animancer, out int count);
            if (count == 0)
            {
                AnimancerPlayable playable = animancer.Playable;
                if (playable != null && playable.IsGraphPlaying) playable.PauseGraph();
            }

            localPauses[animancer] = count + 1;
            paused.Add(animancer);
        }

        if (paused.Count > 0) StartCoroutine(ResumeAfter(seconds, paused));
    }

    private IEnumerator ResumeAfter(float seconds, List<AnimancerComponent> targets)
    {
        // 实时期限：全局顿帧/暂停期间也要能到点恢复
        yield return new WaitForSecondsRealtime(seconds);

        for (int i = 0; i < targets.Count; i++)
        {
            AnimancerComponent animancer = targets[i];
            if (animancer == null)
            {
                localPauses.Remove(animancer);
                continue;
            }

            if (!localPauses.TryGetValue(animancer, out int count)) continue;

            count--;
            if (count > 0)
            {
                localPauses[animancer] = count;
                continue;
            }

            localPauses.Remove(animancer);
            AnimancerPlayable playable = animancer.Playable;
            if (playable != null) playable.UnpauseGraph();
        }
    }

    // ──────────────────────────────
    // 表现
    // ──────────────────────────────

    private void PlayHitVfx(Vector3 hitPoint, bool isHeavy, Component follow = null)
    {
        HitFeelProfile p = profile;
        string key = isHeavy
            ? (p != null ? p.heavyHitVfxKey : "enemy_hit")
            : (p != null ? p.lightHitVfxKey : "enemy_hit");
        if (string.IsNullOrEmpty(key)) return;

        float scale = isHeavy
            ? (p != null ? p.heavyVfxScale : 1.15f)
            : (p != null ? p.lightVfxScale : 0.75f);

        VfxManager vfx = VfxManager.Instance;
        if (vfx == null) return;

        if (follow != null) vfx.PlayAttached(key, follow.transform, Vector3.up * 0.9f, default, scale);
        else vfx.Play(key, hitPoint, default, scale);
    }

    private void PlayHitSound(Component victim, bool isHeavy)
    {
        HitFeelProfile p = profile;
        AudioClip[] clips = isHeavy
            ? (p != null ? p.heavyImpactClips : null)
            : (p != null ? p.lightImpactClips : null);
        if (clips == null || clips.Length == 0) return;

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return;

        float volume = isHeavy
            ? (p != null ? p.heavyImpactVolume : 0.95f)
            : (p != null ? p.lightImpactVolume : 0.7f);

        AudioManager audio = AudioManager.Instance;
        if (audio != null) audio.PlayEffect(clip, volume, true, victim);
    }

    /// <summary>按 VfxCatalog 的键播一次世界空间特效</summary>
    private void PlayVfx(string key, Vector3 position, float scale)
    {
        if (string.IsNullOrEmpty(key)) return;
        VfxManager vfx = VfxManager.Instance;
        if (vfx != null) vfx.Play(key, position, default, scale);
    }

    /// <summary>弹刀金属音（IMPT-blocks）；没有配置就退回重击命中音</summary>
    private void PlayDeflectSound(Component at)
    {
        HitFeelProfile p = profile;
        AudioClip[] clips = p != null ? p.deflectClips : null;

        if (clips == null || clips.Length == 0)
        {
            PlayHitSound(at, true);
            return;
        }

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return;

        AudioManager audio = AudioManager.Instance;
        if (audio != null) audio.PlayEffect(clip, p.deflectVolume, true, at);
    }

    /// <summary>
    /// 确保挑战场景有伤害飘字。
    /// tiaozhan 里没有任何代码访问 DamageTextManager.MainInstance，
    /// 自举单例不会自己出现 → 实测挑战场景一个伤害数字都没有。
    /// </summary>
    private void EnsureDamageText()
    {
        if (damageTextEnsured) return;
        damageTextEnsured = true;
        _ = DamageTextManager.MainInstance;
    }

    /// <summary>命中点：优先用受击 Collider 上离角色最近的点（贴住刃口），失败退回受击者胸口</summary>
    private static Vector3 ResolveHitPoint(Collider hitCollider, Vector3 fallback)
    {
        if (hitCollider == null) return fallback;
        Vector3 inside = hitCollider.transform.position + Vector3.up * 0.9f;
        return hitCollider.ClosestPoint(inside);
    }
}
