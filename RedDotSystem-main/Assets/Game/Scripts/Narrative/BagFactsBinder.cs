using System.Collections.Generic;
using System.Text;
using Miemie.DialogSystem;
using UnityEngine;

/// <summary>
/// 背包→对话事实绑定器（集成层，照 BagManager 自举模式，场景零接线）
/// 订阅 BagManager.BagUpdated（镜像刷新：列表到达/操作成功/登录清缓存都触发），
/// 把每个 itemId 的持有总量写进全局事实 item_{itemId}_count。
/// 对话图里声明同名 Int 变量后，条件直接用现有 IntGreater/IntEquals 判断"持有道具"。
/// 注意：读取的是服务端权威的客户端镜像，开对话前背包未拉取则全为 0。
/// </summary>
public static class BagFactsBinder
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        // BagManager 自举在 BeforeSceneLoad，AfterSceneLoad 时实例必已存在
        if (BagManager.Instance == null)
        {
            Debug.LogWarning("[BagFactsBinder] BagManager 不存在，跳过绑定");
            return;
        }
        BagManager.Instance.BagUpdated += Recompute;
        Recompute(); // 初始同步：背包未拉取时全 0
    }

    static readonly Dictionary<int, int> lastCounts = new();

    static void Recompute()
    {
        var bag = BagManager.Instance;
        if (bag == null) return;

        // 聚合每个 itemId 的持有总量
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < bag.Capacity; i++)
        {
            var slot = bag.GetSlot(i);
            if (slot == null || slot.Count <= 0 || slot.ItemId <= 0) continue;
            counts.TryGetValue(slot.ItemId, out int c);
            counts[slot.ItemId] = c + slot.Count;
        }

        // 上轮有、本轮无的写 0（丢弃/使用耗尽后不能残留旧数量）
        var sb = new StringBuilder();
        foreach (var pair in lastCounts)
        {
            if (counts.ContainsKey(pair.Key)) continue;
            DialogueFacts.SetIntFact(Key(pair.Key), 0);
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"{Key(pair.Key)}=0");
        }

        foreach (var pair in counts)
        {
            // 无变化的跳过，避免每次背包操作都刷日志
            if (lastCounts.TryGetValue(pair.Key, out int old) && old == pair.Value) continue;
            DialogueFacts.SetIntFact(Key(pair.Key), pair.Value);
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"{Key(pair.Key)}={pair.Value}");
        }

        if (sb.Length > 0)
            Debug.Log($"[BagFactsBinder] 事实表变化: {sb} | 当前表: {DialogueFacts.Describe()}");

        lastCounts.Clear();
        foreach (var pair in counts) lastCounts[pair.Key] = pair.Value;
    }

    static string Key(int itemId) => $"item_{itemId}_count";
}
