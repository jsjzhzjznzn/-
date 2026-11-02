/**
 * 压缩单测（P5 验收）：切点算法、准备、编排、提示词与「工具结果不进摘要」这条关键约束。
 *
 * 全部注入式（SummarizeFn 用假实现）——不起网络，断言确定性。
 */
import { describe, expect, it, vi } from "vitest";

import {
  DEFAULT_COMPACTION_SETTINGS,
  compact,
  estimateContextTokens,
  estimateTokens,
  findCutPoint,
  generateSummaryWithUsage,
  prepareCompaction,
  shouldCompact,
  type CompactionSettings,
  type SummarizeFn,
} from "../../src/session/compaction.js";
import type { MessageEntry, SessionEntry } from "../../src/session/types.js";

const SETTINGS: CompactionSettings = { enabled: true, reserveTokens: 1000, keepRecentTokens: 100 };

let seq = 0;
function userEntry(text: string): MessageEntry {
  seq += 1;
  return {
    type: "message",
    id: `u${seq}`,
    parentId: null,
    createdMs: 0,
    message: { role: "user", content: [{ type: "text", text }], timestamp: 0 } as never,
  };
}

function assistantEntry(text: string, toolCalls: { name: string; args: Record<string, unknown> }[] = []): MessageEntry {
  seq += 1;
  const content: unknown[] = [];
  if (text) content.push({ type: "text", text });
  toolCalls.forEach((call, index) =>
    content.push({ type: "toolCall", id: `tc${index}`, name: call.name, arguments: call.args }),
  );
  return {
    type: "message",
    id: `a${seq}`,
    parentId: null,
    createdMs: 0,
    message: {
      role: "assistant",
      content,
      api: "openai-completions",
      provider: "deepseek",
      model: "deepseek-chat",
      usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0 },
      stopReason: toolCalls.length > 0 ? "toolUse" : "stop",
      timestamp: 0,
    } as never,
  };
}

function toolResultEntry(toolName: string, text: string): MessageEntry {
  seq += 1;
  return {
    type: "message",
    id: `t${seq}`,
    parentId: null,
    createdMs: 0,
    message: {
      role: "toolResult",
      toolCallId: `tc-${seq}`,
      toolName,
      content: [{ type: "text", text }],
      isError: false,
      timestamp: 0,
    } as never,
  };
}

/** 一「轮」= user + assistant(+toolCall) + toolResult + assistant */
function turn(question: string, answer: string, toolPayload?: string): SessionEntry[] {
  const entries: SessionEntry[] = [userEntry(question)];
  if (toolPayload) {
    entries.push(assistantEntry("", [{ name: "get_bag_items", args: {} }]));
    entries.push(toolResultEntry("get_bag_items", toolPayload));
  }
  entries.push(assistantEntry(answer));
  return entries;
}

describe("token 估算与触发判据（pi 原文）", () => {
  it("estimateTokens = ceil(chars/4)，assistant 计入工具调用参数", () => {
    expect(estimateTokens(userEntry("12345678").message)).toBe(2);
    expect(estimateTokens(assistantEntry("12345678").message)).toBe(2);
    const withCall = assistantEntry("", [{ name: "get_bag_items", args: { a: 1 } }]);
    expect(estimateTokens(withCall.message)).toBeGreaterThan(0);
  });

  it("estimateContextTokens：有 assistant usage 时以它为基准 + 其后估算；否则全量估算", () => {
    const noUsage = estimateContextTokens([userEntry("你好").message]);
    expect(noUsage.usageTokens).toBe(0);
    expect(noUsage.tokens).toBe(noUsage.trailingTokens);

    const withUsage = assistantEntry("回复");
    (withUsage.message as { usage: Record<string, number>; stopReason: string }).usage = {
      input: 1000,
      output: 20,
      cacheRead: 0,
      cacheWrite: 0,
      totalTokens: 1020,
    };
    const estimate = estimateContextTokens([userEntry("你好").message, withUsage.message]);
    expect(estimate.usageTokens).toBe(1020);
    expect(estimate.tokens).toBeGreaterThanOrEqual(1020);
  });

  it("shouldCompact：contextTokens > contextWindow - reserveTokens；enabled=false 永不触发", () => {
    // contextWindow 10000、reserve 1000 → 阈值 9000
    expect(shouldCompact(9000, 10_000, SETTINGS)).toBe(false);
    expect(shouldCompact(9001, 10_000, SETTINGS)).toBe(true);
    expect(shouldCompact(99_999, 10_000, { ...SETTINGS, enabled: false })).toBe(false);
  });

  it("默认设置与 pi 一致（reserve 16384 / keepRecent 20000）", () => {
    expect(DEFAULT_COMPACTION_SETTINGS).toEqual({ enabled: true, reserveTokens: 16384, keepRecentTokens: 20000 });
  });
});

