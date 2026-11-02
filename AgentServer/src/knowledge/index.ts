/**
 * 知识库索引（逐字搬 knowledge.py 的索引/懒重建逻辑，方案 §14）。
 *
 * - 知识源：knowledge/gameplay.md（玩法攻略）+ 历次更新公告（8897 内部 API）。
 * - 索引：node:sqlite 内存库 + FTS5（unicode61）+ 中文双字切分（见 chunk.ts）。
 * - 懒重建：gameplay.md 的 (mtime, size) 签名 + 公告内容哈希，变了才重建
 *   （改知识文件无需重启即生效）；公告每 5 分钟刷新、失败 60s 后重试。
 */
import { createHash } from "node:crypto";
import { statSync, readFileSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";

import {
  ANNOUNCE_TIMEOUT_MS,
  GAMEPLAY_FILE,
  INTERNAL_API_BASE,
  KB_ANNOUNCE_REFRESH_MS,
  KB_ANNOUNCE_RETRY_MS,
} from "../core/config.js";
import { getJson } from "../core/http.js";
import { log } from "../core/logger.js";
import { chunkAnnouncements, chunkGameplay, tokenUnits, type KnowledgeChunk } from "./chunk.js";

const ANNOUNCE_URL = `${INTERNAL_API_BASE}/internal/agent/announcements`;

interface IndexState {
  conn: DatabaseSync | null;
  gameplaySig: string | null;
  announceChunks: KnowledgeChunk[];
  announceHash: string | null;
  nextAnnounceTryMs: number;
  builtCounts: { total: number; gameplay: number; announcement: number };
}

const state: IndexState = {
  conn: null,
  gameplaySig: null,
  announceChunks: [],
  announceHash: null,
  nextAnnounceTryMs: 0,
  builtCounts: { total: 0, gameplay: 0, announcement: 0 },
};

let building: Promise<void> | null = null;

function gameplaySignature(): string | null {
  try {
    const stat = statSync(GAMEPLAY_FILE);
    return `${stat.mtimeMs}:${stat.size}`;
  } catch {
    return null;
  }
}

function readGameplay(): string {
  try {
    return readFileSync(GAMEPLAY_FILE, "utf8");
  } catch {
    return "";
  }
}

/** 建 FTS5 内存索引（两份 token 列参与 bm25 排序）。 */
function buildConn(chunks: readonly KnowledgeChunk[]): DatabaseSync {
  const conn = new DatabaseSync(":memory:");
  conn.exec(
    "CREATE VIRTUAL TABLE kb USING fts5(" +
      "src UNINDEXED, title UNINDEXED, content UNINDEXED, tok_title, tok_content, " +
      "tokenize='unicode61')",
  );
  const insert = conn.prepare(
    "INSERT INTO kb(src, title, content, tok_title, tok_content) VALUES (?, ?, ?, ?, ?)",
  );
  for (const chunk of chunks) {
    insert.run(
      chunk.src,
      chunk.title,
      chunk.content,
      tokenUnits(chunk.title).join(" "),
      tokenUnits(chunk.content).join(" "),
    );
  }
  return conn;
}

/** 拉取公告（失败返回 null：保留旧块，检索不阻塞）。 */
async function fetchAnnouncements(): Promise<Record<string, unknown>[] | null> {
  try {
    const data = await getJson<unknown>(ANNOUNCE_URL, ANNOUNCE_TIMEOUT_MS);
    const items = Array.isArray(data)
      ? data
      : data && typeof data === "object" && Array.isArray((data as { items?: unknown }).items)
        ? ((data as { items: unknown[] }).items as unknown[])
        : null;
    if (!items) return null;
    return items.filter((item): item is Record<string, unknown> => typeof item === "object" && item !== null);
  } catch (err) {
    log.debug(`知识库公告拉取失败：${err instanceof Error ? err.name : String(err)}`);
    return null;
  }
}

/** 懒重建：签名没变直接复用；公告按周期刷新；重建整体替换（并发只跑一份）。 */
export async function ensureIndex(): Promise<void> {
  const nowMs = Date.now();
  const gameplaySig = gameplaySignature();

  let nextAnnounceChunks = state.announceChunks;
  let nextAnnounceHash = state.announceHash;
  if (state.conn === null || nowMs >= state.nextAnnounceTryMs) {
    const data = await fetchAnnouncements();
    if (data !== null) {
      nextAnnounceChunks = chunkAnnouncements(data);
      nextAnnounceHash = createHash("sha1").update(JSON.stringify(data)).digest("hex");
      state.nextAnnounceTryMs = nowMs + KB_ANNOUNCE_REFRESH_MS;
    } else {
      state.nextAnnounceTryMs = nowMs + KB_ANNOUNCE_RETRY_MS;
    }
  }

  if (state.conn !== null && gameplaySig === state.gameplaySig && nextAnnounceHash === state.announceHash) {
    return;
  }

  if (!building) {
    building = (async () => {
      const chunks = [...chunkGameplay(readGameplay()), ...nextAnnounceChunks];
      const conn = buildConn(chunks);
      const old = state.conn;
      state.conn = conn;
      state.gameplaySig = gameplaySig;
      state.announceChunks = nextAnnounceChunks;
      state.announceHash = nextAnnounceHash;
      state.builtCounts = {
        total: chunks.length,
        gameplay: chunks.filter((chunk) => chunk.src === "gameplay").length,
        announcement: chunks.filter((chunk) => chunk.src === "announcement").length,
      };
      if (old) {
        try {
          old.close();
        } catch {
          /* 旧连接关闭失败无妨 */
        }
      }
      log.info(
        `知识库索引就绪 共${state.builtCounts.total}块（玩法${state.builtCounts.gameplay}/公告${state.builtCounts.announcement}）`,
      );
    })().finally(() => {
      building = null;
    });
  }
  await building;
}

export function getConn(): DatabaseSync | null {
  return state.conn;
}

/** 索引状态快照（测试断言用）。 */
export function indexStats(): Record<string, unknown> {
  return {
    built: state.conn !== null,
    chunks: state.builtCounts.total,
    gameplay_chunks: state.builtCounts.gameplay,
    announcement_chunks: state.builtCounts.announcement,
    gameplay_sig: state.gameplaySig,
    announce_hash: state.announceHash,
  };
}

/** 清空索引与状态（测试隔离用）。 */
export function resetIndex(): void {
  if (state.conn) {
    try {
      state.conn.close();
    } catch {
      /* ignore */
    }
  }
  state.conn = null;
  state.gameplaySig = null;
  state.announceChunks = [];
  state.announceHash = null;
  state.nextAnnounceTryMs = 0;
  state.builtCounts = { total: 0, gameplay: 0, announcement: 0 };
}
