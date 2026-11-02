/**
 * 串行队列测试（P7 验收）：同玩家不并发跑 run 的不变量。
 *
 * 为什么用单测而不是计时对比：真机计时无法区分串行/并行（第一轮太快时两种假设数值相同），
 * 这里直接断言「任务区间永不重叠 + 顺序保持 + 异常不破坏队列」。
 */
import { describe, expect, it } from "vitest";

import { createSerialQueue } from "../../src/agent/runtime.js";

const tick = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe("createSerialQueue", () => {
  it("任务区间永不重叠、按提交顺序执行", async () => {
    const queue = createSerialQueue();
    const log: string[] = [];
    let running = 0;
    let maxConcurrent = 0;

    const task = (name: string, ms: number) => async () => {
      running += 1;
      maxConcurrent = Math.max(maxConcurrent, running);
      log.push(`${name}:start`);
      await tick(ms);
      log.push(`${name}:end`);
      running -= 1;
      return name;
    };

    const results = await Promise.all([
      queue.enqueue(task("A", 30)),
      queue.enqueue(task("B", 5)),
      queue.enqueue(task("C", 1)),
    ]);

    expect(maxConcurrent).toBe(1); // 关键不变量
    expect(results).toEqual(["A", "B", "C"]); // 顺序保持（先提交先执行）
    expect(log).toEqual(["A:start", "A:end", "B:start", "B:end", "C:start", "C:end"]);
  });

  it("某个任务抛错不会卡死后续任务（错误照常抛给调用方）", async () => {
    const queue = createSerialQueue();
    const first = queue.enqueue(async () => {
      throw new Error("boom");
    });
    const second = queue.enqueue(async () => "ok");

    await expect(first).rejects.toThrow("boom");
    await expect(second).resolves.toBe("ok");
  });

  it("busy 反映是否有任务在排队/执行", async () => {
    const queue = createSerialQueue();
    expect(queue.busy).toBe(false);
    const pending = queue.enqueue(async () => {
      await tick(5);
      return 1;
    });
    expect(queue.busy).toBe(true);
    await pending;
    await tick(1);
    expect(queue.busy).toBe(false);
  });
});
