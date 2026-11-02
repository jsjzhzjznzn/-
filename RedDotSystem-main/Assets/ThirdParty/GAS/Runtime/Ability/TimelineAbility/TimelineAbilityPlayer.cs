using System.Collections.Generic;
using GAS.General;
using UnityEngine;
//using UnityEngine.Profiling;

namespace GAS.Runtime
{
    internal abstract class RuntimeClipInfo
    {
        public int endFrame;
        public int startFrame;
    }

    internal class RuntimeDurationCueClip : RuntimeClipInfo
    {
        public GameplayCueDurationalSpec cueSpec;
    }

    internal class RuntimeBuffClip : RuntimeClipInfo
    {
        public GameplayEffect buff;
        public GameplayEffectSpec buffSpec;
    }

    internal class RuntimeTaskClip : RuntimeClipInfo
    {
        public OngoingAbilityTask task;
    }

    internal class RuntimeTaskMark
    {
        public int startFrame;
        public InstantAbilityTask task;
    }

    public class TimelineAbilityPlayer<T> where T : AbstractAbility
    {
        private readonly TimelineAbilitySpecT<T> _abilitySpec;

        // ---- 瞬时事件缓存(按 startFrame 升序) + 推进指针 ----
        private readonly List<InstantCueMarkEvent> _cacheInstantCues = new();

        private readonly List<ReleaseGameplayEffectMarkEvent> _cacheReleaseGameplayEffect = new();

        private readonly List<RuntimeTaskMark> _cacheInstantTasks = new();
        private int _instantCuePtr;
        private int _releaseGePtr;
        private int _instantTaskPtr;

        // ---- 持续clip缓存(按 startFrame 升序稳定排序) + 推进指针 + 活跃窗口 ----
        private readonly List<RuntimeDurationCueClip> _cacheDurationalCueTrack = new();

        private readonly List<RuntimeBuffClip> _cacheBuffGameplayEffectTrack = new();

        private readonly List<RuntimeTaskClip> _cacheOngoingTaskTrack = new();

        private readonly List<RuntimeDurationCueClip> _activeDurationalCues = new();
        private readonly List<RuntimeBuffClip> _activeBuffs = new();
        private readonly List<RuntimeTaskClip> _activeOngoingTasks = new();
        private int _durationalCueStartPtr;
        private int _buffStartPtr;
        private int _ongoingTaskStartPtr;

        // cache for target catcher, avoid new in TickFrame
        // 这个是一个泛型类, 这个变量就不作为static了
        private readonly List<AbilitySystemComponent> _targets = new();

        private int _currentFrame;
        private float _playTotalTime;

        public TimelineAbilityPlayer(TimelineAbilitySpecT<T> abilitySpec)
        {
            _abilitySpec = abilitySpec;
            Cache();
        }

        public bool IsPlaying { get; private set; }

        public TimelineAbilityAssetBase AbilityAsset => _abilitySpec.Ability.DataReference as TimelineAbilityAssetBase;
        private int FrameCount => AbilityAsset.FrameCount;
        private int FrameRate => GASTimer.FrameRate;

        private void Cache()
        {
            Cache_InstantCues();
            Cache_ReleaseGameplayEffects();
            Cache_InstantTasks();
            Cache_DurationalGameplayCues();
            Cache_BuffGameplayEffects();
            Cache_OngoingTasks();
        }

        private void Cache_InstantCues()
        {
            _cacheInstantCues.Clear();
            foreach (var trackData in AbilityAsset.InstantCues)
            {
                _cacheInstantCues.AddRange(trackData.markEvents);
            }

            _cacheInstantCues.Sort((a, b) => a.startFrame.CompareTo(b.startFrame));
        }

        private void Cache_ReleaseGameplayEffects()
        {
            _cacheReleaseGameplayEffect.Clear();
            foreach (var trackData in AbilityAsset.ReleaseGameplayEffect)
            {
                _cacheReleaseGameplayEffect.AddRange(trackData.markEvents);
            }

            _cacheReleaseGameplayEffect.Sort((a, b) => a.startFrame.CompareTo(b.startFrame));
            foreach (var releaseGameplayEffectMarkEvent in _cacheReleaseGameplayEffect)
            {
                releaseGameplayEffectMarkEvent.CacheTargetCatcher();
            }
        }

