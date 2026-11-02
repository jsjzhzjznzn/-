using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using MieMieFrameWork;
using UnityEngine;

/// <summary>
/// 角色音效门面（挂在角色 / 怪物身上）
///
/// 只做三件事：查表 / 节流 / 转调框架音频管理器。
/// 不再自己创建 AudioSource：所有 3D 音效都从 AudioManager 的播放器池借出，
/// 通过 component 参数跟随本角色位置，角色销毁后播放器自动回池，不会残留。
///
/// 与全局音频的分工：这里是"跟着角色走的声音"，BGM / 环境音 / UI 音走 AudioManager 的其它通道。
/// </summary>
public class ActorAudioComponent : MonoBehaviour
{
    [Header("数据驱动配置（Inspector 拖入）")]
    /// <summary>角色音效配置表（所有角色可共用一份，表内按 characterName 区分）</summary>
    [SerializeField] private SoundData soundData;

    /// <summary>本角色名；Null = 通用音效</summary>
    [SerializeField] private CharacterNameList characterName = CharacterNameList.Null;

    [Header("播放节流")]
    /// <summary>同一类型音效的最小重复间隔，防止高频触发叠音</summary>
    [SerializeField, Range(0f, 1f)] private float minPlayInterval = 0.05f;

    [Header("脚步步频")]
    /// <summary>行走时每 footstepInterval 秒播一声脚步（短音按间隔触发，不做无缝循环）</summary>
    [SerializeField, Min(0.05f)] private float footstepInterval = 0.45f;

    /// <summary>各类型上次播放时间（节流用）</summary>
    private readonly Dictionary<SoundStyle, float> lastStylePlayTime = new Dictionary<SoundStyle, float>();

    /// <summary>当前循环音效句柄</summary>
    private AudioPlaybackHandle loopHandle;

    /// <summary>脚步循环取消源</summary>
    private CancellationTokenSource footstepCts;

    #region 一次性音效

    /// <summary>
    /// 直接播一段 3D 音效（调用方已持有具体 clip，不走查表）
    /// </summary>
    public void PlayOneShot3D(AudioClip clip, float volume = 1f)
    {
        if (clip == null)
            return;

        var Audio = AudioManager.Instance;
        if (Audio == null)
            return;

        // 音效总量由 Mixer 的 EffectVolume 统一控制，这里只处理单次增益
        Audio.PlayEffect(clip, Mathf.Clamp01(volume), true, this);
    }

    /// <summary>
    /// 按音效类型查表播放（推荐入口）：随机抽一条后交给 AudioManager
    /// </summary>
    public void PlayByStyle(SoundStyle style, float volume = 1f)
    {
        if (soundData == null)
            return;

        if (lastStylePlayTime.TryGetValue(style, out float Last) && Time.time - Last < minPlayInterval)
            return;

        if (!soundData.TryGetSound(style, characterName, out AudioClip Clip, out string Path))
            return;

        var Audio = AudioManager.Instance;
        if (Audio == null)
            return;

        // 查表成功才记时间：表里没配也不该白占冷却窗口
        lastStylePlayTime[style] = Time.time;
        float VolumeScale = Mathf.Clamp01(volume);
        if (!string.IsNullOrEmpty(Path))
            Audio.PlayEffect(Path, this, VolumeScale, true);
        else
            Audio.PlayEffect(Clip, VolumeScale, true, this);
    }

    /// <summary>
    /// 播放连击段角色语音（随机抽一条，乘语音系数）
    /// </summary>
    /// <returns>实际播出的下标；没播返回 -1（网络同步要拿这个下标广播给远端）</returns>
    public int PlayComboVoice(ComboData comboData)
    {
        int Index = PickRandomIndex(comboData != null ? comboData.characterVoice : null);
        if (Index < 0)
            return -1;
        return PlayClip(comboData.characterVoice[Index], GameSettings.VoiceScale) ? Index : -1;
    }

    /// <summary>
    /// 播放连击段武器挥砍 / 打击音效
    /// </summary>
    /// <returns>实际播出的下标；没播返回 -1</returns>
    public int PlayWeaponSound(ComboData comboData)
    {
        int Index = PickRandomIndex(comboData != null ? comboData.weaponSound : null);
        if (Index < 0)
            return -1;
        return PlayClip(comboData.weaponSound[Index], 1f) ? Index : -1;
    }

    /// <summary>
    /// 远端同步用：按数组下标播一条（连招台词 / 武器音），不查表也不节流
    /// </summary>
    public bool PlayRemoteClip(AudioClip[] clips, int index, bool isVoice)
    {
        if (clips == null || index < 0 || index >= clips.Length)
            return false;
        return PlayClip(clips[index], isVoice ? GameSettings.VoiceScale : 1f);
    }

    #endregion

    #region 循环音效

    /// <summary>
    /// 开启循环音效：脚步走步频循环，其余类型走循环句柄
    /// </summary>
    public void PlayLoopSound(SoundStyle style, float volume = 1f)
    {
        if (style == SoundStyle.FOOT || style == SoundStyle.FOOTBACK)
        {
            StartFootstepLoop(style, volume);
            return;
        }

        if (soundData == null)
            return;
        if (!soundData.TryGetSound(style, characterName, out AudioClip Clip, out string Path))
            return;

        var Audio = AudioManager.Instance;
        if (Audio == null)
            return;

        StopLoopSource();
        float VolumeScale = Mathf.Clamp01(volume);
        loopHandle = string.IsNullOrEmpty(Path)
            ? Audio.PlayEffect(Clip, VolumeScale, true, this, null, 0f, true)
            : Audio.PlayEffect(Path, this, VolumeScale, true, null, 0f, true);
    }

    /// <summary>停止循环音效（脚步 + 循环句柄）</summary>
    public void StopLoopSound()
    {
        StopLoopSource();
        StopFootstepLoop();
    }

    private void StopLoopSource()
    {
        if (!loopHandle.IsValid)
        {
            loopHandle = default;
            return;
        }

        var Audio = AudioManager.Instance;
        Audio?.StopEffect(loopHandle);
        loopHandle = default;
    }

    #endregion

    #region 脚步步频循环

    private void StartFootstepLoop(SoundStyle style, float volume)
    {
        if (footstepCts != null)
            return;

        footstepCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
        FootstepLoopAsync(style, volume, footstepCts.Token).Forget();
    }

    private void StopFootstepLoop()
    {
        footstepCts?.Cancel();
        footstepCts?.Dispose();
        footstepCts = null;
    }

    private async UniTaskVoid FootstepLoopAsync(SoundStyle style, float volume, CancellationToken token)
    {
        PlayByStyle(style, volume);
        while (true)
        {
            await UniTask.WaitForSeconds(footstepInterval, cancellationToken: token);
            PlayByStyle(style, volume);
        }
    }

    #endregion

    /// <summary>角色销毁时必须停循环：池播放器只跟随位置，不会随角色一起销毁</summary>
    private void OnDestroy()
    {
        StopLoopSound();
    }

    /// <summary>统一出口：乘系数后交给 AudioManager，3D 且跟随本角色</summary>
    private bool PlayClip(AudioClip clip, float volumeScale)
    {
        if (clip == null)
            return false;

        var Audio = AudioManager.Instance;
        if (Audio == null)
            return false;

        Audio.PlayEffect(clip, Mathf.Clamp01(volumeScale), true, this);
        return true;
    }

    private static int PickRandomIndex(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0)
            return -1;
        return Random.Range(0, clips.Length);
    }
}
