/**
 * P6 单测：反思质检解析与触发守卫、子代理白名单与预算收尾文案。
 *
 * 说明：反思的「判不过 → 重答」真实路径走 LLM，放在真机 E2E 验；
 * 这里覆盖它**不该触发**的全部分支（错误/中断回合、预算耗尽、无数据工具、空回答）
 * 与解析容错（fail-open）。
 */
import { describe, expect, it } from "vitest";

import { critiqueBlockText, parseVerdict } from "../../src/agent/critic.js";
import { makeFinishTurn, type SessionView } from "../../src/agent/hooks.js";
import { newRunState, type RunState } from "../../src/agent/runState.js";
import {
  SUB_SYSTEM_PROMPT,
  salvageBudgetText,
  trimSubResult,
} from "../../src/agent/subagent.js";
import { buildMainTools, buildSubagentTools } from "../../src/agent/tools/index.js";
import type { ToolDeps } from "../../src/agent/tools/shared.js";

function deps(playerId = 903): ToolDeps {
  const run: RunState = newRunState(playerId, "req-sub", {});
  return { playerId, run };
}

function view(run: RunState, entries: SessionView["entries"] = []): SessionView {
  return { playerId: run.playerId, tools: [], entries, run };
}

/** 造一条「助手文本回答」的 turn（不用带工具结果——对照数据在 run.dataToolResults 上）。 */
function turn(overrides: { stopReason?: string; answer?: string; withToolCall?: boolean } = {}) {
  const content: unknown[] = [];
  if (overrides.withToolCall) content.push({ type: "toolCall", id: "tc", name: "get_bag_items", arguments: {} });
  if (overrides.answer !== "") content.push({ type: "text", text: overrides.answer ?? "你背包里有 3 格物品。" });
  return {
    message: {
      role: "assistant",
      content,
      stopReason: overrides.stopReason ?? "stop",
    },
    toolResults: [],
    context: { messages: [] },
    newMessages: [],
  } as never;
}

describe("质检结论解析（fail-open 宽松原则）", () => {
  it("pass:true / 缺 pass 字段 / 非法 JSON / 空串 一律判过", () => {
    expect(parseVerdict('{"pass": true}').passed).toBe(true);
    expect(parseVerdict('{"reason":"x"}').passed).toBe(true);
    expect(parseVerdict("这不是 JSON").passed).toBe(true);
    expect(parseVerdict("").passed).toBe(true);
    expect(parseVerdict("```json\nnot json\n```").passed).toBe(true);
  });

  it("pass:false 取 reason 并截断到 120 字；无 reason 用默认病症", () => {
    expect(parseVerdict('{"pass": false, "reason": "数字与工具结果矛盾"}')).toEqual({
      passed: false,
      reason: "数字与工具结果矛盾",
    });
    expect(parseVerdict('{"pass": false}')).toEqual({ passed: false, reason: "回复未通过对照校验" });
    const long = parseVerdict(`{"pass": false, "reason": "${"长".repeat(200)}"}`);
    expect(long.reason.length).toBe(120);
  });

  it("容忍 ```json 围栏与前后噪音", () => {
    expect(parseVerdict('```json\n{"pass": false, "reason": "编造了数字"}\n```')).toEqual({
      passed: false,
      reason: "编造了数字",
    });
    expect(parseVerdict('好的，结论如下 {"pass": true} 完毕').passed).toBe(true);
  });
});

describe("【反思】批评文案（逐字）", () => {
  it("与 Python 版一致", () => {
    expect(critiqueBlockText("数字对不上")).toBe(
      "【反思】你刚才的回复没有通过质检：数字对不上。请立刻重新回答玩家的问题，" +
        "事实性数字必须与工具结果一致，禁止复述有问题的内容。",
    );
  });
});

