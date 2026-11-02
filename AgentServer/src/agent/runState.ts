/**
 * 单轮（run）状态：每次 /agent/chat 调用重置同一份对象。
 *
 * 为什么是「重置同一个对象」而不是每轮 new：工具闭包捕获的是这个对象
 * （工具在 Agent 创建时构建一次），换对象会让工具失去与本轮的关联。
 *
 * 对应 Python 版挂在 LangGraph state 上的字段：
 * tool_round_count / reflection_count / reflection_runs / reflection_critique，
 * 外加：request_id（防护闸门作用域）、本轮工具名（used_tools）、玩家快照。
 */
export interface RunState {
  playerId: number;
  /** Phase 14：写工具幂等/限频的作用域键（一次请求一个）。 */
  requestId: string;
  /** 已完成的工具轮数（每轮含 ≥1 个工具结果才算）。 */
  toolRounds: number;
  /** 工具轮预算耗尽：已按兜底文案收尾（对应 Python respond 节点的 rounds-exhausted 路径）。 */
  roundsExhausted: boolean;
  /** 本轮出现过的工具名（按出现顺序，含重复；对应响应里的 used_tools）。 */
  toolNames: string[];
  /** Phase 6：反思重答次数（≤1）。 */
  reflectionCount: number;
  /** Phase 6：critic 实际评审次数（诊断水位）。 */
  reflectionRuns: number;
  /** Phase 6：非空=质检不过的病因（重试信号；通过/不评时清零）。 */
  reflectionCritique: string;
  /** 玩家快照（服务端注入，来自请求体 context）。 */
  playerContext: Record<string, unknown>;
  /** 本轮是否命中时效关键词（缓存守卫 + 强制查库提示共用）。 */
  freshnessHinted: boolean;
  /**
   * 时效强制查库的重答次数（≤MAX_FRESHNESS_NUDGES；D10）。
   * 触发时**同时**自增 reflectionCount，与反思质检共享"每轮最多重答一次"的预算。
   */
  freshnessNudges: number;
  /** 非空=时效强制查库的批评块（重答信号；与 reflectionCritique 同位置注入）。 */
  freshnessCritique: string;
  /**
   * 本次请求的上下文里（投影后）是否含工具结果 —— D12 缓存判据。
   * 含义：模型这次作答「能看到历史工具数据」，答案可能引用了会变的数据，因而不许写进相似问题缓存。
   */
  hadToolContext: boolean;
  /** 单轮工具调用计数（防护闸门的单轮预算用；每轮工具执行前重置）。 */
  roundToolCalls: number;
  /** 本轮是否已做过压缩判定（每轮只压一次，对应 Python 版 ingest 节点的语义）。 */
  compactionChecked: boolean;
  /**
   * 本轮用过的**数据类**工具结果（`[工具名] 文本`）。
   * 为什么要累积：pi 的 turn 里工具结果属于"执行工具那一轮"，最终作答轮的 toolResults 是空的；
   * Python 版 critic 看的是"最后一个 human 消息之后的所有 tool 消息"——这里等价复刻该口径。
   */
  dataToolResults: string[];
  /** 流式端点：助手正文增量回调（打字机）。 */
  onDelta?: (text: string) => void;
  /** 流式端点：需要作废已外流文本时回调（工具轮 / 反思重答）。 */
  onReset?: () => void;
}

export function newRunState(playerId: number, requestId: string, playerContext: Record<string, unknown>): RunState {
  return {
    playerId,
    requestId,
    toolRounds: 0,
    roundsExhausted: false,
    toolNames: [],
    reflectionCount: 0,
    reflectionRuns: 0,
    reflectionCritique: "",
    playerContext,
    freshnessHinted: false,
    freshnessNudges: 0,
    freshnessCritique: "",
    hadToolContext: false,
    roundToolCalls: 0,
    compactionChecked: false,
    dataToolResults: [],
  };
}

/** 在同一个对象上重置本轮字段（保住工具闭包的引用）。 */
export function resetRunState(
  run: RunState,
  playerId: number,
  requestId: string,
  playerContext: Record<string, unknown>,
): RunState {
  const fresh = newRunState(playerId, requestId, playerContext);
  Object.assign(run, fresh);
  return run;
}
