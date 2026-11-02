/**
 * 游戏数据工具（只读 7 个 + 公告全文）——逐字搬 tools/game_tools.py 的描述与降级文案。
 *
 * 数据链路：工具 → HTTP(127.0.0.1:8897) → .NET 网关内部 API → 真实数据。
 * player_id 由闭包注入，工具 schema 里没有这个参数——机制上杜绝「查别人数据」。
 * 失败抛 ToolFailure（框架转 isError）：文案里带「下一步怎么做」，让模型停手、据实告知，
 * 而不是把故障当成「没有数据」去换个工具乱试。
 */
import type { AgentTool } from "@earendil-works/pi-agent-core";
import { Type } from "typebox";

import { INTERNAL_API_BASE, INTERNAL_TIMEOUT_MS } from "../../core/config.js";
import { HttpFailure, getText, postJsonText } from "../../core/http.js";
import { ToolFailure, defineTool, type ToolDeps } from "./shared.js";

const PLAYER_BASE = `${INTERNAL_API_BASE}/internal/agent/player`;
const ANNOUNCEMENTS_URL = `${INTERNAL_API_BASE}/internal/agent/announcements`;

const NO_PARAMS = Type.Object({});

function failureKind(err: unknown): string {
  return err instanceof HttpFailure ? err.kind : err instanceof Error ? err.name : "Error";
}

/** GET 玩家维度内部 API：成功原样返回文本，失败抛 ToolFailure（由框架转 isError）。 */
async function fetchPlayer(playerId: number, path: string, signal?: AbortSignal): Promise<string> {
  try {
    return await getText(`${PLAYER_BASE}/${playerId}${path}`, INTERNAL_TIMEOUT_MS, signal);
  } catch (err) {
    throw new ToolFailure(
      `查询失败（游戏服内部API不可达: ${failureKind(err)}）。这是服务端故障，不是「没有这项数据」；` +
        "请据实告知玩家稍后再试，不要换其他工具反复重试。",
    );
  }
}

export function makeGameTools(deps: ToolDeps): AgentTool<any>[] {
  return [
    defineTool(
      {
        name: "get_player_profile",
        label: "玩家资料",
        description: "获取当前玩家的基本信息：昵称、注册时间、在线状态。",
        parameters: NO_PARAMS,
        run: (_params, d, signal) => fetchPlayer(d.playerId, "/profile", signal),
      },
      deps,
    ),
    defineTool(
      {
        name: "get_bag_items",
        label: "背包清单",
        description: "获取当前玩家背包的物品清单：容量、已用格数、各物品的名称/数量/稀有度。",
        parameters: NO_PARAMS,
        run: (_params, d, signal) => fetchPlayer(d.playerId, "/bag", signal),
      },
      deps,
    ),
    defineTool(
      {
        name: "get_quest_status",
        label: "任务状态",
        description: "获取当前玩家的任务状态：进行中、可提交、可接取的任务列表与进度。",
        parameters: NO_PARAMS,
        run: (_params, d, signal) => fetchPlayer(d.playerId, "/quests", signal),
      },
      deps,
    ),
    defineTool(
      {
        name: "get_mail_summary",
        label: "邮箱摘要",
        description: "获取当前玩家的邮箱摘要：总邮件数、未读数、待领取附件数、最近几封邮件的标题。",
        parameters: NO_PARAMS,
        run: (_params, d, signal) => fetchPlayer(d.playerId, "/mail_summary", signal),
      },
      deps,
    ),
    defineTool(
      {
        name: "get_friend_list",
        label: "好友列表",
        description: "获取当前玩家的好友列表：每位好友的昵称与当前在线状态。",
        parameters: NO_PARAMS,
        run: (_params, d, signal) => fetchPlayer(d.playerId, "/friend_list", signal),
      },
      deps,
    ),
    defineTool(
      {
        name: "get_leaderboard",
        label: "排行榜",
        description: "获取挑战排行榜：前10名玩家（名字与分数）以及我自己的名次和最高分。",
        parameters: NO_PARAMS,
        run: (_params, d, signal) => fetchPlayer(d.playerId, "/leaderboard", signal),
      },
      deps,
    ),
    defineTool(
      {
        name: "get_gacha_status",
        label: "唤取保底",
        description: "获取当前玩家的抽卡保底状态：各卡池累计抽数与距5星保底还差的抽数。",
        parameters: NO_PARAMS,
        run: (_params, d, signal) => fetchPlayer(d.playerId, "/gacha_status", signal),
      },
      deps,
    ),
    defineTool(
      {
        name: "get_update_announcements",
        label: "更新公告",
        description:
          "获取游戏历次更新公告的全文（版本号/标题/日期/正文）。\n\n" +
          '适用：玩家问"这次更新加了什么""总结一下更新公告""上次更新讲了啥"时调用。\n' +
          "注意：公告全文可能很长——玩家要的是总结/解读，不是全文背诵；长公告的\n" +
          "总结任务建议派给研究子代理处理（它读全文并提炼要点）。",
        parameters: NO_PARAMS,
        run: async (_params, _d, signal) => {
          try {
            return await getText(ANNOUNCEMENTS_URL, INTERNAL_TIMEOUT_MS, signal);
          } catch (err) {
            throw new ToolFailure(
              `查询失败（更新公告接口不可达: ${failureKind(err)}）。这是服务端故障，不是「没有公告」；` +
                "请据实告知玩家稍后再试，不要凭印象编造更新内容。",
            );
          }
        },
      },
      deps,
    ),
  ];
}

const CLAIM_PARAMS = Type.Object({
  mail_id: Type.Integer({ description: "要领取附件的邮件 ID（来自 get_mail_summary）" }),
});

/**
 * 写操作工具：帮当前玩家领取指定邮件的附件（物品会进背包）。
 *
 * 这是唯一的写操作工具（guard 的 WRITE_TOOLS 只登记它）——描述里的「确认纪律」是
 * 行为契约的一部分，必须与 Python 版逐字一致。
 */
export function makeMailClaimTool(deps: ToolDeps): AgentTool<typeof CLAIM_PARAMS> {
  return defineTool(
    {
      name: "claim_mail_attachments",
      label: "领取附件",
      description:
        "【写操作】帮当前玩家领取指定邮件的附件（物品会进背包）。\n\n" +
        "这是唯一的写操作工具，必须遵守确认纪律：\n" +
        "1. 调用前必须先用 get_mail_summary 查到该邮件（mail_id 对得上），\n" +
        "   向玩家复述清楚（哪封邮件、有什么附件），并得到玩家的明确同意\n" +
        "   （玩家说了\"领吧/确认/可以\"之类的话）才允许调用；\n" +
        "2. 玩家没有明确同意前，一律只复述、不调用；\n" +
        "3. mail_id 必须来自 get_mail_summary 返回的 mail_id，禁止猜测编造。\n\n" +
        "Args:\n" +
        "    mail_id: 要领取附件的邮件 ID（来自 get_mail_summary）",
      parameters: CLAIM_PARAMS,
      run: async (params, d, signal) => {
        try {
          return await postJsonText(
            `${PLAYER_BASE}/${d.playerId}/mail/claim`,
            { mail_id: Math.trunc(params.mail_id) },
            INTERNAL_TIMEOUT_MS,
            signal,
          );
        } catch (err) {
          throw new ToolFailure(
            `领取失败（写操作未执行: ${failureKind(err)}）。附件仍在邮箱里，没有领取成功；` +
              "请告知玩家稍后重试，绝不能回复「已领取」。",
          );
        }
      },
    },
    deps,
  );
}
