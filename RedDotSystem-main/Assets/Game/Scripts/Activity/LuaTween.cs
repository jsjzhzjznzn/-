using DG.Tweening;
using UnityEngine;

namespace Game.Activity
{
    /// <summary>
    /// 给 Lua 用的 DOTween 薄封装（Lua 调不到扩展方法，统一走这里的静态入口）。
    /// 只用 DOTween 核心 API（To/Kill/Complete），不依赖 UI 模块的扩展类，避免模块缺失时编译不过。
    /// </summary>
    public static class LuaTween
    {
        /// <summary>世界坐标位移</summary>
        public static void Move(Transform target, float x, float y, float z, float duration)
        {
            if (target == null) return;
            ShortcutExtensions.DOMove(target, new Vector3(x, y, z), duration);
        }

        /// <summary>统一缩放</summary>
        public static void Scale(Transform target, float scale, float duration)
        {
            if (target == null) return;
            ShortcutExtensions.DOScale(target, scale, duration);
        }

        /// <summary>本地坐标位移（手牌/场上卡牌这类挂在容器下的对象用它）</summary>
        public static void LocalMove(Transform target, float x, float y, float z, float duration)
        {
            if (target == null) return;
            Vector3 end = new Vector3(x, y, z);
            // SetTarget：让 Stop(target) 能找到它，也避免无 target 的补间被 DOTween 安全模式剔除
            DOTween.To(() => target.localPosition, value => target.localPosition = value, end, duration).SetTarget(target);
        }

        /// <summary>本地欧拉角补间（手牌扇形角度，对应原版 DOLocalRotate）</summary>
        public static void LocalRotate(Transform target, float x, float y, float z, float duration)
        {
            if (target == null) return;
            ShortcutExtensions.DOLocalRotate(target, new Vector3(x, y, z), duration);
        }

        /// <summary>攻击冲撞：冲到目标位置再弹回（原版 DOMove + SetLoops(2, Yoyo) + InExpo）</summary>
        public static void AttackLunge(Transform target, float x, float y, float z, float duration)
        {
            if (target == null) return;
            ShortcutExtensions.DOMove(target, new Vector3(x, y, z), duration)
                .SetLoops(2, LoopType.Yoyo)
                .SetEase(Ease.InExpo);
        }

        /// <summary>数值跳动：放大到 peak 再回到原值（原版 DOScale(1.5, 0.3) + Yoyo）</summary>
        public static void ScalePulse(Transform target, float peak, float duration)
        {
            if (target == null) return;
            ShortcutExtensions.DOScale(target, peak, duration).SetLoops(2, LoopType.Yoyo);
        }

        /// <summary>CanvasGroup 透明度</summary>
        public static void Fade(CanvasGroup group, float alpha, float duration)
        {
            if (group == null) return;
            DOTween.To(() => group.alpha, value => group.alpha = value, alpha, duration);
        }

        /// <summary>界面元素 anchoredPosition 位移</summary>
        public static void AnchorPos(RectTransform target, float x, float y, float duration)
        {
            if (target == null) return;
            Vector2 end = new Vector2(x, y);
            DOTween.To(() => target.anchoredPosition, value => target.anchoredPosition = value, end, duration);
        }

        /// <summary>停止某个对象上的所有补间（回位/取消拖拽时用）</summary>
        public static void Stop(Transform target)
        {
            if (target == null) return;
            DOTween.Kill(target);
        }

        /// <summary>立即结束某个对象上的补间（停在终点，避免回位时不稳）</summary>
        public static void Finish(Transform target)
        {
            if (target == null) return;
            DOTween.Complete(target, false);
        }
    }
}