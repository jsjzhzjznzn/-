/**
 * 契约测试（P7）：在进程内起一个真实 HTTP 服务（临时库、独立端口），
 * 逐条核对 7 个端点的**契约形状**与错误口径。
 *
 * 全程离线：只用不需要 LLM 的路径——
 *   /health、鉴权 401、请求体校验 400、history 分页、debug 形状、
 *   缓存命中（qa_cache 预置 → /agent/chat 与 /agent/chat/stream 走 cache 分支）、
 *   简报（8897 不可达 → 单源失败省略，仍有确定性文案）、
 *   proactive 未知 kind → 模板兜底（零 LLM）。
 */
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { afterAll, beforeAll, describe, expect, it } from "vitest";

const PORT = 8891;
const tempDir = mkdtempSync(path.join(tmpdir(), "afu-contract-"));
process.env.AGENT_DB_PATH = path.join(tempDir, "contract.db");
process.env.AGENT_PORT = String(PORT);
process.env.AGENT_INTERNAL_API_BASE = "http://127.0.0.1:1"; // 内部 API 不可达 → 简报单源降级
process.env.AGENT_INTERNAL_TOKEN = "contract-token";

type ServerModule = typeof import("../../src/http/server.js");
type ChatRepo = typeof import("../../src/db/chatRepo.js");
type QaCache = typeof import("../../src/db/qaCacheRepo.js");
type Sqlite = typeof import("../../src/db/sqlite.js");
let serverModule: ServerModule;
let chatRepo: ChatRepo;
let qaCache: QaCache;
let sqlite: Sqlite;

const PLAYER = 904;
const TOKEN = { "X-Agent-Token": "contract-token" };
const base = `http://127.0.0.1:${PORT}`;

