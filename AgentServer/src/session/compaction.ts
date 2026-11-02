/**
 * 上下文压缩（移植自 pi 的 compaction，方案 §7）。
 *
 * Ported from `@earendil-works/pi-coding-agent@1.3.0`
 *   src/core/compaction/compaction.ts（MIT）+ src/core/compaction/utils.ts（MIT）
 * 保留：shouldCompact 判据、findCutPoint 切点算法（对齐轮起点、不拆散工具调用对）、
 *       摘要提示词（初版 / 增量更新 / 轮前缀）与「摘要不得调用工具 / 长度截断算失败」校验、
 *       serializeConversation 序列化（工具结果单条截断 2000 字）、cut/keep 的 token 估算。
 *
 * 三处有意的裁剪（游戏 agent 用不到，且都有明确理由）：
 *  1. **分支/树结构**：我们每玩家线性会话，去掉 ProjectedSessionEntry / context_edit 那套投影逻辑
 *     （pi 的 branch-summarization 也未移植）。
 *  2. **文件操作追踪**：游戏 agent 没有文件。`computeFileLists/formatFileOperations` 与
 *     `<read-files>/<modified-files>` 段落一并去掉——将来要做「游戏事实追踪」（记住查过哪些
 *     item/邮件/任务 id）时，在这里接上即可（extractFactsFromMessages 预留位）。
 *  3. **摘要输入过滤工具结果**（我们独有的关键约束）：工具结果带的是**会过期的游戏数据**
 *     （背包数量/名次/保底计数），写进摘要就会被后续轮次复读。因此摘要输入只保留
 *     user/assistant 文本与「查了什么工具」，工具结果本体不喂给摘要器（工具调用行仍保留）。
 *     → 见 DROP_TOOL_RESULTS_FROM_SUMMARY。
 *
 * 另：`compact()` 的 LLM 调用改为注入式 SummarizeFn（真实实现见 session/summarizer.ts），
 * 这样切点/编排逻辑可以纯单测，不必起网络。
 */
import { contentText, type AssistantMessage, type Message, type Usage } from "@earendil-works/pi-ai";

import type { AgentMessage } from "@earendil-works/pi-agent-core";

import { isMessageEntry, type CompactionEntry, type SessionEntry } from "./types.js";
import { projectConversation } from "./projection.js";

// ============================================================================
// 设置与类型
// ============================================================================

export interface CompactionSettings {
  enabled: boolean;
  reserveTokens: number;
  keepRecentTokens: number;
}

export const DEFAULT_COMPACTION_SETTINGS: CompactionSettings = {
  enabled: true,
  reserveTokens: 16384,
  keepRecentTokens: 20000,
};

/** 摘要输入是否剔除工具结果（我们独有；理由见文件头注释 3）。 */
export const DROP_TOOL_RESULTS_FROM_SUMMARY = true;

// ============================================================================
// token 估算（pi 原文：chars/4，偏保守）
// ============================================================================

const ESTIMATED_IMAGE_CHARS = 4800;

function estimateTextAndImageContentChars(
  content: string | Array<{ type: string; text?: string }>,
): number {
  if (typeof content === "string") return content.length;
  let chars = 0;
  for (const block of content) {
    if (block.type === "text" && block.text) chars += block.text.length;
    else if (block.type === "image") chars += ESTIMATED_IMAGE_CHARS;
  }
  return chars;
}

export function calculateContextTokens(usage: Usage): number {
  return usage.totalTokens || usage.input + usage.output + usage.cacheRead + usage.cacheWrite;
}

function getAssistantUsage(message: AgentMessage): Usage | undefined {
  if (message.role === "assistant" && "usage" in message) {
    const assistant = message as AssistantMessage;
    if (
      assistant.stopReason !== "aborted" &&
      assistant.stopReason !== "error" &&
      assistant.usage &&
      calculateContextTokens(assistant.usage) > 0
    ) {
      return assistant.usage;
    }
  }
  return undefined;
}

export function getLastAssistantUsage(entries: readonly SessionEntry[]): Usage | undefined {
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const entry = entries[i]!;
    if (entry.type === "message") {
      const usage = getAssistantUsage(entry.message);
      if (usage) return usage;
    }
  }
  return undefined;
}

