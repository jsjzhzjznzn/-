/**
 * 工具调用防护层：幂等 + 限频（逐字搬 tools/guard.py，方案 §10）。
 *
 * 堵的洞：LLM 会重复调用同一工具（ReAct 多轮复读、生成重试）；只读工具重复只是浪费往返，
 * 但写工具（claim_mail_attachments 这类改状态的）重复执行就是真事故。
 *
 * 三个设计决策（与 Python 版一致）：
 * 1. **幂等只覆盖写工具**（WRITE_TOOLS）。只读工具刻意不去重：数据会变，重查才对。
 * 2. 幂等作用域 = **单次对话请求**（playerId + requestId）。跨对话不去重——玩家明确要求
 *    重做时不该被挡（真正的防重仍在 .NET 侧业务事务里，如邮件 item_mask）。
 * 3. 幂等只在**执行未抛异常**时落账（超时/异常会撤掉占位、允许重试），并留"执行中"占位防重入。
 *
 * 状态全在模块级（进程内存）：工具表每次构建都会重建实例，状态挂实例上活不过一个节点。
 * 本文件**不含**单轮预算——那是 RunState 的事（对应 Python 里 graph.tools_node 的职责）。
 */
import {
  IDEMPOTENCY_TTL_SECONDS,
  RATE_MAX_CALLS,
  RATE_WINDOW_SECONDS,
  WRITE_RATE_MAX_CALLS,
  WRITE_TOOLS,
} from "../core/config.js";
import { log } from "../core/logger.js";

const WINDOW_MS = RATE_WINDOW_SECONDS * 1000;
const TTL_MS = IDEMPOTENCY_TTL_SECONDS * 1000;

/** 幂等键 → [过期时刻(monotonic ms), 结果（null = 执行中占位）] */
const idempotency = new Map<string, [number, string | null]>();
/** player_id → 全部工具调用时刻窗口（monotonic ms） */
const callWindows = new Map<number, number[]>();
/** player_id → 写工具调用时刻窗口 */
const writeWindows = new Map<number, number[]>();

/** 单调时钟（等价 Python 的 time.monotonic；System 时钟回拨也不受骗）。 */
function monotonicMs(): number {
  return performance.now();
}

/** 参数的稳定序列化：字段顺序差异不能算成两次不同的操作。 */
function canonical(args: unknown): string {
  try {
    return JSON.stringify(sortKeys(args)) ?? "null";
  } catch {
    return String(args);
  }
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const key of Object.keys(value as Record<string, unknown>).sort()) {
      out[key] = sortKeys((value as Record<string, unknown>)[key]);
    }
    return out;
  }
  return value;
}

function idemKey(playerId: number, requestId: string, name: string, args: unknown): string {
  return `${playerId}|${requestId}|${name}|${canonical(args)}`;
}

/** 惰性清理过期记录（只在写工具闸门处调用；表规模=单次请求的写操作数）。 */
function sweepIdempotency(now: number): void {
  for (const [key, [deadline]] of idempotency) {
    if (deadline <= now) idempotency.delete(key);
  }
}

/** 滑窗判定：淘汰窗口外记录 → 未满则计入并放行。返回 [放行?, 需等待秒数]。 */
function passWindow(window: number[], limit: number, now: number): [boolean, number] {
  while (window.length > 0 && now - window[0]! >= WINDOW_MS) window.shift();
  if (window.length >= limit) {
    const wait = Math.max(1, Math.ceil(RATE_WINDOW_SECONDS - (now - window[0]!) / 1000));
    return [false, wait];
  }
  window.push(now);
  return [true, 0];
}

export interface GuardOutcome {
  /** 幂等键（放行的写工具非空；只读工具/未启用幂等时为空串）。 */
  key: string;
  /** 非 null = 被拦，文本要原样回给 LLM（正常文本观察，不是异常）。 */
  blocked: string | null;
}

/**
 * 一次请求的防护闸门（幂等 + 限频）。实例无状态，状态在模块级表里。
 */
