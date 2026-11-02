/**
 * 全局配置：.env 载入 + 全部常量集中地。
 *
 * 对应 Python 版：llm.py（provider 配置）+ context.py（上下文预算/时效关键词）
 * + tools/*.py（超时/截断/防护参数）+ storage.py（上限）+ knowledge.py（知识库参数）。
 *
 * 冻结约束（方案 §4.4）：只读 .env 的 AGENT_LLM_BASE_URL / AGENT_LLM_API_KEY /
 * AGENT_LLM_MODEL / AGENT_INTERNAL_TOKEN 四个变量；此外允许的可选项一律带默认值。
 * 所有「文案/数值」类常量都与 Python 版逐字对齐——改它们等于改行为（方案 §4.2 冻结契约）。
 */
import { existsSync } from "node:fs";
import path from "node:path";
import process from "node:process";

import dotenv from "dotenv";

/** 仓库根目录（src/core 或 dist/core 的上两级）。 */
export const ROOT = path.resolve(import.meta.dirname, "..", "..");

const ENV_FILE = path.join(ROOT, ".env");
if (existsSync(ENV_FILE)) {
  dotenv.config({ path: ENV_FILE });
}

// ────────────────────────────── 路径 ──────────────────────────────

/** 可选覆盖（默认与 Python 版同路径）：E2E/测试用临时库，避免污染真实记忆库。 */
export const DB_PATH = process.env.AGENT_DB_PATH || path.join(ROOT, "data", "agent_memory.db");
export const LOG_DIR = path.join(ROOT, "logs");
export const LOG_FILE = path.join(LOG_DIR, "agent_server.log");
export const AUDIT_FILE = path.join(LOG_DIR, "audit.log");
export const KNOWLEDGE_DIR = path.join(ROOT, "knowledge");
/** 可选覆盖（默认 knowledge/gameplay.md）：测试用临时知识文件。 */
export const GAMEPLAY_FILE = process.env.AGENT_GAMEPLAY_FILE || path.join(KNOWLEDGE_DIR, "gameplay.md");

// ────────────────────────────── 服务 ──────────────────────────────

/** 仅本机回环（与 Python 版一致：Agent 永不暴露公网）。 */
export const HOST = "127.0.0.1";
/** AGENT_PORT 仅供开发期联调（P0–P6 用 8890，避免占用 Python 版的 8898）。 */
export const PORT = Number.parseInt(process.env.AGENT_PORT ?? "8898", 10) || 8898;

/** 请求体上限（字节）：正常请求远小于此，只是防脏数据。 */
export const MAX_BODY_BYTES = 64 * 1024;
/** 单个对话请求的端到端预算（网关 HTTP 总预算 60s 之内）。 */
export const RUN_BUDGET_MS = 55_000;

// ────────────────────────────── LLM（pi-ai provider）──────────────────────────────

export const LLM_BASE_URL = process.env.AGENT_LLM_BASE_URL || "https://api.deepseek.com";
export const LLM_API_KEY = process.env.AGENT_LLM_API_KEY || "";
export const LLM_MODEL = process.env.AGENT_LLM_MODEL || "deepseek-chat";
/** LLM 侧调用超时（长回复 + 工具链余量），对应 llm.py 的 _TIMEOUT_S = 60。 */
export const LLM_TIMEOUT_MS = 60_000;

/**
 * 模型元数据覆盖（可选）：pi-ai 内置目录里没有 .env 指定的模型名时，
 * 我们会继承目录里某个模型的 contextWindow/maxTokens——压缩阈值依赖它，
 * 继承错就会「该压不压」（实测：deepseek-chat 不在目录中，继承了 deepseek-flash 的 1M 窗口）。
 */
export const LLM_CONTEXT_WINDOW_OVERRIDE = Number.parseInt(process.env.AGENT_LLM_CONTEXT_WINDOW ?? "", 10);
export const LLM_MAX_TOKENS_OVERRIDE = Number.parseInt(process.env.AGENT_LLM_MAX_TOKENS ?? "", 10);

