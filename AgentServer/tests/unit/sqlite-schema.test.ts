/**
 * SQLite 层测试：建表幂等 + 既有 4 表结构不变 + 新 2 表存在 + WAL 生效。
 *
 * 用 AGENT_DB_PATH 指向临时库，避免碰真实记忆库。
 */
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { afterAll, beforeAll, describe, expect, it } from "vitest";

const tempDir = mkdtempSync(path.join(tmpdir(), "afu-agent-db-"));
process.env.AGENT_DB_PATH = path.join(tempDir, "test_memory.db");

type SqliteModule = typeof import("../../src/db/sqlite.js");
let sqlite: SqliteModule;

beforeAll(async () => {
  sqlite = await import("../../src/db/sqlite.js");
});

afterAll(() => {
  sqlite?.closeDb();
  rmSync(tempDir, { recursive: true, force: true });
});

describe("SQLite 层", () => {
  it("建表齐全且可重复打开（幂等）", () => {
    sqlite.openDb();
    sqlite.openDb(); // 再开一次不应报错
    const counts = sqlite.tableCounts();
    expect(Object.keys(counts).sort()).toEqual([
      "chat_messages",
      "longterm_memory",
      "player_prefs",
      "qa_cache",
      "session_entries",
      "session_state",
    ]);
    for (const value of Object.values(counts)) expect(value).toBe(0);
  });

  it("WAL 生效", () => {
    const row = sqlite.getDb().prepare("PRAGMA journal_mode").get() as { journal_mode: string };
    expect(row.journal_mode).toBe("wal");
  });

  it("既有表的列名与 Python 版一致（chat_messages / player_prefs / qa_cache / longterm_memory）", () => {
    const db = sqlite.getDb();
    const columns = (table: string) =>
      (db.prepare(`PRAGMA table_info(${table})`).all() as { name: string }[]).map((r) => r.name);

    expect(columns("chat_messages")).toEqual(["id", "player_id", "role", "content", "created_ms"]);
    expect(columns("player_prefs")).toEqual(["player_id", "style", "updated_ms"]);
    expect(columns("qa_cache")).toEqual([
      "player_id", "norm_question", "question", "reply", "created_ms",
    ]);
    expect(columns("longterm_memory")).toEqual(["player_id", "mem_key", "content", "updated_ms"]);
  });

  it("事务回滚不留痕（inTransaction）", () => {
    const db = sqlite.getDb();
    expect(() =>
      sqlite.inTransaction(() => {
        db.prepare(
          "INSERT INTO chat_messages(player_id, role, content, created_ms) VALUES (?,?,?,?)",
        ).run(1, "user", "回滚测试", Date.now());
        throw new Error("boom");
      }),
    ).toThrow("boom");
    const row = db.prepare("SELECT COUNT(*) AS c FROM chat_messages").get() as { c: number };
    expect(row.c).toBe(0);
  });
});
