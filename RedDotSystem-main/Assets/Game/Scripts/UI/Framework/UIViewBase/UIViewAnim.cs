using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using UnityEngine;

namespace SkierFramework
{
    public enum UIAnimPropType
    {
        Alpha,
        AnchoredPos,
        LocalScale,
        LocalRotation,
        Clip,
    }

    [Serializable]
    public class UIViewAnimStep
    {
        public int targetIndex = -1;
        public UIAnimPropType prop = UIAnimPropType.Alpha;

        public float fromAlpha;
        public float toAlpha = 1f;

        public Vector2 fromPos;
        public Vector2 toPos;

        public Vector3 fromScale = Vector3.one;
        public Vector3 toScale = Vector3.one;

        public Vector3 fromRotEuler;
        public Vector3 toRotEuler;

        public AnimationClip clip;

        [Min(0f)] public float duration = 0.2f;
        [Min(0f)] public float delay;

        /// <summary>与上一条步骤并排播放（Join），而不是等它播完（Append）。多条属性同时动就用它</summary>
        public bool joinWithPrevious;

        public bool useCurve;
        public AnimationCurve curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        public Ease ease = Ease.OutQuad;
        public bool unscaledTime;
    }

    /// <summary>
    /// 界面开/关动画。DOTween 步骤式序列：每条步骤补间一个属性（alpha/锚点位移/缩放/旋转/legacy Animation clip）。
    /// OpenAsync/CloseAsync 保持 UniTask 契约供 UIViewController 等待；被打断时从当前实际值续放到新终点。
    /// </summary>
    [DisallowMultipleComponent]
    public class UIViewAnim : MonoBehaviour
    {
        public List<Transform> targets = new List<Transform>();
        public List<UIViewAnimStep> openSteps = new List<UIViewAnimStep>();
        public List<UIViewAnimStep> closeSteps = new List<UIViewAnimStep>();

        /// <summary>从头播放时把该组步骤的起点写回目标，避免池复用/上次未播完留下的中间值</summary>
        public bool autoResetOnPlay = true;

        private enum PlayState
        {
            Idle,
            Opening,
            Closing,
        }

        private CanvasGroup _canvasGroup;
        private Sequence _sequence;
        private UniTaskCompletionSource _tcs;
        private PlayState _state = PlayState.Idle;
        private readonly List<Animation> _playingClips = new List<Animation>();

        public bool IsPlaying => _state != PlayState.Idle;

        private CanvasGroup CanvasGroupRef
        {
            get
            {
                if (_canvasGroup == null)
                    _canvasGroup = gameObject.GetOrAddComponent<CanvasGroup>();
                return _canvasGroup;
            }
        }

        private void OnEnable()
        {
            // 对象池复用：丢弃残留状态（此时上一轮的序列应已被 OnDisable 回收）
            if (_sequence != null)
            {
                if (_sequence.active)
                    _sequence.Kill(false);
                _sequence = null;
            }
            _tcs = null;
            _state = PlayState.Idle;
            _playingClips.Clear();
        }

        private void OnDisable()
        {
            var seq = _sequence;
            var tcs = _tcs;
            _sequence = null;
            _tcs = null;
            _state = PlayState.Idle;

            if (seq != null && seq.active)
                seq.Kill(false);
            else
                StopClips();

            // 界面被隐藏/回收时，避免等待动画结束的 await 悬挂
            tcs?.TrySetResult();
        }

        public async UniTask OpenAsync(CancellationToken cancellationToken = default)
        {
            await PlayAsync(openSteps, PlayState.Opening, cancellationToken);
        }

        public async UniTask CloseAsync(CancellationToken cancellationToken = default)
        {
            await PlayAsync(closeSteps, PlayState.Closing, cancellationToken);
        }

        private async UniTask PlayAsync(List<UIViewAnimStep> steps, PlayState state, CancellationToken cancellationToken)
        {
            bool interrupted = Interrupt();

            if (!gameObject.activeSelf)
                return;

            if (steps == null || steps.Count == 0)
            {
                _state = PlayState.Idle;
                return;
            }

            if (autoResetOnPlay && !interrupted)
                ResetTargets(steps);

            Sequence seq = BuildSequence(steps);
            if (seq == null)
            {
                _state = PlayState.Idle;
                StopClips();
                return;
            }

            var tcs = new UniTaskCompletionSource();
            _sequence = seq;
            _tcs = tcs;
            _state = state;

            seq.OnComplete(() => Settle(seq, tcs));
            seq.OnKill(() => { StopClips(); Settle(seq, tcs); });

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                await tcs.Task;
            }
        }

        /// <summary>终止进行中的序列并放行其等待者，返回是否真的打断了一段还在播的动画</summary>
        private bool Interrupt()
        {
            var seq = _sequence;
            if (seq == null)
                return false;

            var tcs = _tcs;
            _sequence = null;
            _tcs = null;
            _state = PlayState.Idle;

            bool wasActive = seq.active;
            if (wasActive)
                seq.Kill(false); // OnKill 里 StopClips + Settle 放行旧等待者
            else
                tcs?.TrySetResult();

            return wasActive;
        }