export function estimateTokens(message: AgentMessage): number {
  let chars = 0;
  switch (message.role) {
    case "system": {
      const system = message as { content: string | Array<{ type: string; text?: string }>; sections?: Record<string, string | null>; toolsAdded?: unknown };
      chars = estimateTextAndImageContentChars(system.content);
      if (system.sections) {
        for (const section of Object.values(system.sections)) if (section) chars += section.length;
      }
      if (system.toolsAdded) chars += JSON.stringify(system.toolsAdded).length;
      return Math.ceil(chars / 4);
    }
    case "user": {
      chars = estimateTextAndImageContentChars(
        (message as { content: string | Array<{ type: string; text?: string }> }).content,
      );
      return Math.ceil(chars / 4);
    }
    case "assistant": {
      for (const block of message.content) {
        if (block.type === "text") chars += block.text.length;
        else if (block.type === "thinking") chars += block.thinking.length;
        else if (block.type === "toolCall") chars += block.name.length + JSON.stringify(block.arguments).length;
      }
      return Math.ceil(chars / 4);
    }
    case "toolResult": {
      chars = estimateTextAndImageContentChars(message.content);
      return Math.ceil(chars / 4);
    }
    default:
      return 0;
  }
}

export interface ContextUsageEstimate {
  tokens: number;
  usageTokens: number;
  trailingTokens: number;
  lastUsageIndex: number | null;
}

/** 用最后一次 assistant 的真实 usage 作基准 + 其后的估算（pi 原文）。 */
export function estimateContextTokens(messages: readonly AgentMessage[]): ContextUsageEstimate {
  let usageInfo: { usage: Usage; index: number } | undefined;
  for (let i = messages.length - 1; i >= 0; i -= 1) {
    const usage = getAssistantUsage(messages[i]!);
    if (usage) {
      usageInfo = { usage, index: i };
      break;
    }
  }

  if (!usageInfo) {
    let estimated = 0;
    for (const message of messages) estimated += estimateTokens(message);
    return { tokens: estimated, usageTokens: 0, trailingTokens: estimated, lastUsageIndex: null };
  }

  const usageTokens = calculateContextTokens(usageInfo.usage);
  let trailingTokens = 0;
  for (let i = usageInfo.index + 1; i < messages.length; i += 1) {
    trailingTokens += estimateTokens(messages[i]!);
  }
  return {
    tokens: usageTokens + trailingTokens,
    usageTokens,
    trailingTokens,
    lastUsageIndex: usageInfo.index,
  };
}

export function shouldCompact(
  contextTokens: number,
  contextWindow: number,
  settings: CompactionSettings,
): boolean {
  if (!settings.enabled) return false;
  return contextTokens > contextWindow - settings.reserveTokens;
}

// ============================================================================
// 切点检测（pi 原文；我们按线性条目简化掉投影层）
// ============================================================================

/** 条目对模型可见的消息（compaction 条目不产生消息——摘要由投影注入）。 */
export function entryContextMessages(entry: SessionEntry): AgentMessage[] {
  return isMessageEntry(entry) ? [entry.message] : [];
}

function isCutPointMessage(message: AgentMessage): boolean {
  return message.role === "user" || message.role === "assistant";
}

function isTurnStartMessage(message: AgentMessage): boolean {
  return message.role === "user";
}

function isTurnStartEntry(entry: SessionEntry): boolean {
  if (entry.type === "compaction") return false;
  return entryContextMessages(entry).some(isTurnStartMessage);
}

/** 合法切点：可见的 user/assistant 消息；**绝不在 toolResult 处切**（必须跟着它的调用）。 */
function findValidCutPoints(entries: readonly SessionEntry[], startIndex: number, endIndex: number): number[] {
  const cutPoints: number[] = [];
  for (let i = startIndex; i < endIndex; i += 1) {
    const entry = entries[i]!;
    if (entry.type === "compaction") continue;
    if (entryContextMessages(entry).some(isCutPointMessage)) cutPoints.push(i);
  }
  return cutPoints;
}

export function findTurnStartIndex(
  entries: readonly SessionEntry[],
  entryIndex: number,
  startIndex: number,
): number {
  for (let i = entryIndex; i >= startIndex; i -= 1) {
    if (isTurnStartEntry(entries[i]!)) return i;
  }
  return -1;
}

export interface CutPointResult {
  /** 保留段起点（条目下标） */
  firstKeptEntryIndex: number;
  /** 被切开那一轮的 user 消息下标；未切开时为 -1 */
  turnStartIndex: number;
  isSplitTurn: boolean;
}

