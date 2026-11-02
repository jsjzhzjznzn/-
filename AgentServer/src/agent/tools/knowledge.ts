/**
 * 知识库检索工具（逐字搬 tools/knowledge_tools.py）：search_game_knowledge。
 *
 * 只读、无 player_id 参数（知识库内容与玩家无关）；主图与子代理白名单都放行，
 * 但子代理不能借此递归（知识工具本身不派发子代理）。
 */
import type { AgentTool } from "@earendil-works/pi-agent-core";
import { Type } from "typebox";

import { KB_DEFAULT_TOP_K, KB_MAX_TOP_K } from "../../core/config.js";
import { searchKnowledge } from "../../knowledge/search.js";
import { defineTool, type ToolDeps } from "./shared.js";

const PARAMS = Type.Object({
  query: Type.String({ description: "关键词或短句，如\"副本 怪物 重生\"" }),
  top_k: Type.Optional(
    Type.Integer({ description: `返回条数，默认 ${KB_DEFAULT_TOP_K}，最多 ${KB_MAX_TOP_K}`, minimum: 1, maximum: KB_MAX_TOP_K }),
  ),
});

export function makeKnowledgeTool(deps: ToolDeps): AgentTool<typeof PARAMS> {
  return defineTool(
    {
      name: "search_game_knowledge",
      label: "知识库检索",
      description:
        "检索游戏知识库（玩法攻略 + 历次更新公告要点）。\n\n" +
        "适用：玩家问玩法细节/规则/攻略（\"副本怪物多久刷新\"\"背包满了怎么办\"\"地图怎么缩放\"）、\n" +
        "系统机制、往期更新内容（\"1.2 版本改了什么\"\"最近更新了啥\"）。\n" +
        "不适用：玩家自己的实时数据（背包/任务/邮件/排行榜等）——那类用对应数据工具查。\n" +
        "提示：query 传关键词或短句（如\"副本 怪物 重生\"）；想要公告全文用 get_update_announcements。",
      parameters: PARAMS,
      run: async (params) => {
        const raw = Number(params.top_k ?? KB_DEFAULT_TOP_K);
        const k = Number.isFinite(raw) ? Math.max(1, Math.min(Math.trunc(raw), KB_MAX_TOP_K)) : KB_DEFAULT_TOP_K;
        return await searchKnowledge(params.query, k);
      },
    },
    deps,
  );
}
