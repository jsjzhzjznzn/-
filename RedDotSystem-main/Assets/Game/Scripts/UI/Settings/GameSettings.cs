using System;
using UnityEngine;

/// <summary>
/// 全局设置（音量 / 全屏）：PlayerPrefs 持久化，进游戏时自动应用一次。
/// 音量分四路：主音量 → Mixer 的 MasterVolume，音乐 → BgmVolume/AmbienceVolume，音效 → EffectVolume
/// （由 <see cref="AudioVolumeBridge"/> 推送）；语音没有独立通道，仍以系数在播放时乘算（见 <see cref="VoiceScale"/>）。
/// MusicScale / SfxScale 保留给未接框架的旧代码，新代码不要再乘。
/// </summary>
public static class GameSettings
{
    public const string KeyMaster = "settings_volume_master";
    public const string KeyMusic = "settings_volume_music";
    public const string KeySfx = "settings_volume_sfx";
    public const string KeyVoice = "settings_volume_voice";
    public const string KeyMuted = "settings_muted";
    public const string KeyFullscreen = "settings_fullscreen";

    /// <summary>任一设置被修改（面板据此刷新显示，音频播放据此重算同名系数）</summary>
    public static event Action Changed;

    public static float MasterVolume { get; private set; } = 1f;
    public static float MusicVolume { get; private set; } = 1f;
    public static float SfxVolume { get; private set; } = 1f;
    public static float VoiceVolume { get; private set; } = 1f;
    public static bool Muted { get; private set; }

    public static bool Fullscreen { get; private set; } = true;

    private static bool _loaded;

    /// <summary>供 AudioSource 播放时乘算的分组系数（静音时全部为 0）</summary>
    public static float MusicScale => Muted ? 0f : MusicVolume;
    public static float SfxScale => Muted ? 0f : SfxVolume;
    public static float VoiceScale => Muted ? 0f : VoiceVolume;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        Load();
        Apply();
    }

    /// <summary>从 PlayerPrefs 读一次（幂等）</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMaster, 1f));
        MusicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMusic, 1f));
        SfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeySfx, 1f));
        VoiceVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyVoice, 1f));
        Muted = PlayerPrefs.GetInt(KeyMuted, 0) != 0;
        Fullscreen = PlayerPrefs.GetInt(KeyFullscreen, Screen.fullScreen ? 1 : 0) != 0;
    }

    /// <summary>把当前值应用到引擎（主音量 / 全屏）</summary>
    public static void Apply()
    {
        // 总音量交给音频框架的 Mixer（MasterVolume）。AudioListener 保持 1，避免 Mixer 之后再衰减一次
        AudioListener.volume = 1f;
        AudioVolumeBridge.Apply();

        // 编辑器里改全屏没有实际意义，且会打断 Game View，跳过
        if (!Application.isEditor && Screen.fullScreen != Fullscreen)
        {
            Screen.fullScreen = Fullscreen;
        }
    }

    public static void SetMasterVolume(float value) { MasterVolume = Mathf.Clamp01(value); Commit(); }
    public static void SetMusicVolume(float value) { MusicVolume = Mathf.Clamp01(value); Commit(); }
    public static void SetSfxVolume(float value) { SfxVolume = Mathf.Clamp01(value); Commit(); }
    public static void SetVoiceVolume(float value) { VoiceVolume = Mathf.Clamp01(value); Commit(); }

    public static void SetMuted(bool value) { Muted = value; Commit(); }

    public static void SetFullscreen(bool value) { Fullscreen = value; Commit(); }

    private static void Commit()
    {
        Save();
        Apply();
        Changed?.Invoke();
    }

    private static void Save()
    {
        PlayerPrefs.SetFloat(KeyMaster, MasterVolume);
        PlayerPrefs.SetFloat(KeyMusic, MusicVolume);
        PlayerPrefs.SetFloat(KeySfx, SfxVolume);
        PlayerPrefs.SetFloat(KeyVoice, VoiceVolume);
        PlayerPrefs.SetInt(KeyMuted, Muted ? 1 : 0);
        PlayerPrefs.SetInt(KeyFullscreen, Fullscreen ? 1 : 0);
        PlayerPrefs.Save();
    }
}
