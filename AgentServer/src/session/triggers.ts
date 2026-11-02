/**
 * 压缩触发编排（方案 §7.4）：在 prepareRequest（每次 provider 请求前）检查并按需压缩。
 *
 * 触发判据（pi）：shouldCompact(estimateContextTokens(projection).tokens, contextWindow, settings)
 *   → contextTokens > contextWindow - reserveTokens
 * 压缩动作（pi）：prepareCompaction（切点：对齐轮起点、不拆散工具调用对）→ compact（摘要）
 *   → 写入一条 compaction 条目（原始条目全部保留；投影时摘要 + 保留段）。
 *
 * 每轮只检查一次（run.compactionChecked）：对应 Python 版在 ingest 节点做的压缩，
 * 也避免工具轮之间反复触发。
 */
import { chatModel } from "../llm/provider.js";
import type { RunState } from "../agent/runState.js";
import type { SessionEntry } from "./types.js";
import { projectConversation } from "./projection.js";
import { appendCompaction } from "./store.js";
import {
  COMPACT_KEEP_RECENT_TOKENS,
  COMPACT_RESERVE_TOKENS,
  COMPACT_ENABLED,
} from "../core/config.js";
import { log } from "../core/logger.js";
import {
  compact,
  estimateContextTokens,
  prepareCompaction,
  shouldCompact,
  type CompactionSettings,
} from "./compaction.js";
import { modelSummarizer } from "./summarizer.js";

/**
 * 摘要焦点（注入 pi 的 customInstructions）——方案 §7.3 的关键约束：
 * 只摘要「人」的事，游戏实时数据不进摘要（否则会被后续轮次复读）。
 */
export const GAME_SUMMARY_FOCUS =
  "额外要求：只摘要\"人\"的事（玩家自称/称呼/偏好/习惯/聊过的事/你答应过的事/未解决的问题）。" +
  "工具返回的游戏实时数据（背包数量、任务进度、邮件数、排行榜名次、保底计数、天气数值等）" +
  "一律不要写进摘要——这些数据随时会变，需要时由你重新调用工具查询。";

export function compactionSettings(): CompactionSettings {
  return {
    enabled: COMPACT_ENABLED,
    reserveTokens: COMPACT_RESERVE_TOKENS,
    keepRecentTokens: COMPACT_KEEP_RECENT_TOKENS,
  };
}

export interface CompactionOutcome {
  compacted: boolean;
  tokensBefore?: number;
  summarizedEntries?: number;
}

/**
 * 检查并在需要时压缩（在 prepareRequest 里 await）。
 * 任何异常都不阻断本轮对话（压缩失败 → 下轮再试，与 Python 版口径一致）。
 */
export async function maybeCompact(
  playerId: number,
  entries: SessionEntry[],
  run: RunState,
  signal?: AbortSignal,
): Promise<CompactionOutcome> {
  if (run.compactionChecked) return { compacted: false };
  run.compactionChecked = true;

  const settings = compactionSettings();
  if (!settings.enabled) return { compacted: false };

  try {
    const projection = projectConversation(entries);
    const estimate = estimateContextTokens(projection);
    const contextWindow = chatModel.contextWindow;
    if (!shouldCompact(estimate.tokens, contextWindow, settings)) return { compacted: false };

    const preparation = prepareCompaction(entries, settings);
    if (!preparation) return { compacted: false };

    const started = Date.now();
    const result = await compact(preparation, modelSummarizer, GAME_SUMMARY_FOCUS, signal);
    const keptIndex = entries.findIndex((entry) => entry.id === result.firstKeptEntryId);
    const summarizedEntries = keptIndex >= 0 ? keptIndex : 0;

    const parentId = entries.length > 0 ? entries[entries.length - 1]!.id : null;
    const entry = appendCompaction(
      playerId,
      {
        summary: result.summary,
        firstKeptEntryId: result.firstKeptEntryId,
        tokensBefore: result.tokensBefore,
      },
      parentId,
    );
    entries.push(entry);

    log.info(
      `压缩完成 player=${playerId} tokensBefore=${result.tokensBefore} ` +
        `估算窗口=${contextWindow} reserve=${settings.reserveTokens} ` +
        `摘要条目=${summarizedEntries} 保留自=${result.firstKeptEntryId.slice(0, 8)} ` +
        `摘要长度=${result.summary.length} 耗时=${Date.now() - started}ms`,
    );
    return { compacted: true, tokensBefore: result.tokensBefore, summarizedEntries };
  } catch (err) {
    log.warn(`压缩失败（不阻断本轮，下轮重试）player=${playerId}`, err);
    return { compacted: false };
  }
}
