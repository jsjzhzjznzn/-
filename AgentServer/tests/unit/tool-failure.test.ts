/**
 * 工具失败语义（2026-10-18 有意变更，对齐 SDK 教程 4.3）：
 * 失败不再降级成「正常文本」，而是抛 ToolFailure，由 pi 转成 isError 的工具结果。
 *
 * 守三条不变量：
 *   ① 失败一定 reject（框架才判 isError，模型才会停手改口）；
 *   ② 只读工具失败后可直接重试（本就不幂等）；
 *   ③ 写工具失败必须撤销幂等占位——否则同一请求内会被判「正在执行」永久挡住。
 */
import { beforeEach, describe, expect, it } from "vitest";
import { Type } from "typebox";

import { resetGuardState } from "../../src/agent/guard.js";
import { newRunState } from "../../src/agent/runState.js";
import { ToolFailure, defineTool, type ToolDeps } from "../../src/agent/tools/shared.js";

const COUNT = { read: 0, write: 0 };

function deps(playerId: number, requestId: string): ToolDeps {
  return { playerId, run: newRunState(playerId, requestId, {}) };
}

function readTool(d: ToolDeps, fail: () => Error) {
  return defineTool(
    {
      name: "get_bag_items",
      label: "假只读工具",
      description: "假只读工具",
      parameters: Type.Object({}),
      run: async () => {
        COUNT.read += 1;
        throw fail();
      },
    },
    d,
  );
}

function writeTool(d: ToolDeps, fail: () => Error) {
  return defineTool(
    {
      name: "claim_mail_attachments",
      label: "假写工具",
      description: "假写工具",
      parameters: Type.Object({ mail_id: Type.Integer() }),
      run: async (params) => {
        COUNT.write += 1;
        if (COUNT.write === 1) throw fail();
        return `已领取邮件 ${params.mail_id}（真实执行第 ${COUNT.write} 次）`;
      },
    },
    d,
  );
}

beforeEach(() => {
  resetGuardState();
  COUNT.read = 0;
  COUNT.write = 0;
});

describe("失败语义：抛错 → 框架转 isError", () => {
  it("未知异常：reject 的文案带工具名 + 下一步动作，且标明是故障不是空数据", async () => {
    const tool = readTool(deps(201, "req-A"), () => new Error("ECONNREFUSED"));
    await expect(tool.execute("c1", {})).rejects.toThrow(/工具 get_bag_items 执行失败/);
    await expect(tool.execute("c2", {})).rejects.toThrow(/不是「没有数据」/);
    await expect(tool.execute("c3", {})).rejects.toThrow(/不要换其他工具盲目重试/);
  });

  it("ToolFailure：业务方组织好的文案原样透出，不被二次包装", async () => {
    const hint = "查询失败（游戏服内部API不可达: timeout）。这是服务端故障，请据实告知玩家稍后再试。";
    const tool = readTool(deps(202, "req-A"), () => new ToolFailure(hint));
    await expect(tool.execute("c1", {})).rejects.toThrow(hint);
  });

  it("只读工具失败后可立即重试（失败不进幂等表）", async () => {
    const tool = readTool(deps(203, "req-A"), () => new Error("boom"));
    await expect(tool.execute("c1", {})).rejects.toThrow();
    await expect(tool.execute("c2", {})).rejects.toThrow();
    expect(COUNT.read).toBe(2);
  });

  it("写工具失败后必须撤销占位：同请求重试仍真实执行，不被「正在执行」挡死", async () => {
    const tool = writeTool(deps(204, "req-A"), () => new ToolFailure("领取失败（写操作未执行: timeout）"));
    await expect(tool.execute("c1", { mail_id: 7 })).rejects.toThrow(/领取失败/);

    const second = await tool.execute("c2", { mail_id: 7 });
    expect(COUNT.write).toBe(2);
    expect(second.content[0]).toMatchObject({ type: "text" });
  });
});