        private void Cache_InstantTasks()
        {
            _cacheInstantTasks.Clear();
            foreach (var trackData in AbilityAsset.InstantTasks)
            {
                foreach (var markEvent in trackData.markEvents)
                {
                    foreach (var taskData in markEvent.InstantTasks)
                    {
                        var runtimeTaskMark = new RuntimeTaskMark
                        {
                            startFrame = markEvent.startFrame,
                            task = taskData.CreateTask(_abilitySpec)
                        };
                        _cacheInstantTasks.Add(runtimeTaskMark);
                    }
                }
            }

            _cacheInstantTasks.Sort((a, b) => a.startFrame.CompareTo(b.startFrame));
        }

        private void Cache_DurationalGameplayCues()
        {
            _cacheDurationalCueTrack.Clear();
            foreach (var track in AbilityAsset.DurationalCues)
            {
                foreach (var clipEvent in track.clipEvents)
                {
                    var cueSpec = clipEvent.cue.ApplyFrom(_abilitySpec);
                    if (cueSpec == null) continue;
                    var runtimeDurationCueClip = new RuntimeDurationCueClip
                    {
                        startFrame = clipEvent.startFrame,
                        endFrame = clipEvent.EndFrame,
                        cueSpec = cueSpec
                    };
                    _cacheDurationalCueTrack.Add(runtimeDurationCueClip);
                }
            }

            // 稳定排序: 同帧的多个clip保持轨道配置顺序, 保证OnAdd触发顺序与旧实现一致
            StableSortByStartFrame(_cacheDurationalCueTrack);
        }

        private void Cache_BuffGameplayEffects()
        {
            _cacheBuffGameplayEffectTrack.Clear();
            foreach (var track in AbilityAsset.BuffGameplayEffects)
            {
                foreach (var clipEvent in track.clipEvents)
                {
                    // 只有持续型的GameplayEffect可视作buff
                    if (clipEvent.gameplayEffect.DurationPolicy is EffectsDurationPolicy.Duration
                        or EffectsDurationPolicy.Infinite)
                    {
                        var runtimeBuffClip = new RuntimeBuffClip
                        {
                            startFrame = clipEvent.startFrame,
                            endFrame = clipEvent.EndFrame,
                            buff = new GameplayEffect(clipEvent.gameplayEffect),
                            buffSpec = null
                        };
                        _cacheBuffGameplayEffectTrack.Add(runtimeBuffClip);
                    }
                }
            }

            // 稳定排序: buff的施加顺序影响属性叠加结果, 同帧必须保持配置顺序
            StableSortByStartFrame(_cacheBuffGameplayEffectTrack);
        }

        private void Cache_OngoingTasks()
        {
            _cacheOngoingTaskTrack.Clear();
            foreach (var track in AbilityAsset.OngoingTasks)
            {
                foreach (var clip in track.clipEvents)
                {
                    var runtimeTaskClip = new RuntimeTaskClip
                    {
                        startFrame = clip.startFrame,
                        endFrame = clip.EndFrame,
                        task = clip.ongoingTask.CreateTask(_abilitySpec)
                    };
                    _cacheOngoingTaskTrack.Add(runtimeTaskClip);
                }
            }

            StableSortByStartFrame(_cacheOngoingTaskTrack);
        }

        /// <summary>
        /// 按 startFrame 升序的插入排序(稳定)。
        /// clip 数量为技能配置规模(几十级别), O(n^2) 一次性成本可忽略;
        /// 稳定性保证同帧 clip 的生命周期触发顺序与轨道配置顺序一致。
        /// </summary>
        private static void StableSortByStartFrame<C>(List<C> clips) where C : RuntimeClipInfo
        {
            for (var i = 1; i < clips.Count; i++)
            {
                var current = clips[i];
                var j = i - 1;
                while (j >= 0 && clips[j].startFrame > current.startFrame)
                {
                    clips[j + 1] = clips[j];
                    j--;
                }

                clips[j + 1] = current;
            }
        }

