/**
 * 摘要器实现（注入给 session/compaction.ts 的 SummarizeFn）：用 pi-ai 的 completeSimple。
 *
 * 与 pi 的 completeSummarization 同款要点：
 *  - maxTokens 由调用方按 reserveTokens 比例给出；
 *  - **不做 prompt 缓存写入**（一次性提示词，缓存无意义）；
 *  - stopReason=error → 抛错；stopReason=length → 抛错（摘要被截断不能当检查点用）；
 *  - 模型试图调用工具 → 抛错。
 */
import { type AssistantMessage, type Message } from "@earendil-works/pi-ai";

import { LLM_TIMEOUT_MS } from "../core/config.js";
import { log } from "../core/logger.js";
import { chatModel, modelsRuntime } from "../llm/provider.js";
import type { SummarizeFn, SummarizeResult } from "./compaction.js";

function failureOf(response: AssistantMessage, label: string): string | undefined {
  if (response.stopReason === "error") {
    return `${label} 失败：${response.errorMessage ?? "未知错误"}`;
  }
  if (response.stopReason === "length") {
    return `${label} 失败：命中 token 上限，摘要不完整`;
  }
  return undefined;
}

/** 真实摘要器（pi-ai completeSimple）。 */
export const modelSummarizer: SummarizeFn = async (
  systemPrompt: string,
  userPrompt: string,
  maxTokens: number,
  signal?: AbortSignal,
): Promise<SummarizeResult> => {
  const timeout = AbortSignal.timeout(LLM_TIMEOUT_MS);
  const merged = signal ? AbortSignal.any([signal, timeout]) : timeout;
  const messages: Message[] = [
    { role: "system", content: systemPrompt, timestamp: Date.now() },
    { role: "user", content: [{ type: "text", text: userPrompt }], timestamp: Date.now() },
  ];

  const response = await modelsRuntime.completeSimple(
    chatModel,
    { messages },
    { maxTokens, signal: merged, cacheRetention: "none" },
  );

  const failure = failureOf(response, "摘要");
  if (failure) throw new Error(failure);
  if (response.content.some((block) => block.type === "toolCall")) {
    throw new Error("摘要器试图调用工具");
  }

  const text = response.content
    .filter((block): block is { type: "text"; text: string } => block.type === "text")
    .map((block) => block.text)
    .join("");

  log.debug(`摘要完成 maxTokens=${maxTokens} len=${text.length}`);
  return { text, usage: response.usage };
};
