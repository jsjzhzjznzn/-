/**
 * HTTP 服务（node:http）：路由 / 鉴权 / 优雅退出。
 *
 * 契约冻结（方案 §4.1）：仅监听 127.0.0.1:8898（开发期可用 AGENT_PORT 改端口），
 * 端点与响应形状与 Python 版逐字一致；未实现的端点暂时返回 501 + JSON，
 * 由 P2–P4 逐阶段替换为真实实现。
 */
import http from "node:http";
import type { IncomingMessage, ServerResponse } from "node:http";
import path from "node:path";

import { DB_PATH, HOST, LLM_MODEL, PORT, ROOT } from "../core/config.js";
import { flushLogs, log } from "../core/logger.js";
import { abortAllSessions, sweepIdleSessions } from "../agent/runtime.js";
import { debugSnapshot } from "../agent/debug.js";
import { closeDb, openDb } from "../db/sqlite.js";
import { authDescription, tokenOk } from "./auth.js";
import { handleBrief } from "./endpoints/brief.js";
import { handleChat } from "./endpoints/chat.js";
import { handleChatStream } from "./endpoints/chatStream.js";
import { handleHistory } from "./endpoints/history.js";
import { handleProactive } from "./endpoints/proactive.js";
import { sendJson } from "./io.js";

export type Handler = (req: IncomingMessage, res: ServerResponse, url: URL) => Promise<void> | void;
type Method = "GET" | "POST";

const server = http.createServer((req, res) => {
  void handle(req, res).catch((err) => {
    log.error(`请求处理异常 ${req.method} ${req.url}`, err);
    if (!res.headersSent) sendJson(res, 500, { ok: false, error: "internal_error" });
    else res.end();
  });
});

/** 空闲回收定时器（启动时创建，退出时清理）。 */
let sweepTimer: NodeJS.Timeout | null = null;

/** 启动时注册的路由表（由 bootstrap 注入各端点，避免模块循环导入）。 */
const routes = new Map<string, Partial<Record<Method, Handler>>>();

export function registerRoute(path: string, method: Method, handler: Handler): void {
  const byMethod = routes.get(path) ?? {};
  byMethod[method] = handler;
  routes.set(path, byMethod);
}

const DEBUG_PREFIX = "/agent/debug/";

async function handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const url = new URL(req.url ?? "/", `http://${HOST}:${PORT}`);
  const pathname = url.pathname;
  const method = (req.method ?? "GET").toUpperCase() as Method;
  const started = Date.now();

  // 鉴权：/agent/* 全部校验（/health 不校验，与 Python 版一致）
  if (pathname.startsWith("/agent/")) {
    const provided = req.headers["x-agent-token"];
    if (!tokenOk(Array.isArray(provided) ? provided[0] : provided)) {
      sendJson(res, 401, { detail: "invalid agent token" });
      return;
    }
  }

  let status = 404;
  try {
    if (pathname.startsWith(DEBUG_PREFIX) && method === "GET") {
      status = debug(res, url);
      return;
    }
    const byMethod = routes.get(pathname);
    if (!byMethod) {
      sendJson(res, 404, { detail: "Not Found" });
      return;
    }
    const handler = byMethod[method];
    if (!handler) {
      status = 405;
      sendJson(res, 405, { detail: "Method Not Allowed" });
      return;
    }
    status = 200;
    await handler(req, res, url);
  } finally {
    if (pathname !== "/health") {
      log.info(`http ${method} ${pathname} -> ${status} ${Date.now() - started}ms`);
    }
  }
}

/** /agent/debug：联调诊断——该玩家的记忆内部状态水位（字段名与 Python 版一致）。 */
function debug(res: ServerResponse, url: URL): number {
  const raw = url.pathname.slice(DEBUG_PREFIX.length);
  const playerId = Number.parseInt(raw, 10);
  if (!Number.isFinite(playerId)) {
    sendJson(res, 400, { detail: "invalid player_id" });
    return 400;
  }
  sendJson(res, 200, debugSnapshot(playerId));
  return 200;
}

/** 注册全部路由（P4 起：7 个端点全部实现）。 */
function registerRoutes(): void {
  registerRoute("/health", "GET", (_req, res) => {
    sendJson(res, 200, { status: "ok", model: LLM_MODEL });
  });
  registerRoute("/agent/chat", "POST", handleChat);
  registerRoute("/agent/chat/stream", "POST", handleChatStream);
  registerRoute("/agent/history", "POST", handleHistory);
  registerRoute("/agent/brief", "POST", handleBrief);
  registerRoute("/agent/proactive", "POST", handleProactive);
}

/** 空闲会话实例回收的巡检周期（30 分钟无活动即回收，条目都在 SQLite）。 */
const SWEEP_INTERVAL_MS = 5 * 60 * 1000;

/** 启动：开库 + 注册路由 + 监听 + 启动日志（文案与 Python 版一致）。 */
export function startServer(): http.Server {
  openDb();
  registerRoutes();
  // 空闲实例回收：内存有界，需要时从 SQLite 重建
  sweepTimer = setInterval(() => {
    try {
      sweepIdleSessions();
    } catch (err) {
      log.warn("空闲会话回收异常", err);
    }
  }, SWEEP_INTERVAL_MS);
  sweepTimer.unref();

  server.listen(PORT, HOST, () => {
    // 打印真实生效的库路径（AGENT_DB_PATH 可覆盖；原先恒打印默认相对路径，E2E 时误导过人）
    const dbLabel = DB_PATH.startsWith(ROOT)
      ? path.relative(ROOT, DB_PATH).replace(/\\/g, "/")
      : DB_PATH;
    log.info(`Agent 服务就绪 model=${LLM_MODEL} db=${dbLabel} auth=${authDescription}`);
    if (!authDescription.startsWith("token")) {
      log.warn("AGENT_INTERNAL_TOKEN 未配置：/agent/* 端点 fail-open（回环是唯一防线，建议配置）");
    }
  });
  return server;
}

/** 优雅退出：停止接收 → 中止在跑 run → 关库 → 退出。 */
export async function shutdown(signal: string): Promise<void> {
  log.info(`收到 ${signal}，开始优雅退出`);
  abortAllSessions(); // 在跑的 run 立刻中断（客户端会收到断流/错误）
  if (sweepTimer) clearInterval(sweepTimer);
  await new Promise<void>((resolve) => {
    server.close(() => resolve());
    // 兜底：5s 内没关干净也退出
    setTimeout(resolve, 5_000).unref();
  });
  closeDb();
  log.info("已退出");
  flushLogs(); // 缓冲日志的最后一次冲刷（进程即将退出）
}
