using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using XLua;

namespace SkierFramework
{
    /// <summary>
    /// 供 Lua 驱动补间动画的静态桥：参数只用 xlua 友好类型（GameObject/string/float/int/object），
    /// DOTween 细节收在 C# 侧，走黑盒反射调用，不需要重跑 xlua gen。
    /// onDone 可以传 Lua function（内部按 LuaFunction 处理）也可以传 C# 的 Action。
    /// 与 UIViewAnim 分工：UIViewAnim 管界面开/关动画，UIAnim 管业务级临时动画；
    /// 同一节点的同名属性不要两边混用，否则会互相争抢。
    /// </summary>
    public static class UIAnim
    {
        private static readonly Dictionary<long, Tween> s_tweens = new Dictionary<long, Tween>();
        private static long s_nextId = 1;

        /// <summary>alpha 补间到目标值</summary>
        public static long FadeTo(GameObject go, float toAlpha, float duration, string ease, object onDone)
        {
            if (go == null)
                return 0;
            CanvasGroup cg = go.GetOrAddComponent<CanvasGroup>();
            return Play(cg.DOFade(toAlpha, duration), go, ease, onDone);
        }

        /// <summary>先把 alpha 置为 fromAlpha 再补间到 toAlpha</summary>
        public static long FadeFrom(GameObject go, float fromAlpha, float toAlpha, float duration, string ease, object onDone)
        {
            if (go == null)
                return 0;
            CanvasGroup cg = go.GetOrAddComponent<CanvasGroup>();
            cg.alpha = fromAlpha;
            return Play(cg.DOFade(toAlpha, duration), go, ease, onDone);
        }

        /// <summary>anchoredPosition 补间；目标不是 RectTransform 时退化到 localPosition</summary>
        public static long MoveTo(GameObject go, float x, float y, float duration, string ease, object onDone)
        {
            if (go == null)
                return 0;

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null)
                return Play(rt.DOAnchorPos(new Vector2(x, y), duration), go, ease, onDone);

            Transform t = go.transform;
            return Play(t.DOLocalMove(new Vector3(x, y, t.localPosition.z), duration), go, ease, onDone);
        }

        /// <summary>从 (fromX,fromY) 瞬移到起点，再滑到 (toX,toY)</summary>
        public static long SlideIn(GameObject go, float fromX, float fromY, float toX, float toY, float duration, string ease, object onDone)
        {
            if (go == null)
                return 0;

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchoredPosition = new Vector2(fromX, fromY);
                return Play(rt.DOAnchorPos(new Vector2(toX, toY), duration), go, ease, onDone);
            }

