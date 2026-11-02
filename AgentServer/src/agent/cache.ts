/**
 * 相似问题缓存（Phase 5 ⑤）：纯知识问答命中直回（app.py 的 _cache_lookup / _cache_skip）。
 *
 * 口径（与 Python 版逐字一致，除 2026-10-18 的 D12 扩展）：
 *  - 时效关键词命中 → 双向跳过（不读也不写：数据类问答会过期，读旧账=给过期答案）；
 *  - D12：**游戏实时数据类问题同样双向跳过**（判据 GAME_DATA_KEYWORDS，理由见 cacheSkip）；
 *  - 先归一化精确匹配，再对全部缓存条目做相似扫描（先按长度差 ≤2 剪枝，再 ratio ≥0.90）；
 *  - 只写「未调工具的纯知识回答」（由调用方判定）。
 */
import { CACHE_LENGTH_TOLERANCE, CACHE_SIMILARITY } from "../core/config.js";
import { normalizeQuestion, similarityRatio } from "../core/distance.js";
import { log } from "../core/logger.js";
import { cacheAllQuestions, cacheGetExact, cachePut } from "../db/qaCacheRepo.js";
import { hintsFreshness, hitsGameData } from "./hooks.js";

/**
 * 命中时效词或游戏数据词 → 双向跳过。
 *
 * 2026-10-18（D12）扩了第二条：**游戏实时数据类问题也双向跳过**。
 * 起因是实测洞：问「刚说的那几样东西里，铁矿有多少个？」不含时效关键词，
 * 而缓存写入的判据是「本轮没调工具」——模型凭历史作答、没查库，同样算"没调工具"，
 * 于是这条会变的答案被写进缓存，之后永远 2ms 返回旧数字，连 D10 的强制查库都绕过了。
 * 数据类问题本就不该走缓存：宁可多一次真实查询，也不给过期答案。
 */
export function cacheSkip(question: string): boolean {
  return hintsFreshness(question ?? "") || hitsGameData(question ?? "");
}

/** 缓存查询：精确命中 → 相似命中（≥0.90）→ null。 */
export function lookupCache(playerId: number, question: string): string | null {
  const norm = normalizeQuestion(question);
  if (!norm) return null;

  const exact = cacheGetExact(playerId, norm);
  if (exact) return exact;

  for (const [cachedNorm, reply] of cacheAllQuestions(playerId)) {
    if (Math.abs(cachedNorm.length - norm.length) > CACHE_LENGTH_TOLERANCE) continue;
    if (similarityRatio(cachedNorm, norm) >= CACHE_SIMILARITY) {
      log.debug(`缓存相似命中 player=${playerId} q=${JSON.stringify(question.slice(0, 24))}`);
      return reply;
    }
  }
  return null;
}

/** 写缓存（调用方须已确认：本轮没调工具 且 非时效类问题）。 */
export function storeCache(playerId: number, question: string, reply: string): void {
  const norm = normalizeQuestion(question);
  if (!norm) return;
  cachePut(playerId, norm, question, reply);
}