async function post(pathname: string, body: unknown, headers: Record<string, string> = TOKEN) {
  const response = await fetch(`${base}${pathname}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
  return { status: response.status, json: (await response.json()) as Record<string, unknown>, response };
}

beforeAll(async () => {
  sqlite = await import("../../src/db/sqlite.js");
  sqlite.openDb();
  chatRepo = await import("../../src/db/chatRepo.js");
  qaCache = await import("../../src/db/qaCacheRepo.js");
  serverModule = await import("../../src/http/server.js");
  serverModule.startServer();
  await new Promise((resolve) => setTimeout(resolve, 300));

  // 预置：历史流水 + 问答缓存（缓存命中路径不需要 LLM）
  chatRepo.appendChat(PLAYER, "user", "讲个笑话");
  chatRepo.appendChat(PLAYER, "assistant", "为什么猫不用电脑？因为它只信自己的喵经验。");
  chatRepo.appendChat(PLAYER, "user", "再来一个");
  qaCache.cachePut(PLAYER, "讲个冷笑话", "讲个冷笑话", "为什么程序员分不清万圣节和圣诞节？因为 Oct 31 == Dec 25。");
});

afterAll(async () => {
  await serverModule?.shutdown("test");
  rmSync(tempDir, { recursive: true, force: true });
});

describe("GET /health", () => {
  it("形状固定为 {status, model}（不需要令牌）", async () => {
    const response = await fetch(`${base}/health`);
    expect(response.status).toBe(200);
    const json = (await response.json()) as Record<string, unknown>;
    expect(json.status).toBe("ok");
    expect(typeof json.model).toBe("string");
  });
});

describe("鉴权与请求体校验", () => {
  it("缺/错令牌 → 401 {detail}", async () => {
    const noToken = await post("/agent/chat", { player_id: PLAYER, content: "hi" }, {});
    expect(noToken.status).toBe(401);
    expect(noToken.json.detail).toBe("invalid agent token");

    const wrong = await post("/agent/chat", { player_id: PLAYER, content: "hi" }, { "X-Agent-Token": "nope" });
    expect(wrong.status).toBe(401);
  });

  it("player_id / content 非法 → 400", async () => {
    expect((await post("/agent/chat", { content: "hi" })).status).toBe(400);
    expect((await post("/agent/chat", { player_id: -1, content: "hi" })).status).toBe(400);
    expect((await post("/agent/chat", { player_id: PLAYER, content: "" })).status).toBe(400);
    expect((await post("/agent/chat", { player_id: PLAYER, content: "x".repeat(2001) })).status).toBe(400);
  });

  it("未知路径 404 / 方法不符 405", async () => {
    expect((await fetch(`${base}/nope`, { headers: TOKEN })).status).toBe(404);
    expect((await fetch(`${base}/health`, { method: "POST", headers: TOKEN })).status).toBe(405);
  });
});

describe("POST /agent/history", () => {
  it("形状 {ok, items:[{role,content,ts}], has_more}，按时间升序，role 0/1", async () => {
    const result = await post("/agent/history", { player_id: PLAYER, limit: 2 });
    expect(result.status).toBe(200);
    expect(result.json.ok).toBe(true);
    const items = result.json.items as { role: number; content: string; ts: number }[];
    expect(items).toHaveLength(2);
    // 预置的三条是 user/assistant/user，取最后 2 条 = [assistant(1), user(0)]
    expect(items.map((item) => item.role)).toEqual([1, 0]);
    expect(items.every((item) => typeof item.ts === "number")).toBe(true);
    expect(result.json.has_more).toBe(true); // 一共 3 条，取 2 条
  });

  it("before_ms 分页取更早一页", async () => {
    const first = await post("/agent/history", { player_id: PLAYER, limit: 2 });
    const items = first.json.items as { ts: number }[];
    const older = await post("/agent/history", { player_id: PLAYER, limit: 2, before_ms: items[0]!.ts });
    expect(older.json.ok).toBe(true);
    const olderItems = older.json.items as unknown[];
    expect(olderItems.length).toBeLessThanOrEqual(1);
  });
});

describe("POST /agent/chat（缓存命中路径，零 LLM）", () => {
  it("命中缓存 → used_tools=[\"cache\"]，并照常写历史", async () => {
    const result = await post("/agent/chat", { player_id: PLAYER, content: "讲个冷笑话" });
    expect(result.status).toBe(200);
    expect(result.json.ok).toBe(true);
    expect(result.json.used_tools).toEqual(["cache"]);
    expect(result.json.reply).toContain("Oct 31");
    expect(typeof result.json.elapsed_ms).toBe("number");
  });

  it("时效类问题不吃缓存（跳过读取，走真实链路 → 在无 LLM 时会失败但口径正确）", async () => {
    // 预置一条「背包」缓存，验证它不会被使用：时效关键词双向跳过
    qaCache.cachePut(PLAYER, "我背包里有什么", "我背包里有什么", "这条不该被用");
    const result = await post("/agent/chat", { player_id: PLAYER, content: "我背包里有什么" });
    expect(result.json.used_tools).not.toEqual(["cache"]);
    expect(String(result.json.reply ?? "")).not.toContain("这条不该被用");
  });
});

describe("POST /agent/chat/stream（缓存命中路径）", () => {
  it("Content-Type 为 ndjson，事件为 delta + done（终值即缓存文本）", async () => {
    const response = await fetch(`${base}/agent/chat/stream`, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...TOKEN },
      body: JSON.stringify({ player_id: PLAYER, content: "讲个冷笑话" }),
    });
    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toContain("application/x-ndjson");

    const text = await response.text();
    const events = text
      .split("\n")
      .filter((line) => line.trim())
      .map((line) => JSON.parse(line) as Record<string, unknown>);
    expect(events.map((event) => event.type)).toEqual(["delta", "done"]);
    expect(String(events[0]!.text)).toContain("Oct 31");
    expect(events[1]!.reply).toBe(events[0]!.text);
    expect(typeof events[1]!.elapsed_ms).toBe("number");
  });
});

describe("POST /agent/brief", () => {
  it("内部 API 不可达时仍给出确定性简报（失败段落省略）并归档", async () => {
    const result = await post("/agent/brief", { player_id: PLAYER });
    expect(result.status).toBe(200);
    expect(result.json.ok).toBe(true);
    expect(result.json.brief).toBe(
      "主人回来啦！阿福给你报个到——邮箱干干净净，没有未读。想聊什么随时叫我，我在的。",
    );
    expect(typeof result.json.created_ms).toBe("number");
  });
});

describe("POST /agent/proactive（模板兜底路径，零 LLM）", () => {
  it("未知 kind → 兜底文案 + used_llm=false，并归档 assistant 一行", async () => {
    const result = await post("/agent/proactive", { player_id: PLAYER, kind: 999, data: {} });
    expect(result.json.ok).toBe(true);
    expect(result.json.message).toBe("主人，有个小提醒，记得回来看看哦。");
    expect(result.json.used_llm).toBe(false);

    const history = await post("/agent/history", { player_id: PLAYER, limit: 1 });
    const items = history.json.items as { role: number; content: string }[];
    expect(items[0]!.role).toBe(1);
    expect(items[0]!.content).toBe("主人，有个小提醒，记得回来看看哦。");
  });
});

describe("GET /agent/debug", () => {
  it("未知玩家 → {exists:false}；有会话 → 水位字段齐全", async () => {
    const missing = await fetch(`${base}/agent/debug/999999`, { headers: TOKEN });
    expect((await missing.json()) as Record<string, unknown>).toEqual({ exists: false });

    const debugBody = await fetch(`${base}/agent/debug/${PLAYER}`, { headers: TOKEN });
    const debug = (await debugBody.json()) as Record<string, unknown>;
    expect(debug.exists).toBe(true);
    for (const key of [
      "message_count", "summarized_count", "rolling_summary_preview", "tool_round_count",
      "style", "longterm_memory", "qa_cache_count", "reflection_count", "reflection_runs",
      "reflection_critique",
      // Node 版新增：时效强制查库计数 + 压缩水位（B2）
      "freshness_nudges", "context_tokens", "context_window", "compact_threshold", "compact_enabled",
    ]) {
      expect(debug, `缺字段 ${key}`).toHaveProperty(key);
    }
    // 压缩阈值必须是「窗口 − reserve」，否则说明模型元数据又回退成了继承值（D11）
    expect(debug.context_window).toBeGreaterThan(0);
    expect(Number(debug.compact_threshold)).toBeLessThan(Number(debug.context_window));
    expect(debug.context_tokens).toBeGreaterThanOrEqual(0);
    expect(debug.qa_cache_count).toBe(2);
  });
});
