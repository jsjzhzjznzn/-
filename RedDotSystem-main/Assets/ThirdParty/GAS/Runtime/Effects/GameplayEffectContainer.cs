using System;
using System.Collections.Generic;

namespace GAS.Runtime
{
    public class GameplayEffectContainer
    {
        private readonly AbilitySystemComponent _owner;
        private readonly List<GameplayEffectSpec> _gameplayEffectSpecs = new List<GameplayEffectSpec>();

        // 延迟移除快照：仅在 GE 增删(结构变化)时重建，稳定态零拷贝，且遍历期间的增删不影响本帧遍历
        private readonly List<GameplayEffectSpec> _tickSnapshot = new List<GameplayEffectSpec>();
        private bool _effectStructureDirty = true;

        // 帧末合并脏标记：GE 增删只登记待重算聚合器，一帧统一重算一次，避免 N 个 buff 触发 N 轮全量重扫
        private readonly HashSet<AttributeAggregator> _dirtyAggregators = new HashSet<AttributeAggregator>();
        private readonly List<AttributeAggregator> _recomputeScratch = new List<AttributeAggregator>();
        private bool _aggregatorFlushing;

        public GameplayEffectContainer(AbilitySystemComponent owner)
        {
            _owner = owner;
        }

        private event Action OnGameplayEffectContainerIsDirty;

        public List<GameplayEffectSpec> GameplayEffects()
        {
            return _gameplayEffectSpecs;
        }

        public void Tick()
        {
            // 仅在结构变化后重建快照；GE 的 Tick 可能增删效果(周期执行/到期移除)，
            // 遍历快照而非原列表可安全容忍这种延迟移除，且不再每帧全量拷贝
            if (_effectStructureDirty)
            {
                _tickSnapshot.Clear();
                _tickSnapshot.AddRange(_gameplayEffectSpecs);
                _effectStructureDirty = false;
            }

            for (var i = 0; i < _tickSnapshot.Count; i++)
            {
                var gameplayEffectSpec = _tickSnapshot[i];
                // 到期效果在本帧被移除时 IsActive 已为 false，跳过即等价于旧的拷贝语义
                if (gameplayEffectSpec.IsActive)
                {
                    gameplayEffectSpec.Tick();
                }
            }

            // 帧末统一聚合：把本帧所有 GE 增删触发的脏标记合并成一次重算
            FlushDirtyAggregators();
        }

        /// <summary>
        /// 由聚合器登记待重算；同一帧内多次标脏天然合并(HashSet 去重)，到帧末或读取前统一处理。
        /// </summary>
        public void MarkAggregatorDirty(AttributeAggregator aggregator)
        {
            _dirtyAggregators.Add(aggregator);
        }

        /// <summary>
        /// 聚合器被禁用时摘除其待重算登记，避免重算持有失效引用。
        /// </summary>
        public void MarkAggregatorClean(AttributeAggregator aggregator)
        {
            _dirtyAggregators.Remove(aggregator);
        }

        /// <summary>
        /// 帧末/读取前调用：对脏聚合器各重算一次并清空脏集合。
        /// _aggregatorFlushing 为重入保护：重算过程中读取其它属性值不再触发二次 flush。
        /// </summary>
        public void FlushDirtyAggregators()
        {
            if (_aggregatorFlushing || _dirtyAggregators.Count == 0) return;

            _aggregatorFlushing = true;
            try
            {
                // while 循环兜底：重算级联理论上不会再标脏，但保证收敛后一次性排空
                while (_dirtyAggregators.Count > 0)
                {
                    _recomputeScratch.Clear();
                    foreach (var aggregator in _dirtyAggregators)
                        _recomputeScratch.Add(aggregator);
                    _dirtyAggregators.Clear();

                    for (var i = 0; i < _recomputeScratch.Count; i++)
                        _recomputeScratch[i].RecomputeIfDirty();
                }
            }
            finally
            {
                _aggregatorFlushing = false;
                _recomputeScratch.Clear();
            }
        }

        public void RegisterOnGameplayEffectContainerIsDirty(Action action)
        {
            OnGameplayEffectContainerIsDirty += action;
        }

        public void UnregisterOnGameplayEffectContainerIsDirty(Action action)
        {
            OnGameplayEffectContainerIsDirty -= action;
        }

