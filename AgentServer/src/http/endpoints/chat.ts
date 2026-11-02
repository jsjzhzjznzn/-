/**
 * POST /agent/chat（非流式）——契约见方案 §4.1。
 *
 * 口径（与 Python 版逐字一致）：
 *  - 成功：{"ok":true,"reply":...,"used_tools":[...],"elapsed_ms":...}
 *  - 失败：HTTP 200 + {"ok":false,"reply":"","error":"agent_error: ..."}（不抛 500）
 *  - 相似问题缓存：命中直接回（used_tools=["cache"]，零 LLM），仍写 chat_messages
 *  - 归档：user / assistant 各一条；长期记忆后台抽取；纯知识回答写缓存
 *  - 审计：logs/audit.log 一行
 */
import type { IncomingMessage, ServerResponse } from "node:http";

import { cacheSkip, lookupCache, storeCache } from "../../agent/cache.js";
import { scheduleExtract } from "../../agent/longterm.js";
import { chat } from "../../agent/runtime.js";
import { CONTENT_MAX_CHARS } from "../../core/config.js";
import { audit, log, pyReprList } from "../../core/logger.js";
import { appendChat } from "../../db/chatRepo.js";
import { badRequest, readJson, sendJson } from "../io.js";

interface ChatRequestBody {
  player_id?: number;
  content?: string;
  context?: Record<string, unknown>;
}

export type { ChatRequestBody };

/** 解析 + 校验请求体；不合法时直接答复并返回 null。 */
export function parseChatBody(
  res: ServerResponse,
  body: ChatRequestBody,
): { playerId: number; content: string; context: Record<string, unknown> } | null {
  const playerId = body.player_id;
  const content = body.content;
  if (!Number.isInteger(playerId) || (playerId as number) <= 0) {
    badRequest(res, "invalid player_id");
    return null;
  }
  if (typeof content !== "string" || content.length < 1) {
    badRequest(res, "invalid content");
    return null;
  }
  if (content.length > CONTENT_MAX_CHARS) {
    badRequest(res, `content too long (max ${CONTENT_MAX_CHARS})`);
    return null;
  }
  const context =
    body.context && typeof body.context === "object" && !Array.isArray(body.context)
      ? body.context
      : {};
  return { playerId: playerId as number, content, context };
}

export async function handleChat(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const started = Date.now();
  let parsed: { playerId: number; content: string; context: Record<string, unknown> } | null = null;
  try {
    const body = await readJson<ChatRequestBody>(req);
    parsed = parseChatBody(res, body);
    if (!parsed) return;

    const skip = cacheSkip(parsed.content);

    // ⑤ 相似问题缓存：纯知识问答命中直回（时效类问题永不走缓存）
    if (!skip) {
      const cached = lookupCache(parsed.playerId, parsed.content);
      if (cached) {
        const elapsedMs = Date.now() - started;
        appendChat(parsed.playerId, "user", parsed.content);
        appendChat(parsed.playerId, "assistant", cached);
        log.info(
          `chat player=${parsed.playerId} tools=['cache'] elapsed=${elapsedMs}ms reply_len=${cached.length}`,
        );
        audit(parsed.playerId, parsed.content, ["cache"], elapsedMs, cached.length, true);
        sendJson(res, 200, {
          ok: true,
          reply: cached,
          used_tools: ["cache"],
          elapsed_ms: elapsedMs,
        });
        return;
      }
    }

    const result = await chat(parsed.playerId, parsed.content, parsed.context);

    // L2 会话记录（给 UGUI 历史回放）
    appendChat(parsed.playerId, "user", parsed.content);
    appendChat(parsed.playerId, "assistant", result.reply);
    // ⑦ L3：回复送出后后台抽取长期记忆（不阻塞响应）
    scheduleExtract(parsed.playerId, parsed.content, result.reply);
    // ⑤ 缓存写：本轮没调工具**且上下文里没有历史工具数据**（D12）且非时效类，才算"纯知识回答"
    if (result.toolRounds === 0 && !skip && !result.hadToolContext) {
      storeCache(parsed.playerId, parsed.content, result.reply);
    }

    log.info(
      `chat player=${parsed.playerId} tools=${pyReprList(result.usedTools)} ` +
        `elapsed=${result.elapsedMs}ms reply_len=${result.reply.length}`,
    );
    audit(parsed.playerId, parsed.content, result.usedTools, result.elapsedMs, result.reply.length, true);
    sendJson(res, 200, {
      ok: true,
      reply: result.reply,
      used_tools: result.usedTools,
      elapsed_ms: result.elapsedMs,
    });
  } catch (err) {
    const playerId = parsed?.playerId ?? 0;
    const content = parsed?.content ?? "";
    const elapsedMs = Date.now() - started;
    log.error(`chat 失败 player=${playerId}`, err);
    audit(playerId, content, [], elapsedMs, 0, false);
    const message = err instanceof Error ? err.message : String(err);
    sendJson(res, 200, { ok: false, reply: "", error: `agent_error: ${message}` });
  }
}
