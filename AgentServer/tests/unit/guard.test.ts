/**
 * 防护闸门测试（P2 验收）：逐条对齐 Python 版 scripts/guard_test.py 的 20 项断言。
 *
 * 全内存行为断言——每个用例先 resetGuardState() 隔离，用独立 player_id 防串味。
 * 覆盖：写工具同请求只真实执行一次 / 只读工具不受幂等影响 / 失败可重试 /
 * 限频按玩家与窗口生效 / 单轮预算截断。
 */
import { beforeEach, describe, expect, it } from "vitest";
import { Type } from "typebox";

import {
  IDEMPOTENCY_TTL_SECONDS,
  MAX_TOOL_CALLS_PER_ROUND,
  RATE_MAX_CALLS,
  RATE_WINDOW_SECONDS,
  ROUND_OVERFLOW_MESSAGE,
  WRITE_RATE_MAX_CALLS,
} from "../../src/core/config.js";
import { ToolGuard, resetGuardState } from "../../src/agent/guard.js";
import { newRunState, type RunState } from "../../src/agent/runState.js";
import { defineTool, type ToolDeps } from "../../src/agent/tools/shared.js";

const COUNT = { write: 0, read: 0 };

function deps(playerId: number, requestId: string): ToolDeps {
  return { playerId, run: newRunState(playerId, requestId, {}) };
}

function writeTool(d: ToolDeps) {
  return defineTool(
    {
      name: "claim_mail_attachments",
      label: "假写工具",
      description: "假写工具（名字必须与 WRITE_TOOLS 登记的一致才会触发幂等）",
      parameters: Type.Object({ mail_id: Type.Integer() }),
      run: async (params) => {
        COUNT.write += 1;
        return `已领取邮件 ${params.mail_id}（真实执行第 ${COUNT.write} 次）`;
      },
    },
    d,
  );
}

function readTool(d: ToolDeps) {
  return defineTool(
    {
      name: "get_bag_items",
      label: "假只读工具",
      description: "假只读工具",
      parameters: Type.Object({}),
      run: async () => {
        COUNT.read += 1;
        return `背包快照（真实执行第 ${COUNT.read} 次）`;
      },
    },
    d,
  );
}

const text = (result: { content: { type: string; text?: string }[] }): string =>
  result.content.map((block) => block.text ?? "").join("");

beforeEach(() => {
  resetGuardState();
  COUNT.write = 0;
  COUNT.read = 0;
});

describe("幂等（只覆盖写工具）", () => {
  it("同请求重复写调用：真实执行仅 1 次，第二次回幂等说明 + 首次结果", async () => {
    const tool = writeTool(deps(101, "req-A"));
    const first = text(await tool.execute("c1", { mail_id: 5 }));
    const second = text(await tool.execute("c2", { mail_id: 5 }));
    expect(COUNT.write).toBe(1);
    expect(first).toContain("已领取邮件 5");
    expect(second).toContain("已执行过");
    expect(second).toContain("已领取邮件 5");
  });

  it("作用域：同请求不同参数各自执行；同玩家新请求不误判；不同玩家不共享", async () => {
    const a = writeTool(deps(102, "req-A"));
    await a.execute("c1", { mail_id: 5 });
    await a.execute("c2", { mail_id: 6 });
    expect(COUNT.write).toBe(2);

    await writeTool(deps(102, "req-B")).execute("c3", { mail_id: 5 });
    expect(COUNT.write).toBe(3); // 新请求 = 玩家明确重做，不该被挡

    await writeTool(deps(103, "req-A")).execute("c4", { mail_id: 5 });
    expect(COUNT.write).toBe(4); // 幂等表不跨玩家
  });

  it("只读工具刻意不去重：每次都查最新值", async () => {
    const tool = readTool(deps(104, "req-A"));
    await tool.execute("c1", {});
    await tool.execute("c2", {});
    expect(COUNT.read).toBe(2);
  });

  it("失败后重试仍放行（失败不进幂等表；执行中被拦）", () => {
    const guard = new ToolGuard(105, "req-A");
    const first = guard.before("claim_mail_attachments", { mail_id: 9 });
    expect(first.blocked).toBeNull();
    expect(first.key).not.toBe("");

    const during = guard.before("claim_mail_attachments", { mail_id: 9 });
    expect(during.blocked).toContain("正在执行");

    guard.abort(first.key); // 模拟执行失败
    const retry = guard.before("claim_mail_attachments", { mail_id: 9 });
    expect(retry.blocked).toBeNull();
    expect(retry.key).not.toBe("");
  });

  it("request_id 缺失时降级为不做幂等（绝无误判跳过执行）", () => {
    const guard = new ToolGuard(109, "");
    const a = guard.before("claim_mail_attachments", { mail_id: 1 });
    const b = guard.before("claim_mail_attachments", { mail_id: 1 });
    expect([a.blocked, b.blocked, a.key, b.key]).toEqual([null, null, "", ""]);
  });

  it("幂等 TTL 常量与 Python 版一致", () => {
    expect(IDEMPOTENCY_TTL_SECONDS).toBe(600);
  });
});

