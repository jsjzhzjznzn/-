/**
 * 入站鉴权（对应 app.py 的 require_agent_token）。
 *
 * Phase 8 共享令牌：回环 ≠ 认证，这道闸挡本机其它进程伪造 player_id 问数据。
 * 未配置 AGENT_INTERNAL_TOKEN 时 fail-open（启动时告警），与 Python 版一致。
 *
 * 比较方式：两边都先做 SHA-256 再 timingSafeEqual —— 长度恒等且常数时间
 * （等价 Python 的 secrets.compare_digest，且不泄漏长度差异）。
 */
import { createHash, timingSafeEqual } from "node:crypto";

import { INTERNAL_TOKEN } from "../core/config.js";

const expected: Buffer | null = INTERNAL_TOKEN
  ? createHash("sha256").update(INTERNAL_TOKEN, "utf8").digest()
  : null;

function sha256(text: string): Buffer {
  return createHash("sha256").update(text, "utf8").digest();
}

/** 令牌校验：未配置时放行（fail-open）。 */
export function tokenOk(provided: string | undefined): boolean {
  if (!expected) return true;
  return timingSafeEqual(sha256(provided ?? ""), expected);
}

/** 启动日志里的 auth 描述（文案与 Python 版一致）。 */
export const authDescription = INTERNAL_TOKEN ? "token" : "OFF(fail-open,回环仍守门)";
