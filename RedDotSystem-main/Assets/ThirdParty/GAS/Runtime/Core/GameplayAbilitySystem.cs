using System.Collections.Generic;
using GAS.General;
using GAS.Runtime;
using UnityEngine;
using UnityEngine.Profiling;

namespace GAS
{
    public class GameplayAbilitySystem
    {
        private static GameplayAbilitySystem _gas;

        private GameplayAbilitySystem()
        {
            const int capacity = 1024;
            AbilitySystemComponents = new List<AbilitySystemComponent>(capacity);
            _cachedAbilitySystemComponents = new List<AbilitySystemComponent>(capacity);
            GASTimer.InitStartTimestamp();

            GasHost = new GameObject("GAS Host").AddComponent<GasHost>();
            GasHost.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(GasHost.gameObject);
            GasHost.gameObject.SetActive(true);
        }

        public List<AbilitySystemComponent> AbilitySystemComponents { get; }

        // 延迟移除模式：稳定快照仅在注册/注销(结构变化)时重建，避免每帧全量拷贝 ASC 列表
        private readonly List<AbilitySystemComponent> _cachedAbilitySystemComponents;
        private bool _ascStructureDirty = true;

        private GasHost GasHost { get; }

        public static GameplayAbilitySystem GAS
        {
            get
            {
                _gas ??= new GameplayAbilitySystem();
                return _gas;
            }
        }

        public bool IsPaused => !GasHost.enabled;

        public void Register(AbilitySystemComponent abilitySystemComponent)
        {
            // if (!GasHost.enabled)
            // {
            //     Debug.LogWarning("[EX] GAS is paused, can't register new ASC!");
            //     return;
            // }

            if (AbilitySystemComponents.Contains(abilitySystemComponent)) return;
            AbilitySystemComponents.Add(abilitySystemComponent);
            _ascStructureDirty = true;
        }

        public bool Unregister(AbilitySystemComponent abilitySystemComponent)
        {
            // if (!GasHost.enabled)
            // {
            //     Debug.LogWarning("[EX] GAS is paused, can't unregister ASC!");
            //     return false;
            // }

            var removed = AbilitySystemComponents.Remove(abilitySystemComponent);
            if (removed) _ascStructureDirty = true;
            return removed;
        }

        public void Pause()
        {
            GasHost.enabled = false;
        }

        public void Unpause()
        {
            GasHost.enabled = true;
        }

        public void ClearComponents()
        {
            foreach (var t in AbilitySystemComponents)
                t.Disable();

            AbilitySystemComponents.Clear();
            _ascStructureDirty = true;
        }

        public void Tick()
        {
            Profiler.BeginSample($"{nameof(GameplayAbilitySystem)}::Tick()");

            // 仅在注册/注销后重建快照，稳定态零拷贝；遍历时注销的 ASC 保留在本帧快照中(与原拷贝语义一致)
            if (_ascStructureDirty)
            {
                _cachedAbilitySystemComponents.Clear();
                _cachedAbilitySystemComponents.AddRange(AbilitySystemComponents);
                _ascStructureDirty = false;
            }

            for (var i = 0; i < _cachedAbilitySystemComponents.Count; i++)
            {
                _cachedAbilitySystemComponents[i].Tick();
            }

            Profiler.EndSample();
        }
    }
}