        private void Settle(Sequence seq, UniTaskCompletionSource tcs)
        {
            if (ReferenceEquals(_sequence, seq))
            {
                _sequence = null;
                _tcs = null;
                _state = PlayState.Idle;
            }
            tcs?.TrySetResult();
        }

        private Sequence BuildSequence(List<UIViewAnimStep> steps)
        {
            var seq = DOTween.Sequence();
            seq.SetTarget(gameObject);

            bool anyUnscaled = false;
            int appended = 0;

            for (int i = 0; i < steps.Count; i++)
            {
                UIViewAnimStep step = steps[i];
                if (step == null)
                    continue;

                if (step.unscaledTime)
                    anyUnscaled = true;

                if (step.delay > 0f)
                    seq.AppendInterval(step.delay);

                if (step.prop == UIAnimPropType.Clip)
                {
                    if (AppendClip(seq, step))
                        appended++;
                    continue;
                }

                Transform target = ResolveTarget(step);
                if (target == null)
                    continue;

                Tween tween = CreateTween(step, target);
                if (tween == null)
                    continue;

                if (step.useCurve && step.curve != null)
                    tween.SetEase(step.curve);
                else
                    tween.SetEase(step.ease);

                if (step.joinWithPrevious && appended > 0)
                    seq.Join(tween);
                else
                    seq.Append(tween);
                appended++;
            }

            if (appended == 0)
            {
                seq.Kill(false);
                return null;
            }

            if (anyUnscaled)
                seq.SetUpdate(UpdateType.Normal, true);

            seq.SetLink(gameObject, LinkBehaviour.KillOnDisable);
            return seq;
        }

        private Tween CreateTween(UIViewAnimStep step, Transform target)
        {
            switch (step.prop)
            {
                case UIAnimPropType.Alpha:
                    return CanvasGroupRef.DOFade(step.toAlpha, step.duration);
                case UIAnimPropType.AnchoredPos:
                    if (target is RectTransform rt)
                        return rt.DOAnchorPos(step.toPos, step.duration);
                    Debug.LogWarning("[UIViewAnim] AnchoredPos 步骤的目标不是 RectTransform，已跳过: " + target.name, this);
                    return null;
                case UIAnimPropType.LocalScale:
                    return target.DOScale(step.toScale, step.duration);
                case UIAnimPropType.LocalRotation:
                    return target.DOLocalRotate(step.toRotEuler, step.duration, RotateMode.Fast);
                default:
                    return null;
            }
        }

        /// <summary>clip 无法倒放，用等长的虚拟计时占位，由 Animation 组件按原速播放</summary>
        private bool AppendClip(Sequence seq, UIViewAnimStep step)
        {
            Transform target = ResolveTarget(step);
            var anim = target != null ? target.GetComponent<Animation>() : null;
            if (anim == null)
            {
                Debug.LogWarning("[UIViewAnim] Clip 步骤的目标上没有 legacy Animation 组件，已跳过（Animator 支持待后续）", this);
                return false;
            }

            AnimationClip clip = step.clip != null ? step.clip : anim.clip;
            float length = clip != null ? clip.length : step.duration;
            if (length <= 0f)
            {
                Debug.LogWarning("[UIViewAnim] Clip 步骤时长为 0，已跳过", this);
                return false;
            }

            string clipName = clip != null ? clip.name : null;
            _playingClips.Add(anim);

            seq.AppendCallback(() =>
            {
                if (anim == null)
                    return;
                if (clipName != null && anim.GetClip(clipName) != null)
                    anim.Play(clipName);
                else
                    anim.Play();
            });
            seq.AppendInterval(length);
            return true;
        }

        private void ResetTargets(List<UIViewAnimStep> steps)
        {
            for (int i = 0; i < steps.Count; i++)
            {
                UIViewAnimStep step = steps[i];
                if (step == null)
                    continue;

                if (step.prop == UIAnimPropType.Alpha)
                {
                    CanvasGroupRef.alpha = step.fromAlpha;
                    continue;
                }

                Transform target = ResolveTarget(step);
                if (target == null)
                    continue;

                switch (step.prop)
                {
                    case UIAnimPropType.AnchoredPos:
                        if (target is RectTransform rt)
                            rt.anchoredPosition = step.fromPos;
                        break;
                    case UIAnimPropType.LocalScale:
                        target.localScale = step.fromScale;
                        break;
                    case UIAnimPropType.LocalRotation:
                        target.localEulerAngles = step.fromRotEuler;
                        break;
                    case UIAnimPropType.Clip:
                        var anim = target.GetComponent<Animation>();
                        if (anim != null)
                            anim.Rewind();
                        break;
                }
            }
        }

        private Transform ResolveTarget(UIViewAnimStep step)
        {
            if (step.targetIndex < 0)
                return transform;

            if (targets == null || step.targetIndex >= targets.Count || targets[step.targetIndex] == null)
            {
                Debug.LogWarning("[UIViewAnim] 步骤 targetIndex 无效，已跳过: " + step.targetIndex, this);
                return null;
            }
            return targets[step.targetIndex];
        }

        private void StopClips()
        {
            for (int i = 0; i < _playingClips.Count; i++)
            {
                if (_playingClips[i] != null)
                    _playingClips[i].Stop();
            }
            _playingClips.Clear();
        }
    }
}
