/**
 * POST /agent/chat/stream（流式，打字机数据源）——契约见方案 §4.1/§12。
 *
 * ndjson 事件（一行一 JSON，.NET 网关逐行读）：
 *   {"type":"delta","text":增量}
 *   {"type":"reset"}                              工具轮前导文本作废 / 反思重答作废
 *   {"type":"done","reply":权威终值,"elapsed_ms":ms}
 *   {"type":"error","error":...}
 * 终值一律以 done.reply 为准（出口裁剪/兜底后再取，比流式累计可靠）。
 *
 * 缓存命中：整段一次成型（一个 delta + done，零 LLM 等待），口径与非流式一致。
 */
import type { IncomingMessage, ServerResponse } from "node:http";

import { cacheSkip, lookupCache, storeCache } from "../../agent/cache.js";
import { scheduleExtract } from "../../agent/longterm.js";
import { chatStream } from "../../agent/runtime.js";
import { audit, log } from "../../core/logger.js";
import { appendChat } from "../../db/chatRepo.js";
import { parseChatBody, type ChatRequestBody } from "./chat.js";
import { readJson, sendJson } from "../io.js";

export async function handleChatStream(req: IncomingMessage, res: ServerResponse): Promise<void> {
  let parsed;
  try {
    parsed = parseChatBody(res, await readJson<ChatRequestBody>(req));
  } catch (err) {
    log.warn("chat-stream 请求体解析失败", err);
    sendJson(res, 400, { detail: "invalid json" });
    return;
  }
  if (!parsed) return;

  res.writeHead(200, {
    "Content-Type": "application/x-ndjson; charset=utf-8",
    "Cache-Control": "no-cache",
  });

  const started = Date.now();
  const skip = cacheSkip(parsed.content);
  let clientGone = false;
  let abortRun: (() => void) | null = null;

  const write = (payload: Record<string, unknown>): void => {
    if (clientGone || res.writableEnded) return;
    res.write(JSON.stringify(payload) + "\n");
  };

  // 客户端断开 → abort 本轮（Python 版同口径：中断不归档）
  res.on("close", () => {
    if (res.writableEnded) return;
    clientGone = true;
    abortRun?.();
  });

  try {
    // 缓存命中：整段一次成型
    if (!skip) {
      const cached = lookupCache(parsed.playerId, parsed.content);
      if (cached) {
        write({ type: "delta", text: cached });
        const elapsedMs = Date.now() - started;
        appendChat(parsed.playerId, "user", parsed.content);
        appendChat(parsed.playerId, "assistant", cached);
        log.info(
          `chat-stream player=${parsed.playerId} tools=['cache'] elapsed=${elapsedMs}ms reply_len=${cached.length}`,
        );
        audit(parsed.playerId, parsed.content, ["cache"], elapsedMs, cached.length, true);
        write({ type: "done", reply: cached, elapsed_ms: elapsedMs });
        return;
      }
    }

    const result = await chatStream(parsed.playerId, parsed.content, parsed.context, {
      delta: (text) => write({ type: "delta", text }),
      reset: () => write({ type: "reset" }),
      attached: (abort) => {
        abortRun = abort;
      },
    });

    if (clientGone) {
      log.info(`chat-stream 客户端已断开，本轮不归档 player=${parsed.playerId}`);
      return;
    }

    // 与非流式同口径：L2 会话记录照常归档（断线重连后历史不缺）
    appendChat(parsed.playerId, "user", parsed.content);
    appendChat(parsed.playerId, "assistant", result.reply);
    // L3 后台抽取 + 缓存写（无工具轮 且 上下文无历史工具数据（D12） 且 非时效类 = 纯知识回答）
    scheduleExtract(parsed.playerId, parsed.content, result.reply);
    if (result.toolRounds === 0 && !skip && !result.hadToolContext) {
      storeCache(parsed.playerId, parsed.content, result.reply);
    }

    write({ type: "done", reply: result.reply, elapsed_ms: result.elapsedMs });
    log.info(
      `chat-stream player=${parsed.playerId} elapsed=${result.elapsedMs}ms ` +
        `reply_len=${result.reply.length} tool_rounds=${result.toolRounds}`,
    );
    audit(
      parsed.playerId,
      parsed.content,
      [`rounds=${result.toolRounds}`],
      result.elapsedMs,
      result.reply.length,
      true,
    );
  } catch (err) {
    const elapsedMs = Date.now() - started;
    log.error(`chat-stream 失败 player=${parsed.playerId}`, err);
    audit(parsed.playerId, parsed.content, [], elapsedMs, 0, false);
    const message = err instanceof Error ? err.message : String(err);
    write({ type: "error", error: `agent_error: ${message}` });
  } finally {
    if (!res.writableEnded) res.end();
  }
}
