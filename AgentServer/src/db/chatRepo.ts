/**
 * 会话流水（chat_messages）：UGUI 历史回放的唯一来源（独立于 session_entries）。
 * 逐句搬 storage.py 的 SQL。
 */
import { getDb } from "./sqlite.js";

export type ChatRole = "user" | "assistant";

export function appendChat(playerId: number, role: ChatRole, content: string): void {
  getDb()
    .prepare("INSERT INTO chat_messages(player_id, role, content, created_ms) VALUES (?,?,?,?)")
    .run(playerId, role, content, Date.now());
}

export interface ChatHistoryPage {
  items: { role: 0 | 1; content: string; ts: number }[];
  has_more: boolean;
}

/**
 * 最近 N 条会话记录，按时间升序（旧→新）返回。
 * before_ms>0 时取「早于该时间」的更早一页（3105 上拉分页）；LIMIT+1 探测 has_more。
 * role 映射为协议口径的数字：0=玩家 / 1=阿福。
 */
export function getRecentChat(playerId: number, limit = 20, beforeMs = 0): ChatHistoryPage {
  const safeLimit = Math.max(1, Math.min(Math.trunc(limit) || 20, 200));
  const db = getDb();
  const rows = (
    beforeMs > 0
      ? db
          .prepare(
            "SELECT role, content, created_ms FROM chat_messages " +
              "WHERE player_id=? AND created_ms<? ORDER BY id DESC LIMIT ?",
          )
          .all(playerId, beforeMs, safeLimit + 1)
      : db
          .prepare(
            "SELECT role, content, created_ms FROM chat_messages " +
              "WHERE player_id=? ORDER BY id DESC LIMIT ?",
          )
          .all(playerId, safeLimit + 1)
  ) as unknown as { role: string; content: string; created_ms: number }[];

  const hasMore = rows.length > safeLimit;
  const page = rows.slice(0, safeLimit).reverse();
  return {
    items: page.map((row) => ({
      role: row.role === "assistant" ? 1 : 0,
      content: row.content,
      ts: row.created_ms,
    })),
    has_more: hasMore,
  };
}