/** 从最新往回累计，攒够 keepRecentTokens 就切（pi 原文算法）。 */
export function findCutPoint(
  entries: readonly SessionEntry[],
  startIndex: number,
  endIndex: number,
  keepRecentTokens: number,
): CutPointResult {
  const cutPoints = findValidCutPoints(entries, startIndex, endIndex);
  if (cutPoints.length === 0) {
    return { firstKeptEntryIndex: startIndex, turnStartIndex: -1, isSplitTurn: false };
  }

  let accumulatedTokens = 0;
  let cutIndex = cutPoints[0]!;
  for (let i = endIndex - 1; i >= startIndex; i -= 1) {
    const messageTokens = entryContextMessages(entries[i]!).reduce(
      (sum, message) => sum + estimateTokens(message),
      0,
    );
    if (messageTokens === 0) continue;
    accumulatedTokens += messageTokens;
    if (accumulatedTokens >= keepRecentTokens) {
      // 优先取 ≥ 当前下标的最近合法切点；若尾部工具结果本身就超预算，
      // 则保留它前面的 assistant 工具调用，而不是退回第一条消息
      cutIndex = cutPoints.find((candidate) => candidate >= i) ?? cutPoints[cutPoints.length - 1]!;
      break;
    }
  }

  // 往回吃掉不影响上下文的相邻条目
  while (cutIndex > startIndex) {
    const prev = entries[cutIndex - 1]!;
    if (prev.type === "compaction" || entryContextMessages(prev).length > 0) break;
    cutIndex -= 1;
  }

  const cutEntry = entries[cutIndex]!;
  const startsTurn = isTurnStartEntry(cutEntry);
  const turnStartIndex = startsTurn ? -1 : findTurnStartIndex(entries, cutIndex, startIndex);
  return {
    firstKeptEntryIndex: cutIndex,
    turnStartIndex,
    isSplitTurn: !startsTurn && turnStartIndex !== -1,
  };
}

// ============================================================================
// 压缩准备（pi 的 prepareCompaction，去掉投影层）
// ============================================================================

export interface CompactionPreparation {
  firstKeptEntryId: string;
  messagesToSummarize: AgentMessage[];
  turnPrefixMessages: AgentMessage[];
  isSplitTurn: boolean;
  tokensBefore: number;
  previousSummary?: string;
  settings: CompactionSettings;
}

export function prepareCompaction(
  entries: readonly SessionEntry[],
  settings: CompactionSettings,
): CompactionPreparation | undefined {
  if (entries.length > 0 && entries[entries.length - 1]!.type === "compaction") return undefined;

  let previousSummary: string | undefined;
  let boundaryStart = 0;
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const entry = entries[i]!;
    if (entry.type !== "compaction") continue;
    previousSummary = entry.summary;
    const keptIndex = entries.findIndex(
      (candidate, index) => index > i && candidate.id === (entry as CompactionEntry).firstKeptEntryId,
    );
    boundaryStart = keptIndex >= 0 ? keptIndex : i + 1;
    break;
  }
  const boundaryEnd = entries.length;

  const tokensBefore = estimateContextTokens(projectConversation(entries)).tokens;
  const cutPoint = findCutPoint(entries, boundaryStart, boundaryEnd, settings.keepRecentTokens);

  const firstKeptEntry = entries[cutPoint.firstKeptEntryIndex];
  if (!firstKeptEntry) return undefined;

  const historyEnd = cutPoint.isSplitTurn ? cutPoint.turnStartIndex : cutPoint.firstKeptEntryIndex;
  const sliceMessages = (from: number, to: number): AgentMessage[] =>
    entries
      .slice(from, to)
      .flatMap((entry) => entryContextMessages(entry))
      .filter((message) => message.role !== "system"); // 系统块是提示词状态，不进摘要

  const messagesToSummarize = sliceMessages(boundaryStart, historyEnd);
  const turnPrefixMessages = cutPoint.isSplitTurn
    ? sliceMessages(cutPoint.turnStartIndex, cutPoint.firstKeptEntryIndex)
    : [];

  if (messagesToSummarize.length === 0 && turnPrefixMessages.length === 0) return undefined;

  // 预留位：游戏事实追踪（查过哪些 item/邮件/任务 id）——当前不做，摘要里也不需要
  return {
    firstKeptEntryId: firstKeptEntry.id,
    messagesToSummarize,
    turnPrefixMessages,
    isSplitTurn: cutPoint.isSplitTurn,
    tokensBefore,
    previousSummary,
    settings,
  };
}

// ============================================================================
// 序列化（pi 原文）
// ============================================================================

