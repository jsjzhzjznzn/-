/**
 * D10 单测：时效强制查库（问的是游戏实时数据、却整轮没查库 → 强制重答一次）。
 *
 * 背景：时效提醒是软约束（实测「我背包里有什么」可能 used_tools=[] 直接复述历史），
 * 而质检的触发条件是「本轮用过数据类工具」——两者之间的缝正是"复述旧数字"的高发区。
 *
 * 真实「重答后确实去查库了」的路径走 LLM，放在真机 E2E 验；这里覆盖判定分支与预算语义。
 * critic 被打桩：本文件关注点不是质检，且单测不该发 LLM 请求。
 */
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import path from "node:path";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";

const tempDir = mkdtempSync(path.join(tmpdir(), "afu-nudge-"));
process.env.AGENT_DB_PATH = path.join(tempDir, "nudge.db");

vi.mock("../../src/agent/critic.js", () => ({
  critiqueBlockText: (reason: string) => `【反思】${reason}`,
  reflect: async () => ({ passed: true, reason: "" }),
}));

type Hooks = typeof import("../../src/agent/hooks.js");
type Store = typeof import("../../src/session/store.js");
type SessionTypes = typeof import("../../src/session/types.js");
type Sqlite = typeof import("../../src/db/sqlite.js");
type RunStateMod = typeof import("../../src/agent/runState.js");
type Config = typeof import("../../src/core/config.js");
let hooks: Hooks;
let store: Store;
let types: SessionTypes;
let sqlite: Sqlite;
let runStateMod: RunStateMod;
let config: Config;

const PLAYER = 905;

beforeAll(async () => {
  sqlite = await import("../../src/db/sqlite.js");
  sqlite.openDb();
  store = await import("../../src/session/store.js");
  types = await import("../../src/session/types.js");
  hooks = await import("../../src/agent/hooks.js");
  runStateMod = await import("../../src/agent/runState.js");
  config = await import("../../src/core/config.js");
});

afterAll(() => {
  sqlite?.closeDb();
  rmSync(tempDir, { recursive: true, force: true });
});

/** 视图：一条用户消息 + 本轮 RunState（工具表对判定无关，留空）。 */
function makeView(question: string) {
  const run = runStateMod.newRunState(PLAYER, "req-nudge", {});
  const entries: SessionTypes.SessionEntry[] = [];
  entries.push(
    store.appendMessage(
      PLAYER,
      { role: "user", content: [{ type: "text", text: question }], timestamp: Date.now() } as never,
      null,
    ),
  );
  return { playerId: PLAYER, tools: [], entries, run };
}

/** 造一条「助手文本作答」的 turn。 */
function answerTurn(text = "你背包里有 12 个高级治疗药剂。"): never {
  return {
    message: { role: "assistant", content: [{ type: "text", text }], stopReason: "stop" },
    toolResults: [],
    context: { messages: [] },
    newMessages: [],
  } as never;
}

/** 造一条「助手请求工具」的 turn。 */
function toolCallTurn(): never {
  return {
    message: {
      role: "assistant",
      content: [{ type: "toolCall", id: "tc", callId: "tc", name: "get_bag_items", arguments: {} }],
      stopReason: "stop",
    },
    toolResults: [],
    context: { messages: [] },
    newMessages: [],
  } as never;
}