/** 对话请求里 content 的长度上限（网关已限 500，这里是兜底防脏数据）。 */
export const CONTENT_MAX_CHARS = 2000;

// ────────────────────────────── 鉴权 / 内部 API ──────────────────────────────

export const INTERNAL_TOKEN = process.env.AGENT_INTERNAL_TOKEN || "";
/** .NET 内部数据 API（实际端口由 .NET 的 CS_AGENT_INTERNAL_PORT 决定，默认 8897）。 */
export const INTERNAL_API_BASE = process.env.AGENT_INTERNAL_API_BASE || "http://127.0.0.1:8897";
/** 内部 API 请求超时：榜单走 Valkey 降级路径时首查约 4s，3s 会误杀首查。 */
export const INTERNAL_TIMEOUT_MS = 8_000;
/** 天气（Open-Meteo）超时，对应 weather.py 的 _TIMEOUT = 5.0。 */
export const WEATHER_TIMEOUT_MS = 5_000;
/** 更新公告拉取超时（回环内网），对应 knowledge.py 的 ANNOUNCE_TIMEOUT_S = 3.0。 */
export const ANNOUNCE_TIMEOUT_MS = 3_000;

// ────────────────────────────── Agent 循环预算（graph.py）──────────────────────────────

/** 工具循环上限（预算耗尽保护）。 */
export const MAX_TOOL_ROUNDS = 5;

// ────────────────────────────── 并发闸门 ──────────────────────────────

/**
 * 进程内同时在跑的 run 上限。
 *
 * 网关那道 300（.NET AgentHandler.forwardGate）是**入口**闸：放进来 300 个就真的
 * 同时建 300 个 Agent、各持一份上下文、一起打同一个 LLM 账号。这里是 Node 侧的第二道，
 * 防的是「入口闸放行的量超过本进程与上游配额能承受的量」。
 *
 * 50 只是起始值，不是测出来的定论：调大前先看单会话常驻内存与上游账号的并发配额。
 * AGENT_MAX_CONCURRENT_RUNS 可覆盖（压测调参用）。
 */
const parsedMaxRuns = Number.parseInt(process.env.AGENT_MAX_CONCURRENT_RUNS ?? "", 10);
export const MAX_CONCURRENT_RUNS = parsedMaxRuns > 0 ? parsedMaxRuns : 50;

/** 抢不到 run 槽位时的等待上限：超时快速失败，不让请求排到网关 60s 超时。 */
export const RUN_GATE_WAIT_MS = 10_000;

/** 后台长期记忆抽取的并发上限（fire-and-forget 不能无节制打上游；超出的直接丢弃）。 */
export const EXTRACT_MAX_CONCURRENCY = 5;
/** 回复限长（出口剪裁，方案 §12.2）。 */
export const MAX_REPLY_CHARS = 1500;
/** 反思重答上限（防环）。 */
export const MAX_REFLECTIONS = 1;
/** 偏好类工具非数据回答，不触发质检。 */
export const NON_DATA_TOOLS: ReadonlySet<string> = new Set(["set_agent_style"]);
/** 工具轮耗尽时的兜底文案（respond 节点，逐字）。 */
export const ROUNDS_EXHAUSTED_REPLY = "抱歉主人，这一单查得有点久还没走完，请稍后再问一次吧。";

/** 时效关键词：命中则旧数字不可信（强制查库提醒 + 相似问题缓存双向跳过）。 */
export const FRESHNESS_KEYWORDS: readonly string[] = [
  "背包", "包裹", "物品", "道具", "任务", "进度", "可提交", "可接取", "排行", "名次",
  "榜单", "邮件", "保底", "好友", "在线", "天气", "现在", "当前", "今天", "几点",
  "月亮", "几点",
];

/** 时效性提醒（代码层确定性注入，比 persona 静态约束可靠；逐字）。 */
export const FRESHNESS_REMINDER =
  "【时效性提醒】玩家询问的是实时游戏数据，此前对话里出现过的数字可能已经过期，" +
  "禁止复用历史对话中的旧数据回答，必须调用对应工具查询最新值后再作答。";

