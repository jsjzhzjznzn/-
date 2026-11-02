/**
 * pi 钩子装配（对应 graph.py 的 ingest / agent / respond 三个节点 + context.build_llm_input）。
 *
 * prepareRequest：每次 provider 请求前重建「规范上下文」（canonical context）——
 *   人设 + 长期记忆 + 摘要 + 玩家快照 + 时效提醒 + 会话消息（方案 §13.2 块序）。
 *   注意：pi 的系统提示与工具声明都挂在转写本的 system 消息上，所以这里必须自己
 *   带上一条声明了 toolsAdded 的首条 system 消息，否则模型会看不到工具
 *   （实测 agent-loop.declareToolChanges 的 diff 语义：声明齐全才不再插系统消息）。
 *
 * finishTurn / afterToolCall / beforeToolCall 在 P2/P6 补（本文件是它们唯一的挂载点）。
 */
import type { AgentMessage, AgentTool, AgentTurnContext } from "@earendil-works/pi-agent-core";
import { toToolDeclaration, type SystemMessage } from "@earendil-works/pi-ai";

import {
  FRESHNESS_KEYWORDS,
  FRESHNESS_NUDGE_BLOCK,
  FRESHNESS_REMINDER,
  GAME_DATA_KEYWORDS,
  MAX_FRESHNESS_NUDGES,
  MAX_REFLECTIONS,
  MAX_TOOL_ROUNDS,
  NON_DATA_TOOLS,
} from "../core/config.js";
import { log } from "../core/logger.js";
import { contentText, lastUserText } from "../core/text.js";
import { getLongterm } from "../db/longtermRepo.js";
import { getStyle } from "../db/prefsRepo.js";
import { projectConversation } from "../session/projection.js";
import type { SessionEntry } from "../session/types.js";
import { maybeCompact } from "../session/triggers.js";
import { critiqueBlockText, reflect } from "./critic.js";
import { buildSystemPrompt, longtermBlock, playerContextBlock } from "./persona.js";
import type { RunState } from "./runState.js";

export interface SessionView {
  playerId: number;
  tools: AgentTool<any>[];
  /** 会话条目（权威）：内存镜像，随 message_end 追加。 */
  entries: SessionEntry[];
  /** 当前 run（每次 prompt 前重建）。 */
  run: RunState;
}

/** 命中时效关键词：本轮问题涉及会变的实时数据。 */
export function hintsFreshness(text: string): boolean {
  return FRESHNESS_KEYWORDS.some((keyword) => keyword && text.includes(keyword));
}

/** 命中游戏实时数据关键词：问的是「必须查库才答得准」的东西（D10 强制查库的判据）。 */
export function hitsGameData(text: string): boolean {
  return GAME_DATA_KEYWORDS.some((keyword) => keyword && text.includes(keyword));
}

function systemBlock(content: string): SystemMessage {
  return { role: "system", content, timestamp: Date.now() };
}

/** 组装规范上下文（等价 Python 的 build_llm_input + 时效提醒注入）。 */
export function buildCanonicalContext(view: SessionView): AgentMessage[] {
  const { playerId, tools, entries, run } = view;

  const projected = projectConversation(entries);
  const carriedSystem = projected.filter((message) => message.role === "system");
  const conversation = projected.filter((message) => message.role !== "system");

  // D12 缓存判据：本次请求能看到历史工具数据 → 答案可能引用了会变的数据，禁止写进相似问题缓存
  run.hadToolContext = projected.some((message) => message.role === "toolResult");

  // 时效性提醒：玩家本轮消息命中数据类关键词时，追加提醒压过「历史对话里查过」的复读惯性
  const userText = lastUserText(conversation);
  const freshness = hintsFreshness(userText);
  run.freshnessHinted = freshness;

  const style = getStyle(playerId);
  const longterm = getLongterm(playerId);

  const messages: AgentMessage[] = [
    // 首条 system：人设 + 工具声明（pi 的 prompt/tools 由转写本的 system 消息承载）
    {
      role: "system",
      content: buildSystemPrompt(style),
      toolsAdded: tools.map(toToolDeclaration),
      timestamp: Date.now(),
    },
  ];
  if (longterm.length > 0) messages.push(longtermBlock(longterm));
  messages.push(...carriedSystem);
  if (Object.keys(run.playerContext ?? {}).length > 0) {
    messages.push(systemBlock(playerContextBlock(run.playerContext)));
  }
  if (freshness) messages.push(systemBlock(FRESHNESS_REMINDER));
  // 【反思】批评：质检不过时注入，逼模型立刻重答（本轮内有效，不落库 —— 见方案 §19.1 D8）
  if (run.reflectionCritique) messages.push(systemBlock(critiqueBlockText(run.reflectionCritique)));
  // 【反思】时效强制查库（D10）：问实时数据却没查库时注入；与质检批评互斥（共用同一重答预算）
  if (run.freshnessCritique) messages.push(systemBlock(run.freshnessCritique));
  messages.push(...conversation);

  // 诊断（AGENT_DEBUG=1）：上下文组成——system 块数应恒为人设+可选块，绝不随轮次增长
  // （若工具声明没被自己的首条 system 消息覆盖，pi 会每轮再插一条系统消息，这里能立刻看出）
  if (log.debugEnabled) {
    const roles = messages.map((m) => m.role).join(",");
    const systemCount = messages.filter((m) => m.role === "system").length;
    log.debug(
      `prepareRequest player=${playerId} total=${messages.length} system=${systemCount} ` +
        `fresh=${freshness} q=${JSON.stringify(userText.slice(0, 24))} roles=[${roles}]`,
    );
  }
  return messages;
}