const TOOL_RESULT_MAX_CHARS = 2000;

function truncateForSummary(text: string, maxChars: number): string {
  if (text.length <= maxChars) return text;
  const truncatedChars = text.length - maxChars;
  return `${text.slice(0, maxChars)}\n\n[... ${truncatedChars} more characters truncated]`;
}

export function serializeConversation(messages: readonly Message[]): string {
  const parts: string[] = [];
  for (const message of messages) {
    if (message.role === "user") {
      const content = contentText(message.content);
      if (content) parts.push(`[User]: ${content}`);
      continue;
    }
    if (message.role === "assistant") {
      const toolCalls: string[] = [];
      for (const block of message.content) {
        if (block.type !== "toolCall") continue;
        const args = (block.arguments ?? {}) as Record<string, unknown>;
        const argsStr = Object.entries(args)
          .map(([key, value]) => `${key}=${JSON.stringify(value)}`)
          .join(", ");
        toolCalls.push(`${block.name}(${argsStr})`);
      }
      if (message.content.some((block) => block.type === "text")) {
        parts.push(`[Assistant]: ${contentText(message.content)}`);
      }
      if (toolCalls.length > 0) {
        parts.push(`[Assistant tool calls]: ${toolCalls.join("; ")}`);
      }
      continue;
    }
    if (message.role === "toolResult") {
      const content = contentText(message.content);
      if (content) parts.push(`[Tool result]: ${truncateForSummary(content, TOOL_RESULT_MAX_CHARS)}`);
    }
  }
  return parts.join("\n\n");
}

// ============================================================================
// 摘要提示词（pi 原文，逐字）
// ============================================================================

export const SUMMARIZATION_SYSTEM_PROMPT =
  "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n\n" +
  "Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";

const SUMMARIZATION_PROMPT = `The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

Use this EXACT format:

## Goal
[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

## Constraints & Preferences
- [Any constraints, preferences, or requirements mentioned by user]
- [Or "(none)" if none were mentioned]

## Progress
### Done
- [x] [Completed tasks/changes]

### In Progress
- [ ] [Current work]

### Blocked
- [Issues preventing progress, if any]

## Key Decisions
- **[Decision]**: [Brief rationale]

## Next Steps
1. [Ordered list of what should happen next]

## Critical Context
- [Any data, examples, or references needed to continue]
- [Or "(none)" if not applicable]

Keep each section concise. Preserve exact file paths, function names, and error messages.`;

const UPDATE_SUMMARIZATION_PROMPT = `The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

Update the existing structured summary with new information. RULES:
- PRESERVE all existing information from the previous summary
- ADD new progress, decisions, and context from the new messages
- UPDATE the Progress section: move items from "In Progress" to "Done" when completed
- UPDATE "Next Steps" based on what was accomplished
- PRESERVE exact file paths, function names, and error messages
- If something is no longer relevant, you may remove it

Use this EXACT format:

## Goal
[Preserve existing goals, add new ones if the task expanded]

## Constraints & Preferences
- [Preserve existing, add new ones discovered]

## Progress
### Done
- [x] [Include previously done items AND newly completed items]

### In Progress
- [ ] [Current work - update based on progress]

### Blocked
- [Current blockers - remove if resolved]

## Key Decisions
- **[Decision]**: [Brief rationale] (preserve all previous, add new)

## Next Steps
1. [Update based on current state]

## Critical Context
- [Preserve important context, add new if needed]

Keep each section concise. Preserve exact file paths, function names, and error messages.`;

const TURN_PREFIX_SUMMARIZATION_PROMPT = `The messages above are earlier context from an ongoing conversation. Later messages are stored separately and do not need to be reconstructed.

Create a concise checkpoint of the user's request and the progress shown above. This checkpoint will be placed before the later messages so the conversation can continue with the necessary context.

## Original Request
[What did the user ask for?]

## Progress So Far
- [Key decisions and work completed in these messages]

## Context Needed to Continue
- [Information from these messages needed to understand the later work]

Only summarize information explicitly present above. Do not infer or recreate later messages.`;

// ============================================================================
// 摘要调用（编排与校验照搬，LLM 调用改为注入）
// ============================================================================

export interface SummarizeResult {
  text: string;
  usage?: Usage;
}

/** 注入式摘要器：真实实现用 pi-ai 的 completeSimple（见 session/summarizer.ts）。 */
export type SummarizeFn = (
  systemPrompt: string,
  userPrompt: string,
  maxTokens: number,
  signal?: AbortSignal,
) => Promise<SummarizeResult>;

