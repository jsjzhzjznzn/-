using UnityEngine;

namespace Game.Activity
{
    /// <summary>
    /// 给 Lua 用的音频入口：音频资源从活动热更包加载（随活动关闭释放），播放统一走工程 AudioManager。
    /// 不缓存播放状态，只做「加载 + 一次性播放 + BGM 起停」。
    /// </summary>
    public static class ActivityAudio
    {
        /// <summary>从热更包加载一次性音效并立即播放</summary>
        public static bool PlayEffect(string location, float volumeScale = 1f)
        {
            AudioClip clip = LoadClip(location);
            if (clip == null) return false;
            return PlayEffectClip(clip, volumeScale);
        }

        /// <summary>播放已加载的音效片段</summary>
        public static bool PlayEffectClip(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null || MieMieFrameWork.AudioManager.Instance == null) return false;
            MieMieFrameWork.AudioManager.Instance.PlayOneShot(clip, volumeScale, false);
            return true;
        }

        /// <summary>从热更包加载 BGM 并循环播放（同一通道，重复调用会替换当前曲子）</summary>
        public static bool PlayBgm(string location, float volume = 0.6f)
        {
            AudioClip clip = LoadClip(location);
            if (clip == null) return false;
            return PlayBgmClip(clip, volume);
        }

        /// <summary>循环播放已加载的 BGM</summary>
        public static bool PlayBgmClip(AudioClip clip, float volume = 0.6f)
        {
            if (clip == null || MieMieFrameWork.AudioManager.Instance == null) return false;
            MieMieFrameWork.AudioManager.Instance.PlayerBgAudio(
                clip, MieMieFrameWork.AudioManager.BgAudioType.BGM, true, volume);
            return true;
        }

        /// <summary>停止 BGM（离开战斗界面时调用）</summary>
        public static void StopBgm()
        {
            MieMieFrameWork.AudioManager.Instance?.StopBgAudio(MieMieFrameWork.AudioManager.BgAudioType.BGM);
        }

        /// <summary>只加载不播放（需要提前预热时用）</summary>
        public static AudioClip LoadClip(string location)
        {
            if (string.IsNullOrEmpty(location)) return null;
            return LuaActivityHost.Instance != null
                ? LuaActivityHost.Instance.LoadAudioClipFromPackage(location)
                : null;
        }
    }
}
