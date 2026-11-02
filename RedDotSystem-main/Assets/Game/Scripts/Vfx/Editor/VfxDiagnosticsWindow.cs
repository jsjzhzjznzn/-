using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// VFX 诊断窗口（编辑器 Tools 菜单，照 MmEventBusWindow 先例）
/// 展示：注册表条目 / 池状态（租借·空闲·累计）/ 活跃实例（key·已播时长·剩余·跟随目标）/ 最近播放记录
/// 用途：联调时确认"特效播了没有、回收了没有、池有没有泄漏"；只读为主，仅留 StopAll 与试播两个操作钮
/// 注意：Domain Reload 关闭时静态实例跨会话残留的判空都在 VfxManager 里做了（Unity fake-null），本窗口无需处理
/// </summary>
public class VfxDiagnosticsWindow : EditorWindow
{
    private bool autoRefresh = true;
    private double nextRepaint;
    private int selectedPlayIndex;
    private readonly List<VfxManager.VfxSnapshot> activeSnapshot = new();
    private readonly List<VfxManager.VfxPoolStats> poolStats = new();

    [MenuItem("Tools/VFX 诊断窗口")]
    public static void Open()
    {
        GetWindow<VfxDiagnosticsWindow>("VFX 诊断");
    }

    private void OnGUI()
    {
        VfxManager manager = VfxManager.Instance;
        if (manager == null || !Application.isPlaying)
        {
            EditorGUILayout.HelpBox("需要进入 Play 模式（VfxManager 运行时自举，无需手动挂载）。", MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField("特效系统诊断", EditorStyles.boldLabel);

        // 注册表状态
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"注册表：{(manager.IsReady ? "已加载" : "<b>缺失 Assets/Game/Resources/VfxCatalog.asset</b>")}");
            EditorGUILayout.LabelField($"活跃实例：{manager.ActiveTotal}");
        }

        EditorGUILayout.Space(4f);

        // 池状态
        EditorGUILayout.LabelField("池状态（key · 预热 · 租借中 · 空闲 · 累计创建 · 时长覆盖 · unscaled）", EditorStyles.miniBoldLabel);
        manager.CollectPoolStats(poolStats);
        if (poolStats.Count == 0)
        {
            EditorGUILayout.HelpBox("没有池记录——注册表还没加载或条目全无效。", MessageType.Warning);
        }
        foreach (VfxManager.VfxPoolStats stats in poolStats)
        {
            EditorGUILayout.LabelField(
                $"  {stats.Key}   预热{stats.Prewarm} · 租借{stats.Rented} · 空闲{stats.Pooled} · 累计{stats.TotalCreated} · 覆盖{(stats.DurationOverride > 0f ? stats.DurationOverride + "s" : "自然时长")} · {(stats.Unscaled ? "unscaled" : "scaled")}");
        }

        EditorGUILayout.Space(4f);

        // 活跃实例
        EditorGUILayout.LabelField("活跃实例", EditorStyles.miniBoldLabel);
        manager.CollectActive(activeSnapshot);
        if (activeSnapshot.Count == 0)
        {
            EditorGUILayout.LabelField("  （空）", EditorStyles.miniLabel);
        }
        foreach (VfxManager.VfxSnapshot snapshot in activeSnapshot)
        {
            string remaining = float.IsPositiveInfinity(snapshot.RemainingSeconds) ? "∞" : snapshot.RemainingSeconds.ToString("F2") + "s";
            EditorGUILayout.LabelField(
                $"  #{snapshot.Id} {snapshot.Key}   已播{snapshot.ElapsedSeconds:F2}s · 剩余{remaining}{(snapshot.FollowName != null ? " · 跟随" + snapshot.FollowName : "")}");
        }

        EditorGUILayout.Space(4f);

        // 最近播放
        EditorGUILayout.LabelField("最近播放（最多16条）", EditorStyles.miniBoldLabel);
        IReadOnlyList<VfxManager.PlayRecord> recent = manager.RecentPlays;
        for (int i = recent.Count - 1; i >= 0; i--)   // 新的在上
        {
            VfxManager.PlayRecord record = recent[i];
            // 记录存的是游戏 Time.unscaledTime（Play 内有效），别拿编辑器 timeSinceStartup 来减——两个时钟
            float secondsAgo = Mathf.Max(0f, Time.unscaledTime - record.UnscaledTime);
            EditorGUILayout.LabelField(
                $"  {record.Key}   {secondsAgo:F1}s前 · ({record.Position.x:F1}, {record.Position.y:F1}, {record.Position.z:F1}){(record.Attached ? " · 附着" : "")}");
        }

        EditorGUILayout.Space(6f);

        // 操作
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("全部停止（StopAll）", GUILayout.Width(160f)))
            {
                manager.StopAll();
            }
            autoRefresh = EditorGUILayout.ToggleLeft("0.5s 自动刷新", autoRefresh);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            string[] keys = new string[poolStats.Count];
            for (int i = 0; i < poolStats.Count; i++) keys[i] = poolStats[i].Key;
            if (keys.Length == 0)
            {
                GUI.enabled = false;
            }
            selectedPlayIndex = EditorGUILayout.Popup("试播", Mathf.Clamp(selectedPlayIndex, 0, Mathf.Max(0, keys.Length - 1)), keys);
            if (GUILayout.Button("在场景原点播放", GUILayout.Width(140f)))
            {
                if (keys.Length > 0)
                {
                    manager.Play(keys[selectedPlayIndex], Vector3.zero + Vector3.up, Quaternion.identity);
                }
            }
            GUI.enabled = true;
        }

        // 自动刷新（EditorApplication.timeNow 在 Tuanjie 不存在，用 timeSinceStartup）
        if (autoRefresh && EditorApplication.timeSinceStartup > nextRepaint)
        {
            nextRepaint = EditorApplication.timeSinceStartup + 0.5;
            Repaint();
        }
    }
}
