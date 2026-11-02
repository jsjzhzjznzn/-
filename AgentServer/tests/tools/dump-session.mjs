/**
 * 联调小工具（非测试）：打印某玩家最近 N 条会话条目的摘要（排查用）。
 * 用法：node tests/tools/dump-session.mjs <playerId> [limit]
 */
import path from "node:path";
import { DatabaseSync } from "node:sqlite";

const ROOT = path.resolve(import.meta.dirname, "..", "..");
const dbPath = process.env.AGENT_DB_PATH || path.join(ROOT, "data", "agent_memory.db");
const playerId = Number.parseInt(process.argv[2] ?? "", 10);
const limit = Number.parseInt(process.argv[3] ?? "5", 10);
const full = process.argv.includes("--full");

const db = new DatabaseSync(dbPath);
const rows = db
  .prepare(
    "SELECT seq, type, payload FROM session_entries WHERE player_id=? ORDER BY seq DESC LIMIT ?",
  )
  .all(playerId, limit);

for (const row of rows.reverse()) {
  const payload = JSON.parse(row.payload);
  if (row.type === "compaction") {
    console.log(`\n[${row.seq}] compaction 摘要长度=${payload.summary?.length ?? 0} 保留自=${payload.firstKeptEntryId?.slice(0, 8)}`);
    console.log((payload.summary ?? "").slice(0, 400));
    continue;
  }
  const message = payload.message;
  const text = (message.content ?? [])
    .map((block) => (block.type === "text" ? block.text : block.type === "toolCall" ? `<toolCall ${block.name}>` : ""))
    .join("");
  const head = `\n[${row.seq}] ${message.role}${message.toolName ? `(${message.toolName})` : ""} stop=${message.stopReason ?? "-"} len=${text.length}`;
  console.log(head);
  console.log(full ? text : text.slice(0, 400));
}
db.close();
