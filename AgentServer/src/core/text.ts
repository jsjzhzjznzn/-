/**
 * 文本小工具：从 pi 消息内容里取纯文本（内容可能是字符串或 Text/Image 块数组）。
 */
import type { AgentMessage } from "@earendil-works/pi-agent-core";

type ContentBlock = { type: string; text?: string };

/** 消息内容 → 纯文本（图片块忽略；思考块忽略）。 */
export function contentText(content: unknown): string {
  if (typeof content === "string") return content;
  if (Array.isArray(content)) {
    return content
      .filter((block): block is ContentBlock => typeof block === "object" && block !== null)
      .filter((block) => block.type === "text" && typeof block.text === "string")
      .map((block) => block.text as string)
      .join("");
  }
  return content === undefined || content === null ? "" : String(content);
}

/** 助手消息 → 可见文本（多个 text 块拼接）。 */
export function assistantText(message: AgentMessage): string {
  if (message.role !== "assistant") return "";
  return contentText(message.content);
}

/** 从消息列表里找最后一条 user 消息的文本。 */
export function lastUserText(messages: readonly AgentMessage[]): string {
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    const message = messages[i]!;
    if (message.role === "user") return contentText(message.content);
  }
  return "";
}
