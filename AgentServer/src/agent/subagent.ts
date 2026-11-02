/**
 * 研究子代理（逐字搬 tools/subagent.py 的语义，方案 §11.3）。
 *
 * 模式（ZCode explore 子代理 / Claude Code Task 工具同款）：
 *  - 主代理持 dispatch_subagent(task) 派发工具，把多步研究/综合规划任务整段下传；
 *  - 子代理 = **独立的 pi Agent 实例**（自己的转写本）——查库中间过程只活在子代理上下文里，
 *    跑完只把 ≤900 字结论回传主图（上下文隔离）；pi 明确不提供 sub-agents，这里是自建；
 *  - 递归防护铁律：子代理工具表只有数据类只读工具，**不含 dispatch_subagent / set_agent_style**
 *    （子代理不能再生成子代理）；
 *  - 无会话持久化：瞬态，一个任务一个实例，结果交回后即消亡（故障也不污染主对话）；
 *  - 预算：≤4 工具轮 + 25s 墙钟（到顶即收，返回已得的部分结论）——外层工具超时 40s 兜底，
 *    保证主图整体在网关 60s HTTP 预算内；
 *  - Phase 14：子代理与主图**共享同一套限频/幂等口径**（guard），request_id 由主图透传
 *    ——否则子代理就是绕过限频的后门。
 */
import { Agent } from "@earendil-works/pi-agent-core";

import { SUB_MAX_ROUNDS, SUB_RESULT_MAX_CHARS, SUB_WALL_BUDGET_MS } from "../core/config.js";
import { log } from "../core/logger.js";
import { chatModel, streamFn } from "../llm/provider.js";
import { contentText } from "../core/text.js";
import { newRunState } from "./runState.js";
import { buildSubagentTools } from "./tools/index.js";

/** 子代理系统提示（逐字）。 */
export const SUB_SYSTEM_PROMPT =
  "你是游戏助手内部的【研究子代理】，玩家看不到你的过程，也看不到你的身份，" +
  "你只需要把任务研究清楚、把结论写明白。" +
  "规则：涉及玩家数据（背包/任务/邮件/好友/排行榜/抽卡等）必须调用提供的工具查真实值，" +
  "禁止编造数字；工具失败就注明'该项数据暂缺'，继续研究其余部分；" +
  "结论用中文纯文本，不超过 800 字，按要点分行，先结论后建议；" +
  "研究完成就输出结论并停止，不要继续调用工具。";

/** 预算耗尽时的部分结论（逐字，与 Python 版一致）。 */
export function salvageBudgetText(toolResultTexts: readonly string[]): string {
  const partials = toolResultTexts.slice(-2).map((text) => String(text).slice(0, 300));
  return (
    "研究预算耗尽（未能完成最后一轮查询），已得的部分结论：\n" +
    (partials.length > 0 ? partials.join("\n") : "（无）")
  );
}

/** 结论长度兜底（主图 registry 还会再截 1200）。 */
export function trimSubResult(text: string): string {
  return text.slice(0, SUB_RESULT_MAX_CHARS);
}

const EMPTY_RESULT = "子代理未能得出结论，请换个问法再试。";

export interface SubagentScope {
  playerId: number;
  requestId: string;
}

/**
 * 跑一次研究子代理：瞬态 Agent（无持久化），返回简明结论文本。
 * 任何异常都降级成结构化文本（子代理失败不阻塞主对话——故障隔离）。
 */
export async function runSubagent(task: string, scope: SubagentScope): Promise<string> {
  const started = Date.now();
  const run = newRunState(scope.playerId, scope.requestId, { source: "subagent" });
  const tools = buildSubagentTools({ playerId: scope.playerId, run });

  const toolResultTexts: string[] = [];
  let lastAssistantText = "";
  let lastAssistantHadToolCall = false;
  let toolRounds = 0;

  let agent: Agent | null = null;
  const controller = new AbortController();
  const wallTimer = setTimeout(() => {
    log.warn(`子代理墙钟到顶（${SUB_WALL_BUDGET_MS}ms），收尾 player=${scope.playerId}`);
    agent?.abort();
  }, SUB_WALL_BUDGET_MS);

  try {
    agent = new Agent({
      initialState: {
        systemPrompt: SUB_SYSTEM_PROMPT,
        model: chatModel,
        tools,
        messages: [],
      },
      streamFn,
      sessionId: `player_${scope.playerId}_sub`, // 独立缓存命名空间（与主图隔离）
      finishTurn: async (turn) => {
        if (turn.toolResults.length > 0) {
          toolRounds += 1;
          // 工具轮预算到顶：结束循环，用已得的部分结论收尾（等价 Python 的路由到 END）
          if (toolRounds >= SUB_MAX_ROUNDS) return { action: "end" as const };
        }
        return undefined;
      },
    });

    agent.subscribe((event) => {
      if (event.type !== "message_end") return;
      const message = event.message;
      if (message.role === "toolResult") {
        toolResultTexts.push(contentText(message.content));
        return;
      }
      if (message.role === "assistant") {
        const text = contentText(message.content);
        const hasToolCall = message.content.some((block) => block.type === "toolCall");
        if (hasToolCall) {
          lastAssistantHadToolCall = true;
        } else if (text.trim()) {
          lastAssistantText = text;
          lastAssistantHadToolCall = false;
        }
      }
    });

    await agent.prompt(task.slice(0, 2000));

    let text = lastAssistantHadToolCall ? "" : lastAssistantText;
    if (!text.trim()) {
      // 预算耗尽在工具调用处截断 → 兜底转写（只取最后两条工具结果）
      text = toolResultTexts.length > 0 ? salvageBudgetText(toolResultTexts) : EMPTY_RESULT;
    }
    if (!text.trim()) text = EMPTY_RESULT;
    const result = trimSubResult(text);
    log.info(
      `子代理完成 player=${scope.playerId} 工具轮=${toolRounds}/4 工具结果=${toolResultTexts.length} ` +
        `耗时=${Date.now() - started}ms 结论长度=${result.length}`,
    );
    return result;
  } catch (err) {
    log.warn(`子代理研究失败 player=${scope.playerId}`, err);
    const reason = err instanceof Error ? err.name : "Error";
    return `子代理研究失败（${reason}），请直接用数据工具查询或稍后再试。`;
  } finally {
    clearTimeout(wallTimer);
    void controller;
  }
}

/** 主图的派发工具实现（闭包绑定 player_id 与 request_id；schema 无这两个参数）。 */
export async function dispatchSubagent(task: string, scope: SubagentScope): Promise<string> {
  return await runSubagent(task, scope);
}

/** 派发工具的给 LLM 的工具描述（逐字照搬 Python docstring）。 */
export const DISPATCH_SUBAGENT_DESCRIPTION =
  "把复杂的多步研究/综合规划类任务，以及长文档的阅读与总结类任务整段派给内部研究子代理深入处理。" +
  "子代理会自己查真实数据、读长文本（如更新公告全文）并提炼要点，返回简明结论（你看不到它的过程，只拿结论）。" +
  "适用：'综合分析我的游戏现状并规划成长路线'、'研究一下怎么提升挑战排名'、" +
  "'帮我总结这次更新公告讲了什么'这类需要消化大量内容或跨多个数据源综合的任务。" +
  "不适用：简单事实查询（如'我背包里有什么'）——那些直接用对应数据工具，别派子代理。" +
  "task 参数写清研究目标与要点，一两句话即可（子代理看不到我们的对话，只看得到这个任务描述）。";
