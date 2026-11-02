/**
 * POST /agent/history —— 契约见方案 §4.1（3105 上拉分页的数据源）。
 *
 * 成功：{"ok":true,"items":[{"role":0|1,"content":..,"ts":ms}],"has_more":bool}
 * 失败：{"ok":false,"items":[],"has_more":false,"error":..}（HTTP 200，与 Python 版一致）
 */
import type { IncomingMessage, ServerResponse } from "node:http";

import { log } from "../../core/logger.js";
import { getRecentChat } from "../../db/chatRepo.js";
import { sendJson } from "../io.js";

interface HistoryBody {
  player_id?: number;
  limit?: number;
  before_ms?: number;
}

export async function readJsonBody(req: IncomingMessage): Promise<Record<string, unknown>> {
  const chunks: Buffer[] = [];
  for await (const chunk of req) chunks.push(chunk as Buffer);
  const text = Buffer.concat(chunks).toString("utf8");
  if (!text.trim()) return {};
  return JSON.parse(text) as Record<string, unknown>;
}

export async function handleHistory(req: IncomingMessage, res: ServerResponse): Promise<void> {
  let playerId = 0;
  try {
    const body = (await readJsonBody(req)) as HistoryBody;
    playerId = Number(body.player_id ?? 0);
    const limit = Number(body.limit ?? 20);
    const beforeMs = Number(body.before_ms ?? 0);
    const page = getRecentChat(playerId, limit, beforeMs);
    sendJson(res, 200, { ok: true, items: page.items, has_more: page.has_more });
  } catch (err) {
    log.error(`history 失败 player=${playerId}`, err);
    const message = err instanceof Error ? err.message : String(err);
    sendJson(res, 200, { ok: false, items: [], has_more: false, error: message });
  }
}
