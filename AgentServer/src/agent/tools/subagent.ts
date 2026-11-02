/**
 * 主图的派发工具：dispatch_subagent(task)（逐字搬 tools/subagent.py 的 docstring 与语义）。
 * 描述文案见 subagent.ts::DISPATCH_SUBAGENT_DESCRIPTION。
 */
import type { AgentTool } from "@earendil-works/pi-agent-core";
import { Type } from "typebox";

import { dispatchSubagent, DISPATCH_SUBAGENT_DESCRIPTION } from "../subagent.js";
import { defineTool, type ToolDeps } from "./shared.js";

const PARAMS = Type.Object({
  task: Type.String({ description: "研究目标与要点，一两句话即可（子代理看不到对话，只看得到这段描述）" }),
});

export function makeSubagentTool(deps: ToolDeps): AgentTool<typeof PARAMS> {
  return defineTool(
    {
      name: "dispatch_subagent",
      label: "研究子代理",
      description: DISPATCH_SUBAGENT_DESCRIPTION,
      parameters: PARAMS,
      run: async (params, d) =>
        await dispatchSubagent(params.task, { playerId: d.playerId, requestId: d.run.requestId }),
    },
    deps,
  );
}
