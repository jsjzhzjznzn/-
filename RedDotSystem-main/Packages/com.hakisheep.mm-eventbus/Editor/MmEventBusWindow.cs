#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MiMieEventBus.Editor
{
    /// <summary>
    /// 事件总线调试窗口：实时列出各总线的注册 Key 与订阅数，排查订阅泄漏与事件链路
    /// 包内不感知任何具体总线——项目侧用 Register(displayName, 获取器) 注册，支持多总线
    /// </summary>
    public class MmEventBusWindow : EditorWindow
    {
        struct BusEntry
        {
            public string Name;
            public Func<EventBusCore> Getter;
        }

        static readonly List<BusEntry> busEntries = new();

        [MenuItem("Tools/MmEventBusWindow")]
        static void Open()
        {
            GetWindow<MmEventBusWindow>("Mm事件总线调试");
        }

        /// <summary>
        /// 项目侧注册总线（同名重复注册忽略；用获取器而非实例，Play 重启后自动拿到新总线）
        /// </summary>
        public static void Register(string displayName, Func<EventBusCore> busGetter)
        {
            foreach (var entry in busEntries)
            {
                if (entry.Name == displayName)
                    return;
            }
            busEntries.Add(new BusEntry { Name = displayName, Getter = busGetter });
        }

        Vector2 scroll;
        bool autoRefresh = true;
        double nextRepaint;

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Play 模式下实时刷新。退出 Play（Domain Reload 开启）静态总线即清空——若窗口仍见订阅残留，说明订阅方没有成对退订（泄漏），或项目关闭了 Reload。最近触发 ▶ 标记来自 EventBusTrace 面包屑。",
                MessageType.None);

            if (busEntries.Count == 0)
            {
                EditorGUILayout.HelpBox("未注册任何总线。项目侧调用 MmEventBusWindow.Register(\"显示名\", () => 总线实例)", MessageType.Info);
                return;
            }

            autoRefresh = EditorGUILayout.ToggleLeft("自动刷新（0.5s）", autoRefresh);
            if (autoRefresh && EditorApplication.timeSinceStartup >= nextRepaint)
            {
                nextRepaint = EditorApplication.timeSinceStartup + 0.5;
                Repaint();
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var entry in busEntries)
            {
                var bus = entry.Getter?.Invoke();
                if (bus == null)
                {
                    EditorGUILayout.LabelField($"◆ {entry.Name}", "（实例未创建）", EditorStyles.boldLabel);
                    continue;
                }

                var keyNames = bus.GetRegisteredKeyNameList();
                int total = 0;
                foreach (var key in keyNames)
                    total += bus.GetListenerCountByName(key);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    $"◆ {entry.Name}    事件槽 {bus.GetEventCount()} · Key {keyNames.Count} · 订阅总数 {total}",
                    EditorStyles.boldLabel);

                if (keyNames.Count == 0)
                {
                    EditorGUILayout.LabelField("    （无注册事件）", EditorStyles.miniLabel);
                    continue;
                }

                foreach (var key in keyNames)
                {
                    int count = bus.GetListenerCountByName(key);
                    bool recent = EventBusTrace.LastKeyName == key;
                    var style = count == 0 ? EditorStyles.miniLabel : EditorStyles.label;
                    EditorGUILayout.LabelField($"{(recent ? "▶" : "  ")} {key}", $"订阅 {count}", style);
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
#endif
