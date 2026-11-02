/**
 * L3 长期记忆（longterm_memory）：跨对话事实（"记住我叫铁蛋"）。
 * 逐句搬 storage.py 的 SQL；条数上限内全量注入（无向量检索）。
 */
import { MAX_LTM_ENTRIES } from "../core/config.js";
import { getDb } from "./sqlite.js";

/** 该玩家的长期记忆 [key, content]，按更新时间倒序。 */
export function getLongterm(playerId: number): [string, string][] {
  const rows = getDb()
    .prepare(
      "SELECT mem_key, content FROM longterm_memory WHERE player_id=? " +
        "ORDER BY updated_ms DESC LIMIT ?",
    )
    .all(playerId, MAX_LTM_ENTRIES) as unknown as { mem_key: string; content: string }[];
  return rows.map((row) => [row.mem_key, row.content]);
}

/** 写/合并一条长期记忆（同 key 覆盖=更新语义）。 */
export function putLongterm(playerId: number, memKey: string, content: string): void {
  getDb()
    .prepare(
      "INSERT INTO longterm_memory(player_id, mem_key, content, updated_ms) VALUES(?,?,?,?) " +
        "ON CONFLICT(player_id, mem_key) DO UPDATE SET content=excluded.content, updated_ms=excluded.updated_ms",
    )
    .run(playerId, memKey, content, Date.now());
}
