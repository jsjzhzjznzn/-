/**
 * LLM Provider（pi-ai）：把 .env 的 AGENT_LLM_* 映射成 pi 的 Models/provider。
 *
 * 设计（方案 §8）：
 *  - 默认走 pi-ai 内置的 deepseek provider（同一个 OpenAI 兼容端点）；
 *  - 当 .env 覆盖了 baseUrl 或模型名时，自建 provider（api = openAICompletionsApi），
 *    模型元数据从内置 deepseek 目录继承（contextWindow/maxTokens/cost 必须正确，
 *    否则压缩判据与 provider 参数会失准）；
 *  - apiKey 用 pi-ai 的 envApiKeyAuth 读 AGENT_LLM_API_KEY —— 保持「改 .env 不改代码」，
 *    且不往 process.env 里塞别的变量名。
 */
import { createModels, createProvider, envApiKeyAuth, type Model, type Provider } from "@earendil-works/pi-ai";
import { openAICompletionsApi } from "@earendil-works/pi-ai/api/openai-completions.lazy";
import { deepseekProvider } from "@earendil-works/pi-ai/providers/deepseek";

import {
  LLM_API_KEY,
  LLM_BASE_URL,
  LLM_CONTEXT_WINDOW_OVERRIDE,
  LLM_MAX_TOKENS_OVERRIDE,
  LLM_MODEL,
} from "../core/config.js";
import { log } from "../core/logger.js";

const DEEPSEEK_DEFAULT_BASE = "https://api.deepseek.com";
const DEEPSEEK_DEFAULT_MODEL = "deepseek-chat";

const models = createModels();
const builtin = deepseekProvider();

/** 是否需要覆盖内置 provider（baseUrl 或模型名被 .env 改过）。 */
const needsOverride = LLM_BASE_URL !== DEEPSEEK_DEFAULT_BASE || LLM_MODEL !== DEEPSEEK_DEFAULT_MODEL;

function modelCatalog(): readonly Model<"openai-completions">[] {
  return builtin.getModels() as readonly Model<"openai-completions">[];
}

/**
 * 内置目录缺失的模型元数据表（2026-10-18 新增，登记为有意变更 D11）。
 *
 * 为什么必须有：.env 用的 `deepseek-chat` 不在 pi-ai 内置 deepseek 目录里（目录里只有
 * deepseek-flash / deepseek-v4-pro），不填就会继承它们的元数据——万一继承到错的窗口，
 * 压缩阈值也会跟着错。
 *
 * 实测依据（2026-10-18，本机直连 api.deepseek.com 探测）：
 *  1. 发 `model:"deepseek-chat"` 的请求，响应回显 `"model":"deepseek-flash"`
 *     → **deepseek-chat 是该账号上的历史别名，实际由 deepseek-flash 提供服务**；
 *  2. `GET /models` 只列 `deepseek-flash`、`deepseek-v4-pro`；发 `deepseek-v4.1` 被拒
 *     （报错原文："The supported API model names are deepseek-flash, deepseek-v4-pro"）；
 *  3. 官方文档（模型 & 价格）：`deepseek-flash` = **DeepSeek-V4.1-Flash**，上下文 **1M**、
 *     最大输出 384K；pi-ai 内置目录对 flash/v4-pro 也标 1,000,000 / 384,000。
 *  4. 思考模式：flash / v4-pro 默认走思考模式（探测响应带 reasoning_tokens），而别名
 *     `deepseek-chat` 的响应**没有** reasoning_tokens——即别名 = 非思考模式 flash，
 *     这对"游戏管家"这种低延迟场景反而合适（也是我们继续用别名的原因之一）。
 *
 * 因此窗口取 **1,000,000**（对齐真实服务模型，而不是早期那代 128K）。
 * maxTokens 保守取 8192：本 agent 回复出口裁剪到 1500 字，输出上限设小即可，
 * 还能顺带约束跑飞的长文（取值不影响输入侧容量，pi-ai 会自行按窗口反推可用输出）。
 *
 * 优先级：AGENT_LLM_CONTEXT_WINDOW / AGENT_LLM_MAX_TOKENS 覆盖 > 本表 > 继承回退。
 * 待办：`deepseek-chat` 是未文档化的别名，若哪天被下线，把 .env 的 AGENT_LLM_MODEL
 * 改为 `deepseek-flash` 即可（那时 pi-ai 内置目录自带正确元数据），但要接受思考模式。
 */
