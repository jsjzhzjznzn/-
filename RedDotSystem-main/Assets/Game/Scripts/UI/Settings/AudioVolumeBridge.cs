using System;
using Cysharp.Threading.Tasks;
using MieMieFrameWork;
using UnityEngine;

/// <summary>
/// 设置 → 音频框架的接线层。
///
/// GameSettings 仍是唯一持久化来源（PlayerPrefs），这里只负责"把值推给 Mixer"：
/// 主音量 → MasterVolume，音乐 → BgmVolume/AmbienceVolume，音效 → EffectVolume。
/// 语音滑条没有独立 Mixer 通道，仍由播放时乘 VoiceScale 实现（见 ActorAudioComponent）。
///
/// 时序：GameSettings.Bootstrap 与框架自举同在 BeforeSceneLoad，谁先谁后不确定，
/// 所以这里"拿不到 AudioManager 就等框架就绪后再补一次"。
/// </summary>
public static class AudioVolumeBridge
{
    private static bool waiting;

    /// <summary>把当前设置推到音频框架；框架未就绪则挂起等待</summary>
    public static void Apply()
    {
        if (MieMieFrameWork.AudioManager.Instance == null)
        {
            ApplyWhenReadyAsync().Forget();
            return;
        }

        ApplyNow(MieMieFrameWork.AudioManager.Instance);
    }

    private static void ApplyNow(MieMieFrameWork.AudioManager audio)
    {
        audio.GlobalVolumeFactor = GameSettings.Muted ? 0f : GameSettings.MasterVolume;
        audio.BgVolumeBaseNum = GameSettings.MusicVolume;
        audio.AmbienceVolumeBaseNum = GameSettings.MusicVolume;
        audio.EffectVolumeBaseNum = GameSettings.SfxVolume;
    }

    private static async UniTaskVoid ApplyWhenReadyAsync()
    {
        if (waiting)
            return;
        waiting = true;

        try
        {
            var Hub = await ModuleHubBootstrap.EnsureAsync();
            if (Hub != null && MieMieFrameWork.AudioManager.Instance != null)
                ApplyNow(MieMieFrameWork.AudioManager.Instance);
        }
        catch (Exception Exception)
        {
            GameLog.Warn("[AudioVolumeBridge] 等待音频框架就绪失败：" + Exception.Message);
        }
        finally
        {
            waiting = false;
        }
    }
}
