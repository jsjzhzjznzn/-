/**
 * POST /agent/proactive —— 主动提醒生成（悬浮球 3107，契约见方案 §4.1）。
 *
 * 事件数据由 .NET 内联给出（省一次工具轮）；LLM 异常 → 模板兜底；
 * 归档只记 assistant 一行（面板历史里就是"阿福主动说了一句"）。
 */
import type { IncomingMessage, ServerResponse } from "node:http";

import { generateProactive, kindName } from "../../agent/proactive.js";
import { log } from "../../core/logger.js";
import { sendJson } from "../io.js";
import { readJsonBody } from "./history.js";

export async function handleProactive(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const started = Date.now();
  let playerId = 0;
  let kind = 1;
  try {
    const body = await readJsonBody(req);
    playerId = Number(body.player_id ?? 0);
    kind = Number(body.kind ?? 1);
    const data = (body.data && typeof body.data === "object" ? body.data : {}) as Record<string, unknown>;

    const result = await generateProactive(playerId, kind, data);
    const createdMs = Date.now();

    log.info(
      `proactive player=${playerId} kind=${kindName(kind)} used_llm=${result.usedLlm} ` +
        `elapsed=${Date.now() - started}ms len=${result.message.length}`,
    );
    sendJson(res, 200, {
      ok: true,
      message: result.message,
      created_ms: createdMs,
      used_llm: result.usedLlm,
    });
  } catch (err) {
    log.error(`proactive 失败 player=${playerId} kind=${kind}`, err);
    const message = err instanceof Error ? err.message : String(err);
    sendJson(res, 200, { ok: false, message: "", created_ms: Date.now(), used_llm: false, error: message });
  }
}
