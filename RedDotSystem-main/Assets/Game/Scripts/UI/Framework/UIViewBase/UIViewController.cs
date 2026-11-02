using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace SkierFramework
{
    public class UIViewController
    {
        // 配置
        /// <summary>
        /// UI 标识（字符串）：C# UI 为 UIType 枚举名，纯 Lua UI 为 json 里的任意字符串
        /// </summary>
        public string uiId;
        public string uiPath;
        public Type uiViewType;
        public UILayerLogic uiLayer;
        public bool isWindow;
        /// <summary>
        /// 纯 Lua UI（LuaOnlyView）的 Lua 模块 require 名，加载实例前注入
        /// </summary>
        public string luaModuleName;

        public UIView uiView;
        public UIViewAnim uiViewAnim;
        public bool isLoading = false;
        public bool isOpen = false;
        public bool isPause = false;
        public int order;
        /// <summary>
        /// 在我上面的界面(非窗口界面)的数量
        /// </summary>
        public int topViewNum;

        public async UniTask Load(object userData = null)
        {
            isLoading = true;

            var loadWatch = UIManager.LogOpenTiming ? System.Diagnostics.Stopwatch.StartNew() : null;

            if (isOpen)
            {
                order = uiLayer.PopOrder(this);
            }
            GameObject go = await ResourceManager.Instance.InstantiateAsync(uiPath);
            long msInstantiate = loadWatch != null ? loadWatch.ElapsedMilliseconds : 0;
            if (go == null)
            {
                // 资源加载失败，避免后续空引用
                isLoading = false;
                Release();
                // 清理 UIManager 的打开标记，避免 IsOpen() 误报
                UIManager.Instance.OnUIOpenFailed(uiId);
                return;
            }

            if (!isLoading)
            {
                // 加载期间面板已关闭：销毁实例 + 卸载底层 AssetHandle，防止句柄泄漏
                // （必须销毁而不是回池：随后就会卸载预制体资源，池中克隆会留下已销毁的材质/字体引用）
                ResourceManager.Instance.Recycle(go, true);
                ResourceManager.Instance.UnLoadAsset(uiPath);
                Release();
                return;
            }

            isLoading = false;
            uiView = (UIView)go.GetOrAddComponent(uiViewType);
            uiViewAnim = go.GetComponent<UIViewAnim>();
            if (uiView is LuaOnlyView luaOnlyView && !string.IsNullOrEmpty(luaModuleName))
            {
                // 通用壳实例共享同一类型，模块名必须按配置注入后再走 OnInit（内部按模块名 require）
                luaOnlyView.luaModuleName = luaModuleName;
            }
            uiView.transform.SetParentEx(uiLayer.canvas.transform);
            RectTransform rectTransform = uiView.transform as RectTransform;

            switch (UIManager.Instance.GetUIBlackType())
            {
                case UIBlackType.None:
                    // 全适配
                    rectTransform.SetAnchor(AnchorPresets.StretchAll);
                    rectTransform.anchoredPosition = Vector2.zero;
                    rectTransform.sizeDelta = Vector2.zero;
                    break;
                case UIBlackType.Height:
                    // 保持高度填满，两边留黑边
                    rectTransform.SetAnchor(AnchorPresets.VertStretchCenter);
                    rectTransform.anchoredPosition = Vector2.zero;
                    rectTransform.sizeDelta = new Vector2(UIManager.Instance.width, 0);
                    rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, UIManager.Instance.width);
                    break;
                case UIBlackType.Width:
                    // 保持宽度填满，上下留黑边
                    rectTransform.SetAnchor(AnchorPresets.HorStretchMiddle);
                    rectTransform.anchoredPosition = Vector2.zero;
                    rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, UIManager.Instance.height);
                    rectTransform.sizeDelta = new Vector2(0, UIManager.Instance.height);
                    break;
            }

            uiView.OnInit(go.GetComponent<UIControlData>(), this);
            uiView.transform.SetAsLastSibling();
            long msBind = loadWatch != null ? loadWatch.ElapsedMilliseconds : 0;

            if (isOpen)
            {
                // 预加载与打开撞车：Load 开始时 isOpen=false，没申请 order，加载完才知道要打开。
                // 这里补一次，否则 order 恒为 0 → 同层排序与非窗口面板"隐藏下层"全部失效。
                if (order == 0) order = uiLayer.PopOrder(this);
                await Open(userData, isFirstOpen: true);
            }
            else
            {
                await Close();
            }

            if (loadWatch != null)
            {
                Debug.Log($"[UITiming] load {uiId}: instantiate={msInstantiate}ms bind={msBind - msInstantiate}ms open={loadWatch.ElapsedMilliseconds - msBind}ms total={loadWatch.ElapsedMilliseconds}ms");
            }
        }

        public async UniTask Open(object userData = null, bool isFirstOpen = false)
        {
            isOpen = true;
            if (isLoading) return;

            if (uiView == null)
            {
                await Load(userData);
            }
            else
            {
                long msBefore = UIManager.LogOpenTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

                // 如果之前打开过，再次掉OpenUI，需要把层级给他拉上来
                // 设计上关于跳转相关的，不应该通过底层的遮挡关系来实现，只允许一个UI同时只能被开启1次。
                if (!isFirstOpen && order > 0)
                {
                    TrueClose();
                }

                TrueOpen(userData, isFirstOpen);
                if (uiViewAnim != null)
                {
                    await uiViewAnim.OpenAsync();
                }

                if (UIManager.LogOpenTiming)
                {
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - msBefore) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Debug.Log($"[UITiming] reopen {uiId}: {ms:F1}ms");
                }
            }
        }

        public async UniTask Close(bool isJump = false)
        {
            isOpen = false;
            if (!isJump)
            {
                UIManager.Instance.OnUIClose(uiId);
            }
            if (isLoading) return;

            if (uiView != null)
            {
                if (uiViewAnim != null)
                {
                    await uiViewAnim.CloseAsync();
                }
                TrueClose();
            }
        }

        public void Release(bool destroyInstance = false)
        {
            if (uiView != null)
            {
                if (isOpen)
                    TrueClose();
                uiView.OnRelease();
                ResourceManager.Instance.Recycle(uiView.gameObject, destroyInstance);
            }
            uiView = null;
            uiViewAnim = null;
            isLoading = false;
            isOpen = false;
            order = 0;
        }

        /// <summary>
        /// 彻底释放：销毁实例 + 卸载底层 AssetHandle（句柄泄漏兜底）
        /// 调用时机：UIManager.ReleaseAll / 非常驻面板 CloseAll 后统一清理
        /// 注意：从未加载过的视图不卸载资源——ReleaseAll 会遍历注册表里的所有 UI，
        /// 对没打开过的面板调 UnLoadAsset 会打一堆"卸载未加载资源"错误（Error Pause 下还会把 Play 暂停）。
        /// </summary>
        public void FullRelease()
        {
            bool hadInstance = uiView != null;
            // 彻底释放必须真正销毁实例：默认的 Release() 只是把克隆回收到对象池，紧接着 UnLoadAsset 会卸载
            // 预制体资源，池中残留克隆仍持有该资源下的字体/材质引用（TMP 子网格材质会变成已销毁，再次启用
            // 就抛 MissingReferenceException）。先销毁再卸载，资源所有权才一致。
            Release(destroyInstance: true);
            if (hadInstance) ResourceManager.Instance.UnLoadAsset(uiPath);
        }

        private void TrueOpen(object userData = null, bool isFirstOpen = false)
        {
            uiLayer.OpenUI(this, isFirstOpen);
            SetVisible(true);
            // 刷新一下显示
            AddTopViewNum(0);
            uiView.OnOpen(userData);
            uiView.OnResume();
        }

        private void TrueClose()
        {
            uiLayer.CloseUI(this);
            // 刷新一下显示
            AddTopViewNum(-100000);
            SetVisible(false);
            uiView.OnPause();
            uiView.OnClose();
        }

        public void SetVisible(bool visible)
        {
            if (uiView != null)
            {
                uiView.gameObject.SetActive(visible);
            }
        }

        public void AddTopViewNum(int num)
        {
            topViewNum += num;
            topViewNum = Mathf.Max(0, topViewNum);
            SetVisible(topViewNum <= 0);
        }
    }

}
