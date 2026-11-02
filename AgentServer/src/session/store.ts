/**
 * 会话条目存储（SQLite，方案 §6.2/§6.3）。
 *
 * 权威性规则：session_entries 是 LLM 上下文的唯一权威；chat_messages 只是 UGUI 回放流水。
 * 这里只做「读写条目」——业务表（偏好/缓存/长期记忆/流水）在各自的 repo 里。
 */
import { randomUUID } from "node:crypto";

import { getDb, inTransaction } from "../db/sqlite.js";
import { isMessageEntry, type CompactionEntry, type MessageEntry, type SessionEntry } from "./types.js";

interface EntryRow {
  seq: number;
  entry_id: string;
  parent_id: string | null;
  type: string;
  payload: string;
  created_ms: number;
}

/**
 * 已编译语句缓存：同一连接内复用 StatementSync，省掉每次调用的 SQL 编译。
 * 连接被 closeDb/重开后自动整体失效（缓存绑定在连接实例上）。
 */
type Prepared = ReturnType<ReturnType<typeof getDb>["prepare"]>;

let cachedConn: ReturnType<typeof getDb> | null = null;
const prepared = new Map<string, Prepared>();

function stmt(sql: string): Prepared {
  const conn = getDb();
  if (conn !== cachedConn) {
    prepared.clear();
    cachedConn = conn;
  }
  let cached = prepared.get(sql);
  if (!cached) {
    cached = conn.prepare(sql);
    prepared.set(sql, cached);
  }
  return cached;
}

function nextSeq(playerId: number): number {
  const row = stmt("SELECT COALESCE(MAX(seq), 0) AS s FROM session_entries WHERE player_id=?")
    .get(playerId) as { s: number };
  return row.s + 1;
}

/** 确保会话状态行存在（leaf/compacted_to/updated_ms）。 */
export function ensureState(playerId: number): void {
  stmt(
    "INSERT INTO session_state(player_id, leaf_id, compacted_to, updated_ms) VALUES(?,NULL,NULL,?) " +
      "ON CONFLICT(player_id) DO NOTHING",
  ).run(playerId, Date.now());
}

/** 该玩家全部条目（按 seq 升序）。 */
export function listEntries(playerId: number): SessionEntry[] {
  const rows = stmt(
    "SELECT seq, entry_id, parent_id, type, payload, created_ms FROM session_entries " +
      "WHERE player_id=? ORDER BY seq ASC",
  ).all(playerId) as unknown as EntryRow[];
  return rows.map((row) => deserialize(row));
}

function deserialize(row: EntryRow): SessionEntry {
  const body = JSON.parse(row.payload) as Record<string, unknown>;
  if (row.type === "compaction") {
    return {
      type: "compaction",
      id: row.entry_id,
      parentId: row.parent_id,
      summary: String(body.summary ?? ""),
      firstKeptEntryId: String(body.firstKeptEntryId ?? ""),
      tokensBefore: Number(body.tokensBefore ?? 0),
      createdMs: row.created_ms,
    } satisfies CompactionEntry;
  }
  return {
    type: "message",
    id: row.entry_id,
    parentId: row.parent_id,
    message: body.message as MessageEntry["message"],
    createdMs: row.created_ms,
  } satisfies MessageEntry;
}

function insert(playerId: number, entry: SessionEntry, payload: unknown): void {
  stmt(
    "INSERT INTO session_entries(player_id, seq, entry_id, parent_id, type, payload, created_ms) " +
      "VALUES(?,?,?,?,?,?,?)",
  ).run(playerId, nextSeq(playerId), entry.id, entry.parentId, entry.type, JSON.stringify(payload), entry.createdMs);
}

/** 追加一条消息条目（parent = 当前 leaf）。 */
export function appendMessage(
  playerId: number,
  message: MessageEntry["message"],
  parentId: string | null,
): MessageEntry {
  const entry: MessageEntry = {
    type: "message",
    id: randomUUID(),
    parentId,
    message,
    createdMs: Date.now(),
  };
  // 单事务：SELECT MAX(seq) + INSERT + UPDATE 三条语句一次提交。
  // 拆开写时每条语句各自 fsync，实测 0.70ms/条；合并后事件循环占用降到约 1/3（压测见 docs）。
  inTransaction(() => {
    insert(playerId, entry, { message });
    setLeaf(playerId, entry.id);
  });
  return entry;
}

/** 追加一条压缩条目。 */
export function appendCompaction(playerId: number, compaction: Omit<CompactionEntry, "type" | "id" | "parentId" | "createdMs">, parentId: string | null): CompactionEntry {
  const entry: CompactionEntry = {
    type: "compaction",
    id: randomUUID(),
    parentId,
    createdMs: Date.now(),
    ...compaction,
  };
  inTransaction(() => {
    insert(playerId, entry, {
      summary: entry.summary,
      firstKeptEntryId: entry.firstKeptEntryId,
      tokensBefore: entry.tokensBefore,
    });
    stmt("UPDATE session_state SET compacted_to=?, updated_ms=? WHERE player_id=?")
      .run(entry.id, Date.now(), playerId);
    setLeaf(playerId, entry.id);
  });
  return entry;
}

function setLeaf(playerId: number, leafId: string): void {
  stmt("UPDATE session_state SET leaf_id=?, updated_ms=? WHERE player_id=?")
    .run(leafId, Date.now(), playerId);
}

export function getLeaf(playerId: number): string | null {
  const row = stmt("SELECT leaf_id FROM session_state WHERE player_id=?").get(playerId) as
    | { leaf_id: string | null }
    | undefined;
  return row?.leaf_id ?? null;
}

/** 最近一次压缩水位（条目 id）。 */
export function getCompactedTo(playerId: number): string | null {
  const row = stmt("SELECT compacted_to FROM session_state WHERE player_id=?").get(playerId) as
    | { compacted_to: string | null }
    | undefined;
  return row?.compacted_to ?? null;
}

/** 清空该玩家会话条目（测试/重置用；不影响 chat_messages 流水）。 */
export function clearSession(playerId: number): void {
  inTransaction(() => {
    stmt("DELETE FROM session_entries WHERE player_id=?").run(playerId);
    stmt("UPDATE session_state SET leaf_id=NULL, compacted_to=NULL, updated_ms=? WHERE player_id=?").run(
      Date.now(),
      playerId,
    );
  });
}

/** 条目统计（debug/测试）。 */
export function entryStats(playerId: number): { messages: number; compactions: number } {
  const entries = listEntries(playerId);
  let messages = 0;
  let compactions = 0;
  for (const entry of entries) {
    if (isMessageEntry(entry)) messages += 1;
    else compactions += 1;
  }
  return { messages, compactions };
}
