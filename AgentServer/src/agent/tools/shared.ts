/**
 * 工具公共设施：统一「定义 → 执行管道」的收口（对应 tools/__init__.py 的执行管道）。
 *
 * 关键设计（方案 §10.1）：防护闸门（限频/幂等/单轮预算）放在**工具内部**，
 * 拦截时返回**正常文本**（给 LLM 的有效观察），而不是用 pi 的 beforeToolCall 拦截
 * （那会变成 isError 的工具结果，改变 LLM 的措辞策略）。
 *
 * 执行失败则相反：**抛 ToolFailure**，由 pi 转成 isError 的工具结果（SDK 教程 4.3 的标准做法）。
 * 两类中断刻意分开——「闸门拦下」是正常的对话事实（模型自己措辞），「执行失败」是异常事件
 * （模型看到 isError 才会停手、改口、告知玩家），混成一种会让模型把故障当数据复述。
 *
 * 执行管道顺序：防护闸门 → 超时 → 执行 → 截断 → 落幂等账（失败则撤销占位并抛出）。
 */
import type { AgentTool, AgentToolResult } from "@earendil-works/pi-agent-core";
import type { Static, TSchema } from "typebox";

import {
  MAX_TOOL_CALLS_PER_ROUND,
  ROUND_OVERFLOW_MESSAGE,
  TOOL_RESULT_LIMITS,
  TOOL_RESULT_MAX_CHARS,
  TOOL_TIMEOUT_DEFAULT_MS,
  TOOL_TIMEOUT_MS,
  TOOL_TRUNCATED_SUFFIX,
} from "../../core/config.js";
import { log } from "../../core/logger.js";
import { ToolGuard } from "../guard.js";
import type { RunState } from "../runState.js";

export interface ToolDeps {
  playerId: number;
  run: RunState;
}

export interface ToolSpec<P extends TSchema> {
  name: string;
  /** pi 要求的人读标签（UI 展示用）。 */
  label: string;
  /** 给 LLM 的工具描述——必须与 Python 版逐字一致（措辞影响行为）。 */
  description: string;
  parameters: P;
  /** 工具主体：成功返回文本；失败必须 throw（文案要写清「为什么失败 + 下一步怎么做」）。 */
  run: (params: Static<P>, deps: ToolDeps, signal?: AbortSignal) => Promise<string>;
}

/**
 * 业务失败：文案由工具自己组织，defineTool 原样抛出（不再二次包装）。
 *
 * 为什么要区分它和意外异常：意外异常给的是 `ECONNREFUSED` 这种码，模型看了只会换工具乱试；
 * 业务失败是我们已经知道「为什么、该怎么办」的场景（接口不可达 / 写操作未生效），
 * 得把这句话直接送到模型面前。
 */
export class ToolFailure extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ToolFailure";
  }
}

/** 文本结果（pi 规范：content 是给模型的可见结果）。 */
export function textResult(text: string): AgentToolResult<undefined> {
  return { content: [{ type: "text", text }], details: undefined };
}

/** 按工具名截断（表外默认 1200 字符）。 */
export function truncateResult(name: string, text: string): string {
  const limit = TOOL_RESULT_LIMITS[name] ?? TOOL_RESULT_MAX_CHARS;
  return text.length > limit ? text.slice(0, limit) + TOOL_TRUNCATED_SUFFIX : text;
}

/** 工具自身超时（默认 8s，dispatch_subagent 40s）。 */
export function toolTimeoutMs(name: string): number {
  return TOOL_TIMEOUT_MS[name] ?? TOOL_TIMEOUT_DEFAULT_MS;
}

/**
 * 定义一个受管工具：防护闸门 → 超时 → 执行 → 截断。
 *
 * 闸门顺序（与 Python 版 tools/__init__.py 的 registry.call 一致）：
 *   ① 单轮预算（超过 MAX_TOOL_CALLS_PER_ROUND 的调用直接回溢出文案）
 *   ② 限频 / 幂等（ToolGuard；拦截也回**正常文本**，让 LLM 自己措辞）
 *   ③ 执行：成功 → 截断 → 落幂等账（失败永不进幂等表）
 *             失败 → 撤销幂等占位 → **抛 ToolFailure**（框架转 isError，附可执行提示）
 */
/**
 * 失败 → 抛给框架的错误。框架（pi-agent-core executePreparedToolCall）会把它转成
 * `{ content: 这句话, isError: true }` 的工具结果，模型看到的就是这段文案。
 *
 * 所以文案要按「给模型的指令」写，而不是按「给日志的调试信息」写：
 * 说清是故障不是空数据、以及下一步该做什么（照实告知玩家 / 别再重试）。
 */
function toToolError(name: string, err: unknown, timedOut: boolean): Error {
  if (err instanceof ToolFailure) return err; // 业务失败：文案已由工具组织好
  if (timedOut) {
    return new ToolFailure(
      `工具 ${name} 执行超时（${toolTimeoutMs(name)}ms）。这是服务端响应过慢，不是参数问题；` +
        "请据实告知玩家稍后再试，不要用同一工具反复重试。",
    );
  }
  const reason = err instanceof Error ? err.name : "Error";
  return new ToolFailure(
    `工具 ${name} 执行失败: ${reason}。这是内部服务故障，不是「没有数据」；` +
      "请据实告知玩家稍后再试，不要换其他工具盲目重试。",
  );
}

export function defineTool<P extends TSchema>(spec: ToolSpec<P>, deps: ToolDeps): AgentTool<P> {
  return {
    name: spec.name,
    label: spec.label,
    description: spec.description,
    parameters: spec.parameters,
    execute: async (_toolCallId, params, signal) => {
      const run = deps.run;

      // ① 单轮预算：LLM 一口气吐几十个 tool_call 时只放行前 N 个
      if (run.roundToolCalls >= MAX_TOOL_CALLS_PER_ROUND) {
        return textResult(ROUND_OVERFLOW_MESSAGE);
      }
      run.roundToolCalls += 1;

      // ② 防护闸门（限频 / 幂等）
      const guard = new ToolGuard(deps.playerId, run.requestId);
      const { key, blocked } = guard.before(spec.name, params);
      if (blocked !== null) return textResult(blocked);

      const timeout = AbortSignal.timeout(toolTimeoutMs(spec.name));
      const merged = signal ? AbortSignal.any([signal, timeout]) : timeout;
      let text: string;
      try {
        text = await spec.run(params, deps, merged);
      } catch (err) {
        // 失败永不进幂等表：必须允许重试（写工具不撤销占位，本请求内会被判「正在执行」永久挡住）
        guard.abort(key);
        if (!(err instanceof ToolFailure)) {
          log.warn(`工具执行失败 name=${spec.name} player=${deps.playerId}`, err);
        }
        throw toToolError(spec.name, err, merged.aborted);
      }
      const truncated = truncateResult(spec.name, text);
      guard.after(key, truncated); // 截断后的文本才是幂等复用的结果（与 Python 版一致）
      return textResult(truncated);
    },
  };
}
