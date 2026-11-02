/**
 * 出站 HTTP 客户端（→ .NET 内部 API 127.0.0.1:8897）。
 *
 * 与 Python 版对齐（tools/game_tools.py）：
 *  - 带共享令牌头 `X-Agent-Token`（未配置则不带）；
 *  - **绝不走系统代理**：内部 API 恒为回环，Windows 上开着代理时走代理会全部 502
 *    （Python 用 httpx(trust_env=False) 规避；Node 的 fetch/undici 默认不读
 *    HTTP_PROXY，这里再加一层防御：显式不设置 dispatcher/agent，并在注释里锁死约定）；
 *  - 失败统一转「结构化错误文本」交给 LLM 转述，绝不让整轮挂掉；
 *  - 错误类型名向 httpx 口径靠（ConnectError/TimeoutException/HTTPStatusError），
 *    保持日志与 LLM 观察的措辞稳定。
 */
import { INTERNAL_TOKEN } from "./config.js";

export class HttpFailure extends Error {
  constructor(
    message: string,
    readonly kind: "ConnectError" | "TimeoutException" | "HTTPStatusError",
  ) {
    super(message);
    this.name = kind;
  }
}

function authHeaders(): Record<string, string> {
  return INTERNAL_TOKEN ? { "X-Agent-Token": INTERNAL_TOKEN } : {};
}

function classify(err: unknown, status?: number): never {
  if (status !== undefined) {
    throw new HttpFailure(`HTTP ${status}`, "HTTPStatusError");
  }
  if (err instanceof Error && (err.name === "AbortError" || err.name === "TimeoutError")) {
    throw new HttpFailure("请求超时", "TimeoutException");
  }
  throw new HttpFailure(err instanceof Error ? err.message : String(err), "ConnectError");
}

/** GET 返回响应文本；失败抛 HttpFailure（调用方决定降级文案）。 */
export async function getText(url: string, timeoutMs: number, signal?: AbortSignal): Promise<string> {
  const timeout = AbortSignal.timeout(timeoutMs);
  const merged = signal ? AbortSignal.any([signal, timeout]) : timeout;
  let res: Response;
  try {
    res = await fetch(url, { headers: authHeaders(), signal: merged, redirect: "follow" });
  } catch (err) {
    classify(err);
  }
  if (!res.ok) classify(new Error(`HTTP ${res.status}`), res.status);
  return await res.text();
}

/** POST JSON 返回响应文本；失败抛 HttpFailure。 */
export async function postJsonText(
  url: string,
  body: unknown,
  timeoutMs: number,
  signal?: AbortSignal,
): Promise<string> {
  const timeout = AbortSignal.timeout(timeoutMs);
  const merged = signal ? AbortSignal.any([signal, timeout]) : timeout;
  let res: Response;
  try {
    res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify(body),
      signal: merged,
    });
  } catch (err) {
    classify(err);
  }
  if (!res.ok) classify(new Error(`HTTP ${res.status}`), res.status);
  return await res.text();
}

/** GET JSON；失败抛异常（调用方决定降级口径，如登录简报的容错段）。 */
export async function getJson<T>(url: string, timeoutMs: number, signal?: AbortSignal): Promise<T> {
  const text = await getText(url, timeoutMs, signal);
  return JSON.parse(text) as T;
}
