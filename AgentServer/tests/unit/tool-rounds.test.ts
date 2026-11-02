/**
 * 工具轮硬上限单测：达到 MAX_TOOL_ROUNDS 必须结束循环并标记 roundsExhausted。
 *
 * 背景：MAX_TOOL_ROUNDS 原先只用于「跳过反思质检」，主图真正停循环靠 55s 的 run 超时 abort
 * ——玩家看到的是报错，而不是配置里那条 ROUNDS_EXHAUSTED_REPLY。这里覆盖判定分支与边界。
 */
import { describe, expect, it } from "vitest";

import { makeFinishTurn } from "../../src/agent/hooks.js";
import { newRunState } from "../../src/agent/runState.js";
import { MAX_TOOL_ROUNDS } from "../../src/core/config.js";

function makeView(toolRounds: number) {
  const run = newRunState(1, "req-rounds", {});
  run.toolRounds = toolRounds;
  return { playerId: 1, tools: [], entries: [], run };
}

/** 一条「助手请求工具 + 已执行出结果」的 turn（finishTurn 看到的形态）。 */
function toolTurn(): never {
  return {
    message: {
      role: "assistant",
      content: [{ type: "toolCall", id: "tc", callId: "tc", name: "get_bag_items", arguments: {} }],
      stopReason: "stop",
    },
    toolResults: [
      {
        role: "toolResult",
        toolCallId: "tc",
        toolName: "get_bag_items",
        content: [{ type: "text", text: "背包 12 格" }],
        timestamp: Date.now(),
      },
    ],
    context: { messages: [] },
    newMessages: [],
  } as never;
}

describe("工具轮硬上限", () => {
  it("第 MAX_TOOL_ROUNDS 轮结束循环并标记兜底", async () => {
    const view = makeView(MAX_TOOL_ROUNDS - 1);

    const decision = await makeFinishTurn(() => view as never)(toolTurn());

    expect(decision).toEqual({ action: "end" });
    expect(view.run.roundsExhausted).toBe(true);
  });

  it("未到上限时放行（继续循环）", async () => {
    const view = makeView(0);

    const decision = await makeFinishTurn(() => view as never)(toolTurn());

    expect(decision).toBeUndefined();
    expect(view.run.roundsExhausted).toBe(false);
  });
});