/** prepareRequest 钩子工厂（异步：可能触发一次上下文压缩）。 */
export function makePrepareRequest(view: () => SessionView) {
  return async (
    request: { context: { messages: AgentMessage[] }; model: unknown; thinkingLevel: unknown },
    signal?: AbortSignal,
  ) => {
    const session = view();
    void request;
    // 压缩判定（每轮一次；失败不阻断本轮）——必须在投影之前，
    // 否则本轮用的还是压缩前的全量上下文
    await maybeCompact(session.playerId, session.entries, session.run, signal);
    return { context: { messages: buildCanonicalContext(session), tools: session.tools } };
  };
}

/** 取助手最后一条可见文本（出口用；对应 respond 节点的取值口径）。 */
export function lastAssistantText(entries: readonly SessionEntry[]): string {
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const entry = entries[i]!;
    if (entry.type !== "message") continue;
    const message = entry.message;
    if (message.role === "assistant") {
      const text = contentText(message.content);
      if (text.trim()) return text;
    }
  }
  return "";
}

/** 取玩家本轮的问题（投影后的最后一条 user 消息）。 */
export function lastUserQuestion(entries: readonly SessionEntry[]): string {
  return lastUserText(projectConversation(entries));
}

/**
 * finishTurn 钩子工厂：生成后反思质检（对照工具结果），不过则注入批评并重答一次。
 *
 * 触发条件（全过才评，与 Python 版 critic_node 一致）：
 *   上一条是文本回答；重答预算未耗尽（<1）；工具轮预算未耗尽（<5）；本轮用过数据类工具
 *   （set_agent_style 等偏好工具不算——闲聊/切风格零成本跳过）。
 *
 * 另：工具轮达到 MAX_TOOL_ROUNDS 时直接 {action:"end"} 收尾（预算兜底，见下方首段），
 * 由 runtime 出口回 ROUNDS_EXHAUSTED_REPLY —— 不能再靠 55s 的 run 超时 abort 当刹车。
 *
 * pi 的语义要点：`{action:"continue"}` = 保证再来一次 provider 请求（即"重答"）；
 * error/aborted 回合是硬退出，必须自己守卫（否则会在失败回合上无意义地重试）。
 */
export function makeFinishTurn(view: () => SessionView) {
  return async (turn: AgentTurnContext, signal?: AbortSignal) => {
    const session = view();
    const run = session.run;
    const message = turn.message;

    if (message.stopReason === "error" || message.stopReason === "aborted") return undefined;

    // 工具轮硬上限：预算耗尽就结束循环（否则只能等 55s 的 run 超时 abort）。
    // run.toolRounds 要到 turn_end 才自增，这里用 turn.toolResults 补上当前这一轮。
    if (turn.toolResults.length > 0 && run.toolRounds + 1 >= MAX_TOOL_ROUNDS) {
      run.roundsExhausted = true;
      log.warn(
        `工具轮耗尽 player=${session.playerId} rounds=${run.toolRounds + 1}/${MAX_TOOL_ROUNDS}，` +
          "结束循环，由出口回兜底文案",
      );
      return { action: "end" as const };
    }

    if (run.reflectionCount >= MAX_REFLECTIONS) return undefined;
    if (run.toolRounds >= MAX_TOOL_ROUNDS) return undefined;

    // 只评「作答轮」：这一轮必须产出了文本且没有工具调用
    // （pi 的 turn 里工具结果属于执行工具那一轮，所以对照数据取自 run.dataToolResults）
    if (message.content.some((block) => block.type === "toolCall")) return undefined;

    const answer = contentText(message.content);
    if (!answer.trim()) return undefined;

    // 【D10】时效强制查库：问的是游戏实时数据、却整轮没调用任何数据工具——这正是
    // 「时效提醒只是软约束 + 质检只在用过工具时触发」之间的缝，也是"复述旧数字"的高发区。
    // 触发时同时自增 reflectionCount（与质检共享重答预算）→ 每轮最多只多一次 LLM 调用。
    if (
      run.freshnessNudges < MAX_FRESHNESS_NUDGES &&
      run.dataToolResults.length === 0 &&
      hitsGameData(lastUserQuestion(session.entries))
    ) {
      run.freshnessNudges += 1;
      run.reflectionCount += 1;
      run.freshnessCritique = FRESHNESS_NUDGE_BLOCK;
      run.onReset?.(); // 流式端点：作废已外流的首答
      log.info(`时效强制查库 player=${session.playerId}（整轮未调用数据工具，强制重答一次）`);
      return { action: "continue" as const };
    }

    if (run.dataToolResults.length === 0) return undefined;

    const question = lastUserQuestion(session.entries);
    // 已知事实：把长期记忆交给质检器（否则「蛋哥」这类合法称呼会被判成编造——
    // 2026-10-18 实测误判，见 critic.ReflectOptions 注释）
    const knownFacts = getLongterm(session.playerId).map(([key, content]) => `${key}：${content}`);
    const verdict = await reflect(question, run.dataToolResults, answer, { knownFacts }, signal);
    run.reflectionRuns += 1;

    if (verdict.passed) return undefined;

    run.reflectionCount += 1;
    run.reflectionCritique = verdict.reason;
    run.onReset?.(); // 流式端点：作废已外流的首答
    log.info(`反思重答 player=${session.playerId} 病因=${verdict.reason}`);
    return { action: "continue" as const };
  };
}

export { systemBlock };
