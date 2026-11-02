/**
 * /agent/debug 的数据来源：该玩家的记忆内部状态水位（§5.9 字段映射，与 Python 版同字段名）。
 *
 * 字段来源：
 *   session_entries（权威会话）→ message_count / summarized_count / rolling_summary_preview
 *   进程内 RunState（最近一轮；run 结束后仍保留最近值）→ tool_round_count / reflection_*
 *   业务表 → style / longterm_memory / qa_cache_count
 */
import { getDb } from "../db/sqlite.js";
import { getLongterm } from "../db/longtermRepo.js";
import { getStyle } from "../db/prefsRepo.js";
import { chatModel } from "../llm/provider.js";
import { estimateContextTokens } from "../session/compaction.js";
import { latestCompaction, projectConversation, summarizedCount } from "../session/projection.js";
import { listEntries } from "../session/store.js";
import { compactionSettings } from "../session/triggers.js";
import { existingRunState } from "./runtime.js";

export interface DebugSnapshot {
  exists: boolean;
  message_count?: number;
  summarized_count?: number;
  rolling_summary_preview?: string;
  tool_round_count?: number;
  style?: string;
  longterm_memory?: { key: string; content: string }[];
  qa_cache_count?: number;
  reflection_count?: number;
  reflection_runs?: number;
  reflection_critique?: string;
  /** 以下为 Node 版新增（B2：压缩水位可观测；Python 版无这些字段）。 */
  freshness_nudges?: number;
  context_tokens?: number;
  context_window?: number;
  compact_threshold?: number;
  compact_enabled?: boolean;
  last_compaction_tokens_before?: number;
  last_compaction_at_ms?: number;
  last_compaction_kept_from?: string;
}

export function debugSnapshot(playerId: number): DebugSnapshot {
  const entries = listEntries(playerId);
  if (entries.length === 0) return { exists: false };

  const compaction = latestCompaction(entries);
  const run = existingRunState(playerId);
  const cacheRow = getDb()
    .prepare("SELECT COUNT(*) AS c FROM qa_cache WHERE player_id=?")
    .get(playerId) as { c: number };
  // 压缩水位（B2）：估算 token 与触发阈值——"该压不压"从此一眼可见，不用翻日志猜
  const settings = compactionSettings();
  const contextTokens = estimateContextTokens(projectConversation(entries)).tokens;

  return {
    exists: true,
    message_count: projectConversation(entries).length,
    summarized_count: summarizedCount(entries),
    rolling_summary_preview: (compaction?.summary ?? "").slice(0, 200),
    tool_round_count: run?.toolRounds ?? 0,
    style: getStyle(playerId),
    longterm_memory: getLongterm(playerId).map(([key, content]) => ({ key, content })),
    qa_cache_count: cacheRow.c,
    reflection_count: run?.reflectionCount ?? 0,
    reflection_runs: run?.reflectionRuns ?? 0,
    reflection_critique: (run?.reflectionCritique ?? "").slice(0, 120),
    freshness_nudges: run?.freshnessNudges ?? 0,
    context_tokens: contextTokens,
    context_window: chatModel.contextWindow,
    compact_threshold: chatModel.contextWindow - settings.reserveTokens,
    compact_enabled: settings.enabled,
    last_compaction_tokens_before: compaction?.tokensBefore,
    last_compaction_at_ms: compaction?.createdMs,
    last_compaction_kept_from: compaction?.firstKeptEntryId,
  };
}