            Transform t = go.transform;
            float z = t.localPosition.z;
            t.localPosition = new Vector3(fromX, fromY, z);
            return Play(t.DOLocalMove(new Vector3(toX, toY, z), duration), go, ease, onDone);
        }

        public static long ScaleTo(GameObject go, float sx, float sy, float sz, float duration, string ease, object onDone)
        {
            if (go == null)
                return 0;
            return Play(go.transform.DOScale(new Vector3(sx, sy, sz), duration), go, ease, onDone);
        }

        /// <summary>从 fromScale 弹到 toScale（弹窗出场常用 fromScale=0）</summary>
        public static long ScaleFrom(GameObject go, float fromScale, float toScale, float duration, string ease, object onDone)
        {
            if (go == null)
                return 0;
            go.transform.localScale = Vector3.one * fromScale;
            return Play(go.transform.DOScale(Vector3.one * toScale, duration), go, ease, onDone);
        }

        /// <summary>localEulerAngles 补间</summary>
        public static long Rotate(GameObject go, float ex, float ey, float ez, float duration, string ease, object onDone)
        {
            if (go == null)
                return 0;
            return Play(go.transform.DOLocalRotate(new Vector3(ex, ey, ez), duration, RotateMode.FastBeyond360), go, ease, onDone);
        }

        public static long Punch(GameObject go, float x, float y, float duration, int vibrato, object onDone)
        {
            if (go == null)
                return 0;

            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt != null)
                return Play(rt.DOPunchAnchorPos(new Vector2(x, y), duration, vibrato, 1f), go, null, onDone);
            return Play(go.transform.DOPunchPosition(new Vector3(x, y, 0f), duration, vibrato, 1f), go, null, onDone);
        }

        /// <summary>
        /// 播放 legacy Animation 上的 clip：DOTween 只负责等时长，clip 由 Animation 组件按原速播。
        /// duration 传 0 表示用 clip.length。找不到组件或 clip 时返回 0。
        /// </summary>
        public static long PlayClip(GameObject go, string clipName, float duration, object onDone)
        {
            if (go == null)
                return 0;

            Animation anim = go.GetComponent<Animation>();
            if (anim == null)
                anim = go.GetComponentInChildren<Animation>();
            if (anim == null)
            {
                Debug.LogWarning("[UIAnim] PlayClip: 目标上没有 legacy Animation 组件: " + go.name);
                return 0;
            }

            AnimationClip clip = string.IsNullOrEmpty(clipName) ? anim.clip : anim.GetClip(clipName);
            if (clip == null)
            {
                Debug.LogWarning("[UIAnim] PlayClip: clip 不存在: " + clipName + " on " + go.name);
                return 0;
            }

            float length = duration > 0f ? duration : clip.length;
            if (length <= 0f)
                return 0;

            float dummy = 0f;
            Tweener dummyTween = DOTween.To(() => dummy, v => dummy = v, 1f, length);
            dummyTween.OnStart(() =>
            {
                if (anim != null)
                    anim.Play(clipName);
            });
            return Play(dummyTween, go, null, onDone);
        }

        /// <summary>延时回调；目标销毁或 Kill 后不再触发</summary>
        public static long Delay(GameObject go, float duration, object onDone)
        {
            if (go == null || duration <= 0f)
                return 0;

            float dummy = 0f;
            return Play(DOTween.To(() => dummy, v => dummy = v, 1f, duration), go, "Linear", onDone);
        }

        /// <summary>Kill 该节点上的补间（含 UIViewAnim 的开/关序列），默认不触发完成回调</summary>
        public static void Kill(GameObject go, bool complete = false)
        {
            if (go == null)
                return;
            DOTween.Kill(go, complete);
        }

        /// <summary>连子节点一起 Kill</summary>
        public static void KillAllUnder(GameObject root, bool complete = false)
        {
            if (root == null)
                return;

            Transform t = root.transform;
            for (int i = 0; i < t.childCount; i++)
                DOTween.Kill(t.GetChild(i).gameObject, complete);
            DOTween.Kill(root, complete);
        }

        /// <summary>只 Kill 指定 handle 那条补间，不影响同节点上的其它动画</summary>
        public static void KillById(long handleId, bool complete = false)
        {
            if (handleId <= 0)
                return;
            if (s_tweens.TryGetValue(handleId, out Tween tween) && tween != null)
                tween.Kill(complete);
            s_tweens.Remove(handleId);
        }

        public static bool IsPlaying(GameObject go)
        {
            return go != null && DOTween.IsTweening(go);
        }

        public static bool IsHandlePlaying(long handleId)
        {
            if (handleId <= 0)
                return false;
            return s_tweens.TryGetValue(handleId, out Tween tween) && tween != null && tween.active;
        }

        private static long Play(Tween tween, GameObject go, string easeName, object onDone)
        {
            if (tween == null)
                return 0;

            Ease ease = ParseEase(easeName);
            if (ease != Ease.Unset)
                tween.SetEase(ease);

            long id = s_nextId++;
            s_tweens[id] = tween;

            Callback callback = Callback.From(onDone, go);
            bool finished = false;

            tween.SetTarget(go);
            tween.SetLink(go, LinkBehaviour.KillOnDestroy);
            tween.OnComplete(() =>
            {
                finished = true;
                s_tweens.Remove(id);
                callback.Invoke();
            });
            // 完成时 DOTween 也会走 OnKill（autoKill），故用 finished 区分打断路径
            tween.OnKill(() =>
            {
                if (!finished)
                {
                    s_tweens.Remove(id);
                    callback.Cancel();
                }
            });
            return id;
        }

        private static Ease ParseEase(string easeName)
        {
            if (string.IsNullOrEmpty(easeName))
                return Ease.Unset;
            if (Enum.TryParse(easeName, true, out Ease ease))
                return ease;

            Debug.LogWarning("[UIAnim] 无法识别的 ease 名称，按默认曲线播放: " + easeName);
            return Ease.Unset;
        }

        /// <summary>
        /// 回调包装：完成时调用一次，打断/Kill/目标销毁时不调用但仍要释放 LuaFunction 引用。
        /// </summary>
        private class Callback
        {
            private Action _action;
            private LuaFunction _luaFunction;
            private readonly GameObject _owner;
            private bool _disposed;

            private Callback(Action action, LuaFunction luaFunction, GameObject owner)
            {
                _action = action;
                _luaFunction = luaFunction;
                _owner = owner;
            }

            public static Callback From(object onDone, GameObject owner)
            {
                if (onDone is LuaFunction luaFunction)
                    return new Callback(null, luaFunction, owner);
                return new Callback(onDone as Action, null, owner);
            }

            public void Invoke()
            {
                if (_disposed)
                    return;
                _disposed = true;

                try
                {
                    // 目标已销毁则不回调，避免 Lua 侧持有失效引用后报错
                    if (_owner != null)
                    {
                        _action?.Invoke();
                        _luaFunction?.Call();
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[UIAnim] 动画回调执行出错: " + e);
                }
                finally
                {
                    Release();
                }
            }

            public void Cancel()
            {
                if (_disposed)
                    return;
                _disposed = true;
                Release();
            }

            private void Release()
            {
                _action = null;
                if (_luaFunction != null)
                {
                    _luaFunction.Dispose();
                    _luaFunction = null;
                }
            }
        }
    }
}
