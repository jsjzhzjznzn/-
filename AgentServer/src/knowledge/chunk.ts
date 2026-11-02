/**
 * 知识库分词与切块（逐字搬 knowledge.py，方案 §14）。
 *
 * 中文检索的关键：FTS5 的 unicode61 不切中文，所以先把 CJK 连续段切成双字 token
 * （"副本系统" → "副本 本系 系统"）再建倒排；查询同法切分后 OR 匹配。
 */
import { KB_STOP_BIGRAMS } from "./stopwords.js";

const _CJK = "\\u3400-\\u4dbf\\u4e00-\\u9fff\\uf900-\\ufaff";
const TOKEN_RE = new RegExp(`[${_CJK}]+|[A-Za-z0-9]+(?:[._][A-Za-z0-9]+)*`, "g");
const CJK_ONLY = new RegExp(`^[${_CJK}]$`);

/** 规范化拉丁/数字 token：v1.2 → 12（版本号口径一致；纯数字去点防 FTS 拆词）。 */
function normAscii(token: string): string {
  let out = token.toLowerCase();
  if (out.startsWith("v") && out.length > 1 && /[0-9]/.test(out[1]!)) out = out.slice(1);
  return out.replace(/[._]/g, "");
}

function isAsciiStart(text: string): boolean {
  const code = text.codePointAt(0) ?? 0;
  return code < 0x80;
}

/** 文本 → token 列表：CJK 连续段切双字（单字段落保留单字），拉丁/数字整体保留。 */
export function tokenUnits(text: string): string[] {
  const units: string[] = [];
  for (const segment of text?.match(TOKEN_RE) ?? []) {
    if (isAsciiStart(segment)) {
      const norm = normAscii(segment);
      if (norm.length >= 2) units.push(norm);
      continue;
    }
    if (segment.length === 1) {
      units.push(segment);
    } else {
      for (let i = 0; i < segment.length - 1; i += 1) units.push(segment.slice(i, i + 2));
    }
  }
  return units;
}

/** 查询 → 检索 token：剔除停用词/单字噪音，上限 16（防超长提问构造巨大 OR）。 */
export function queryUnits(query: string): string[] {
  let units = tokenUnits(query).filter((unit) => !KB_STOP_BIGRAMS.has(unit));
  if (units.length === 0) units = tokenUnits(query); // 整句全是停用词：退回原始切分
  return units.slice(0, 16);
}

export interface KnowledgeChunk {
  src: "gameplay" | "announcement";
  title: string;
  content: string;
}

/** gameplay.md 按 H2/H3 切块；标题=章节路径（H1 与引用块跳过）。 */
export function chunkGameplay(text: string): KnowledgeChunk[] {
  const chunks: KnowledgeChunk[] = [];
  let h2 = "";
  let h3 = "";
  let lines: string[] = [];

  const flush = (): void => {
    const content = lines.join("\n").trim();
    if (content) {
      chunks.push({
        src: "gameplay",
        title: h3 ? `${h2} > ${h3}` : h2 || "总览",
        content: content.trim(),
      });
    }
  };

  for (const line of (text ?? "").split(/\r?\n/)) {
    const m2 = /^##\s+(.+)$/.exec(line);
    const m3 = /^###\s+(.+)$/.exec(line);
    if (m2 || m3) {
      flush();
      lines = [];
      if (m2) {
        h2 = m2[1]!.trim();
        h3 = "";
      } else {
        h3 = m3![1]!.trim();
      }
    } else if (line.startsWith("#") || line.startsWith(">")) {
      continue; // H1 与文件说明块不计入知识块
    } else {
      lines.push(line);
    }
  }
  flush();
  return chunks;
}

/**
 * 公告列表 → 每版本一块；标题带版本号与日期（便于「1.2 版本改了什么」这类历史提问）。
 * 日期按东八区展示（与 Python 版一致）。
 */
export function chunkAnnouncements(items: readonly Record<string, unknown>[]): KnowledgeChunk[] {
  const chunks: KnowledgeChunk[] = [];
  for (const item of items) {
    const version = String(item.version ?? "").trim();
    const title = String(item.title ?? "").trim();
    const content = String(item.content ?? "").trim();
    if (!content) continue;
    const day = cstDate(Number(item.date_ms ?? 0));
    const head = `更新公告 v${version}` + (day ? `（${day}）` : "");
    chunks.push({
      src: "announcement",
      title: `${head} · ${title}`.replace(/(^[\s·]+)|([\s·]+$)/g, ""),
      content,
    });
  }
  return chunks;
}

/** 毫秒时间戳 → 东八区 YYYY-MM-DD（无效返回空串）。 */
export function cstDate(dateMs: number): string {
  if (!Number.isFinite(dateMs) || dateMs <= 0) return "";
  const shifted = new Date(dateMs + 8 * 3600 * 1000);
  if (Number.isNaN(shifted.getTime())) return "";
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${shifted.getUTCFullYear()}-${pad(shifted.getUTCMonth() + 1)}-${pad(shifted.getUTCDate())}`;
}

export { CJK_ONLY };