        private void Prepare()
        {
            _instantCuePtr = 0;
            _releaseGePtr = 0;
            _instantTaskPtr = 0;
            _durationalCueStartPtr = 0;
            _buffStartPtr = 0;
            _ongoingTaskStartPtr = 0;
            _activeDurationalCues.Clear();
            _activeBuffs.Clear();
            _activeOngoingTasks.Clear();

            foreach (var runtimeBuffClip in _cacheBuffGameplayEffectTrack)
            {
                runtimeBuffClip.buffSpec = null;
            }
        }

        public void Play()
        {
            _currentFrame = -1; // 为了播放第0帧
            _playTotalTime = 0;
            IsPlaying = true;
            Prepare();
        }

        public void Stop()
        {
            if (!IsPlaying) return;

            // 只清理"已开始未结束"的活跃clip。
            // (旧实现对全部缓存clip做兜底, 会误对"尚未开始"的clip调用OnRemove/OnEnd, 此处为行为修正。)
            foreach (var clip in _activeDurationalCues)
            {
                clip.cueSpec.OnRemove();
            }

            _activeDurationalCues.Clear();

            foreach (var clip in _activeBuffs)
            {
                if (clip.buffSpec != null)
                    _abilitySpec.Owner.RemoveGameplayEffect(clip.buffSpec);
                clip.buffSpec = null;
            }

            _activeBuffs.Clear();

            foreach (var clip in _activeOngoingTasks)
            {
                clip.task.OnEnd(clip.endFrame);
            }

            _activeOngoingTasks.Clear();

            IsPlaying = false;
        }

        public void Tick()
        {
            if (!IsPlaying) return;

            _playTotalTime += Time.deltaTime;
            var targetFrame = (int)(_playTotalTime * FrameRate);

            // 追帧: 逐帧推进保证每个帧号都被"经过"一次, 这是 TickFrame 中指针推进正确性的前提
            while (_currentFrame < targetFrame)
            {
                _currentFrame++;
                TickFrame(_currentFrame);
            }

            if (_currentFrame >= FrameCount)
            {
                _currentFrame++; //确保不重复触发cue的onRemove
                OnPlayEnd();
            }
        }

        /// <summary>
        /// 播放结束
        /// </summary>
        private void OnPlayEnd()
        {
            IsPlaying = false;

            if (!AbilityAsset.manualEndAbility)
                _abilitySpec.TryEndAbility();
        }

        /// <summary>
        /// 当前帧的事件。
        /// 瞬时事件用推进指针(排序后 startFrame<=frame 的才检查, 均摊 O(触发数));
        /// 持续clip用"进入指针 + 活跃窗口"(活跃集内做OnTick/OnRemove, 均摊 O(触发数+活跃数))。
        /// </summary>
        /// <param name="frame"></param>
        private void TickFrame(int frame)
        {
            TickFrame_InstantGameplayCues(frame);
            TickFrame_ReleaseGameplayEffects(frame);
            TickFrame_InstantTasks(frame);
            TickFrame_DurationalGameplayCues(frame);
            TickFrame_BuffGameplayEffects(frame);
            TickFrame_OngoingTasks(frame);
        }

        private void TickFrame_InstantGameplayCues(int frame)
        {
            var list = _cacheInstantCues;
            while (_instantCuePtr < list.Count && list[_instantCuePtr].startFrame <= frame)
            {
                var cueMark = list[_instantCuePtr];
                if (cueMark.startFrame == frame)
                {
                    foreach (var cue in cueMark.cues)
                    {
                        cue.ApplyFrom(_abilitySpec);
                    }
                }

                _instantCuePtr++;
            }
        }

        private void TickFrame_ReleaseGameplayEffects(int frame)
        {
            var list = _cacheReleaseGameplayEffect;
            while (_releaseGePtr < list.Count && list[_releaseGePtr].startFrame <= frame)
            {
                var mark = list[_releaseGePtr];
                if (mark.startFrame == frame)
                {
                    var catcher = mark.TargetCatcher;
                    catcher.Init(_abilitySpec.Owner);

                    catcher.CatchTargetsNonAllocSafe(_abilitySpec.Target, _targets);

                    foreach (var asc in _targets)
                    {
                        foreach (var gea in mark.gameplayEffectAssets)
                        {
                            var ge = new GameplayEffect(gea);
                            _abilitySpec.Owner.ApplyGameplayEffectTo(ge, asc);
                        }
                    }

                    _targets.Clear();
                }

                _releaseGePtr++;
            }
        }

