/**
 * 一次性 LLM 调用（不进 Agent 转写本）：摘要 / 反思质检 / 长期记忆抽取。
 *
 * 对应 Python 版里直接 new model 再 ainvoke 的那几处（context.summarize_batch、
 * reflection.reflect、memory.extract_and_store）。用 pi-ai 的 completeSimple。
 */
import type { Message } from "@earendil-works/pi-ai";

import { LLM_TIMEOUT_MS } from "../core/config.js";
import { chatModel, modelsRuntime } from "./provider.js";

/** 系统 + 用户两条消息 → 文本回复（温度由调用方给）。 */
export async function completeText(
  system: string,
  user: string,
  temperature: number,
  timeoutMs = LLM_TIMEOUT_MS,
  signal?: AbortSignal,
): Promise<string> {
  const timeout = AbortSignal.timeout(timeoutMs);
  const merged = signal ? AbortSignal.any([signal, timeout]) : timeout;
  const messages: Message[] = [
    { role: "system", content: system, timestamp: Date.now() },
    { role: "user", content: user, timestamp: Date.now() },
  ];
  const assistant = await modelsRuntime.completeSimple(chatModel, { messages }, { temperature, signal: merged });
  if (assistant.stopReason === "error" || assistant.stopReason === "aborted") {
    throw new Error(assistant.errorMessage ?? `LLM ${assistant.stopReason}`);
  }
  return assistant.content
    .filter((block): block is { type: "text"; text: string } => block.type === "text")
    .map((block) => block.text)
    .join("");
}
