/**
 * 日志：运行日志 + 审计日志（两条独立通道）。
 *
 * 与 Python 版对齐（app.py 的 logging 配置）：
 *   运行日志 `logs/agent_server.log`：`%(asctime)s %(levelname)s %(message)s`
 *   审计日志 `logs/audit.log`：`%(asctime)s %(message)s`，行格式
 *     `player=%s ok=%s tools=%s elapsed=%dms reply_len=%d q=[%s]`
 *   其中 ok 用 Python 的 True/False、tools 用 Python 列表 repr（['a', 'b']）——
 *   保持既有 grep/排查习惯不失效。
 *
 * 落盘方式：缓冲 + 定时批量 appendFileSync（200ms 或积压 2000 行即冲刷）。
 * 为什么改：逐条 appendFileSync 实测 0.26ms/行、每轮 3 行，1000 并发时白占单核约 5%，
 * 而且它跑在事件循环上，会直接抬高所有玩家的延迟。代价是进程被强杀时最多丢 200ms 日志；
 * 正常退出（SIGINT/SIGTERM）会调 flushLogs() 冲刷干净。
 */
import { appendFileSync, mkdirSync } from "node:fs";
import process from "node:process";

import { AUDIT_FILE, LOG_DIR, LOG_FILE } from "./config.js";

/** 诊断日志开关（AGENT_DEBUG=1）：排查上下文装配等问题时打开。 */
const DEBUG_ENABLED = process.env.AGENT_DEBUG === "1";

let ready = false;

function ensureDir(): void {
  if (ready) return;
  mkdirSync(LOG_DIR, { recursive: true });
  ready = true;
}

/** 冲刷间隔（ms）：够小以保留排障实时性，够大以摊薄 fsync 次数。 */
const FLUSH_INTERVAL_MS = 200;
/** 待写行数上限：日志风暴时先落盘，不让内存无限涨。 */
const MAX_PENDING_LINES = 2000;

/** 按文件缓冲的待写行（运行日志与审计日志各自一条队列）。 */
const pending = new Map<string, string[]>();
let flushTimer: NodeJS.Timeout | null = null;

/** 立即冲刷全部缓冲（退出钩子/测试用）。 */
export function flushLogs(): void {
  if (flushTimer) {
    clearTimeout(flushTimer);
    flushTimer = null;
  }
  for (const [file, lines] of pending) {
    if (lines.length === 0) continue;
    try {
      appendFileSync(file, lines.join("\n") + "\n", "utf8");
    } catch {
      // 日志写失败绝不能影响服务主流程
    }
  }
  pending.clear();
}

function scheduleFlush(): void {
  if (flushTimer) return;
  flushTimer = setTimeout(() => {
    flushTimer = null;
    flushLogs();
  }, FLUSH_INTERVAL_MS);
  flushTimer.unref(); // 不阻止进程退出
}

/** `2026-10-18 13:14:56,123`（本地时间，毫秒用逗号，格式同 Python logging） */
function stamp(): string {
  const d = new Date();
  const p = (n: number, w = 2) => String(n).padStart(w, "0");
  return (
    `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ` +
    `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())},${p(d.getMilliseconds(), 3)}`
  );
}

function write(file: string, line: string): void {
  ensureDir();
  const lines = pending.get(file);
  if (lines) lines.push(line);
  else pending.set(file, [line]);
  if ((pending.get(file)?.length ?? 0) >= MAX_PENDING_LINES) {
    flushLogs();
    return;
  }
  scheduleFlush();
}

function emit(level: string, message: string, err?: unknown): void {
  const text = err === undefined ? message : `${message}\n${stackOf(err)}`;
  const line = `${stamp()} ${level} ${text}`;
  write(LOG_FILE, line);
  // 控制台同步输出（对应 Python 的 StreamHandler），便于开发期观察
  if (level === "ERROR" || level === "WARNING") console.error(line);
  else console.log(line);
}

function stackOf(err: unknown): string {
  if (err instanceof Error) return err.stack ?? `${err.name}: ${err.message}`;
  return String(err);
}

export const log = {
  info(message: string): void {
    emit("INFO", message);
  },
  warn(message: string, err?: unknown): void {
    emit("WARNING", message, err);
  },
  error(message: string, err?: unknown): void {
    emit("ERROR", message, err);
  },
  /** 诊断日志：仅当 AGENT_DEBUG=1 时写（上下文装配/prompt 组成等排查用，不进审计）。 */
  debug(message: string): void {
    if (!DEBUG_ENABLED) return;
    write(LOG_FILE, `${stamp()} DEBUG ${message}`);
  },
  /** 是否开启诊断日志（供调用方决定是否做昂贵的字符串拼接）。 */
  debugEnabled: DEBUG_ENABLED,
};

/** Python 布尔口径（审计行/兼容 grep） */
export const pyReprBool = (v: boolean): string => (v ? "True" : "False");

/** Python 列表口径：['a', 'b']（字符串元素无数值/布尔时的 repr 形态一致） */
export function pyReprList(items: readonly string[]): string {
  return `[${items.map((s) => `'${s.replace(/'/g, "\\'")}'`).join(", ")}]`;
}

/**
 * 一轮 Agent 交互一条审计行（学 ZCode 操作留痕）：
 * 谁问了什么、调了什么工具、耗时与结果概况。
 */
export function audit(
  playerId: number,
  question: string,
  tools: readonly string[],
  elapsedMs: number,
  replyLen: number,
  ok: boolean,
): void {
  const q = (question ?? "").slice(0, 80).replace(/\n/g, " ");
  write(
    AUDIT_FILE,
    `${stamp()} player=${playerId} ok=${pyReprBool(ok)} tools=${pyReprList(tools)} ` +
      `elapsed=${elapsedMs}ms reply_len=${replyLen} q=[${q}]`,
  );
}