        private void TickFrame_InstantTasks(int frame)
        {
            var list = _cacheInstantTasks;
            while (_instantTaskPtr < list.Count && list[_instantTaskPtr].startFrame <= frame)
            {
                var instantTask = list[_instantTaskPtr];
                if (instantTask.startFrame == frame)
                {
                    instantTask.task.OnExecute();
                }

                _instantTaskPtr++;
            }
        }

        private void TickFrame_DurationalGameplayCues(int frame)
        {
            var list = _cacheDurationalCueTrack;
            while (_durationalCueStartPtr < list.Count && list[_durationalCueStartPtr].startFrame <= frame)
            {
                var clip = list[_durationalCueStartPtr];
                if (clip.startFrame == frame)
                {
                    clip.cueSpec.OnAdd();
                }

                _activeDurationalCues.Add(clip);
                _durationalCueStartPtr++;
            }

            for (var i = _activeDurationalCues.Count - 1; i >= 0; i--)
            {
                var clip = _activeDurationalCues[i];
                if (frame >= clip.startFrame && frame <= clip.endFrame)
                {
                    clip.cueSpec.OnTick();
                }

                if (frame == clip.endFrame)
                {
                    clip.cueSpec.OnRemove();
                    // swap-remove: 活跃集顺序无语义, O(1)移除
                    _activeDurationalCues[i] = _activeDurationalCues[_activeDurationalCues.Count - 1];
                    _activeDurationalCues.RemoveAt(_activeDurationalCues.Count - 1);
                }
            }
        }

        private void TickFrame_BuffGameplayEffects(int frame)
        {
            // buff持续时间以Timeline配置时间为准（执行策略全部改为Infinite）
            var list = _cacheBuffGameplayEffectTrack;
            while (_buffStartPtr < list.Count && list[_buffStartPtr].startFrame <= frame)
            {
                var buffClip = list[_buffStartPtr];
                if (buffClip.startFrame == frame)
                {
                    var buffSpec = _abilitySpec.Owner.ApplyGameplayEffectToSelf(buffClip.buff);
                    buffSpec.SetDurationPolicy(EffectsDurationPolicy.Infinite);
                    buffClip.buffSpec = buffSpec;
                }

                _activeBuffs.Add(buffClip);
                _buffStartPtr++;
            }

            for (var i = _activeBuffs.Count - 1; i >= 0; i--)
            {
                var buffClip = _activeBuffs[i];
                if (frame == buffClip.endFrame)
                {
                    if (buffClip.buffSpec != null)
                    {
                        _abilitySpec.Owner.RemoveGameplayEffect(buffClip.buffSpec);
                        buffClip.buffSpec = null;
                    }

                    _activeBuffs[i] = _activeBuffs[_activeBuffs.Count - 1];
                    _activeBuffs.RemoveAt(_activeBuffs.Count - 1);
                }
            }
        }

        private void TickFrame_OngoingTasks(int frame)
        {
            var list = _cacheOngoingTaskTrack;
            while (_ongoingTaskStartPtr < list.Count && list[_ongoingTaskStartPtr].startFrame <= frame)
            {
                var clip = list[_ongoingTaskStartPtr];
                if (clip.startFrame == frame)
                {
                    clip.task.OnStart(frame);
                }

                _activeOngoingTasks.Add(clip);
                _ongoingTaskStartPtr++;
            }

            for (var i = _activeOngoingTasks.Count - 1; i >= 0; i--)
            {
                var clip = _activeOngoingTasks[i];
                if (frame >= clip.startFrame && frame <= clip.endFrame)
                {
                    clip.task.OnTick(frame, clip.startFrame, clip.endFrame);
                }

                if (frame == clip.endFrame)
                {
                    clip.task.OnEnd(frame);
                    _activeOngoingTasks[i] = _activeOngoingTasks[_activeOngoingTasks.Count - 1];
                    _activeOngoingTasks.RemoveAt(_activeOngoingTasks.Count - 1);
                }
            }
        }
    }
}
