/**
 * 联调小工具（非测试）：往某个玩家的会话里灌入合成条目，用于压缩/E2E 场景。
 *
 * 用法：
 *   node tests/tools/seed-session.mjs <playerId> [--turns 40] [--stale] [--clear]
 *     --turns N  灌 N 轮闲聊（默认 40）
 *     --stale    额外灌一轮「只在工具结果里出现过 999 铁矿」的历史（复读检测用）
 *     --clear    先清空该玩家的会话条目
 *
 * 直接写 session_entries（与 config 的 DB 路径一致），不经过 Agent，毫秒级完成。
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";
import { randomUUID } from "node:crypto";

const ROOT = path.resolve(import.meta.dirname, "..", "..");
const envText = readFileSync(path.join(ROOT, ".env"), "utf8");
const dbPath = process.env.AGENT_DB_PATH || path.join(ROOT, "data", "agent_memory.db");

const args = process.argv.slice(2);
const playerId = Number.parseInt(args[0] ?? "", 10);
if (!Number.isFinite(playerId)) {
  console.error("用法: node tests/tools/seed-session.mjs <playerId> [--turns 40] [--stale] [--clear]");
  process.exit(2);
}
const turnsIndex = args.indexOf("--turns");
const turns = turnsIndex >= 0 ? Number.parseInt(args[turnsIndex + 1] ?? "40", 10) : 40;
const withStale = args.includes("--stale");
const clear = args.includes("--clear");
void envText;

const db = new DatabaseSync(dbPath);
db.exec("PRAGMA journal_mode=WAL");
db.exec("PRAGMA busy_timeout=5000");

if (clear) {
  db.prepare("DELETE FROM session_entries WHERE player_id=?").run(playerId);
  db.prepare("DELETE FROM session_state WHERE player_id=?").run(playerId);
  console.log(`已清空 player=${playerId} 的会话条目`);
}

db.prepare(
  "INSERT INTO session_state(player_id, leaf_id, compacted_to, updated_ms) VALUES(?,NULL,NULL,?) " +
    "ON CONFLICT(player_id) DO NOTHING",
).run(playerId, Date.now());

const nextSeq = () => {
  const row = db
    .prepare("SELECT COALESCE(MAX(seq),0) AS s FROM session_entries WHERE player_id=?")
    .get(playerId);
  return row.s + 1;
};

const insert = db.prepare(
  "INSERT INTO session_entries(player_id, seq, entry_id, parent_id, type, payload, created_ms) " +
    "VALUES(?,?,?,?,?,?,?)",
);

function append(message) {
  const id = randomUUID();
  insert.run(playerId, nextSeq(), id, null, "message", JSON.stringify({ message }), Date.now());
  return id;
}

const assistantBase = {
  api: "openai-completions",
  provider: "deepseek",
  model: "deepseek-chat",
  usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0 },
  stopReason: "stop",
  timestamp: 0,
};

function appendTurn(question, answer) {
  append({ role: "user", content: [{ type: "text", text: question }], timestamp: 0 });
  append({
    ...assistantBase,
    role: "assistant",
    content: [{ type: "text", text: answer }],
  });
}

if (withStale) {
  // 这一轮的 999 只出现在工具结果里，assistant 文本刻意不带数字——
  // 压缩后若模型还能说出 999，说明工具数据沉淀进了摘要（回归）
  // 注意：toolCall.id 与 toolResult.toolCallId 必须一致，否则 API 直接 400
  // （OpenAI 兼容接口的硬约束：带 tool_calls 的助手消息后面必须跟对应的 tool 消息）
  const staleCallId = randomUUID();
  append({ role: "user", content: [{ type: "text", text: "我背包里有多少铁矿？" }], timestamp: 0 });
  append({
    ...assistantBase,
    role: "assistant",
    stopReason: "toolUse",
    content: [{ type: "toolCall", id: staleCallId, name: "get_bag_items", arguments: {} }],
  });
  append({
    role: "toolResult",
    toolCallId: staleCallId,
    toolName: "get_bag_items",
    content: [{ type: "text", text: "背包快照：容 500 格，已用 3 格；铁矿 999 个、治疗药剂 7 个。" }],
    isError: false,
    timestamp: 0,
  });
  append({
    ...assistantBase,
    role: "assistant",
    content: [{ type: "text", text: "帮你查过了，铁矿的准确数量我按系统返回值说就好。" }],
  });
}

const smallTalk = [
  ["今天心情不错", "那挺好呀，想聊点什么随时说。"],
  ["你觉得我该先做哪个任务", "看你手头进度，先挑能立刻交的吧。"],
  ["这游戏有意思吗", "玩法挺多的，慢慢摸就熟了。"],
  ["我有点累了", "那就歇会儿，游戏不急。"],
  ["给我讲点轻松的", "行，我说点轻松的。"],
];
for (let i = 0; i < turns; i += 1) {
  const [question, answer] = smallTalk[i % smallTalk.length];
  appendTurn(`${question}（第 ${i + 1} 次）`, `${answer}这是第 ${i + 1} 轮的闲聊内容，用来把上下文堆长一点。`);
}

const count = db
  .prepare("SELECT COUNT(*) AS c FROM session_entries WHERE player_id=?")
  .get(playerId);
console.log(`player=${playerId} 会话条目=${count.c}（含 1 条待压缩的历史尾段：${withStale ? "有" : "无"}）`);
db.close();
