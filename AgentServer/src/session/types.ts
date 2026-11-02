/**
 * 会话条目类型（对齐 pi 的 SessionEntry 形状，方案 §6.2）。
 *
 * 只落三类「模型消息」：user / assistant / toolResult —— system 块（人设/长期记忆/
 * 摘要/玩家快照/时效提醒）由 prepareRequest 每轮动态重建，不落库（方案 §6.3）。
 * 压缩条目（P5）是另一类条目：只存摘要与保留起点，不存被摘要掉的消息。
 */
import type { AgentMessage } from "@earendil-works/pi-agent-core";
import type { AssistantMessage, ToolResultMessage, UserMessage } from "@earendil-works/pi-ai";

/** 落库的消息（不含 system）。 */
export type PersistedMessage = UserMessage | AssistantMessage | ToolResultMessage;

export interface MessageEntry {
  type: "message";
  id: string;
  parentId: string | null;
  message: PersistedMessage;
  createdMs: number;
}

/** 压缩条目（P5 由 vendored pi compaction 产出）。 */
export interface CompactionEntry {
  type: "compaction";
  id: string;
  parentId: string | null;
  summary: string;
  /** 保留段起点（= 摘要之后第一条仍在上下文里的条目 id）。 */
  firstKeptEntryId: string;
  tokensBefore: number;
  createdMs: number;
}

export type SessionEntry = MessageEntry | CompactionEntry;

export function isMessageEntry(entry: SessionEntry): entry is MessageEntry {
  return entry.type === "message";
}

export function isCompactionEntry(entry: SessionEntry): entry is CompactionEntry {
  return entry.type === "compaction";
}

/** 运行期判断：某条 AgentMessage 是否是我们需要落盘的模型消息。 */
export function isPersistedMessage(message: AgentMessage): message is PersistedMessage {
  return message.role === "user" || message.role === "assistant" || message.role === "toolResult";
}
