/**
 * P4 单测：简报文案（逐字）、主动提醒模板/兜底、长期记忆抽取解析、相似问题缓存。
 */
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

import { afterAll, beforeAll, describe, expect, it } from "vitest";

const tempDir = mkdtempSync(path.join(tmpdir(), "afu-p4-"));
process.env.AGENT_DB_PATH = path.join(tempDir, "p4.db");

type Brief = typeof import("../../src/agent/brief.js");
type Proactive = typeof import("../../src/agent/proactive.js");
type Longterm = typeof import("../../src/agent/longterm.js");
type Cache = typeof import("../../src/agent/cache.js");
type Sqlite = typeof import("../../src/db/sqlite.js");
let brief: Brief;
let proactive: Proactive;
let longterm: Longterm;
let cache: Cache;
let sqlite: Sqlite;

const PLAYER = 901;

beforeAll(async () => {
  sqlite = await import("../../src/db/sqlite.js");
  sqlite.openDb();
  brief = await import("../../src/agent/brief.js");
  proactive = await import("../../src/agent/proactive.js");
  longterm = await import("../../src/agent/longterm.js");
  cache = await import("../../src/agent/cache.js");
});

afterAll(() => {
  sqlite?.closeDb();
  rmSync(tempDir, { recursive: true, force: true });
});

describe("登录简报（确定性模板，逐字）", () => {
  it("三段数据齐备时的完整文案", () => {
    const text = brief.renderLoginBrief(
      { unread: 2, claimable: 1 },
      { submittable: [{ name: "社交达人" }] },
      { banners: [{ total_draws: 10, pity5_remaining: 28, title: "武器唤取·苍鳞" }] },
    );
    expect(text).toBe(
      "主人回来啦！阿福给你报个到——" +
        "邮箱里有 2 封未读邮件，其中 1 封的附件还没领，记得去邮箱看看。" +
        "任务这边有可以提交的了：社交达人，交了就能领奖励。" +
        "唤取【武器唤取·苍鳞】距5星保底还差 28 抽。" +
        "想聊什么随时叫我，我在的。",
    );
  });

  it("邮箱干净 / 无抽卡记录 / 可提交超过三个 的省略规则", () => {
    const text = brief.renderLoginBrief(
      {},
      { submittable: [{ name: "A" }, { name: "B" }, { name: "C" }, { name: "D" }] },
      { banners: [{ total_draws: 0, pity5_remaining: 5, title: "没抽过" }] },
    );
    expect(text).toContain("邮箱干干净净，没有未读。");
    expect(text).toContain("任务这边有可以提交的了：A、B、C 等共 4 个，交了就能领奖励。");
    expect(text).not.toContain("没抽过"); // 没抽过卡的池子不打扰
  });

  it("数据源失败（空对象）时只剩寒暄", () => {
    const text = brief.renderLoginBrief({}, {}, {});
    expect(text).toBe("主人回来啦！阿福给你报个到——邮箱干干净净，没有未读。想聊什么随时叫我，我在的。");
  });
});

describe("主动提醒模板与兜底", () => {
  it("kind=1 有标题 → 引用邮件标题；无标题多封 → 报数量；单封 → 泛指", () => {
    expect(proactive.buildProactivePrompt(1, { title: "补偿" })).toContain("《补偿》");
    expect(proactive.buildProactivePrompt(1, { claimable: 3 })).toContain("还有 3 封带附件的邮件");
    expect(proactive.buildProactivePrompt(1, {})).toContain("有封带附件的邮件还没领取");
    expect(proactive.buildProactivePrompt(1, {})).toContain("【系统主动事件·服务端注入】");
  });

  it("未知 kind → 空模板（走兜底文案不进 LLM）", () => {
    expect(proactive.buildProactivePrompt(999, {})).toBe("");
    expect(proactive.kindName(1)).toBe("mail_unclaimed");
    expect(proactive.kindName(999)).toBe("kind999");
  });
});

describe("L3 长期记忆抽取解析", () => {
  it("解析 JSON 数组并截断字段（key≤8、content≤80、最多 2 条）", () => {
    const facts = longterm.parseFacts(
      '[{"key":"称呼","content":"玩家叫蛋哥"},{"key":"这是一个很长的主题名称","content":"' +
        "内容".repeat(60) +
        '"},{"key":"第三","content":"丢弃"}]',
    );
    expect(facts).toHaveLength(2);
    expect(facts[0]).toEqual({ key: "称呼", content: "玩家叫蛋哥" });
    expect(facts[1]!.key).toHaveLength(8);
    expect(facts[1]!.content.length).toBeLessThanOrEqual(80);
  });

  it("容错 ```json 围栏与非法 JSON", () => {
    expect(longterm.parseFacts('```json\n[{"key":"喜好","content":"爱挖矿"}]\n```')).toEqual([
      { key: "喜好", content: "爱挖矿" },
    ]);
    expect(longterm.parseFacts("这不是 JSON")).toEqual([]);
    expect(longterm.parseFacts("[{}]")).toEqual([]);
  });
});

describe("相似问题缓存", () => {
  it("时效关键词 → 双向跳过；闲聊 → 参与缓存", () => {
    expect(cache.cacheSkip("我背包里有什么")).toBe(true);
    expect(cache.cacheSkip("排行榜第一名是谁")).toBe(true);
    expect(cache.cacheSkip("讲个笑话")).toBe(false);
  });

  it("精确命中与相似命中（≥0.90）都能取回；不同问法不命中", () => {
    cache.storeCache(PLAYER, "挑战怎么玩？", "倒计时内疯狂点击计分。");
    expect(cache.lookupCache(PLAYER, "挑战怎么玩？")).toBe("倒计时内疯狂点击计分。");
    // 归一化后 "挑战怎么玩?" 与 "挑战怎么玩" 相似度 ≈0.909 → 命中
    expect(cache.lookupCache(PLAYER, "挑战怎么玩")).toBe("倒计时内疯狂点击计分。");
    // 换说法的问法相似度不足 → 不命中（与 Python 版一致的保守行为）
    expect(cache.lookupCache(PLAYER, "副本怪物多久刷新")).toBeNull();
    expect(cache.lookupCache(PLAYER, "   ")).toBeNull();
  });
});
