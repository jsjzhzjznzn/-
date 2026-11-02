/**
 * Agent 运行时：每个玩家一个 pi Agent 实例 + 串行队列 + 会话落盘 + 流式事件。
 *
 * 对应 Python 版里「LangGraph checkpointer（thread_id=player_{id}）」的角色，
 * 但权威在上面一层：session_entries（SQLite）是上下文的唯一真相，
 * prepareRequest 每轮从条目投影重建 context（方案 §6.3）。
 *
 * 每轮流程：
 *   1) 串行入队（同玩家不并发跑 run；pi 本身拒绝并发 prompt）
 *   2) 重置 RunState（request_id / 计数 / 玩家快照）
 *   3) agent.reset() → 丢开 pi 自己的历史，防止与权威投影漂移（内存也有界）
 *   4) agent.prompt(content)（带 run 级超时 → abort）
 *   5) 收集本轮 used_tools 与最终回复（出口裁剪），异常按端点契约抛出
 *
 * 流式：订阅事件把 text_delta 外流；助手消息带工具调用时通知端点作废前导文本（reset）。
 */
import { randomBytes } from "node:crypto";

import { Agent } from "@earendil-works/pi-agent-core";

import {
  MAX_CONCURRENT_RUNS,
  MAX_REPLY_CHARS,
  NON_DATA_TOOLS,
  ROUNDS_EXHAUSTED_REPLY,
  RUN_BUDGET_MS,
  RUN_GATE_WAIT_MS,
} from "../core/config.js";
import { log } from "../core/logger.js";
import { contentText } from "../core/text.js";
import { chatModel, streamFn } from "../llm/provider.js";
import { appendMessage, ensureState, listEntries } from "../session/store.js";
import { isPersistedMessage, type SessionEntry } from "../session/types.js";
import { lastAssistantText, makeFinishTurn, makePrepareRequest, type SessionView } from "./hooks.js";
import { buildSystemPrompt } from "./persona.js";
import { resetRunState, type RunState } from "./runState.js";
import { buildMainTools } from "./tools/index.js";

export interface ChatResult {
  reply: string;
  /** 本轮的 tool_call 名字（按出现顺序，含重复）。 */
  usedTools: string[];
  elapsedMs: number;
  /** 已完成的工具轮数（流式端点用它决定能否写问答缓存）。 */
  toolRounds: number;
  /** 本轮是否命中时效关键词（缓存双向跳过的判据）。 */
  freshnessHinted: boolean;
  /** 本轮请求上下文里是否含工具结果（D12：含则不许写缓存——答案可能复述了会变的数据）。 */
  hadToolContext: boolean;
}

/** 流式端点回调集合。 */
export interface StreamHandlers {
  delta(text: string): void;
  reset(): void;
  /** run 启动后回调，供端点登记「客户端断开 → abort」 */
  attached?(abort: () => void): void;
}

/**
 * 串行队列：同玩家不并发跑 run（pi 本身拒绝并发 prompt；串行也消除状态交错）。
 * 抽成独立函数是为了可单测「任务永不重叠」这条不变量（计时法证明不了）。
 */
export function createSerialQueue(): {
  enqueue<T>(task: () => Promise<T>): Promise<T>;
  readonly busy: boolean;
} {
  let tail: Promise<unknown> = Promise.resolve();
  let depth = 0;
  return {
    enqueue<T>(task: () => Promise<T>): Promise<T> {
      depth += 1;
      const next = tail.then(task, task);
      tail = next.then(
        () => {
          depth -= 1;
        },
        () => {
          depth -= 1;
        },
      );
      return next;
    },
    get busy(): boolean {
      return depth > 0;
    },
  };
}

/**
 * 进程内 run 全局闸（计数信号量 + 等待超时）。
 *
 * 与 per-player 串行队列正交：串行队列管「同一玩家不并发」，这里管「全进程不超过 N 个在跑」。
 * 槽位在真正开跑前才占用——排在前面那个玩家身后的请求不占全局槽位。
 */
export function createRunGate(limit: number): {
  acquire(timeoutMs: number): Promise<boolean>;
  release(): void;
  readonly active: number;
} {
  let active = 0;
  const waiters: Array<() => void> = [];
  return {
    async acquire(timeoutMs: number): Promise<boolean> {
      if (active < limit) {
        active += 1;
        return true;
      }
      return new Promise<boolean>((resolve) => {
        let settled = false;
        let timer: NodeJS.Timeout | undefined;
        function settle(ok: boolean): void {
          if (settled) return;
          settled = true;
          if (timer) clearTimeout(timer);
          const index = waiters.indexOf(wake);
          if (index >= 0) waiters.splice(index, 1);
          resolve(ok);
        }
        function wake(): void {
          settle(true);
        }
        timer = setTimeout(() => settle(false), timeoutMs);
        waiters.push(wake);
      });
    },
    release(): void {
      const next = waiters.shift();
      if (next) next(); // 槽位直接转交，active 不变
      else active -= 1;
    },
    get active(): number {
      return active;
    },
  };
}