describe("切点算法", () => {
  it("从最新往回攒够 keepRecentTokens，且**绝不在 toolResult 处切**", () => {
    // 造 6 轮，每轮都带工具结果
    const entries: SessionEntry[] = [];
    for (let i = 0; i < 6; i += 1) entries.push(...turn(`问题${i}`, `回答${i}`, `背包快照${i}`));

    const cut = findCutPoint(entries, 0, entries.length, 40);
    expect(cut.firstKeptEntryIndex).toBeGreaterThan(0);
    expect(entries[cut.firstKeptEntryIndex]!.type).toBe("message");
    const kept = entries[cut.firstKeptEntryIndex]! as MessageEntry;
    expect(kept.message.role).not.toBe("toolResult"); // 关键不变量
  });

  it("切在轮中间时 isSplitTurn=true 并给出该轮起点", () => {
    const entries: SessionEntry[] = [];
    for (let i = 0; i < 6; i += 1) entries.push(...turn(`问题${i}`, `回答${i}`));
    // keepRecentTokens 很小 → 尽量靠近尾部切，可能落在 assistant 上
    const cut = findCutPoint(entries, 0, entries.length, 1);
    if (cut.isSplitTurn) {
      expect(cut.turnStartIndex).toBeGreaterThanOrEqual(0);
      const turnStart = entries[cut.turnStartIndex]! as MessageEntry;
      expect(turnStart.message.role).toBe("user");
    } else {
      expect(cut.turnStartIndex).toBe(-1);
    }
  });

  it("条目太少（不足以切）时退回起点，不产生空摘要", () => {
    const entries = turn("问题", "回答");
    const cut = findCutPoint(entries, 0, entries.length, 100_000);
    expect(cut.firstKeptEntryIndex).toBe(0);
  });
});

describe("压缩准备（prepareCompaction）", () => {
  it("切点之后就没什么可摘要 → undefined（防死循环）", () => {
    const entries = turn("问题", "回答");
    expect(prepareCompaction(entries, { ...SETTINGS, keepRecentTokens: 1_000_000 })).toBeUndefined();
  });

  it("最新条目本身就是压缩条目 → undefined", () => {
    const entries: SessionEntry[] = [
      ...turn("问题", "回答"),
      {
        type: "compaction",
        id: "c1",
        parentId: null,
        summary: "旧摘要",
        firstKeptEntryId: "x",
        tokensBefore: 1,
        createdMs: 0,
      },
    ];
    expect(prepareCompaction(entries, SETTINGS)).toBeUndefined();
  });

  it("摘要对象排除 system 块；tokensBefore 由投影估算", () => {
    const entries: SessionEntry[] = [];
    for (let i = 0; i < 8; i += 1) entries.push(...turn(`问题${i}`, `回答${i}`, `背包快照${i}` + "x".repeat(50)));
    const prep = prepareCompaction(entries, { ...SETTINGS, keepRecentTokens: 20 });
    expect(prep).toBeDefined();
    expect(prep!.messagesToSummarize.length).toBeGreaterThan(0);
    expect(prep!.messagesToSummarize.every((message) => message.role !== "system")).toBe(true);
    expect(prep!.tokensBefore).toBeGreaterThan(0);
  });

  it("已有压缩条目时带上 previousSummary，并从上次保留段之后开始", () => {
    const early = turn("老问题", "老回答");
    const kept = turn("新问题", "新回答");
    const entries: SessionEntry[] = [
      ...early,
      {
        type: "compaction",
        id: "c1",
        parentId: null,
        summary: "旧摘要内容",
        firstKeptEntryId: kept[0]!.id,
        tokensBefore: 10,
        createdMs: 0,
      },
      ...kept,
      ...turn("最新问题", "最新回答"),
    ];
    const prep = prepareCompaction(entries, { ...SETTINGS, keepRecentTokens: 1 });
    if (prep) {
      expect(prep.previousSummary).toBe("旧摘要内容");
      // 老问题已经被摘要过，不应重复进摘要对象
      expect(JSON.stringify(prep.messagesToSummarize)).not.toContain("老问题");
    }
  });
});

