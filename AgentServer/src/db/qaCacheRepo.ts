/**
 * 相似问题缓存（qa_cache）：纯知识问答命中直回，省 token 省等待（Phase 5 ⑤）。
 * 逐句搬 storage.py 的 SQL（含每玩家上限的最旧淘汰）。
 */
import { MAX_QA_CACHE_PER_PLAYER } from "../core/config.js";
import { getDb } from "./sqlite.js";

/** 归一化精确命中 → 返回缓存回复，否则 null。 */
export function cacheGetExact(playerId: number, normQuestion: string): string | null {
  const row = getDb()
    .prepare("SELECT reply FROM qa_cache WHERE player_id=? AND norm_question=?")
    .get(playerId, normQuestion) as { reply: string } | undefined;
  return row?.reply ?? null;
}

/** 该玩家全部缓存条目 [normQuestion, reply]，供相似度扫描（条数有上限，扫描成本可控）。 */
export function cacheAllQuestions(playerId: number): [string, string][] {
  const rows = getDb()
    .prepare("SELECT norm_question, reply FROM qa_cache WHERE player_id=?")
    .all(playerId) as unknown as { norm_question: string; reply: string }[];
  return rows.map((row) => [row.norm_question, row.reply]);
}

/** 写缓存（UPSERT）；超出每玩家上限按最旧淘汰。只应写入「未调工具的纯知识回答」。 */
export function cachePut(
  playerId: number,
  normQuestion: string,
  question: string,
  reply: string,
): void {
  const db = getDb();
  const now = Date.now();
  db.prepare(
    "INSERT INTO qa_cache(player_id, norm_question, question, reply, created_ms) VALUES(?,?,?,?,?) " +
      "ON CONFLICT(player_id, norm_question) DO UPDATE SET question=excluded.question, " +
      "reply=excluded.reply, created_ms=excluded.created_ms",
  ).run(playerId, normQuestion, question, reply, now);
  db.prepare(
    "DELETE FROM qa_cache WHERE player_id=? AND norm_question IN (" +
      "  SELECT norm_question FROM qa_cache WHERE player_id=? " +
      "  ORDER BY created_ms DESC LIMIT -1 OFFSET ?)",
  ).run(playerId, playerId, MAX_QA_CACHE_PER_PLAYER);
}

/** 清空该玩家缓存（风格切换 / 长期记忆更新时旧语气回复不再命中）。 */
export function cacheClear(playerId: number): void {
  getDb().prepare("DELETE FROM qa_cache WHERE player_id=?").run(playerId);
}