function buildSummarizationUserPrompt(
  conversationText: string,
  basePrompt: string,
  previousSummary: string | undefined,
  customInstructions: string | undefined,
): string {
  let prompt = `<conversation>\n${conversationText}\n</conversation>\n\n`;
  if (previousSummary) prompt += `<previous-summary>\n${previousSummary}\n</previous-summary>\n\n`;
  if (customInstructions) prompt += `Additional focus: ${customInstructions}\n\n`;
  return prompt + basePrompt;
}

/** pi 同款：把 LLM 消息序列化成文本（可选剔除工具结果，见文件头注释 3）。 */
function conversationTextFor(messages: readonly AgentMessage[]): string {
  const filtered = DROP_TOOL_RESULTS_FROM_SUMMARY
    ? messages.filter((message) => message.role !== "toolResult")
    : messages;
  return serializeConversation(filtered as unknown as Message[]);
}

/** 生成或增量更新摘要（pi 的 generateSummaryWithUsage + 失败校验）。 */
export async function generateSummaryWithUsage(
  messages: readonly AgentMessage[],
  settings: CompactionSettings,
  summarize: SummarizeFn,
  customInstructions?: string,
  previousSummary?: string,
  signal?: AbortSignal,
): Promise<SummarizeResult> {
  const maxTokens = Math.min(Math.floor(0.8 * settings.reserveTokens), 8192);
  const basePrompt = previousSummary ? UPDATE_SUMMARIZATION_PROMPT : SUMMARIZATION_PROMPT;
  const promptText = buildSummarizationUserPrompt(
    conversationTextFor(messages),
    basePrompt,
    previousSummary,
    customInstructions,
  );
  return await summarize(SUMMARIZATION_SYSTEM_PROMPT, promptText, maxTokens, signal);
}

/** 轮前缀摘要（切在轮中间时用，pi 原文；预算为 reserve 的一半）。 */
async function generateTurnPrefixSummary(
  messages: readonly AgentMessage[],
  settings: CompactionSettings,
  summarize: SummarizeFn,
  signal?: AbortSignal,
): Promise<SummarizeResult> {
  const maxTokens = Math.min(Math.floor(0.5 * settings.reserveTokens), 4096);
  const promptText =
    `# Conversation\n${conversationTextFor(messages)}\n\n# Instructions\n` +
    TURN_PREFIX_SUMMARIZATION_PROMPT;
  return await summarize(SUMMARIZATION_SYSTEM_PROMPT, promptText, maxTokens, signal);
}

export interface CompactionResult {
  summary: string;
  firstKeptEntryId: string;
  tokensBefore: number;
  usage?: Usage;
}

/** 主编排（pi 的 compact：轮前缀 + 历史摘要合并；文件段落已去）。 */
export async function compact(
  preparation: CompactionPreparation,
  summarize: SummarizeFn,
  customInstructions?: string,
  signal?: AbortSignal,
): Promise<CompactionResult> {
  const {
    firstKeptEntryId,
    messagesToSummarize,
    turnPrefixMessages,
    isSplitTurn,
    tokensBefore,
    previousSummary,
    settings,
  } = preparation;

  let summary: string;
  let usage: Usage | undefined;

  if (isSplitTurn && turnPrefixMessages.length > 0) {
    let historyText = previousSummary ?? "No prior history.";
    let historyUsage: Usage | undefined;
    if (messagesToSummarize.length > 0) {
      const historyResult = await generateSummaryWithUsage(
        messagesToSummarize,
        settings,
        summarize,
        customInstructions,
        previousSummary,
        signal,
      );
      historyText = historyResult.text;
      historyUsage = historyResult.usage;
    }
    const turnPrefixResult = await generateTurnPrefixSummary(
      turnPrefixMessages,
      settings,
      summarize,
      signal,
    );
    summary = `${historyText}\n\n---\n\n**Turn Context (split turn):**\n\n${turnPrefixResult.text}`;
    usage = historyUsage ?? turnPrefixResult.usage;
  } else {
    const result = await generateSummaryWithUsage(
      messagesToSummarize,
      settings,
      summarize,
      customInstructions,
      previousSummary,
      signal,
    );
    summary = result.text;
    usage = result.usage;
  }

  if (!firstKeptEntryId) {
    throw new Error("压缩切点条目缺少 id——会话存储可能损坏");
  }
  return { summary, firstKeptEntryId, tokensBefore, usage };
}
