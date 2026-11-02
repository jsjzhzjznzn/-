using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkierFramework
{
    public enum UIAdaptType
    {
        All,
        LeftOrTop,
        RightOrBottom,
    }

    public class UIAdapter : MonoBehaviour
    {
        public UIAdaptType uIAdaptType = UIAdaptType.All;

        private float cd;

        private void Update()
        {
            // 为避免旋转屏幕，华为分屏机等导致分辨率变化，且安全区变化的问题，需要持续检测
            if (Time.time > cd)
            {
                InitAdapter();
                cd = Time.time + 1;
            }
        }

        private void InitAdapter()
        {
            var rectTransform = transform as RectTransform;
            if (rectTransform == null) return;

            var safeArea = Screen.safeArea;
            if (UIManager.Instance != null)
            {
                safeArea = UIManager.Instance.GetSafeArea();
            }
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            var orientation = Screen.orientation;

            // PC/编辑器（以及部分 Android 自动旋转模式）下 Screen.orientation 恒为 Unknown：
            // 原实现在这里两个分支都不进、却仍把 sizeDelta 清零，固定尺寸的贴边元素会直接缩没。
            // 现在按真实屏幕宽高比兜底，保证任何平台都走进一个适配分支。
            bool isLandscape = orientation switch
            {
                ScreenOrientation.LandscapeLeft or ScreenOrientation.LandscapeRight => true,
                ScreenOrientation.Portrait or ScreenOrientation.PortraitUpsideDown => false,
                _ => screenWidth >= screenHeight
            };
            // Unknown 时按常规朝向处理：横屏视作 LandscapeLeft、竖屏视作 Portrait
            bool isMirrored = orientation switch
            {
                ScreenOrientation.LandscapeRight or ScreenOrientation.PortraitUpsideDown => true,
                _ => false
            };

            if (isLandscape)
            {
                switch (uIAdaptType)
                {
                    case UIAdaptType.All:
                        rectTransform.anchorMin = new Vector2(safeArea.xMin / screenWidth, 0);
                        rectTransform.anchorMax = new Vector2(safeArea.xMax / screenWidth, 1);
                        break;
                    case UIAdaptType.LeftOrTop:
                        if (!isMirrored)
                        {
                            rectTransform.anchorMin = new Vector2(safeArea.xMin / screenWidth, 0);
                            rectTransform.anchorMax = new Vector2(1, 1);
                        }
                        else
                        {
                            rectTransform.anchorMin = new Vector2(0, 0);
                            rectTransform.anchorMax = new Vector2(safeArea.xMax / screenWidth, 1);
                        }
                        break;
                    case UIAdaptType.RightOrBottom:
                        if (!isMirrored)
                        {
                            rectTransform.anchorMin = new Vector2(0, 0);
                            rectTransform.anchorMax = new Vector2(safeArea.xMax / screenWidth, 1);
                        }
                        else
                        {
                            rectTransform.anchorMin = new Vector2(safeArea.xMin / screenWidth, 0);
                            rectTransform.anchorMax = new Vector2(1, 1);
                        }
                        break;
                }
            }
            else
            {
                switch (uIAdaptType)
                {
                    case UIAdaptType.All:
                        rectTransform.anchorMin = new Vector2(0 , safeArea.yMin / screenHeight);
                        rectTransform.anchorMax = new Vector2(1 , safeArea.yMax / screenHeight);
                        break;
                    case UIAdaptType.LeftOrTop:
                        if (!isMirrored)
                        {
                            rectTransform.anchorMin = new Vector2(0, 0);
                            rectTransform.anchorMax = new Vector2(1, safeArea.yMax / screenHeight);
                        }
                        else
                        {
                            rectTransform.anchorMin = new Vector2(0, safeArea.yMin / screenHeight);
                            rectTransform.anchorMax = new Vector2(1, 1);
                        }
                        break;
                    case UIAdaptType.RightOrBottom:
                        if (!isMirrored)
                        {
                            rectTransform.anchorMin = new Vector2(0, safeArea.yMin / screenHeight);
                            rectTransform.anchorMax = new Vector2(1, 1);
                        }
                        else
                        {
                            rectTransform.anchorMin = new Vector2(0, 0);
                            rectTransform.anchorMax = new Vector2(1, safeArea.yMax / screenHeight);
                        }
                        break;
                }
            }

            // 锚点已按安全区更新，才清 sizeDelta 让拉伸完全贴合（原来无条件清是 PC 上元素消失的根源）
            rectTransform.sizeDelta = Vector2.zero;
        }
    }
}
