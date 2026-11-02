using System.Collections.Generic;
using Common.Network;

/// <summary>
/// 活动中心侧栏的单个活动条目（ActivityBridge.GetActivityInfoAt 返回给 Lua 的纯数据对象）
/// 与 ctx 的单活动字段同源，但含 type（决定挂哪张内容页）——多活动并存后侧栏按它分流
/// </summary>
public class ActivityEntryData
{
    public int activityId;
    public int type;              // 1=武器唤取 2=每日签到（其余=未支持，热更层显示占位页）
    public int bannerId;          // 唤取类=卡池ID；签到类=0
    public string title = string.Empty;
    public string subtitle = string.Empty;
    public string contentVersion = string.Empty;
    public long startAtMs;
    public long endAtMs;
}

/// <summary>
/// 交给 Lua 的签到结果（纯数据对象；xLua 生成代码直读字段，无反射开销）
/// days 数组与 responses 顺序一致且 **0 基**：第 i 天 = days[i] / itemIds[i] / ...
/// </summary>
public class SignInResultData
{
    public bool success;
    public string message = string.Empty;

    /// <summary>签到活动ID / 本次签到的天次（列表请求与失败签到为 0）</summary>
    public int signId;
    public int day;

    /// <summary>总天数 / 已签天数 / 今天是否已签</summary>
    public int totalDays;
    public int signedDays;
    public bool todaySigned;
    public int count;

    public int[] days = new int[0];
    public int[] itemIds = new int[0];
    public string[] itemNames = new string[0];
    public string[] iconKeys = new string[0];
    public int[] rarities = new int[0];
    public int[] counts = new int[0];
    public bool[] claimed = new bool[0];

    /// <summary>从协议回执填充（按天次升序）</summary>
    public void Fill(IReadOnlyList<SignInDayInfo> dayInfos)
    {
        int n = dayInfos != null ? dayInfos.Count : 0;
        days = new int[n];
        itemIds = new int[n];
        itemNames = new string[n];
        iconKeys = new string[n];
        rarities = new int[n];
        counts = new int[n];
        claimed = new bool[n];
        for (int i = 0; i < n; i++)
        {
            SignInDayInfo day = dayInfos[i];
            days[i] = day.Day;
            itemIds[i] = day.ItemId;
            itemNames[i] = day.Name;
            iconKeys[i] = day.IconKey;
            rarities[i] = day.Rarity;
            counts[i] = day.Count;
            claimed[i] = day.Claimed;
        }
        count = n;
    }
}

/// <summary>
/// 交给 Lua 的唤取结果（纯数据对象；xLua 生成代码直读字段，无反射开销）
/// 数组与 results 顺序一致：第 i 抽 = itemIds[i] / rarities[i] / ...
/// </summary>
public class GachaDrawResultData
{
    public bool success;
    public string message = string.Empty;

    /// <summary>本次抽后 5★ 保底计数 / 阈值 / 卡池累计抽数 / 本次抽数</summary>
    public int pityCount;
    public int pityThreshold;
    public int totalDraws;
    public int count;

    /// <summary>下一次 5★ 是否必定 UP（大保底状态）</summary>
    public bool guaranteedUp;

    /// <summary>本次是否同一 request_id 的原结果重放（未扣媒介、未重 roll）</summary>
    public bool replayed;

    /// <summary>传输层失败（超时/断线）时为 true：可用同一 request_id 安全重试</summary>
    public bool retryable;

    public int[] itemIds = new int[0];
    public string[] itemNames = new string[0];
    public string[] iconKeys = new string[0];
    public int[] rarities = new int[0];
    public bool[] isUp = new bool[0];
    public bool[] isPity = new bool[0];
    public bool[] isNew = new bool[0];
    /// <summary>保底类型：0 无 / 1 5★计数 / 2 十连4★ / 3 大保底</summary>
    public int[] pityKinds = new int[0];

    /// <summary>从协议回执填充（顺序保持服务端 roll 顺序）</summary>
    public void Fill(IReadOnlyList<GachaResultItem> items)
    {
        int n = items != null ? items.Count : 0;
        itemIds = new int[n];
        itemNames = new string[n];
        iconKeys = new string[n];
        rarities = new int[n];
        isUp = new bool[n];
        isPity = new bool[n];
        isNew = new bool[n];
        pityKinds = new int[n];
        for (int i = 0; i < n; i++)
        {
            GachaResultItem item = items[i];
            itemIds[i] = item.ItemId;
            itemNames[i] = item.Name;
            iconKeys[i] = item.IconKey;
            rarities[i] = item.Rarity;
            isUp[i] = item.IsUp;
            isPity[i] = item.IsPity;
            isNew[i] = item.IsNew;
            pityKinds[i] = item.PityKind;
        }
    }
}

/// <summary>
/// 交给 Lua 的唤取流水结果（鸣潮"唤取详单"；倒序最近优先）
/// </summary>
public class GachaHistoryResultData
{
    public bool success;
    public string message = string.Empty;
    public int totalDraws;
    public int pityCount;
    public bool guaranteedUp;
    public int count;

    public int[] bannerIds = new int[0];
    public int[] itemIds = new int[0];
    public string[] itemNames = new string[0];
    public string[] iconKeys = new string[0];
    public int[] rarities = new int[0];
    public bool[] isPity = new bool[0];
    public int[] pityKinds = new int[0];
    public long[] createdAtMs = new long[0];

    /// <summary>从协议回执填充</summary>
    public void Fill(IReadOnlyList<GachaHistoryEntry> entries)
    {
        int n = entries != null ? entries.Count : 0;
        bannerIds = new int[n];
        itemIds = new int[n];
        itemNames = new string[n];
        iconKeys = new string[n];
        rarities = new int[n];
        isPity = new bool[n];
        pityKinds = new int[n];
        createdAtMs = new long[n];
        for (int i = 0; i < n; i++)
        {
            GachaHistoryEntry entry = entries[i];
            bannerIds[i] = entry.BannerId;
            itemIds[i] = entry.ItemId;
            itemNames[i] = entry.Name;
            iconKeys[i] = entry.IconKey;
            rarities[i] = entry.Rarity;
            isPity[i] = entry.IsPity;
            pityKinds[i] = entry.PityKind;
            createdAtMs[i] = entry.CreatedAtMs;
        }
    }
}