/**
 * 时效强制查库的判据关键词（2026-10-18 新增，登记为有意变更 D10）。
 *
 * 为什么不直接复用 FRESHNESS_KEYWORDS：那份表里有「现在/当前/今天/几点/月亮/天气」这类
 * 通用词（服务于缓存守卫与静态提醒），拿它当"必须查库"的判据会让「现在几点了」这种
 * 问题白跑一轮重答。这里只保留「问的是游戏实时数据」的词。
 */
export const GAME_DATA_KEYWORDS: readonly string[] = [
  "背包", "包裹", "物品", "道具", "任务", "进度", "可提交", "可接取", "排行", "名次",
  "榜单", "邮件", "邮箱", "保底", "好友", "在线",
];

/** 时效强制查库的重答上限（与 MAX_REFLECTIONS 共享重答预算，合计每轮 ≤1 次额外调用）。 */
export const MAX_FRESHNESS_NUDGES = 1;

/**
 * 时效强制查库的批评块（D10 新增文案，已登记进方案 §19.1 / §4.2）。
 *
 * 背景：时效提醒是**软约束**（实测「我背包里有什么」可能 used_tools=[] 直接复述历史），
 * 而质检的触发条件是「本轮用过数据类工具」——两者之间有缝，正好是"复述旧数字"的高发区。
 * 这条批评块把缝补成机制：问的是游戏实时数据却没查库 → 强制重答一次，并明确要求调工具。
 */
export const FRESHNESS_NUDGE_BLOCK =
  "【反思】玩家问的是实时游戏数据，但你没有查询就作答了——历史对话里的数字可能已经过期，禁止复用。" +
  "请立刻调用对应工具核实后重新回答（背包/物品用 get_bag_items、任务进度用 get_quest_status、" +
  "邮件用 get_mail_summary、好友用 get_friend_list、排行榜/名次用 get_leaderboard、" +
  "抽卡保底用 get_gacha_status），再据实作答。";

// ────────────────────────────── 上下文压缩（方案 §7.4）──────────────────────────────
// 触发判据来自 pi 的 shouldCompact：contextTokens > contextWindow - reserveTokens。
// 默认值先取 pi 默认，待 §17.4 对照用例校准后写回文档。

export const COMPACT_ENABLED = (process.env.PI_COMPACT_ENABLED ?? "true") !== "false";
export const COMPACT_RESERVE_TOKENS = Number.parseInt(process.env.PI_COMPACT_RESERVE_TOKENS ?? "16384", 10);
export const COMPACT_KEEP_RECENT_TOKENS = Number.parseInt(process.env.PI_COMPACT_KEEP_RECENT_TOKENS ?? "20000", 10);

// ────────────────────────────── 工具层（tools/__init__.py / game_tools.py）──────────────────────────────

/** 按工具超时（ms）：子代理内部 ≤4 轮工具 + 多步推理，8s 会误杀，给 40s 兜底。 */
export const TOOL_TIMEOUT_MS: Readonly<Record<string, number>> = { dispatch_subagent: 40_000 };
export const TOOL_TIMEOUT_DEFAULT_MS = 8_000;

/** 大结果截断，防撑爆上下文（默认 1200；公告与知识库按需放宽）。 */
export const TOOL_RESULT_LIMITS: Readonly<Record<string, number>> = {
  get_update_announcements: 6000,
  search_game_knowledge: 1500,
};
export const TOOL_RESULT_MAX_CHARS = 1200;
export const TOOL_TRUNCATED_SUFFIX = "…(结果过长已截断)";

/**
 * 仅联调开关：强制质检判不过（AGENT_CRITIC_FORCE_FAIL=1）。
 * 用于确定性验证「反思重答」路径（否则要制造一个模型真的答错的场景）。
 * 生产不要开；默认关闭。
 */
export const CRITIC_FORCE_FAIL = process.env.AGENT_CRITIC_FORCE_FAIL === "1";

// ────────────────────────────── 防护闸门（tools/guard.py）──────────────────────────────

/** 有副作用的工具名——幂等只对它们生效（子代理白名单也取这份）。 */
export const WRITE_TOOLS: ReadonlySet<string> = new Set(["claim_mail_attachments"]);

