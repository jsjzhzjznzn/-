/**
 * 知识库检索（逐字搬 knowledge.py 的检索/重排/渲染，方案 §14）。
 *
 * 检索：FTS5 召回候选（bm25 排序）→ 按「查询词命中率 + 标题命中加成」重排
 * （质量门防泛化双字组误命中）→ top-k（≤5）、单块截断 320 字。
 * 短查询兜底：切分后只剩 1 个 token（如"背包"/"1.2"）走 LIKE 包含查询。
 */
import type { DatabaseSync } from "node:sqlite";

import { KB_CANDIDATE_LIMIT, KB_CHUNK_TEXT_CAP, KB_DEFAULT_TOP_K, KB_MAX_TOP_K } from "../core/config.js";
import { queryUnits, tokenUnits } from "./chunk.js";
import { ensureIndex, getConn } from "./index.js";

const TAG_BY_SRC: Record<string, string> = { gameplay: "玩法攻略", announcement: "更新公告" };

type Row = { src: string; title: string; content: string };

function ftsSearch(conn: DatabaseSync, units: readonly string[]): Row[] {
  const expr = units.map((unit) => `"${unit.replace(/"/g, '""')}"`).join(" OR ");
  try {
    return conn
      .prepare(
        "SELECT src, title, content FROM kb WHERE kb MATCH ? " +
          "ORDER BY bm25(kb, 0, 0, 0, 3.0, 1.0) LIMIT ?",
      )
      .all(expr, KB_CANDIDATE_LIMIT) as unknown as Row[];
  } catch {
    return [];
  }
}

function likeSearch(conn: DatabaseSync, query: string): Row[] {
  const escaped = query.replace(/\\/g, "\\\\").replace(/%/g, "\\%").replace(/_/g, "\\_");
  const pattern = `%${escaped}%`;
  return conn
    .prepare(
      "SELECT src, title, content FROM kb WHERE title LIKE ? ESCAPE '\\' " +
        "OR content LIKE ? ESCAPE '\\' LIMIT ?",
    )
    .all(pattern, pattern, KB_CANDIDATE_LIMIT) as unknown as Row[];
}

/**
 * 重排：命中率 + 标题加成，并做最低相关性过滤。
 * 质量门：命中含数字型 token（版本号/数量，如 12/500）→ 视为强标识直接放行；
 * 否则短查询（≤6 词）命中率 ≥50%、长查询 ≥30%。
 */
function rerank(rows: readonly Row[], units: readonly string[]): { score: number; row: Row }[] {
  const qset = new Set(units);
  const qlen = Math.max(1, qset.size);
  const scored: { score: number; row: Row }[] = [];
  for (const row of rows) {
    const docUnits = new Set(tokenUnits(`${row.title} ${row.content}`));
    const matched = [...qset].filter((unit) => docUnits.has(unit));
    if (matched.length === 0) continue;
    const base = matched.length / qlen;
    if (!matched.some((unit) => /[0-9]/.test(unit))) {
      const minFraction = qlen <= 6 ? 0.5 : 0.3;
      if (base < minFraction) continue;
    }
    const titleUnits = new Set(tokenUnits(row.title));
    const titleHit = matched.some((unit) => titleUnits.has(unit));
    scored.push({ score: base + (titleHit ? 0.25 : 0), row });
  }
  scored.sort((a, b) => b.score - a.score);
  return scored;
}

function render(picks: readonly { score: number; row: Row }[]): string {
  const lines = ["【知识库检索结果】"];
  picks.forEach((pick, index) => {
    let text = pick.row.content.replace(/\s+/g, " ").trim();
    if (text.length > KB_CHUNK_TEXT_CAP) text = text.slice(0, KB_CHUNK_TEXT_CAP) + "…";
    lines.push(`[${index + 1}] ${TAG_BY_SRC[pick.row.src] ?? pick.row.src} · ${pick.row.title}：${text}`);
  });
  return lines.join("\n");
}

/** 检索知识库并返回可直接进 LLM 上下文的文本；未命中返回明确的空结果说明。 */
export async function searchKnowledge(query: string, topK = KB_DEFAULT_TOP_K): Promise<string> {
  const q = (query ?? "").trim();
  if (!q) return "（知识库检索：检索词为空）";

  await ensureIndex();
  const conn = getConn();
  if (!conn) return "（知识库暂不可用）";

  const k = Math.max(1, Math.min(Math.trunc(topK) || KB_DEFAULT_TOP_K, KB_MAX_TOP_K));
  const units = queryUnits(q);
  let rows = units.length <= 1 ? likeSearch(conn, q) : [];
  if (rows.length === 0) rows = ftsSearch(conn, units);
  const picks = rerank(rows, units).slice(0, k);
  if (picks.length === 0) {
    return `知识库未命中与「${q}」相关的内容（可换关键词或短句再查）。`;
  }
  return render(picks);
}
