using static MieMieFrameWork.ModuleHub;

namespace MieMieFrameWork
{
    using System;
    using UnityEngine;

    /// <summary>
    /// MonoBehaviour 生命周期代理：需要每帧驱动但不方便继承 MonoBehaviour 的管理器，把方法挂到这里的委托上
    /// </summary>
    [ManagerAttribute(1)]
    public class MonoManager : MonoBehaviour, IManagerBase
    {
        private Action updateEvent;
        private Action laterUpdateEvent;
        private Action fixedUpdateEvent;

        public void Init()
        {
        }

        public void AddUpdateListener(Action action) => updateEvent += action;

        public void RemoveUpdateListener(Action action) => updateEvent -= action;

        public void AddLaterUpdateListener(Action action) => laterUpdateEvent += action;

        public void RemoveLaterUpdateListener(Action action) => laterUpdateEvent -= action;

        public void AddFixedUpdateListener(Action action) => fixedUpdateEvent += action;

        public void RemoveFixedUpdateListener(Action action) => fixedUpdateEvent -= action;

        private void Update() => updateEvent?.Invoke();

        private void LateUpdate() => laterUpdateEvent?.Invoke();

        private void FixedUpdate() => fixedUpdateEvent?.Invoke();
    }
}
