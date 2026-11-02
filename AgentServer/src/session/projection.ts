/**
 * 会话条目 → 模型上下文（canonical projection）。
 *
 * pi 的语义：会话（转写本）是上下文的权威。这里把 SQLite 里的条目投影成
 * 「摘要块（如有）+ 保留段消息」，供 prepareRequest 每轮重建。
 * 移植参考：@earendil-works/pi-coding-agent 的 sessionEntryToContextMessages /
 * buildSessionProjection（我们只保留线性会话所需的部分）。
 */
import type { AgentMessage } from "@earendil-works/pi-agent-core";

import { isCompactionEntry, isMessageEntry, type CompactionEntry, type SessionEntry } from "./types.js";

/** 摘要块的包装文案（不向玩家暴露摘要本身，与 Python 版口径一致）。 */
export function summaryBlockText(summary: string): string {
  return (
    "【此前对话的滚动摘要（背景资料，自然接着聊即可；" +
    "不要向玩家提及摘要本身，也不要复述摘要原文）】" + summary
  );
}

/** 最近一次压缩条目（无则 null）。 */
export function latestCompaction(entries: readonly SessionEntry[]): CompactionEntry | null {
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const entry = entries[i]!;
    if (isCompactionEntry(entry)) return entry;
  }
  return null;
}

/**
 * 投影成消息数组：
 *  - 无压缩条目 → 全部消息条目按序返回；
 *  - 有压缩条目 → [摘要 system 块] + 从 firstKeptEntryId（含）起的消息条目。
 */
export function projectConversation(entries: readonly SessionEntry[]): AgentMessage[] {
  const compaction = latestCompaction(entries);
  if (!compaction) {
    return entries.filter(isMessageEntry).map((entry) => entry.message);
  }
  const startIndex = entries.findIndex((entry) => entry.id === compaction.firstKeptEntryId);
  const kept = (startIndex >= 0 ? entries.slice(startIndex) : []).filter(isMessageEntry);
  const keptMessages = kept.map((entry) => entry.message);
  if (!compaction.summary.trim()) return keptMessages;
  return [{ role: "system", content: summaryBlockText(compaction.summary), timestamp: Date.now() }, ...keptMessages];
}

/** 已被摘要取代的条目数（debug 水位：对应 Python 的 summarized_count）。 */
export function summarizedCount(entries: readonly SessionEntry[]): number {
  const compaction = latestCompaction(entries);
  if (!compaction) return 0;
  const startIndex = entries.findIndex((entry) => entry.id === compaction.firstKeptEntryId);
  return startIndex < 0 ? 0 : startIndex;
}
