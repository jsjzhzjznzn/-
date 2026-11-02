namespace MieMieFrameWork.M_InputSystem
{
    using System;
    using System.Collections.Generic;
    using Sirenix.OdinInspector;
    using UnityEngine;
    using UnityEngine.Events;
    using static ModuleHub;

    /// <summary>
    /// 可缓冲的输入类型（按本工程 playerinputaction 实际动作裁剪）
    /// 源框架还含 Jump，本工程角色无跳跃动作，未纳入
    /// </summary>
    [Serializable]
    public enum E_InputType
    {
        /// <summary>轻攻击（连段主要依赖它）</summary>
        Attack,

        /// <summary>重攻击 / 防御键（本工程右键兼作闪避）</summary>
        HeavyAttack,

        /// <summary>冲刺（Shift）</summary>
        Dash,

        /// <summary>技能（E）</summary>
        Skill,
    }

    /// <summary>
    /// 输入缓冲管理器（移植自 Mm-FrameWork 的 InputBuffer3D）
    ///
    /// 职责：把"按键按下"与"动作可执行"解耦。
    /// 按下瞬间只入缓冲（带剩余时间），消费方在自己的窗口里查一次，
    /// 窗口关死期间的按键不会凭空消失，也不会在很久以后突然出手。
    ///
    /// 与源实现一致的要点：
    /// - 用 deltaTime 递减 → 顿帧（timeScale→0）期间缓冲不流逝
    /// - 消费即清空（一次性），杜绝一次按键接两段
    /// - Interrupt 用于主动作废（断连、死亡、切场景）
    /// </summary>
    [ManagerAttribute(8)]
    public class InputBuffer3D : MonoBehaviour, IManagerBase, IDisposable
    {
        [Header("缓冲设置")]
        [SerializeField, LabelText("默认缓冲时间"), Range(0.1f, 1f)]
        private float defaultBufferTime = 0.2f;

        [SerializeField, LabelText("攻击缓冲时间"), Range(0.1f, 1f)]
        private float attackBufferTime = 0.4f;

        [SerializeField, LabelText("单类型队列上限"), Range(1, 10)]
        private int maxQueueSize = 3;

        /// <summary>当前生效的缓冲管理器（ModuleHub 初始化后可用，未初始化时为 null）</summary>
        public static InputBuffer3D Instance { get; private set; }

        /// <summary>单一缓冲槽，下标与 E_InputType 对齐</summary>
        private readonly BufferSlot[] bufferSlots = new BufferSlot[InputTypeCount];

        /// <summary>队列缓冲（预留：连按两次的预处理，当前玩法未启用）</summary>
        private readonly Queue<BufferSlot>[] queueBuffers = new Queue<BufferSlot>[InputTypeCount];

        private static readonly E_InputType[] InputTypes = (E_InputType[])Enum.GetValues(typeof(E_InputType));

        private static int InputTypeCount => InputTypes.Length;

        #region 生命周期

        public void Init()
        {
            InitializeBuffers();

            if (ModuleHub.Instance != null && ModuleHub.Instance.TryGetManager<MonoManager>(out var FrameDriver))
                FrameDriver.AddUpdateListener(Tick);

            Instance = this;
        }

        public void Dispose()
        {
            if (ModuleHub.Instance != null && ModuleHub.Instance.TryGetManager<MonoManager>(out var FrameDriver))
                FrameDriver.RemoveUpdateListener(Tick);

            InterruptAll();

            if (Instance == this)
                Instance = null;
        }

        private void InitializeBuffers()
        {
            for (int Index = 0; Index < InputTypeCount; Index++)
            {
                bufferSlots[Index] = new BufferSlot(GetBufferTime((E_InputType)Index));
                queueBuffers[Index] = new Queue<BufferSlot>();
            }
        }

        #endregion

        #region 写入

        /// <summary>
        /// 写入一次输入缓冲（重复写入直接刷新剩余时间）
        /// </summary>
        /// <param name="inputType">动作类型</param>
        /// <param name="customBufferTime">自定义缓冲时长（&lt;=0 用该类型的默认值）</param>
        public void BufferInput(E_InputType inputType, float customBufferTime = -1f)
        {
            if (!IsValid(inputType))
                return;

            bufferSlots[(int)inputType].Activate(customBufferTime > 0f ? customBufferTime : GetBufferTime(inputType));
        }

        /// <summary>
        /// 写入队列缓冲（同一动作多次按下的先后顺序保留）
        /// </summary>
        public void BufferQueueInput(E_InputType inputType, float customBufferTime = -1f)
        {
            if (!IsValid(inputType))
                return;

            var Queue = queueBuffers[(int)inputType];
            if (Queue.Count >= maxQueueSize)
                Queue.Dequeue();

            Queue.Enqueue(new BufferSlot(customBufferTime > 0f ? customBufferTime : GetBufferTime(inputType)));
        }

        #endregion

        #region 消费

        /// <summary>
        /// 检查并消耗缓冲：有效则清空并返回 true
        /// </summary>
        public bool ConsumeBuffer(E_InputType inputType, UnityAction callback = null)
        {
            if (!IsValid(inputType) || !bufferSlots[(int)inputType].IsActive)
                return false;

            bufferSlots[(int)inputType].Consume();
            callback?.Invoke();
            return true;
        }

        /// <summary>
        /// 检查是否有缓冲（不消耗）
        /// </summary>
        public bool HasBuffer(E_InputType inputType)
        {
            return IsValid(inputType) && bufferSlots[(int)inputType].IsActive;
        }

        /// <summary>剩余缓冲时间（秒），无缓冲为 0</summary>
        public float GetRemainTime(E_InputType inputType)
        {
            return IsValid(inputType) ? bufferSlots[(int)inputType].RemainingTime : 0f;
        }

        /// <summary>缓冲进度 0→1（用于 UI 显示按键余量，调试用）</summary>
        public float GetProgress(E_InputType inputType)
        {
            return IsValid(inputType) ? bufferSlots[(int)inputType].Progress : 0f;
        }

        /// <summary>
        /// 检查并消耗队列缓冲（FIFO）
        /// </summary>
        public bool ConsumeQueueBuffer(E_InputType inputType, UnityAction callback = null)
        {
            if (!IsValid(inputType))
                return false;

            var Queue = queueBuffers[(int)inputType];
            while (Queue.Count > 0)
            {
                var Slot = Queue.Dequeue();
                if (!Slot.IsActive)
                    continue;

                callback?.Invoke();
                return true;
            }

            return false;
        }

        #endregion

        #region 作废

        /// <summary>作废指定类型的缓冲（断连、状态强制切换时用）</summary>
        public void Interrupt(E_InputType inputType)
        {
            if (!IsValid(inputType))
                return;

            bufferSlots[(int)inputType].Interrupt();
            queueBuffers[(int)inputType].Clear();
        }

        /// <summary>作废全部缓冲（死亡、切场景、UI 接管时用）</summary>
        public void InterruptAll()
        {
            for (int Index = 0; Index < InputTypeCount; Index++)
            {
                bufferSlots[Index]?.Interrupt();
                queueBuffers[Index]?.Clear();
            }
        }

        #endregion

        #region 帧驱动

        private void Tick()
        {
            float DeltaTime = Time.deltaTime;

            for (int Index = 0; Index < InputTypeCount; Index++)
            {
                bufferSlots[Index].Update(DeltaTime);
                UpdateQueueBuffer(queueBuffers[Index], DeltaTime);
            }
        }

        /// <summary>
        /// 队列按写入时间排序：队首未过期则后面的也不会过期，直接跳出
        /// </summary>
        private static void UpdateQueueBuffer(Queue<BufferSlot> queue, float deltaTime)
        {
            while (queue.Count > 0)
            {
                var Slot = queue.Peek();
                Slot.Update(deltaTime);

                if (Slot.IsActive)
                    break;

                queue.Dequeue();
            }
        }

        #endregion

        #region 内部工具

        private float GetBufferTime(E_InputType inputType)
        {
            return inputType == E_InputType.Attack ? attackBufferTime : defaultBufferTime;
        }

        private static bool IsValid(E_InputType inputType)
        {
            int Index = (int)inputType;
            return Index >= 0 && Index < InputTypeCount;
        }

        #endregion
    }

    /// <summary>
    /// 缓冲槽：剩余时间 + 激活标记，过期自动失效
    /// </summary>
    [Serializable]
    public class BufferSlot
    {
        [SerializeField] private float bufferTime;

        private float remainingTime;
        private bool isActive;

        public bool IsActive => isActive && remainingTime > 0f;

        public float RemainingTime => IsActive ? remainingTime : 0f;

        public float Progress => bufferTime <= 0f ? 1f : Mathf.Clamp01(1f - remainingTime / bufferTime);

        public BufferSlot(float bufferTime = 0.2f)
        {
            this.bufferTime = bufferTime;
            remainingTime = 0f;
            isActive = false;
        }

        public void Activate(float duration)
        {
            bufferTime = duration;
            remainingTime = duration;
            isActive = true;
        }

        public void Consume() => Interrupt();

        public void Interrupt()
        {
            isActive = false;
            remainingTime = 0f;
        }

        public void Update(float deltaTime)
        {
            if (!isActive)
                return;

            remainingTime -= deltaTime;
            if (remainingTime <= 0f)
                Interrupt();
        }
    }
}
