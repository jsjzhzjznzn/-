using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace SkierFramework
{
    public class UIView : MonoBehaviour, IBindableUI
    {
        private UIViewController _controller;
        private GameObject _lastSelect;
        private Canvas _canvas;
        public GameObject DefaultSelect;

        public UIViewController Controller => _controller;
        public object userData;

        public virtual void OnInit(UIControlData uIControlData, UIViewController controller)
        {
            if (uIControlData != null)
            {
                uIControlData.BindDataTo(this);
            }
            _controller = controller;
            // 缩放由层 Canvas 的 CanvasScaler 统一负责，嵌套面板只加 Canvas（做排序/批渲染）+ 射线接收，
            // 不挂未配置的 CanvasScaler——其默认值 ConstantPixelSize 会与层缩放语义打架
            _canvas = gameObject.GetOrAddComponent<Canvas>();
            gameObject.GetOrAddComponent<GraphicRaycaster>();
            // 框架 UI 相机只渲染 UI 层；预制体/程序化面板的整棵子树必须统一到 UI 层，
            // 否则面板逻辑正常但画面完全不可见（只渲染到 Default 层）。
            gameObject.SetLayerRecursively(Layer.UI);
        }

        /// <summary>
        /// 事件监听
        /// </summary>
        public virtual void OnAddListener() { }

        /// <summary>
        /// 事件移除
        /// </summary>
        public virtual void OnRemoveListener() { }

        /// <summary>
        /// 打开
        /// </summary>
        public virtual void OnOpen(object userData)
        {
            this.userData = userData;
            SortOrder();

            _canvas.overrideSorting = true;
            _canvas.sortingOrder = _controller.order;

            OnAddListener();

            _lastSelect = UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject;
        }

        protected virtual void SortOrder()
        {
            SortOrder(transform, _controller.order + 1);
        }

        /// <summary>
        /// 递归的将所有孩子层级设置正确：一些默认摆在UI上的特效等正确分配层级
        /// </summary>
        protected int SortOrder(Transform target, int order)
        {
            var canvas = target.GetComponent<Canvas>();
            if (canvas != null && canvas != _canvas)
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder = order++;
                canvas.gameObject.layer = Layer.UI;
            }
            var psr = target.GetComponent<ParticleSystemRenderer>();
            if (psr != null)
            {
                psr.sortingOrder = order++;
                psr.gameObject.layer = Layer.UI;
            }
            var sortGroup = target.GetComponent<SortingGroup>();
            if (sortGroup != null)
            {
                sortGroup.sortingOrder = order++;
                sortGroup.gameObject.SetLayerRecursively(Layer.UI);
            }
            var sr = target.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.sortingOrder = order++;
                sr.gameObject.layer = Layer.UI;
            }
            for (int i = 0; i < target.childCount; i++)
            {
                order = SortOrder(target.GetChild(i), order);
            }
            return order;
        }

        /// <summary>
        /// 恢复
        /// </summary>
        public virtual void OnResume()
        {
            if (DefaultSelect != null)
            {
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(DefaultSelect);
            }
        }

        /// <summary>
        /// 被覆盖
        /// </summary>
        public virtual void OnPause()
        {
        }

        /// <summary>
        /// 被关闭
        /// </summary>
        public virtual void OnClose()
        {
            OnRemoveListener();

            if (_lastSelect != null && _lastSelect.activeInHierarchy)
            {
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(_lastSelect);
            }

            // 关闭动画已播完才走到这里，此刻清掉业务侧残留补间是安全的
            UIAnim.Kill(gameObject);
        }

        /// <summary>
        /// 取消按钮响应
        /// </summary>
        public virtual void OnCancel()
        {
            UIManager.Instance.Close(_controller.uiId).Forget();
        }

        /// <summary>
        /// 被卸载释放
        /// </summary>
        public virtual void OnRelease()
        {
            // 回收进对象池不会走 OnDestroy，子节点上的业务补间必须在这里清干净
            UIAnim.KillAllUnder(gameObject);
        }
    }
}
