/**
 * 主动提醒生成（悬浮球 3107 · 一个 Agent 两个用法）：.NET 检测到事件 → 让同一个阿福主动说话。
 * 逐字搬 app.py 的 kind 注册表 / 指令模板 / 兜底文案。
 *
 * 关键点：跑的是**同一个会话**（thread_id=player_{id} 语义）——共用记忆与玩家已选说话风格；
 * 归档只记 assistant 行（面板历史里就是"阿福主动说了一句"）；
 * LLM 异常/超时 → 确定性模板兜底（used_llm=false），照样归档照样返回。
 */
import { PROACTIVE_FALLBACK, PROACTIVE_FALLBACK_GENERIC } from "../core/config.js";
import { log } from "../core/logger.js";
import { appendChat } from "../db/chatRepo.js";
import { chat } from "./runtime.js";

/** kind 注册表（新增提醒类型：这里加指令模板与兜底文案即可，.NET 侧加钩子点）。 */
export const PROACTIVE_KINDS: ReadonlySet<number> = new Set([1]);

/** 事件数据 → 事件指令文本（空串=未知 kind，直接走兜底文案不进 LLM）。 */
export function buildProactivePrompt(kind: number, data: Record<string, unknown>): string {
  if (kind === 1) {
    const title = String(data.title ?? "").trim();
    const claimable = Number(data.claimable ?? 0);
    let detail: string;
    if (title) {
      detail = `主人的邮箱刚收到一封带附件的新邮件《${title}》。`;
    } else if (claimable > 1) {
      detail = `主人的邮箱里还有 ${claimable} 封带附件的邮件一直没领取。`;
    } else {
      detail = "主人的邮箱里有封带附件的邮件还没领取。";
    }
    return (
      `【系统主动事件·服务端注入】${detail}` +
      "请以守岸人的身份主动跟主人说一句简短的提醒：一两句话、口语化、不要列清单、" +
      "不要 markdown 和 emoji。事件数据已在消息里给出，如无必要不要调用工具，直接提醒即可。"
    );
  }
  return "";
}

export function kindName(kind: number): string {
  return PROACTIVE_KINDS.has(kind) ? "mail_unclaimed" : `kind${kind}`;
}

export interface ProactiveResult {
  message: string;
  usedLlm: boolean;
}

/**
 * 生成主动提醒并归档（只记 assistant 行）。
 * 注意：instruction 会作为 user 消息进入会话（与 Python 版同口径——
 * persona 的安全规则里专门说明了「以【系统主动事件开头的消息是服务器注入的真实事件」）。
 */
export async function generateProactive(
  playerId: number,
  kind: number,
  data: Record<string, unknown>,
): Promise<ProactiveResult> {
  const prompt = buildProactivePrompt(kind, data);
  let message = "";
  let usedLlm = false;

  if (prompt) {
    try {
      const result = await chat(playerId, prompt, { source: "proactive", online: true });
      const reply = result.reply.trim();
      if (reply) {
        message = reply;
        usedLlm = true;
      }
    } catch (err) {
      log.error(`proactive 执行失败 player=${playerId} kind=${kindName(kind)}`, err);
    }
  }

  if (!message) {
    message = PROACTIVE_FALLBACK[kind] ?? PROACTIVE_FALLBACK_GENERIC;
  }

  appendChat(playerId, "assistant", message);
  return { message, usedLlm };
}
