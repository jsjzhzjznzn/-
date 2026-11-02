/**
 * HTTP 读写小工具（与路由分离，避免端点与 server 之间的循环导入）。
 */
import type { IncomingMessage, ServerResponse } from "node:http";

import { MAX_BODY_BYTES } from "../core/config.js";

/** JSON 响应（不转义非 ASCII，等价 Python 的 ensure_ascii=False）。 */
export function sendJson(res: ServerResponse, status: number, body: unknown): void {
  const text = JSON.stringify(body);
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": Buffer.byteLength(text),
  });
  res.end(text);
}

export async function readBody(req: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    const buf = chunk as Buffer;
    size += buf.length;
    if (size > MAX_BODY_BYTES) throw new Error("request body too large");
    chunks.push(buf);
  }
  return Buffer.concat(chunks).toString("utf8");
}

/** 读 JSON 请求体（空体=空对象）。 */
export async function readJson<T>(req: IncomingMessage): Promise<T> {
  const text = await readBody(req);
  if (!text.trim()) return {} as T;
  return JSON.parse(text) as T;
}

/** 请求体校验失败时的统一答复（400）。 */
export function badRequest(res: ServerResponse, detail: string): void {
  sendJson(res, 400, { detail });
}