describe("D10 时效强制查库判定", () => {
  it("问游戏实时数据 + 整轮没查库 → 强制重答一次，计数与批评块都落位", async () => {
    const view = makeView("我背包里现在有什么？");
    const run = view.run;
    const decision = await hooks.makeFinishTurn(() => view as never)(answerTurn());

    expect(decision).toEqual({ action: "continue" });
    expect(run.freshnessNudges).toBe(1);
    expect(run.freshnessCritique).toBe(config.FRESHNESS_NUDGE_BLOCK);
    // 与质检共享重答预算：这里同时自增 reflectionCount，保证每轮最多只多一次 LLM 调用
    expect(run.reflectionCount).toBe(1);
  });

  it("重答预算已用尽 → 不再强制（自己用过 或 质检用过 都算）", async () => {
    const usedByNudge = makeView("我背包里现在有什么？");
    usedByNudge.run.freshnessNudges = 1;
    usedByNudge.run.reflectionCount = 1;
    expect(await hooks.makeFinishTurn(() => usedByNudge as never)(answerTurn())).toBeUndefined();

    const usedByCritic = makeView("我背包里现在有什么？");
    usedByCritic.run.reflectionCount = 1; // 质检已经重答过一次 → 共享预算耗尽
    expect(await hooks.makeFinishTurn(() => usedByCritic as never)(answerTurn())).toBeUndefined();
    expect(usedByCritic.run.freshnessNudges).toBe(0);
  });

  it("问的不是游戏数据（只命中通用时效词）→ 不强制，不给「现在几点了」白跑一轮", async () => {
    const view = makeView("现在几点了？今天天气咋样？");
    expect(await hooks.makeFinishTurn(() => view as never)(answerTurn("我不太清楚时间。"))).toBeUndefined();
    expect(view.run.freshnessNudges).toBe(0);
  });

  it("整轮查过数据工具 → 不强制（这种情况归质检看管）", async () => {
    const view = makeView("我背包里现在有什么？");
    view.run.dataToolResults.push("[get_bag_items] 背包快照");
    expect(await hooks.makeFinishTurn(() => view as never)(answerTurn())).toBeUndefined();
    expect(view.run.freshnessNudges).toBe(0);
  });

  it("还没作答（本轮带工具调用）→ 不强制", async () => {
    const view = makeView("我背包里现在有什么？");
    expect(await hooks.makeFinishTurn(() => view as never)(toolCallTurn())).toBeUndefined();
    expect(view.run.freshnessNudges).toBe(0);
  });

  it("hitsGameData 判定表：只认游戏实时数据词", () => {
    for (const question of ["我背包里有什么", "看下任务进度", "排行榜第一是谁", "邮箱有信吗", "抽卡保底还剩几抽"]) {
      expect(hooks.hitsGameData(question), question).toBe(true);
    }
    for (const question of ["现在几点了", "今天天气如何", "讲个笑话", "你好呀"]) {
      expect(hooks.hitsGameData(question), question).toBe(false);
    }
  });

  it("强制查库的批评块会被注入到下一轮上下文", () => {
    const view = makeView("我背包里现在有什么？");
    view.run.freshnessCritique = config.FRESHNESS_NUDGE_BLOCK;
    const messages = hooks.buildCanonicalContext(view as never);
    expect(JSON.stringify(messages)).toContain("【反思】玩家问的是实时游戏数据");
    expect(messages.filter((m) => m.role === "system")).toHaveLength(3); // 人设 + 时效提醒 + 强制查库块
  });

  it("D12：游戏数据类问题不进相似问题缓存（读与写都跳过）", async () => {
    const cache = await import("../../src/agent/cache.js");
    expect(cache.cacheSkip("我背包还剩多少空位")).toBe(true);
    expect(cache.cacheSkip("看下我的任务进度")).toBe(true);
    expect(cache.cacheSkip("邮箱有未读吗")).toBe(true);
    // 纯知识/闲聊仍走缓存（省钱；关键词判据只管能被穷举的常见问法）
    expect(cache.cacheSkip("讲个笑话")).toBe(false);
    expect(cache.cacheSkip("游戏里送金币的节日有哪些")).toBe(false);
  });

  it("D12：上下文里含历史工具结果 → 标记为「不许写缓存」", () => {
    const view = makeView("随便聊聊吧");
    hooks.buildCanonicalContext(view as never);
    expect(view.run.hadToolContext).toBe(false);

    // 追加一条工具结果条目：模型这次作答能看到历史数据 → 答案可能复述它 → 不许缓存
    view.entries.push({
      type: "message",
      id: "tr-1",
      parentId: null,
      createdMs: Date.now(),
      message: {
        role: "toolResult",
        toolName: "get_bag_items",
        content: [{ type: "text", text: "[get_bag_items] 背包 21/500" }],
      },
    } as never);

    hooks.buildCanonicalContext(view as never);
    expect(view.run.hadToolContext).toBe(true);
  });
});