        public void RemoveGameplayEffectWithAnyTags(GameplayTagSet tags)
        {
            if (tags.Empty) return;

            var removeList = new List<GameplayEffectSpec>();
            foreach (var gameplayEffectSpec in _gameplayEffectSpecs)
            {
                var assetTags = gameplayEffectSpec.GameplayEffect.TagContainer.AssetTags;
                if (!assetTags.Empty && assetTags.HasAnyTags(tags))
                {
                    removeList.Add(gameplayEffectSpec);
                    continue;
                }

                var grantedTags = gameplayEffectSpec.GameplayEffect.TagContainer.GrantedTags;
                if (!grantedTags.Empty && grantedTags.HasAnyTags(tags)) removeList.Add(gameplayEffectSpec);
            }

            foreach (var gameplayEffectSpec in removeList) RemoveGameplayEffectSpec(gameplayEffectSpec);
        }

        /// <summary>
        /// </summary>
        /// <param name="spec"></param>
        /// <returns>
        ///     Returns true if the gameplay effect is successfully applied and remains active.
        ///     Returns false if the gameplay effect is applied but immediately removed due to a tag(in `AssetTags` or `GrantedTags`) match
        ///     with the `RemoveGameplayEffectsWithTags` function, indicating that the effect did not persist.
        /// </returns>
        public GameplayEffectSpec AddGameplayEffectSpec(AbilitySystemComponent source,GameplayEffectSpec effectSpec,bool overwriteEffectLevel = false,int effectLevel = 0)
        {
            if (!effectSpec.GameplayEffect.CanApplyTo(_owner)) return null;
            
            if (effectSpec.GameplayEffect.IsImmune(_owner))
            {
                // TODO 免疫Cue触发
                // var lv = overwriteEffectLevel ? effectLevel : source.Level;
                // effectSpec.Init(source, _owner, lv);
                // effectSpec.TriggerOnImmunity();
                return null;
            }
            
            var level = overwriteEffectLevel ? effectLevel : source.Level;
            if (effectSpec.DurationPolicy == EffectsDurationPolicy.Instant)
            {
                effectSpec.Init(source, _owner, level);
                effectSpec.TriggerOnExecute();
                return null;
            }

            // Check GE Stacking
            if (effectSpec.Stacking.stackingType == StackingType.None)
            {
                return Operation_AddNewGameplayEffectSpec(source, effectSpec,overwriteEffectLevel,effectLevel);
            }
            
            // 处理GE堆叠
            // 基于Target类型GE堆叠
            if (effectSpec.Stacking.stackingType == StackingType.AggregateByTarget)
            {
                GetStackingEffectSpecByData(effectSpec.GameplayEffect, out var geSpec);
                // 新添加GE
                if (geSpec == null)
                    return Operation_AddNewGameplayEffectSpec(source, effectSpec,overwriteEffectLevel,effectLevel);
                bool stackCountChange = geSpec.RefreshStack();
                if (stackCountChange) OnRefreshStackCountMakeContainerDirty();
                return geSpec;
            }
            
            // 基于Source类型GE堆叠
            if (effectSpec.Stacking.stackingType == StackingType.AggregateBySource)
            {
                GetStackingEffectSpecByDataFrom(effectSpec.GameplayEffect,source, out var geSpec);
                if (geSpec == null)
                    return Operation_AddNewGameplayEffectSpec(source, effectSpec,overwriteEffectLevel,effectLevel);
                bool stackCountChange = geSpec.RefreshStack();
                if (stackCountChange) OnRefreshStackCountMakeContainerDirty();
                return geSpec;
            }

            return null;
        }

        public GameplayEffectSpec AddGameplayEffectSpec(AbilitySystemComponent source, GameplayEffect effect,int effectLevel)
        {
            var spec = effect.CreateSpec();
            return AddGameplayEffectSpec(source, spec, true,effectLevel);
        }
        
        public void RemoveGameplayEffectSpec(GameplayEffectSpec spec)
        {
            spec.DisApply();
            spec.TriggerOnRemove();
            _gameplayEffectSpecs.Remove(spec);
            _effectStructureDirty = true;

            OnGameplayEffectContainerIsDirty?.Invoke();
        }

