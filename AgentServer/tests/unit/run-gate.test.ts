/**
 * 全局 run 闸测试：进程内并发上限 + 等待超时 + 槽位转交。
 *
 * 为什么单独测：闸的逻辑在「满载 + 超时 + 同时释放」这几个边界上最容易写错
 * （多减一次 active 会变成无限并发，少减一次会永久饿死）。计时压测看不出这些。
 */
import { describe, expect, it } from "vitest";

import { createRunGate } from "../../src/agent/runtime.js";

const tick = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe("createRunGate", () => {
  it("超过上限的请求排队，release 时槽位直接转交", async () => {
    const gate = createRunGate(2);
    expect(await gate.acquire(50)).toBe(true);
    expect(await gate.acquire(50)).toBe(true);
    expect(gate.active).toBe(2);

    let third: boolean | null = null;
    const pending = gate.acquire(500).then((ok) => {
      third = ok;
    });

    await tick(10);
    expect(third).toBeNull(); // 还在等槽位

    gate.release(); // 转交给排队者，而不是先减后加
    await pending;
    expect(third).toBe(true);
    expect(gate.active).toBe(2); // 在跑数不变
  });

  it("等不到槽位时超时返回 false，且不占槽位", async () => {
    const gate = createRunGate(1);
    expect(await gate.acquire(50)).toBe(true);
    expect(await gate.acquire(20)).toBe(false);

    gate.release();
    expect(gate.active).toBe(0);
    expect(await gate.acquire(20)).toBe(true); // 槽位没有被超时路径吃掉
  });

  it("任务抛错后 release 仍能归还槽位", async () => {
    const gate = createRunGate(1);
    const run = async (): Promise<string> => {
      expect(await gate.acquire(50)).toBe(true);
      try {
        throw new Error("boom");
      } finally {
        gate.release();
      }
    };
    await expect(run()).rejects.toThrow("boom");
    expect(gate.active).toBe(0);
  });
});
