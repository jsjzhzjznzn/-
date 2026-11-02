using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Activity
{
    /// <summary>
    /// 全屏输入探针：把指针按下/拖动/抬起转成**屏幕坐标**事件，Lua 在 on_update 里逐帧取走。
    /// 事件格式："down|sx,sy" / "drag|sx,sy" / "up|sx,sy"（坐标为 Screen 像素，直接喂给相机射线）。
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class ActivityDragProbe : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private readonly Queue<string> _events = new Queue<string>();
        private Vector2 _lastScreenPoint;
        private bool _pressing;

        /// <summary>是否处于按下状态</summary>
        public bool IsPressing => _pressing;

        /// <summary>最近一次的屏幕坐标</summary>
        public Vector2 LastPoint => _lastScreenPoint;

        private void Awake()
        {
            var image = GetComponent<Image>();
            if (image != null)
            {
                image.raycastTarget = true;
                if (image.color.a <= 0.001f) image.color = new Color(0f, 0f, 0f, 0.001f); // 全透明也要收事件
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _pressing = true;
            Push("down", eventData);
        }

        private void Update()
        {
            // 悬停需要“指针当前位置”：没按住时不会产生按下/拖动/抬起事件，直接读鼠标位置
            if (!_pressing) _lastScreenPoint = Input.mousePosition;
        }

        public void OnDrag(PointerEventData eventData)
        {
            Push("drag", eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _pressing = false;
            Push("up", eventData);
        }

        /// <summary>取出一条待处理事件，没有则返回空串（Lua 每帧调一次直到空）</summary>
        public string PopEvent()
        {
            return _events.Count > 0 ? _events.Dequeue() : string.Empty;
        }

        /// <summary>清空未处理事件（切界面/关闭时用）</summary>
        public void ClearEvents()
        {
            _events.Clear();
            _pressing = false;
        }

        private void Push(string kind, PointerEventData eventData)
        {
            _lastScreenPoint = eventData.position;
            _events.Enqueue(kind + "|" + eventData.position.x.ToString("F1", CultureInfo.InvariantCulture)
                                   + "," + eventData.position.y.ToString("F1", CultureInfo.InvariantCulture));
            if (_events.Count > 64) _events.Dequeue();
        }
    }
}
