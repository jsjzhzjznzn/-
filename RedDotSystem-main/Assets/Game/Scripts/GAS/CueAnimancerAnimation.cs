using System;
using Animancer;
using GAS.Runtime;
using UnityEngine;
// BoxGroup/LabelText/InfoBox 都在运行时 Sirenix.OdinInspector.Attributes 程序集（全平台启用），
// 不能包在 #if UNITY_EDITOR 里——下面的字段特性在条件编译外使用，打 AssetBundle（玩家侧编译）会 CS0246
using Sirenix.OdinInspector;

#if UNITY_EDITOR
using GAS.General;
#endif

namespace GAS.Cues
{
    /// <summary>
    /// Animancer 版动画 Cue: 用 AnimancerComponent 播放技能动画片段, 支持混合过渡(Fade)与播放速度。
    /// 与原版 CueAnimation(Animator 硬切 Play/CrossFade)互补:
    /// 本 Cue 直接驱动 AnimationClip(无需 AnimatorController 状态), 天然支持"技能片段自由拼接+攻速变速"。
    /// </summary>
    [CreateAssetMenu(menuName = "EX-GAS/Cue/Animancer Animation Cue", fileName = "CueAnimancerAnimation")]
    public class CueAnimancerAnimation : GameplayCueDurational
    {
#if UNITY_EDITOR
        [BoxGroup]
        [InfoBox("Animancer组件的相对路径, 留空=直接在能力持有者(ASC所在物体)上查找")]
        [LabelText("Animancer相对路径")]
        [SerializeField]
        private string _animancerRelativePath;

        [BoxGroup]
        [LabelText("包含子物体查找")]
        [SerializeField]
        private bool _includeChildren = true;
#else
        [SerializeField] private string _animancerRelativePath;
        [SerializeField] private bool _includeChildren = true;
#endif

        [BoxGroup]
        [LabelText("动画片段")]
        [SerializeField]
        private AnimationClip _clip;

        [BoxGroup]
        [LabelText("混合过渡(秒)")]
        [Tooltip("Fade 过渡时长, 0=无混合立即播放")]
        [SerializeField]
        private float _fadeDuration = 0.25f;

        [BoxGroup]
        [LabelText("Fade模式")]
        [Tooltip("FixedSpeed: 按速度过渡 | FixedDuration: 固定过渡时长 | FromStart: 从头开始且固定时长")]
        [SerializeField]
        private FadeMode _fadeMode = FadeMode.FixedSpeed;

        [BoxGroup]
        [LabelText("播放速度")]
        [Tooltip("1=原速; 攻速加成时>1, 减速技能<1")]
        [SerializeField]
        private float _speed = 1f;

        [BoxGroup]
        [LabelText("Clip结束时停止")]
        [Tooltip("OnRemove 时调用 state.Stop(): 让动画在Clip边界处按Fade自然退出, 而不是停在本帧姿势")]
        [SerializeField]
        private bool _stopOnRemove;

        public string AnimancerRelativePath => _animancerRelativePath;
        public bool IncludeChildren => _includeChildren;
        public AnimationClip Clip => _clip;
        public float FadeDuration => _fadeDuration;
        public FadeMode FadeMode => _fadeMode;
        public float Speed => _speed;
        public bool StopOnRemove => _stopOnRemove;

        public override GameplayCueDurationalSpec CreateSpec(GameplayCueParameters parameters)
        {
            return new CueAnimancerAnimationSpec(this, parameters);
        }

#if UNITY_EDITOR
        public override void OnEditorPreview(GameObject previewObject, int frame, int startFrame, int endFrame)
        {
            if (Clip == null || startFrame > frame || frame > endFrame) return;

            var transform = string.IsNullOrEmpty(AnimancerRelativePath)
                ? previewObject.transform
                : previewObject.transform.Find(AnimancerRelativePath);
            if (transform == null) return;

            var animator = IncludeChildren
                ? transform.GetComponentInChildren<Animator>()
                : transform.GetComponent<Animator>();
            if (animator == null) return;

            float clipFrameCount = (int)(Clip.frameRate * Clip.length);
            if (clipFrameCount <= 0) return;
            if (frame <= clipFrameCount + startFrame)
            {
                var progress = (frame - startFrame) / clipFrameCount;
                if (progress > 1 && Clip.isLooping) progress -= (int)progress;
                Clip.SampleAnimation(animator.gameObject, progress * Clip.length);
            }
        }
#endif
    }

    public class CueAnimancerAnimationSpec : GameplayCueDurationalSpec<CueAnimancerAnimation>
    {
        private readonly AnimancerComponent _animancer;
        private AnimancerState _state;

        public CueAnimancerAnimationSpec(CueAnimancerAnimation cue, GameplayCueParameters parameters) : base(cue,
            parameters)
        {
            var transform = string.IsNullOrEmpty(cue.AnimancerRelativePath)
                ? Owner.transform
                : Owner.transform.Find(cue.AnimancerRelativePath);
            if (transform == null)
            {
                Debug.LogError(
                    $"[GAS] CueAnimancerAnimation: 路径不存在 {cue.AnimancerRelativePath}, cue资产: {cue.name}");
                return;
            }

            _animancer = cue.IncludeChildren
                ? transform.GetComponentInChildren<AnimancerComponent>()
                : transform.GetComponent<AnimancerComponent>();
            if (_animancer == null)
                Debug.LogError(
                    $"[GAS] CueAnimancerAnimation: 未找到 AnimancerComponent, cue资产: {cue.name}, " +
                    $"路径: {cue.AnimancerRelativePath}, IncludeChildren: {cue.IncludeChildren}");
        }

        public override void OnAdd()
        {
            if (_animancer == null || cue.Clip == null) return;

            _state = cue.FadeDuration > 0
                ? _animancer.Play(cue.Clip, cue.FadeDuration, cue.FadeMode)
                : _animancer.Play(cue.Clip);

            // 攻速/减速技能: 通过状态速度控制动画播放速率
            if (cue.Speed != 1f && _state != null)
                _state.Speed = cue.Speed;
        }

        public override void OnRemove()
        {
            // 默认不干预: 动画自然走完(常见于"技能表现短于动画"的场景);
            // 配置 stopOnRemove 时让状态按正常方式淡出, 避免角色停在本帧姿势
            if (cue.StopOnRemove && _state != null && _state.IsValid)
                _state.Stop();
            _state = null;
        }

        public override void OnGameplayEffectActivate()
        {
        }

        public override void OnGameplayEffectDeactivate()
        {
        }

        public override void OnTick()
        {
        }
    }
}