describe("反思触发守卫（不该评的路径一律不评、不发请求）", () => {
  it("错误/中断回合：硬退出，不评", async () => {
    const run = newRunState(903, "req", {});
    const finish = makeFinishTurn(() => view(run));
    for (const stopReason of ["error", "aborted"]) {
      expect(await finish(turn({ stopReason }))).toBeUndefined();
    }
    expect(run.reflectionRuns).toBe(0);
  });

  it("重答预算已耗尽（≥1）→ 不评（防环）", async () => {
    const run = newRunState(903, "req", {});
    run.reflectionCount = 1;
    expect(await makeFinishTurn(() => view(run))(turn())).toBeUndefined();
    expect(run.reflectionRuns).toBe(0);
  });

  it("工具轮预算耗尽（≥5）→ 不评（兜底回答重试无意义）", async () => {
    const run = newRunState(903, "req", {});
    run.toolRounds = 5;
    expect(await makeFinishTurn(() => view(run))(turn())).toBeUndefined();
  });

  it("本轮没有累积到数据类工具结果 → 不评（闲聊/切风格零成本）", async () => {
    const run = newRunState(903, "req", {});
    expect(await makeFinishTurn(() => view(run))(turn())).toBeUndefined();
    // 只调用过偏好类工具时 runtime 不会累积（NON_DATA_TOOLS 过滤在 runtime 的 message_end 里）
    expect(run.dataToolResults).toHaveLength(0);
  });

  it("还没作答（本轮带工具调用）→ 不评", async () => {
    const run = newRunState(903, "req", {});
    run.dataToolResults.push("[get_bag_items] 背包快照");
    expect(await makeFinishTurn(() => view(run))(turn({ withToolCall: true }))).toBeUndefined();
    expect(run.reflectionRuns).toBe(0);
  });

  it("回答为空 → 不评", async () => {
    const run = newRunState(903, "req", {});
    run.dataToolResults.push("[get_bag_items] 背包快照");
    expect(await makeFinishTurn(() => view(run))(turn({ answer: "" }))).toBeUndefined();
  });
});

describe("研究子代理", () => {
  it("系统提示包含身份隐藏、数据必须查真值、失败注明暂缺、结论格式约束", () => {
    expect(SUB_SYSTEM_PROMPT).toContain("研究子代理");
    expect(SUB_SYSTEM_PROMPT).toContain("玩家看不到你的过程");
    expect(SUB_SYSTEM_PROMPT).toContain("禁止编造数字");
    expect(SUB_SYSTEM_PROMPT).toContain("该项数据暂缺");
    expect(SUB_SYSTEM_PROMPT).toContain("不超过 800 字");
  });

  it("预算耗尽的收尾文案：只取最后两条、单条截 300 字、无结果写（无）", () => {
    expect(salvageBudgetText([])).toBe("研究预算耗尽（未能完成最后一轮查询），已得的部分结论：\n（无）");

    // 只保留最后两条：最早那条（"OLD"）被整条丢弃
    const dropped = salvageBudgetText(["OLD", "B", "C"]);
    expect(dropped).toContain("研究预算耗尽（未能完成最后一轮查询），已得的部分结论：");
    expect(dropped).not.toContain("OLD");
    expect(dropped).toContain("B");
    expect(dropped).toContain("C");

    // 长结果截到 300 字（这条在最后两条之内）
    const trimmed = salvageBudgetText(["B", "A".repeat(400)]);
    expect(trimmed).toContain("A".repeat(300));
    expect(trimmed).not.toContain("A".repeat(301));
  });

  it("结论长度上限 900 字", () => {
    expect(trimSubResult("x".repeat(2000)).length).toBe(900);
  });

  it("白名单：只读工具进、写操作/偏好/派发工具不进（结构性防递归）", () => {
    const main = buildMainTools(deps()).map((tool) => tool.name);
    const sub = buildSubagentTools(deps()).map((tool) => tool.name);

    expect(main).toContain("dispatch_subagent");
    expect(main).toContain("claim_mail_attachments");
    expect(main).toContain("set_agent_style");

    expect(sub).not.toContain("dispatch_subagent");
    expect(sub).not.toContain("claim_mail_attachments");
    expect(sub).not.toContain("set_agent_style");
    // 数据类只读工具与知识库/天气都放行
    for (const name of [
      "get_bag_items", "get_quest_status", "get_mail_summary", "get_friend_list",
      "get_leaderboard", "get_gacha_status", "get_player_profile", "get_update_announcements",
      "get_weather", "search_game_knowledge",
    ]) {
      expect(sub, `${name} 应在子代理白名单里`).toContain(name);
    }
    expect(sub).toHaveLength(main.length - 3);
  });

  it("主图工具数 = 13（与 Python 版一致）", () => {
    expect(buildMainTools(deps())).toHaveLength(13);
  });
});
