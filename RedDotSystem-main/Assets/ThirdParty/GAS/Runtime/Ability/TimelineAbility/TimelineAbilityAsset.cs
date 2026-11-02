using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using GAS.General;
using Sirenix.OdinInspector;
using UnityEditor;
#endif

namespace GAS.Runtime
{
    public abstract class TimelineAbilityAssetBase : AbilityAsset, UnityEngine.ISerializationCallbackReceiver
    {
        /// <summary>
        /// 资产数据结构版本号。
        /// 结构变更时递增本值, 并在 OnBeforeSerialize/OnAfterDeserialize 中编写逐级迁移逻辑。
        /// </summary>
        [SerializeField] [HideInInspector] private int _serializedVersion = 1;

        public int SerializedVersion => _serializedVersion;

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器侧注入的时间轴编辑入口(由 GAS.Editor 的 TimelineEditorInjection 注册)。
        /// 代替旧的 Assembly.Load + GetType + GetMethod 反射调用, 消除字符串耦合;
        /// 事件触发时若无人注册(编辑器程序集未加载), 会给出明确警告而不是抛异常。
        /// </summary>
        public static event Action<TimelineAbilityAssetBase> OnEditTimelineRequested;

        /// <summary>反序列化时是否修复过脏数据(供编辑器窗口打开时落盘一次)。</summary>
        public bool SanitizeChangedOnLoad { get; private set; }

        public void ClearSanitizeChangedOnLoad()
        {
            SanitizeChangedOnLoad = false;
        }
#endif

#if UNITY_EDITOR
        [TitleGroup("Data")]
        [HorizontalGroup("Data/H1", 1 / 3f)]
        [TabGroup("Data/H1/V1", "Timeline", SdfIconType.ClockHistory, TextColor = "#00FF00")]
        [Button("查看/编辑能力时间轴", ButtonSizes.Large, Icon = SdfIconType.Hammer)]
        [PropertyOrder(-1)]
        private void EditAbilityTimeline()
        {
            if (OnEditTimelineRequested == null)
            {
                Debug.LogWarning("[GAS] 时间轴编辑器入口尚未注册(编辑器程序集未加载), 无法打开窗口。");
                return;
            }

            OnEditTimelineRequested.Invoke(this);
        }

        [TabGroup("Data/H1/V1", "Timeline")]
        [LabelText(GASTextDefine.ABILITY_MANUAL_ENDABILITY)]
        [LabelWidth(100)]
#endif
        public bool manualEndAbility;

        [HideInInspector]
        public int FrameCount; // 能力结束时间

        [HideInInspector]
        public List<DurationalCueTrackData> DurationalCues = new List<DurationalCueTrackData>();

        [HideInInspector]
        public List<InstantCueTrackData> InstantCues = new List<InstantCueTrackData>();

        [HideInInspector]
        public List<ReleaseGameplayEffectTrackData> ReleaseGameplayEffect =
            new List<ReleaseGameplayEffectTrackData>();

        [HideInInspector]
        public List<BuffGameplayEffectTrackData> BuffGameplayEffects = new List<BuffGameplayEffectTrackData>();

        [HideInInspector]
        public List<TaskMarkEventTrackData> InstantTasks = new List<TaskMarkEventTrackData>();

        [HideInInspector]
        public List<TaskClipEventTrackData> OngoingTasks = new List<TaskClipEventTrackData>();

        #region Serialization Version & Sanitize

        public void OnBeforeSerialize()
        {
            // 版本迁移点: 结构变更时在此按 _serializedVersion 编写"旧版本 -> 新版本"的迁移逻辑,
            // 迁移完成后将 _serializedVersion 更新为最新版本号。
            // 当前版本 1, 无需迁移。
        }

        public void OnAfterDeserialize()
        {
            Sanitize();
        }

        /// <summary>
        /// 脏数据自愈: 消除非法帧号/时长, 保证运行时播放器的帧命中判定不会遇到 startFrame&lt;0 或 endFrame&lt;startFrame 的数据。
        /// 仅修复内存数据; 编辑器下通过 SanitizeChangedOnLoad 由时间轴窗口打开时落盘。
        /// </summary>
        private void Sanitize()
        {
            var changed = false;

            if (FrameCount < 0)
            {
                FrameCount = 0;
                changed = true;
            }

            changed |= SanitizeClipTracks(DurationalCues, t => t.clipEvents);
            changed |= SanitizeMarkTracks(InstantCues, t => t.markEvents);
            changed |= SanitizeMarkTracks(ReleaseGameplayEffect, t => t.markEvents);
            changed |= SanitizeClipTracks(BuffGameplayEffects, t => t.clipEvents);
            changed |= SanitizeMarkTracks(InstantTasks, t => t.markEvents);
            changed |= SanitizeClipTracks(OngoingTasks, t => t.clipEvents);

#if UNITY_EDITOR
            SanitizeChangedOnLoad = changed;
            if (changed)
                Debug.Log($"[GAS] TimelineAbilityAsset({GetType().Name}) 加载时修复了非法轨道数据(帧号/时长/空元素)。");
#endif
        }

        private static bool SanitizeClipTracks<TTrack, TClip>(List<TTrack> tracks,
            Func<TTrack, List<TClip>> getClips)
            where TTrack : TrackDataBase
            where TClip : ClipEventBase
        {
            if (tracks == null) return false;
            var changed = false;

            foreach (var track in tracks)
            {
                if (track == null) continue;
                if (track.trackName == null)
                {
                    track.trackName = string.Empty;
                    changed = true;
                }

                var clips = getClips(track);
                if (clips == null) continue;

                for (var i = clips.Count - 1; i >= 0; i--)
                {
                    var clip = clips[i];
                    if (clip == null)
                    {
                        clips.RemoveAt(i);
                        changed = true;
                        continue;
                    }

                    if (clip.startFrame < 0)
                    {
                        clip.startFrame = 0;
                        changed = true;
                    }

                    if (clip.durationFrame < 1)
                    {
                        clip.durationFrame = 1;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        private static bool SanitizeMarkTracks<TTrack, TMark>(List<TTrack> tracks,
            Func<TTrack, List<TMark>> getMarks)
            where TTrack : TrackDataBase
            where TMark : MarkEventBase
        {
            if (tracks == null) return false;
            var changed = false;

            foreach (var track in tracks)
            {
                if (track == null) continue;
                if (track.trackName == null)
                {
                    track.trackName = string.Empty;
                    changed = true;
                }

                var marks = getMarks(track);
                if (marks == null) continue;

                for (var i = marks.Count - 1; i >= 0; i--)
                {
                    var mark = marks[i];
                    if (mark == null)
                    {
                        marks.RemoveAt(i);
                        changed = true;
                        continue;
                    }

                    if (mark.startFrame < 0)
                    {
                        mark.startFrame = 0;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        #endregion

#if UNITY_EDITOR
        public void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }
#endif
    }

    public abstract class TimelineAbilityAssetT<T> : TimelineAbilityAssetBase where T : class
    {
        public sealed override Type AbilityType()
        {
            return typeof(T);
        }
    }

    /// <summary>
    /// 这是一个最朴素的TimelineAbilityAsset实现, 如果要实现更复杂的TimelineAbilityAsset, 请用TimelineAbilityAssetBase或TimelineAbilityAssetT为基类
    /// </summary>
    public sealed class TimelineAbilityAsset : TimelineAbilityAssetT<TimelineAbility>
    {
    }
}