describe("摘要编排与小节提示词（compact）", () => {
  const entries: SessionEntry[] = [];
  for (let i = 0; i < 8; i += 1) entries.push(...turn(`问题${i}`, `回答${i}`, `背包快照-过期数字${i}`));

  const fakeSummarizer = (captured: { system: string; user: string; maxTokens: number }[]): SummarizeFn => {
    return async (system, user, maxTokens) => {
      captured.push({ system, user, maxTokens });
      return { text: "## Goal\n摘要正文" };
    };
  };

  it("首轮压缩：提示词含 <conversation> 与结构化格式要求，结果回传切点与 tokensBefore", async () => {
    const captured: { system: string; user: string; maxTokens: number }[] = [];
    const prep = prepareCompaction(entries, { ...SETTINGS, keepRecentTokens: 20 })!;
    const result = await compact(prep, fakeSummarizer(captured), "游戏事实焦点");

    expect(result.summary).toContain("摘要正文");
    expect(result.firstKeptEntryId).toBe(prep.firstKeptEntryId);
    expect(result.tokensBefore).toBe(prep.tokensBefore);
    expect(captured[0]!.system).toContain("Do NOT continue the conversation");
    expect(captured[0]!.user).toContain("<conversation>");
    expect(captured[0]!.user).toContain("## Critical Context");
    expect(captured[0]!.user).toContain("Additional focus: 游戏事实焦点");
    expect(captured[0]!.maxTokens).toBe(Math.floor(0.8 * SETTINGS.reserveTokens));
  });

  it("**工具结果不进摘要**（过期游戏数据不得沉淀进摘要），但保留「查了什么工具」", async () => {
    const captured: { system: string; user: string; maxTokens: number }[] = [];
    const prep = prepareCompaction(entries, { ...SETTINGS, keepRecentTokens: 20 })!;
    await compact(prep, fakeSummarizer(captured), undefined);

    expect(captured[0]!.user).not.toContain("过期数字");
    expect(captured[0]!.user).toContain("[Assistant tool calls]: get_bag_items(");
    expect(captured[0]!.user).toContain("[User]: 问题");
  });

  it("增量压缩：带 <previous-summary> 且用更新版提示词", async () => {
    const captured: { system: string; user: string; maxTokens: number }[] = [];
    const kept = turn("新问题", "新回答");
    const withPrev: SessionEntry[] = [
      ...turn("老问题", "老回答"),
      {
        type: "compaction",
        id: "c1",
        parentId: null,
        summary: "旧摘要内容",
        firstKeptEntryId: kept[0]!.id,
        tokensBefore: 10,
        createdMs: 0,
      },
      ...kept,
      ...turn("最新问题", "最新回答"),
    ];
    const prep = prepareCompaction(withPrev, { ...SETTINGS, keepRecentTokens: 1 })!;
    await compact(prep, fakeSummarizer(captured), undefined);

    expect(captured[0]!.user).toContain("<previous-summary>\n旧摘要内容\n</previous-summary>");
    expect(captured[0]!.user).toContain("PRESERVE all existing information");
  });

  it("摘要器抛错 → compact 抛出（由触发层兜住，不当检查点用）", async () => {
    const prep = prepareCompaction(entries, { ...SETTINGS, keepRecentTokens: 20 })!;
    const boom: SummarizeFn = vi.fn(async () => {
      throw new Error("摘要失败：命中 token 上限，摘要不完整");
    });
    await expect(compact(prep, boom, undefined)).rejects.toThrow("摘要不完整");
  });

  it("generateSummaryWithUsage 的 maxTokens 不超过 8192", async () => {
    const captured: { maxTokens: number }[] = [];
    await generateSummaryWithUsage(
      [userEntry("x").message],
      { ...SETTINGS, reserveTokens: 100_000 },
      fakeSummarizer(captured as never),
    );
    expect(captured[0]!.maxTokens).toBe(8192);
  });
});
