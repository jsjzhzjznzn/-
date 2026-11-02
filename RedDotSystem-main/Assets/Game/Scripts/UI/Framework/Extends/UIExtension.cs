using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SkierFramework
{

    public enum AnchorPresets
    {
        TopLeft,
        TopCenter,
        TopRight,

        MiddleLeft,
        MiddleCenter,
        MiddleRight,

        BottomLeft,
        BottonCenter,
        BottomRight,
        BottomStretch,

        VertStretchLeft,
        VertStretchRight,
        VertStretchCenter,

        HorStretchTop,
        HorStretchMiddle,
        HorStretchBottom,

        StretchAll
    }

    public enum PivotPresets
    {
        TopLeft,
        TopCenter,
        TopRight,

        MiddleLeft,
        MiddleCenter,
        MiddleRight,

        BottomLeft,
        BottomCenter,
        BottomRight,
    }

    public static class UIExtension
    {
        public static void SetAnchor(this RectTransform source, AnchorPresets allign, int offsetX = 0, int offsetY = 0)
        {
            source.anchoredPosition = new Vector3(offsetX, offsetY, 0);

            switch (allign)
            {
                case (AnchorPresets.TopLeft):
                    {
                        source.anchorMin = new Vector2(0, 1);
                        source.anchorMax = new Vector2(0, 1);
                        break;
                    }
                case (AnchorPresets.TopCenter):
                    {
                        source.anchorMin = new Vector2(0.5f, 1);
                        source.anchorMax = new Vector2(0.5f, 1);
                        break;
                    }
                case (AnchorPresets.TopRight):
                    {
                        source.anchorMin = new Vector2(1, 1);
                        source.anchorMax = new Vector2(1, 1);
                        break;
                    }

                case (AnchorPresets.MiddleLeft):
                    {
                        source.anchorMin = new Vector2(0, 0.5f);
                        source.anchorMax = new Vector2(0, 0.5f);
                        break;
                    }
                case (AnchorPresets.MiddleCenter):
                    {
                        source.anchorMin = new Vector2(0.5f, 0.5f);
                        source.anchorMax = new Vector2(0.5f, 0.5f);
                        break;
                    }
                case (AnchorPresets.MiddleRight):
                    {
                        source.anchorMin = new Vector2(1, 0.5f);
                        source.anchorMax = new Vector2(1, 0.5f);
                        break;
                    }

                case (AnchorPresets.BottomLeft):
                    {
                        source.anchorMin = new Vector2(0, 0);
                        source.anchorMax = new Vector2(0, 0);
                        break;
                    }
                case (AnchorPresets.BottonCenter):
                    {
                        source.anchorMin = new Vector2(0.5f, 0);
                        source.anchorMax = new Vector2(0.5f, 0);
                        break;
                    }
                case (AnchorPresets.BottomRight):
                    {
                        source.anchorMin = new Vector2(1, 0);
                        source.anchorMax = new Vector2(1, 0);
                        break;
                    }

                case (AnchorPresets.HorStretchTop):
                    {
                        source.anchorMin = new Vector2(0, 1);
                        source.anchorMax = new Vector2(1, 1);
                        break;
                    }
                case (AnchorPresets.HorStretchMiddle):
                    {
                        source.anchorMin = new Vector2(0, 0.5f);
                        source.anchorMax = new Vector2(1, 0.5f);
                        break;
                    }
                case (AnchorPresets.HorStretchBottom):
                    {
                        source.anchorMin = new Vector2(0, 0);
                        source.anchorMax = new Vector2(1, 0);
                        break;
                    }

                case (AnchorPresets.VertStretchLeft):
                    {
                        source.anchorMin = new Vector2(0, 0);
                        source.anchorMax = new Vector2(0, 1);
                        break;
                    }
                case (AnchorPresets.VertStretchCenter):
                    {
                        source.anchorMin = new Vector2(0.5f, 0);
                        source.anchorMax = new Vector2(0.5f, 1);
                        break;
                    }
                case (AnchorPresets.VertStretchRight):
                    {
                        source.anchorMin = new Vector2(1, 0);
                        source.anchorMax = new Vector2(1, 1);
                        break;
                    }

                case (AnchorPresets.StretchAll):
                    {
                        source.anchorMin = new Vector2(0, 0);
                        source.anchorMax = new Vector2(1, 1);
                        source.sizeDelta = Vector2.zero;
                        break;
                    }
            }
        }

        public static void SetPivot(this RectTransform source, PivotPresets preset)
        {

            switch (preset)
            {
                case (PivotPresets.TopLeft):
                    {
                        source.pivot = new Vector2(0, 1);
                        break;
                    }
                case (PivotPresets.TopCenter):
                    {
                        source.pivot = new Vector2(0.5f, 1);
                        break;
                    }
                case (PivotPresets.TopRight):
                    {
                        source.pivot = new Vector2(1, 1);
                        break;
                    }

                case (PivotPresets.MiddleLeft):
                    {
                        source.pivot = new Vector2(0, 0.5f);
                        break;
                    }
                case (PivotPresets.MiddleCenter):
                    {
                        source.pivot = new Vector2(0.5f, 0.5f);
                        break;
                    }
                case (PivotPresets.MiddleRight):
                    {
                        source.pivot = new Vector2(1, 0.5f);
                        break;
                    }

                case (PivotPresets.BottomLeft):
                    {
                        source.pivot = new Vector2(0, 0);
                        break;
                    }
                case (PivotPresets.BottomCenter):
                    {
                        source.pivot = new Vector2(0.5f, 0);
                        break;
                    }
                case (PivotPresets.BottomRight):
                    {
                        source.pivot = new Vector2(1, 0);
                        break;
                    }
            }
        }

        public static CanvasGroup CreateBlackMask(Transform parent, float alpha = 0, string name = null)
        {
            GameObject maskGo = new GameObject("Black Mask");
            RectTransform rectTransform = maskGo.AddComponent<RectTransform>();
            rectTransform.SetParentEx(parent);
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            Image image = maskGo.AddComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = false;
            CanvasGroup canvasGroup = maskGo.AddComponent<CanvasGroup>();
            canvasGroup.alpha = alpha;
            if(name != null)
                canvasGroup.name = name;
            return canvasGroup;
        }

        public static Canvas CreateLayerCanvas(UILayer layer, bool is3D, Transform parent, Camera camera, float width, float height)
        {
            GameObject canvasGo = new GameObject(layer.ToString());
            RectTransform rectTransform = canvasGo.AddComponent<RectTransform>();
            rectTransform.SetParentEx(parent);
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            canvasGo.layer = is3D ? Layer.Default : Layer.UI;
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = is3D ? RenderMode.WorldSpace : RenderMode.ScreenSpaceCamera;
            canvas.overrideSorting = true;
            canvas.sortingOrder = (int)layer;
            canvas.worldCamera = camera;
            canvas.pixelPerfect = false;
            CanvasScaler canvasScaler = canvasGo.AddComponent<CanvasScaler>();
            ApplyScaler(canvasScaler, width, height);

            canvasGo.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        /// <summary>
        /// 统一缩放配置入口：UI 框架建的所有 Canvas（层 Canvas / 编辑器工具建的 Canvas）走同一套，
        /// 避免同一界面两条构建路径尺寸对不上。
        /// Shrink 与 UIManager 的黑边逻辑配套：canvas 恒 ≥ 设计区域，多余部分是边缘出血；
        /// Match 模式下 canvas 可能小于设计区域，黑边判定（AutoBlack）会失真，不要换。
        /// 注：Shrink/Expand 模式下 matchWidthOrHeight 不参与计算，无需设置。
        /// </summary>
        public static void ApplyScaler(CanvasScaler scaler, float width, float height)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(width, height);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Shrink;
        }

        /// <summary>
        /// 为方便统一处理点击音效等
        /// </summary>
        public static void AddClick(this Button button, UnityAction callback)
        {
            button.onClick.AddListener(callback);
        }
    }
}
