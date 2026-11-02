/**
 * POST /agent/brief —— 登录主动简报（Phase 5 ②，契约见方案 §4.1）。
 *
 * 拉真实数据 → 确定性文案（零 LLM）→ 归档 assistant 一行 → 返回给 .NET 推 3104。
 */
import type { IncomingMessage, ServerResponse } from "node:http";

import { buildLoginBrief } from "../../agent/brief.js";
import { log } from "../../core/logger.js";
import { appendChat } from "../../db/chatRepo.js";
import { sendJson } from "../io.js";
import { readJsonBody } from "./history.js";

export async function handleBrief(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const started = Date.now();
  try {
    const body = await readJsonBody(req);
    const playerId = Number(body.player_id ?? 0);

    const brief = await buildLoginBrief(playerId);
    const createdMs = Date.now();
    appendChat(playerId, "assistant", brief);

    log.info(`brief player=${playerId} elapsed=${Date.now() - started}ms len=${brief.length}`);
    sendJson(res, 200, { ok: true, brief, created_ms: createdMs });
  } catch (err) {
    log.error("brief 失败", err);
    const message = err instanceof Error ? err.message : String(err);
    sendJson(res, 200, { ok: false, brief: "", created_ms: Date.now(), error: message });
  }
}
