/**
 * 上下文装配测试（P3）：prepareRequest 的块序、时效提醒注入、工具声明。
 *
 * 关键不变量（方案 §13.2）：
 *  - 首条 system 必带 toolsAdded（否则 pi 会每轮额外插系统消息）；
 *  - 命中时效关键词才追加【时效性提醒】，位置固定在人设/记忆/摘要/快照之后、会话消息之前；
 *  - 会话消息顺序与条目顺序一致（工具结果也在）。
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import path from "node:path";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";

const tempDir = mkdtempSync(path.join(tmpdir(), "afu-hooks-"));
process.env.AGENT_DB_PATH = path.join(tempDir, "hooks.db");

type Hooks = typeof import("../../src/agent/hooks.js");
type Store = typeof import("../../src/session/store.js");
type SessionTypes = typeof import("../../src/session/types.js");
type Sqlite = typeof import("../../src/db/sqlite.js");
type RunStateMod = typeof import("../../src/agent/runState.js");
let hooks: Hooks;
let store: Store;
let types: SessionTypes;
let sqlite: Sqlite;
let runStateMod: RunStateMod;

const PLAYER = 900;

beforeAll(async () => {
  sqlite = await import("../../src/db/sqlite.js");
  sqlite.openDb();
  store = await import("../../src/session/store.js");
  types = await import("../../src/session/types.js");
  hooks = await import("../../src/agent/hooks.js");
  runStateMod = await import("../../src/agent/runState.js");
});

afterAll(() => {
  sqlite?.closeDb();
  rmSync(tempDir, { recursive: true, force: true });
});

/** 造一个最小视图：一条用户消息 + 一条助手消息 + 一条工具结果。 */
function makeView(freshness = false) {
  const run = runStateMod.newRunState(PLAYER, "req-1", { online: true });
  run.freshnessHinted = freshness;
  const view = {
    playerId: PLAYER,
    tools: [
      {
        name: "get_bag_items",
        label: "背包",
        description: "背包",
        parameters: { type: "object", properties: {} } as never,
        execute: async () => ({ content: [{ type: "text", text: "" }], details: undefined }),
      },
    ],
    entries: [] as SessionTypes.SessionEntry[],
    run,
  };
  return view;
}

function appendUser(view: ReturnType<typeof makeView>, text: string) {
  const entry = store.appendMessage(
    PLAYER,
    { role: "user", content: [{ type: "text", text }], timestamp: Date.now() } as never,
    view.entries.at(-1)?.id ?? null,
  );
  view.entries.push(entry);
}

describe("canonical 上下文装配", () => {
  it("首条 system 声明工具；命中时效关键词时追加【时效性提醒】", () => {
    const view = makeView();
    appendUser(view, "我背包里有什么？");
    const messages = hooks.buildCanonicalContext(view as never);

    expect(messages[0]!.role).toBe("system");
    expect((messages[0] as { toolsAdded?: unknown[] }).toolsAdded).toHaveLength(1);
    // 人设 + 玩家快照 + 时效提醒
    const systems = messages.filter((m) => m.role === "system");
    expect(systems).toHaveLength(3);
    expect(JSON.stringify(systems[2])).toContain("【时效性提醒】");
    expect(view.run.freshnessHinted).toBe(true);
    // 会话消息在最后，顺序不变
    expect(messages.at(-1)!.role).toBe("user");
  });

  it("未命中时效关键词时不注入提醒（闲聊零成本）", () => {
    const view = makeView();
    // 注意别用含时效关键词的字样（"今天/现在/天气/在线"等都在关键词表里）
    appendUser(view, "心情不错，随便聊聊天吧");
    const messages = hooks.buildCanonicalContext(view as never);
    expect(messages.filter((m) => m.role === "system")).toHaveLength(2);
    expect(JSON.stringify(messages)).not.toContain("【时效性提醒】");
    expect(view.run.freshnessHinted).toBe(false);
  });

  it("无玩家快照时省掉快照块", () => {
    const view = makeView();
    view.run.playerContext = {};
    appendUser(view, "你好");
    const messages = hooks.buildCanonicalContext(view as never);
    expect(messages.filter((m) => m.role === "system")).toHaveLength(1);
  });

  it("hintsFreshness 对数据类关键词判定正确", () => {
    expect(hooks.hintsFreshness("我背包里有啥")).toBe(true);
    expect(hooks.hintsFreshness("排行榜第一名是谁")).toBe(true);
    expect(hooks.hintsFreshness("讲个笑话")).toBe(false);
  });
});