const runGate = createRunGate(MAX_CONCURRENT_RUNS);

/** 占一个全局槽位再跑；抢不到就快速失败，不让请求堆到网关 60s 超时。 */
async function withRunSlot<T>(playerId: number, task: () => Promise<T>): Promise<T> {
  const acquired = await runGate.acquire(RUN_GATE_WAIT_MS);
  if (!acquired) {
    log.warn(
      `run 并发闸拒载 player=${playerId}（在跑 ${MAX_CONCURRENT_RUNS} 个，` +
        `等待 ${RUN_GATE_WAIT_MS}ms 未获槽位）`,
    );
    throw new Error("当前咨询的人有点多，请稍后再试");
  }
  try {
    return await task();
  } finally {
    runGate.release();
  }
}

interface PlayerSession {
  playerId: number;
  agent: Agent;
  view: SessionView;
  queue: ReturnType<typeof createSerialQueue>;
  lastUsedMs: number;
}

const sessions = new Map<number, PlayerSession>();
/** 空闲多久回收实例（条目都在 SQLite，随时可重建）。 */
const IDLE_DISPOSE_MS = 30 * 60 * 1000;

function lastEntryId(entries: readonly SessionEntry[]): string | null {
  return entries.length > 0 ? entries[entries.length - 1]!.id : null;
}

/** 创建会话（载入条目 + 建 Agent + 订阅事件）。 */
function createSession(playerId: number, playerContext: Record<string, unknown>): PlayerSession {
  ensureState(playerId);
  const entries = listEntries(playerId);
  const run: RunState = resetRunState(
    {
      playerId,
      requestId: "",
      toolRounds: 0,
      roundsExhausted: false,
      toolNames: [],
      reflectionCount: 0,
      reflectionRuns: 0,
      reflectionCritique: "",
      playerContext,
      freshnessHinted: false,
      freshnessNudges: 0,
      freshnessCritique: "",
      hadToolContext: false,
      roundToolCalls: 0,
      compactionChecked: false,
      dataToolResults: [],
    },
    playerId,
    "",
    playerContext,
  );

  const session: PlayerSession = {
    playerId,
    // 先占位，随后覆盖（Agent 创建需要 view 已经存在）
    view: { playerId, tools: [], entries, run },
    agent: undefined as unknown as Agent,
    queue: createSerialQueue(),
    lastUsedMs: Date.now(),
  };

  session.view.tools = buildMainTools({ playerId, run });

  session.agent = new Agent({
    initialState: {
      systemPrompt: buildSystemPrompt("gentle"), // 真实提示词每轮由 prepareRequest 重建
      model: chatModel,
      tools: session.view.tools,
      messages: [],
    },
    streamFn,
    sessionId: `player_${playerId}`, // 供 provider 侧的缓存（对齐 Python 的 thread_id）
    prepareRequest: makePrepareRequest(() => session.view),
    finishTurn: makeFinishTurn(() => session.view), // 生成后反思质检（不过则注入批评重答一次）
  });

  session.agent.subscribe((event) => {
    const currentRun = session.view.run;

    if (event.type === "turn_start") {
      // 单轮预算复位：pi 的一轮 = 一条助手消息 + 它的工具执行，对应 Python 版
      // graph.tools_node 的一次执行（单轮 8 个调用的闸门在此归零）
      currentRun.roundToolCalls = 0;
      return;
    }

    if (event.type === "message_update") {
      // 打字机数据源：只放行助手消息的正文增量（思考块/工具参数增量不外流）
      const update = event.assistantMessageEvent;
      if (event.message.role === "assistant" && update?.type === "text_delta" && update.delta) {
        currentRun.onDelta?.(update.delta);
      }
      return;
    }

    if (event.type === "message_end") {
      const message = event.message;
      if (isPersistedMessage(message)) {
        // 落盘（会话条目权威）+ 内存镜像（下一轮投影用）
        const entry = appendMessage(playerId, message, lastEntryId(session.view.entries));
        session.view.entries.push(entry);
      }
      if (message.role === "toolResult" && !NON_DATA_TOOLS.has(message.toolName)) {
        // 累积本轮的数据类工具结果（反思质检的对照数据；见 runState.dataToolResults 注释）
        currentRun.dataToolResults.push(`[${message.toolName}] ${contentText(message.content)}`);
      }
      if (message.role === "assistant") {
        let hasToolCall = false;
        for (const block of message.content) {
          if (block.type === "toolCall") {
            hasToolCall = true;
            currentRun.toolNames.push(block.name);
          }
        }
        // 工具轮：本轮已外流的「前导文本」作废（客户端收到 reset 清空打字机）
        if (hasToolCall) currentRun.onReset?.();
      }
      return;
    }

    if (event.type === "turn_end") {
      if (event.toolResults.length > 0) currentRun.toolRounds += 1;
    }
  });

  sessions.set(playerId, session);
  return session;
}