        public void RefreshGameplayEffectState()
        {
            foreach (var gameplayEffectSpec in _gameplayEffectSpecs)
            {
                if (!gameplayEffectSpec.IsApplied) continue;
                if (!gameplayEffectSpec.IsActive)
                {
                    // new active gameplay effects
                    if (gameplayEffectSpec.GameplayEffect.CanRunning(_owner)) gameplayEffectSpec.Activate();
                }
                else
                {
                    // new deactive gameplay effects
                    if (!gameplayEffectSpec.GameplayEffect.CanRunning(_owner)) gameplayEffectSpec.Deactivate();
                }
            }

            OnGameplayEffectContainerIsDirty?.Invoke();
        }

        public CooldownTimer CheckCooldownFromTags(GameplayTagSet tags)
        {
            float longestCooldown = 0;
            float maxDuration = 0;

            // Check if the cooldown tag is granted to the player, and if so, capture the remaining duration for that tag
            foreach (var spec in _gameplayEffectSpecs)
            {
                if (spec.IsActive)
                {
                    var grantedTags = spec.GameplayEffect.TagContainer.GrantedTags;
                    if (grantedTags.Empty) continue;
                    foreach (var t in grantedTags.Tags)
                    foreach (var targetTag in tags.Tags)
                    {
                        if (t != targetTag) continue;
                        // If this is an infinite GE, then return null to signify this is on CD
                        if (spec.GameplayEffect.DurationPolicy ==
                            EffectsDurationPolicy.Infinite)
                            return new CooldownTimer { TimeRemaining = -1, Duration = 0 };

                        var durationRemaining = spec.DurationRemaining();

                        if (!(durationRemaining > longestCooldown)) continue;
                        longestCooldown = durationRemaining;
                        maxDuration = spec.Duration;
                    }
                }
            }

            return new CooldownTimer { TimeRemaining = longestCooldown, Duration = maxDuration };
        }

        public void ClearGameplayEffect()
        {
            foreach (var gameplayEffectSpec in _gameplayEffectSpecs)
            {
                gameplayEffectSpec.DisApply();
                gameplayEffectSpec.TriggerOnRemove();
            }

            _gameplayEffectSpecs.Clear();
            _effectStructureDirty = true;

            OnGameplayEffectContainerIsDirty?.Invoke();
        }

        private void GetStackingEffectSpecByData(GameplayEffect effect, out GameplayEffectSpec spec)
        {
            foreach (var gameplayEffectSpec in _gameplayEffectSpecs)
                if (gameplayEffectSpec.GameplayEffect.StackEqual(effect))
                {
                    spec = gameplayEffectSpec;
                    return;
                }

            spec = null;
        }

        private void GetStackingEffectSpecByDataFrom(GameplayEffect effect,AbilitySystemComponent source, 
            out GameplayEffectSpec spec)
        {
            foreach (var gameplayEffectSpec in _gameplayEffectSpecs)
                if (gameplayEffectSpec.Source == source && 
                    gameplayEffectSpec.GameplayEffect.StackEqual(effect))
                {
                    spec = gameplayEffectSpec;
                    return;
                }

            spec = null;
        }

        private void OnRefreshStackCountMakeContainerDirty()
        {
            OnGameplayEffectContainerIsDirty?.Invoke();
        }
        
        private GameplayEffectSpec Operation_AddNewGameplayEffectSpec(AbilitySystemComponent source,GameplayEffectSpec effectSpec,
            bool overwriteEffectLevel,int effectLevel)
        {
            var level = overwriteEffectLevel ? effectLevel : source.Level;
            effectSpec.Init(source, _owner, level);
            _gameplayEffectSpecs.Add(effectSpec);
            _effectStructureDirty = true;
            effectSpec.TriggerOnAdd();
            effectSpec.Apply();

            // If the gameplay effect was removed immediately after being applied, return false
            if (!_gameplayEffectSpecs.Contains(effectSpec))
            {
#if UNITY_EDITOR
                UnityEngine.Debug.LogWarning(
                    $"GameplayEffect {effectSpec.GameplayEffect.GameplayEffectName} was removed immediately after being applied. This may indicate a problem with the RemoveGameplayEffectsWithTags.");
#endif
                // No need to trigger OnGameplayEffectContainerIsDirty, it has already been triggered when it was removed.
                return null;
            }

            OnGameplayEffectContainerIsDirty?.Invoke();
            return effectSpec;
        }
    }
}