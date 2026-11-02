/**
 * SQLite 层（node:sqlite，Node 内置，零依赖）。
 *
 * 与 Python 版共用同一个库文件 `data/agent_memory.db`：
 *  - 既有 4 张业务表（chat_messages / player_prefs / qa_cache / longterm_memory）
 *    表名、列名、语义一律不变 —— 两个实现可互相接手同一个库（方案 §4.3）；
 *  - 新增 2 张表承载 pi 的会话条目与状态（方案 §6.2）。
 *
 * 并发：单进程同步访问；WAL 让读不阻塞写；busy_timeout 兜底（开发期 Python 版
 * 可能同时在写同一个库）。
 */
import { mkdirSync } from "node:fs";
import path from "node:path";
import { DatabaseSync } from "node:sqlite";

import { DB_PATH } from "../core/config.js";

/** 既有 4 表（逐字搬 storage.py 的建表语句）。 */
const CREATE_CHAT = `
CREATE TABLE IF NOT EXISTS chat_messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    player_id INTEGER NOT NULL,
    role TEXT NOT NULL,
    content TEXT NOT NULL,
    created_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_chat_player ON chat_messages(player_id, id);
`;

const CREATE_PREFS = `
CREATE TABLE IF NOT EXISTS player_prefs (
    player_id INTEGER PRIMARY KEY,
    style TEXT NOT NULL DEFAULT 'gentle',
    updated_ms INTEGER NOT NULL
);
`;

const CREATE_QA_CACHE = `
CREATE TABLE IF NOT EXISTS qa_cache (
    player_id INTEGER NOT NULL,
    norm_question TEXT NOT NULL,
    question TEXT NOT NULL,
    reply TEXT NOT NULL,
    created_ms INTEGER NOT NULL,
    PRIMARY KEY (player_id, norm_question)
);
`;

const CREATE_LTM = `
CREATE TABLE IF NOT EXISTS longterm_memory (
    player_id INTEGER NOT NULL,
    mem_key TEXT NOT NULL,
    content TEXT NOT NULL,
    updated_ms INTEGER NOT NULL,
    PRIMARY KEY (player_id, mem_key)
);
`;

/** 新增：会话条目（pi SessionEntry 形状的 JSON，一条一个消息/压缩条目）。 */
const CREATE_SESSION_ENTRIES = `
CREATE TABLE IF NOT EXISTS session_entries (
    player_id  INTEGER NOT NULL,
    seq        INTEGER NOT NULL,
    entry_id   TEXT    NOT NULL,
    parent_id  TEXT,
    type       TEXT    NOT NULL,
    payload    TEXT    NOT NULL,
    created_ms INTEGER NOT NULL,
    PRIMARY KEY (player_id, seq)
);
CREATE INDEX IF NOT EXISTS idx_session_player_seq ON session_entries(player_id, seq);
`;

/** 新增：会话状态（单行/玩家）：当前 leaf、压缩水位、最近使用时间。 */
const CREATE_SESSION_STATE = `
CREATE TABLE IF NOT EXISTS session_state (
    player_id    INTEGER PRIMARY KEY,
    leaf_id      TEXT,
    compacted_to TEXT,
    updated_ms   INTEGER NOT NULL
);
`;

let db: DatabaseSync | null = null;

/** 打开数据库（幂等）：WAL + busy_timeout + 全部建表。 */
export function openDb(): DatabaseSync {
  if (db) return db;
  mkdirSync(path.dirname(DB_PATH), { recursive: true });
  const conn = new DatabaseSync(DB_PATH);
  conn.exec("PRAGMA journal_mode=WAL");
  // synchronous=NORMAL（WAL 下）：每次提交不再 fsync，实测 0.33ms → 0.06ms/条
  // （本机 2000 条基准）。代价：操作系统崩溃/断电可能丢最近几条已提交记录；
  // 进程崩溃不受影响（WAL 仍能恢复到一致状态）。聊天记录按这个口径可接受。
  conn.exec("PRAGMA synchronous=NORMAL");
  conn.exec("PRAGMA busy_timeout=5000");
  for (const ddl of [
    CREATE_CHAT,
    CREATE_PREFS,
    CREATE_QA_CACHE,
    CREATE_LTM,
    CREATE_SESSION_ENTRIES,
    CREATE_SESSION_STATE,
  ]) {
    conn.exec(ddl);
  }
  db = conn;
  return conn;
}

/** 取连接（未打开则打开）。 */
export function getDb(): DatabaseSync {
  return db ?? openDb();
}

/** 关闭连接（优雅退出用）。 */
export function closeDb(): void {
  if (!db) return;
  try {
    db.close();
  } finally {
    db = null;
  }
}

/** 一次性事务执行（BEGIN / COMMIT / ROLLBACK）。 */
export function inTransaction<T>(fn: () => T): T {
  const conn = getDb();
  conn.exec("BEGIN");
  try {
    const out = fn();
    conn.exec("COMMIT");
    return out;
  } catch (err) {
    try {
      conn.exec("ROLLBACK");
    } catch {
      // 回滚失败（连接已坏）时保留原始异常
    }
    throw err;
  }
}

/** 测试/诊断用：各表行数快照。 */
export function tableCounts(): Record<string, number> {
  const conn = getDb();
  const tables = ["chat_messages", "player_prefs", "qa_cache", "longterm_memory", "session_entries", "session_state"];
  const out: Record<string, number> = {};
  for (const t of tables) {
    const row = conn.prepare(`SELECT COUNT(*) AS c FROM ${t}`).get() as { c: number };
    out[t] = row.c;
  }
  return out;
}