function getSession(playerId: number, playerContext: Record<string, unknown>): PlayerSession {
  const existing = sessions.get(playerId);
  if (existing) {
    existing.lastUsedMs = Date.now();
    return existing;
  }
  log.info(`创建会话实例 player=${playerId} 条目=${listEntries(playerId).length}`);
  return createSession(playerId, playerContext);
}

/** 同玩家串行：后到的请求排队（实现见 createSerialQueue）。 */
function enqueue<T>(session: PlayerSession, task: () => Promise<T>): Promise<T> {
  return session.queue.enqueue(task);
}

function newRequestId(): string {
  // 与 Python 的 secrets.token_hex(16) 等价（32 位十六进制）
  return randomBytes(16).toString("hex");
}

function lastAssistantMessage(entries: readonly SessionEntry[]) {
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const entry = entries[i]!;
    if (entry.type !== "message") continue;
    if (entry.message.role === "assistant") return entry.message;
  }
  return null;
}

/** 跑一次 run 并收集结果（流式/非流式共用；调用方保证已在串行队列里）。 */
async function executeRun(session: PlayerSession, playerId: number, content: string): Promise<ChatResult> {
  const started = Date.now();
  const run = session.view.run;
  session.agent.reset(); // 丢掉 pi 自己的历史；上下文每轮由 prepareRequest 重建

  const timer = setTimeout(() => {
    log.warn(`run 超时（${RUN_BUDGET_MS}ms）player=${playerId}，abort`);
    session.agent.abort();
  }, RUN_BUDGET_MS);

  try {
    await session.agent.prompt(content);
  } finally {
    clearTimeout(timer);
  }

  const last = lastAssistantMessage(session.view.entries);
  if (!last) throw new Error("agent 未产出助手回复");
  if (last.stopReason === "error") throw new Error(last.errorMessage ?? "LLM 调用失败");
  if (last.stopReason === "aborted") throw new Error(last.errorMessage ?? "run 被中断（超时或取消）");

  // 工具轮耗尽：最后一轮是工具调用、没有正文，不能取 lastAssistantText（那是更早的前导文本）
  let reply = run.roundsExhausted ? ROUNDS_EXHAUSTED_REPLY : lastAssistantText(session.view.entries);
  if (reply.length > MAX_REPLY_CHARS) reply = reply.slice(0, MAX_REPLY_CHARS) + "…";

  return {
    reply,
    usedTools: [...run.toolNames],
    elapsedMs: Date.now() - started,
    toolRounds: run.toolRounds,
    freshnessHinted: run.freshnessHinted,
    hadToolContext: run.hadToolContext,
  };
}

/** 业务对话（非流式）。 */
export function chat(
  playerId: number,
  content: string,
  playerContext: Record<string, unknown> = {},
): Promise<ChatResult> {
  const session = getSession(playerId, playerContext);
  return enqueue(session, () =>
    withRunSlot(playerId, () => {
      resetRunState(session.view.run, playerId, newRequestId(), playerContext);
      return executeRun(session, playerId, content);
    }),
  );
}

/** 业务对话（流式）：delta 逐段外流，工具轮/反思重答时发 reset 作废。 */
export function chatStream(
  playerId: number,
  content: string,
  playerContext: Record<string, unknown>,
  handlers: StreamHandlers,
): Promise<ChatResult> {
  const session = getSession(playerId, playerContext);
  return enqueue(session, () =>
    withRunSlot(playerId, async () => {
      const run = resetRunState(session.view.run, playerId, newRequestId(), playerContext);
      run.onDelta = (text) => handlers.delta(text);
      run.onReset = () => handlers.reset();
      handlers.attached?.(() => session.agent.abort());
      try {
        return await executeRun(session, playerId, content);
      } finally {
        run.onDelta = undefined;
        run.onReset = undefined;
      }
    }),
  );
}

/** 空闲实例回收（由服务启动时挂定时器）。 */
export function sweepIdleSessions(now = Date.now()): number {
  let disposed = 0;
  for (const [playerId, session] of sessions) {
    if (now - session.lastUsedMs < IDLE_DISPOSE_MS) continue;
    sessions.delete(playerId);
    disposed += 1;
  }
  if (disposed > 0) log.info(`回收空闲会话实例 ${disposed} 个`);
  return disposed;
}

/** 中止所有在跑的 run（优雅退出用）；返回中止数量。 */
export function abortAllSessions(): number {
  let aborted = 0;
  for (const session of sessions.values()) {
    try {
      session.agent.abort();
      aborted += 1;
    } catch {
      /* 已经结束的 run 忽略 */
    }
  }
  if (aborted > 0) log.info(`退出前中止在跑 run ${aborted} 个`);
  return aborted;
}

/** 测试用：清空实例缓存。 */
export function resetSessions(): void {
  sessions.clear();
}

/** 取进程内该玩家的 RunState（若实例还在）——/agent/debug 的水位读数用。 */
export function existingRunState(playerId: number): RunState | null {
  return sessions.get(playerId)?.view.run ?? null;
}