export class ToolGuard {
  constructor(
    private readonly playerId: number,
    private readonly requestId: string = "",
  ) {}

  /**
   * 执行前闸门：
   * - 放行：幂等键非空（写工具已占位"执行中"）或为空（只读工具），blocked = null；
   * - 拦截：key 为空串，blocked = 要回给 LLM 的说明（限频 / 幂等命中）。
   */
  before(name: string, args: unknown): GuardOutcome {
    const now = monotonicMs();

    // ① 限频先于幂等：重复调用同样是压力，不能因为"反正是重复"就放过去
    const callWindow = callWindows.get(this.playerId) ?? [];
    if (!callWindows.has(this.playerId)) callWindows.set(this.playerId, callWindow);
    const [allowed, wait] = passWindow(callWindow, RATE_MAX_CALLS, now);
    if (!allowed) {
      log.warn(
        `限频拦截 player=${this.playerId} tool=${name}（${RATE_WINDOW_SECONDS}s 窗口内已达 ${RATE_MAX_CALLS} 次，需等 ${wait}s）`,
      );
      return {
        key: "",
        blocked:
          `工具调用过于频繁（每 ${RATE_WINDOW_SECONDS} 秒最多 ${RATE_MAX_CALLS} 次），` +
          `请 ${wait} 秒后再试，或先用已有信息回答玩家。`,
      };
    }

    if (!WRITE_TOOLS.has(name)) return { key: "", blocked: null }; // 只读工具：不幂等，只吃整体限频

    const writeWindow = writeWindows.get(this.playerId) ?? [];
    if (!writeWindows.has(this.playerId)) writeWindows.set(this.playerId, writeWindow);
    const [writeAllowed, writeWait] = passWindow(writeWindow, WRITE_RATE_MAX_CALLS, now);
    if (!writeAllowed) {
      log.warn(
        `写限频拦截 player=${this.playerId} tool=${name}（${RATE_WINDOW_SECONDS}s 窗口内已达 ${WRITE_RATE_MAX_CALLS} 次，需等 ${writeWait}s）`,
      );
      return {
        key: "",
        blocked:
          `写操作过于频繁（每 ${RATE_WINDOW_SECONDS} 秒最多 ${WRITE_RATE_MAX_CALLS} 次），` +
          `请 ${writeWait} 秒后再试，并告知玩家稍等。`,
      };
    }

    // request_id 缺失时**不做幂等**（fail-safe）：宁可不保护，也绝不能把不同请求的调用误判成同一次
    if (!this.requestId) return { key: "", blocked: null };

    sweepIdempotency(now);
    const key = idemKey(this.playerId, this.requestId, name, args);
    const entry = idempotency.get(key);
    if (entry) {
      const [, result] = entry;
      if (result === null) {
        log.info(`幂等拦截（执行中）player=${this.playerId} tool=${name}`);
        return { key: "", blocked: "该操作本次对话中正在执行，请勿重复发起。" };
      }
      log.info(`幂等命中（未重复执行）player=${this.playerId} tool=${name}`);
      return { key: "", blocked: `（该操作本次对话已执行过，未重复执行）先前结果：${result}` };
    }

    idempotency.set(key, [now + TTL_MS, null]); // 占位：执行中
    return { key, blocked: null };
  }

  /** 执行成功：把结果落进幂等表，后续同请求的重复调用直接复用。 */
  after(key: string, result: string): void {
    if (!key) return;
    idempotency.set(key, [monotonicMs() + TTL_MS, result]);
  }

  /** 执行失败/超时：撤掉占位，允许重试（失败永不进幂等表）。 */
  abort(key: string): void {
    if (!key) return;
    idempotency.delete(key);
  }
}

/** 清空全部防护状态（测试用；等价于重启进程）。 */
export function resetGuardState(): void {
  idempotency.clear();
  callWindows.clear();
  writeWindows.clear();
}
