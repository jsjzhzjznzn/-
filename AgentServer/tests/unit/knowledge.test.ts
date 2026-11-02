/**
 * 知识库测试（P2）：分词/切块（纯函数）+ 检索端到端（临时知识文件 + 公告端点不可达）。
 *
 * 注意：config 在导入时读环境变量，所以这里先设 env 再动态导入。
 */
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { afterAll, beforeAll, describe, expect, it } from "vitest";

const tempDir = mkdtempSync(path.join(tmpdir(), "afu-kb-"));
const gameplayFile = path.join(tempDir, "gameplay.md");
writeFileSync(
  gameplayFile,
  [
    "# 玩法总览",
    "> 这是说明块，不该进知识块",
    "",
    "## 副本系统",
    "### 怪物刷新",
    "拾金鼠与藏宝龟每 5 秒重生一次，击杀后掉落物品。",
    "",
    "### 自动拾取",
    "走近掉落物约 1.3 米会自动拾取。",
    "",
    "## 背包",
    "背包容量恒定 500 格。",
  ].join("\n"),
  "utf8",
);
// 公告端点指向不可达端口 → 快速失败、不阻塞（等价 Python 版的抓取失败保留旧块）
process.env.AGENT_GAMEPLAY_FILE = gameplayFile;
process.env.AGENT_INTERNAL_API_BASE = "http://127.0.0.1:1";

type ChunkModule = typeof import("../../src/knowledge/chunk.js");
type SearchModule = typeof import("../../src/knowledge/search.js");
type IndexModule = typeof import("../../src/knowledge/index.js");
let chunk: ChunkModule;
let search: SearchModule;
let index: IndexModule;

beforeAll(async () => {
  chunk = await import("../../src/knowledge/chunk.js");
  index = await import("../../src/knowledge/index.js");
  search = await import("../../src/knowledge/search.js");
});

afterAll(() => {
  index?.resetIndex();
  rmSync(tempDir, { recursive: true, force: true });
});

describe("中文分词（双字切分）", () => {
  it("CJK 连续段切双字、单字保留；拉丁/数字整体保留并规范化", () => {
    expect(chunk.tokenUnits("副本系统")).toEqual(["副本", "本系", "系统"]);
    expect(chunk.tokenUnits("我")).toEqual(["我"]);
    expect(chunk.tokenUnits("v1.2 版本")).toEqual(["12", "版本"]);
    expect(chunk.tokenUnits("TOP100")).toEqual(["top100"]);
  });

  it("查询侧剔除疑问/口语停用词（整词剔除，「久刷」这类跨词双字保留），最长 16 个 token", () => {
    // Python 版同样是「整词剔除」：多久 被剔掉，但切分出的 久刷 仍在
    expect(chunk.queryUnits("副本怪物多久刷新")).toEqual([
      "副本", "本怪", "怪物", "物多", "久刷", "刷新",
    ]);
    const long = chunk.queryUnits("一".repeat(60));
    expect(long.length).toBeLessThanOrEqual(16);
  });
});

describe("切块", () => {
  it("gameplay 按 H2/H3 切块，H1 与引用块跳过，标题带章节路径", () => {
    const chunks = chunk.chunkGameplay(
      ["# 标题", "> 说明", "", "## A", "内容A", "### B", "内容B"].join("\n"),
    );
    expect(chunks.map((c) => c.title)).toEqual(["A", "A > B"]);
    expect(chunks[0]!.content).toBe("内容A");
  });

  it("公告每版本一块，日期按东八区", () => {
    const chunks = chunk.chunkAnnouncements([
      { version: "1.2", title: "新副本", content: "正文", date_ms: Date.UTC(2026, 9, 1, 0, 0, 0) },
      { version: "1.1", title: "空正文", content: "" },
    ]);
    expect(chunks).toHaveLength(1);
    expect(chunks[0]!.title).toBe("更新公告 v1.2（2026-10-01） · 新副本");
  });
});

describe("检索", () => {
  it("命中玩法攻略块并渲染出标题与来源标签", async () => {
    const result = await search.searchKnowledge("副本 怪物 刷新", 3);
    expect(result).toContain("【知识库检索结果】");
    expect(result).toContain("玩法攻略");
    expect(result).toContain("副本系统 > 怪物刷新");
    expect(result).toContain("5 秒重生");
  });

  it("单 token 短查询走 LIKE 兜底", async () => {
    const result = await search.searchKnowledge("背包", 3);
    expect(result).toContain("背包容量恒定 500 格");
  });

  it("未命中给明确空结果文案；空查询也有明确回复", async () => {
    expect(await search.searchKnowledge("外星人降临", 3)).toContain("知识库未命中与");
    expect(await search.searchKnowledge("   ", 3)).toBe("（知识库检索：检索词为空）");
  });

  it("索引统计：只有玩法块（公告端点不可达时保留为空）", async () => {
    const stats = index.indexStats();
    expect(stats.built).toBe(true);
    expect(stats.gameplay_chunks).toBe(3);
    expect(stats.announcement_chunks).toBe(0);
  });
});
