/**
 * P0 契约测试：冻结常量必须与 Python 版逐字一致（方案 §4.2/§4.3 冻结契约）。
 *
 * 这些断言是「重构期间防行为漂移」的第一道闸：改这些常量 = 改行为，
 * 必须先在方案文档里登记为「有意变更」。
 */
import { describe, expect, it } from "vitest";

import {
  CACHE_LENGTH_TOLERANCE,
  CACHE_SIMILARITY,
  FRESHNESS_KEYWORDS,
  FRESHNESS_NUDGE_BLOCK,
  FRESHNESS_REMINDER,
  GAME_DATA_KEYWORDS,
  IDEMPOTENCY_TTL_SECONDS,
  KB_ANNOUNCE_RETRY_MS,
  KB_CANDIDATE_LIMIT,
  KB_CHUNK_TEXT_CAP,
  KB_DEFAULT_TOP_K,
  KB_MAX_TOP_K,
  LTM_KEY_MAX_CHARS,
  LTM_MAX_FACTS_PER_ROUND,
  MAX_FRESHNESS_NUDGES,
  MAX_LTM_ENTRIES,
  MAX_QA_CACHE_PER_PLAYER,
  MAX_REFLECTIONS,
  MAX_REPLY_CHARS,
  MAX_TOOL_CALLS_PER_ROUND,
  MAX_TOOL_ROUNDS,
  NON_DATA_TOOLS,
  PROACTIVE_FALLBACK,
  PROACTIVE_FALLBACK_GENERIC,
  RATE_MAX_CALLS,
  RATE_WINDOW_SECONDS,
  ROUNDS_EXHAUSTED_REPLY,
  ROUND_OVERFLOW_MESSAGE,
  SUB_MAX_ROUNDS,
  SUB_RESULT_MAX_CHARS,
  SUB_WALL_BUDGET_MS,
  TOOL_RESULT_LIMITS,
  TOOL_RESULT_MAX_CHARS,
  TOOL_TIMEOUT_DEFAULT_MS,
  TOOL_TIMEOUT_MS,
  TOOL_TRUNCATED_SUFFIX,
  WRITE_TOOLS,
  WRITE_RATE_MAX_CALLS,
} from "../../src/core/config.js";

describe("冻结常量（对齐 Python 版）", () => {
  it("Agent 循环预算", () => {
    expect(MAX_TOOL_ROUNDS).toBe(5);
    expect(MAX_REPLY_CHARS).toBe(1500);
    expect(MAX_REFLECTIONS).toBe(1);
    expect([...NON_DATA_TOOLS]).toEqual(["set_agent_style"]);
    expect(ROUNDS_EXHAUSTED_REPLY).toBe(
      "抱歉主人，这一单查得有点久还没走完，请稍后再问一次吧。",
    );
  });

  it("时效关键词逐字（含重复项，避免行为漂移）", () => {
    expect([...FRESHNESS_KEYWORDS]).toEqual([
      "背包", "包裹", "物品", "道具", "任务", "进度", "可提交", "可接取", "排行", "名次",
      "榜单", "邮件", "保底", "好友", "在线", "天气", "现在", "当前", "今天", "几点",
      "月亮", "几点",
    ]);
    expect(FRESHNESS_REMINDER).toBe(
      "【时效性提醒】玩家询问的是实时游戏数据，此前对话里出现过的数字可能已经过期，" +
        "禁止复用历史对话中的旧数据回答，必须调用对应工具查询最新值后再作答。",
    );
  });

  it("时效强制查库（D10：堵住「没查库也没质检」的缝，已登记为有意变更）", () => {
    expect([...GAME_DATA_KEYWORDS]).toEqual([
      "背包", "包裹", "物品", "道具", "任务", "进度", "可提交", "可接取", "排行", "名次",
      "榜单", "邮件", "邮箱", "保底", "好友", "在线",
    ]);
    expect(MAX_FRESHNESS_NUDGES).toBe(1);
    expect(FRESHNESS_NUDGE_BLOCK).toBe(
      "【反思】玩家问的是实时游戏数据，但你没有查询就作答了——历史对话里的数字可能已经过期，禁止复用。" +
        "请立刻调用对应工具核实后重新回答（背包/物品用 get_bag_items、任务进度用 get_quest_status、" +
        "邮件用 get_mail_summary、好友用 get_friend_list、排行榜/名次用 get_leaderboard、" +
        "抽卡保底用 get_gacha_status），再据实作答。",
    );
  });

  it("防护闸门参数与文案", () => {
    expect(MAX_TOOL_CALLS_PER_ROUND).toBe(8);
    expect(RATE_WINDOW_SECONDS).toBe(60);
    expect(RATE_MAX_CALLS).toBe(40);
    expect(WRITE_RATE_MAX_CALLS).toBe(5);
    expect(IDEMPOTENCY_TTL_SECONDS).toBe(600);
    expect([...WRITE_TOOLS]).toEqual(["claim_mail_attachments"]);
    expect(ROUND_OVERFLOW_MESSAGE).toBe(
      "单轮工具调用数超过上限（8 个），本条调用已被省略，请只保留最必要的查询后重试。",
    );
  });

  it("工具超时与结果长度表", () => {
    expect(TOOL_TIMEOUT_MS["dispatch_subagent"]).toBe(40_000);
    expect(TOOL_TIMEOUT_DEFAULT_MS).toBe(8_000);
    expect(TOOL_RESULT_MAX_CHARS).toBe(1200);
    expect(TOOL_RESULT_LIMITS["get_update_announcements"]).toBe(6000);
    expect(TOOL_RESULT_LIMITS["search_game_knowledge"]).toBe(1500);
    expect(TOOL_TRUNCATED_SUFFIX).toBe("…(结果过长已截断)");
  });

  it("子代理预算", () => {
    expect(SUB_MAX_ROUNDS).toBe(4);
    expect(SUB_WALL_BUDGET_MS).toBe(25_000);
    expect(SUB_RESULT_MAX_CHARS).toBe(900);
  });

  it("记忆与缓存上限", () => {
    expect(MAX_QA_CACHE_PER_PLAYER).toBe(100);
    expect(MAX_LTM_ENTRIES).toBe(12);
    expect(CACHE_SIMILARITY).toBe(0.9);
    expect(CACHE_LENGTH_TOLERANCE).toBe(2);
    expect(LTM_KEY_MAX_CHARS).toBe(8);
    expect(LTM_MAX_FACTS_PER_ROUND).toBe(2);
  });

  it("知识库参数", () => {
    expect(KB_CHUNK_TEXT_CAP).toBe(320);
    expect(KB_DEFAULT_TOP_K).toBe(3);
    expect(KB_MAX_TOP_K).toBe(5);
    expect(KB_CANDIDATE_LIMIT).toBe(12);
    expect(KB_ANNOUNCE_RETRY_MS).toBe(60_000);
  });

  it("主动提醒兜底文案", () => {
    expect(PROACTIVE_FALLBACK[1]).toBe("主人，邮箱里有带附件的信还没领，记得去看看哦。");
    expect(PROACTIVE_FALLBACK_GENERIC).toBe("主人，有个小提醒，记得回来看看哦。");
  });
});