/** 单轮 tool_call 数上限：LLM 一口气吐几十个调用时的闸门（主图与子代理共用）。 */
export const MAX_TOOL_CALLS_PER_ROUND = 8;

export const RATE_WINDOW_SECONDS = 60;
/** 全部工具合计：5 轮 × 8 个/轮 也够用，再高就是异常。 */
export const RATE_MAX_CALLS = 40;
/** 写工具单独更严——代执行是不可逆的。 */
export const WRITE_RATE_MAX_CALLS = 5;
/** 幂等记录兜底存活时长（主作用域是 request_id，TTL 只为清理内存）。 */
export const IDEMPOTENCY_TTL_SECONDS = 600;

/** 单轮超限时回给 LLM 的说明（主图/子代理口径一致；逐字）。 */
export const ROUND_OVERFLOW_MESSAGE =
  `单轮工具调用数超过上限（${MAX_TOOL_CALLS_PER_ROUND} 个），本条调用已被省略，` +
  "请只保留最必要的查询后重试。";

// ────────────────────────────── 研究子代理（tools/subagent.py）──────────────────────────────

export const SUB_MAX_ROUNDS = 4;
/** 子代理墙钟预算（防 LLM/工具慢把网关 60s 拖爆）。 */
export const SUB_WALL_BUDGET_MS = 25_000;
export const SUB_TOOL_TIMEOUT_MS = 8_000;
/** 结论长度上限（回传主图前兜底；主图还会再截 1200）。 */
export const SUB_RESULT_MAX_CHARS = 900;

// ────────────────────────────── 记忆 / 缓存（storage.py / memory.py）──────────────────────────────

export const MAX_QA_CACHE_PER_PLAYER = 100;
export const MAX_LTM_ENTRIES = 12;

/** 缓存相似度判据（app.py）：长度差 ≤2 先剪枝，再 difflib ratio ≥ 0.90。 */
export const CACHE_SIMILARITY = 0.9;
export const CACHE_LENGTH_TOLERANCE = 2;

/** LLM 抽取最长输出（memory.py: key ≤8 / content ≤80 / 最多 2 条）。 */
export const LTM_KEY_MAX_CHARS = 8;
export const LTM_CONTENT_MAX_CHARS = 80;
export const LTM_MAX_FACTS_PER_ROUND = 2;

/** 抽摘要/质检/抽取用的温度（与 Python 版一致）。 */
export const TEMPERATURE_SUMMARY = 0.3;
export const TEMPERATURE_EXTRACT = 0.2;
export const TEMPERATURE_CRITIC = 0;
export const TEMPERATURE_AGENT = 0.7;
export const TEMPERATURE_SUBAGENT = 0.3;

export const MAX_SUMMARY_CHARS = 800;
export const CRITIC_MAX_TOOL_CONTEXT_CHARS = 6000;

// ────────────────────────────── 知识库（knowledge.py）──────────────────────────────

export const KB_CHUNK_TEXT_CAP = 320;
export const KB_DEFAULT_TOP_K = 3;
export const KB_MAX_TOP_K = 5;
export const KB_CANDIDATE_LIMIT = 12;
export const KB_ANNOUNCE_REFRESH_MS = 300_000;
export const KB_ANNOUNCE_RETRY_MS = 60_000;
export const KB_BM25_WEIGHTS = [0, 0, 0, 3.0, 1.0] as const;

// ────────────────────────────── 主动提醒（app.py）──────────────────────────────

/** kind 注册表：新增提醒类型时这里加指令模板与兜底文案。 */
export const PROACTIVE_KINDS: ReadonlySet<number> = new Set([1]);
/** LLM 失败/超时/未知 kind 的确定性兜底文案——提醒永不因 LLM 抖动丢失。 */
export const PROACTIVE_FALLBACK: Readonly<Record<number, string>> = {
  1: "主人，邮箱里有带附件的信还没领，记得去看看哦。",
};
export const PROACTIVE_FALLBACK_GENERIC = "主人，有个小提醒，记得回来看看哦。";