const KNOWN_MODEL_METADATA: Readonly<Record<string, { contextWindow: number; maxTokens: number }>> = {
  "deepseek-chat": { contextWindow: 1_000_000, maxTokens: 8_192 },
};

/** 取目标模型的元数据：优先精确命中；其次已知元数据表；最后用默认模型元数据改名（并告警）。 */
function resolveModel() {
  const catalog = modelCatalog();
  const exact = catalog.find((m) => m.id === LLM_MODEL);
  if (exact) return { ...exact, baseUrl: LLM_BASE_URL };
  const template = catalog.find((m) => m.id === DEEPSEEK_DEFAULT_MODEL) ?? catalog[0];
  if (!template) throw new Error("pi-ai 未提供 deepseek 模型目录，无法装配 provider");

  const known = KNOWN_MODEL_METADATA[LLM_MODEL];
  if (known) {
    log.info(
      `模型 ${LLM_MODEL} 使用已知元数据表：contextWindow=${known.contextWindow} ` +
        `maxTokens=${known.maxTokens}（压缩阈值=${known.contextWindow}−reserve）`,
    );
    return { ...template, id: LLM_MODEL, name: LLM_MODEL, baseUrl: LLM_BASE_URL, ...known };
  }

  log.warn(
    `.env 指定的模型 ${LLM_MODEL} 不在 pi-ai 内置 deepseek 目录中、也不在已知元数据表里，` +
      `将沿用 ${template.id} 的上下文/计费元数据（contextWindow=${template.contextWindow}）` +
      "——压缩阈值依赖该值，如与实际不符请设 AGENT_LLM_CONTEXT_WINDOW 显式覆盖",
  );
  return { ...template, id: LLM_MODEL, name: LLM_MODEL, baseUrl: LLM_BASE_URL };
}

/** 应用显式元数据覆盖（可选环境变量）：压缩判据依赖 contextWindow。 */
function applyMetadataOverrides<T extends { contextWindow: number; maxTokens?: number }>(target: T): T {
  const out = { ...target };
  if (Number.isFinite(LLM_CONTEXT_WINDOW_OVERRIDE) && LLM_CONTEXT_WINDOW_OVERRIDE > 0) {
    out.contextWindow = LLM_CONTEXT_WINDOW_OVERRIDE;
  }
  if (Number.isFinite(LLM_MAX_TOKENS_OVERRIDE) && LLM_MAX_TOKENS_OVERRIDE > 0) {
    out.maxTokens = LLM_MAX_TOKENS_OVERRIDE;
  }
  return out;
}

const model = applyMetadataOverrides(resolveModel());

if (!needsOverride) {
  // 内置 provider：key 走 DEEPSEEK_API_KEY —— 用 AGENT_LLM_API_KEY 的值补上，
  // 保证 .env 仍是唯一配置入口（不新增必填变量）
  if (LLM_API_KEY && !process.env.DEEPSEEK_API_KEY) process.env.DEEPSEEK_API_KEY = LLM_API_KEY;
  models.setProvider(builtin);
} else {
  const provider: Provider<"openai-completions"> = createProvider({
    id: "deepseek",
    name: "DeepSeek",
    baseUrl: LLM_BASE_URL,
    auth: { apiKey: envApiKeyAuth("Agent LLM API key", ["AGENT_LLM_API_KEY"]) },
    models: [model],
    api: openAICompletionsApi(),
  });
  models.setProvider(provider);
  log.info(`LLM provider 使用自定义端点 baseUrl=${LLM_BASE_URL} model=${LLM_MODEL}`);
}

/** 对话模型（Agent 循环用）。 */
export const chatModel = model;

/** 供 pi 的 Agent / completeSimple 使用的流式函数。 */
export const streamFn = models.streamSimple.bind(models);

/** 一次性调用（摘要/反思/事实抽取，见 oneShot.ts）。 */
export const modelsRuntime = models;

/** 启动自检：key 缺失时快速失败（与 Python 版 llm.py 的语义一致）。 */
export function assertLlmConfigured(): void {
  if (!LLM_API_KEY) {
    throw new Error("AGENT_LLM_API_KEY 未配置（检查 AgentServer/.env）");
  }
}
