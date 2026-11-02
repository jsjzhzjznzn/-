using System;
using System.Collections.Generic;

using UnityEngine;

using XLua;

/// <summary>
/// 项目自己的 xLua 生成配置（Assets/ThirdParty/XLua/Editor/ExampleConfig.cs 是官方自带默认配置，保留不动）
///
/// 这里声明"参与生成适配代码"的类型，改完必须重新执行菜单 XLua/Generate Code：
///   LuaCallCSharp —— Lua 侧要访问的 C# 类型。不配也能靠反射访问，但性能差，
///                    且 IL2CPP/AOT 下对值类型、泛型、委托的支持不可靠，正式项目必须配置；
///   CSharpCallLua —— C# 侧要接收的 Lua 函数（委托/接口），这类**必须**生成代码，
///                    否则 IL2CPP 下把 Lua function 转成委托会直接崩。
///
/// 以后要给 Lua 开放项目自己的类型（BagManager、ChatManager 等），往下面的列表里加 typeof 即可。
/// </summary>
public static class XLuaGenConfig
{
    /// <summary>Lua 侧可直接访问（走生成代码，不走反射）的 C# 类型</summary>
    [LuaCallCSharp]
    public static List<Type> LuaCallCSharp = new List<Type>()
    {
        typeof(UnityEngine.Object),
        typeof(GameObject),
        typeof(Component),
        typeof(Transform),
        typeof(Debug),
        typeof(Time),
        typeof(Application),
        typeof(Resources),
        typeof(TextAsset),
        typeof(Mathf),
        typeof(UnityEngine.Random),
        typeof(Vector3),
        typeof(Quaternion),

        // ── 活动热更层（Lua 构建/驱动 UI 需要；加完必须重跑 XLua/Generate Code）──
        typeof(RectTransform),                       // UI 定位（锚点/尺寸/位置）
        typeof(Canvas),                              // 画布（Canvas 组件读写）
        typeof(UnityEngine.UI.Button),              // 唤取按钮（onClick/AddListener）
        typeof(UnityEngine.UI.Image),                // 图片（sprite/color/fillAmount）
        typeof(UnityEngine.UI.CanvasScaler),
        typeof(UnityEngine.UI.ScrollRect),           // 唤取详单滚动列表
        typeof(UnityEngine.UI.RectMask2D),           // 滚动视口裁剪
        typeof(CanvasGroup),                         // 淡入淡出（alpha/interactable）
        typeof(TMPro.TMP_Text),                     // TMP 文本基类（SetSize/文本）
        typeof(TMPro.TextMeshProUGUI),              // TMP UGUI 文本（活动面板文案）
        typeof(TMPro.TMP_FontAsset),                 // 换字体（程序化 TMP 用）
        typeof(Color),                               // 配色
        typeof(Sprite),                              // 热更图片引用
        typeof(UnityEngine.Events.UnityEvent),       // Button.onClick 的事件对象
        typeof(ActivityBridge),                      // 活动桥（Lua 的唯一固定 API 面）
        typeof(ActivityUi),                          // uGUI 原语工厂（贴图/元素/布局）
        typeof(GachaDrawResultData),                 // 唤取结果交付对象
        typeof(GachaHistoryResultData),              // 唤取流水交付对象
        typeof(SignInResultData),                    // 签到状态/结果交付对象
        typeof(ActivityEntryData),                   // 活动中心侧栏的活动条目
    };

    /// <summary>C# 侧接收的 Lua 函数类型（委托）</summary>
    [CSharpCallLua]
    public static List<Type> CSharpCallLua = new List<Type>()
    {
        typeof(Action),
        typeof(Action<int>),
        typeof(Action<string>),
        typeof(Func<int, int>),
        typeof(Func<int, string>),
        typeof(UnityEngine.Events.UnityAction),     // Button.onClick.AddListener(lua function)
    };
}