describe("限频", () => {
  it(`整体限频：${RATE_WINDOW_SECONDS}s 窗口只放行 ${RATE_MAX_CALLS} 次，超限文案可读，按玩家隔离`, () => {
    const guard = new ToolGuard(106, "req-A");
    let allowed = 0;
    for (let i = 0; i < RATE_MAX_CALLS + 5; i += 1) {
      if (guard.before("get_bag_items", {}).blocked === null) allowed += 1;
    }
    expect(allowed).toBe(RATE_MAX_CALLS);

    const blocked = guard.before("get_bag_items", {}).blocked;
    expect(blocked).toContain("频繁");

    expect(new ToolGuard(107, "req-B").before("get_bag_items", {}).blocked).toBeNull();
  });

  it(`写工具限频更严：${RATE_WINDOW_SECONDS}s 窗口只放行 ${WRITE_RATE_MAX_CALLS} 次`, () => {
    const guard = new ToolGuard(108, "req-A");
    let allowed = 0;
    for (let i = 0; i < WRITE_RATE_MAX_CALLS + 3; i += 1) {
      const outcome = guard.before("claim_mail_attachments", { mail_id: 100 + i });
      if (outcome.blocked === null) {
        allowed += 1;
        guard.abort(outcome.key); // 撤掉占位，本用例只测写限频窗口
      }
    }
    expect(allowed).toBe(WRITE_RATE_MAX_CALLS);
  });
});

describe("单轮预算", () => {
  it(`单轮只放行 ${MAX_TOOL_CALLS_PER_ROUND} 个调用，超限回溢出文案`, async () => {
    const run: RunState = newRunState(110, "req-A", {});
    const tool = readTool({ playerId: 110, run });
    const total = MAX_TOOL_CALLS_PER_ROUND + 3;
    const results: string[] = [];
    for (let i = 0; i < total; i += 1) {
      results.push(text(await tool.execute(`c${i}`, {})));
    }
    expect(COUNT.read).toBe(MAX_TOOL_CALLS_PER_ROUND);
    const overflow = results.filter((r) => r === ROUND_OVERFLOW_MESSAGE);
    expect(overflow).toHaveLength(3);
  });

  it("轮次边界复位后可继续放行（turn_start 语义）", async () => {
    const run: RunState = newRunState(111, "req-A", {});
    const tool = readTool({ playerId: 111, run });
    for (let i = 0; i < MAX_TOOL_CALLS_PER_ROUND; i += 1) await tool.execute(`c${i}`, {});
    expect(text(await tool.execute("c-over", {}))).toBe(ROUND_OVERFLOW_MESSAGE);

    run.roundToolCalls = 0; // 模拟 pi 的 turn_start
    expect(text(await tool.execute("c-next", {}))).toContain("背包快照");
  });
});
