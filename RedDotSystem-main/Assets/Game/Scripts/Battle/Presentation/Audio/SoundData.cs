using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 角色音效配置总表（ScriptableObject）
/// 每条 = 音效类型 + 角色 + 该类型下的若干音频；播放时随机抽一条。
/// 两种填法（优先用路径，便于 YooAsset 热更）：
/// 1. clipPaths：完整资源路径（如 Assets/Content/Resource/audoi/Audio绝区零/FOOT/脚步声1.wav）
/// 2. clips：直接引用 AudioClip（过渡期兜底，YooAsset 未初始化时也能出声）
/// </summary>
[CreateAssetMenu(fileName = "SoundData", menuName = "Create/Asset/SoundData")]
public class SoundData : ScriptableObject
{
    /// <summary>单条音效配置</summary>
    [System.Serializable]
    public class SoundInfo
    {
        /// <summary>音效类型（脚步/受击/闪避…，见 SoundStyle）</summary>
        public SoundStyle soundStyle;

        /// <summary>所属角色（Null = 通用音效）</summary>
        public CharacterNameList characterName;

        /// <summary>直接引用的音频片段（与 clipPaths 二选一，clipPaths 优先）</summary>
        public AudioClip[] clips;

        /// <summary>YooAsset 完整资源路径（优先于 clips）</summary>
        public string[] clipPaths;
    }

    [SerializeField] public List<SoundInfo> soundInfoList = new List<SoundInfo>();

    /// <summary>
    /// 按类型 + 角色查表并随机抽一条：成功返回 true，路径优先、片段兜底
    /// </summary>
    public bool TryGetSound(SoundStyle soundStyle, CharacterNameList characterName, out AudioClip clip, out string path)
    {
        clip = null;
        path = null;

        SoundInfo Info = FindInfo(soundStyle, characterName);
        if (Info == null)
            return false;

        if (Info.clipPaths != null && Info.clipPaths.Length > 0)
        {
            path = Info.clipPaths[Random.Range(0, Info.clipPaths.Length)];
            if (!string.IsNullOrEmpty(path))
                return true;
            path = null;
        }

        if (Info.clips != null && Info.clips.Length > 0)
        {
            clip = Info.clips[Random.Range(0, Info.clips.Length)];
            return clip != null;
        }

        return false;
    }

    /// <summary>
    /// 兼容旧入口：只取直接引用的片段（不返回路径条目）
    /// </summary>
    public AudioClip GetAudioClip(SoundStyle soundStyle, CharacterNameList characterName)
    {
        TryGetSound(soundStyle, characterName, out AudioClip Clip, out string _);
        return Clip;
    }

    /// <summary>通用音效取第一条匹配；角色音效要求类型 + 角色同时匹配</summary>
    private SoundInfo FindInfo(SoundStyle soundStyle, CharacterNameList characterName)
    {
        if (characterName == CharacterNameList.Null)
        {
            for (int Index = 0; Index < soundInfoList.Count; Index++)
            {
                var Info = soundInfoList[Index];
                if (Info.soundStyle != soundStyle)
                    continue;
                if (HasData(Info))
                    return Info;
            }
            return null;
        }

        var Matched = soundInfoList.Find(Info => Info.soundStyle == soundStyle && Info.characterName == characterName);
        return Matched != null && HasData(Matched) ? Matched : null;
    }

    private static bool HasData(SoundInfo info)
        => (info.clipPaths != null && info.clipPaths.Length > 0) || (info.clips != null && info.clips.Length > 0);
}
