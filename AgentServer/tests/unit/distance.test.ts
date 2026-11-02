/**
 * 相似度移植测试（P4 验收）：与 CPython difflib 逐位对拍（22 组用例，含空串、
 * 重复字符、≥200 字符触发 autojunk 的长串、中日文与版本号）。
 *
 * 判据是 qa_cache 的「同问」阈值（≥0.90）——算法必须逐位一致，否则命中率整体漂移。
 */
import { describe, expect, it } from "vitest";

import { normalizeQuestion, similarityRatio } from "../../src/core/distance.js";
import { DIFFLIB_CASES } from "../fixtures/difflib-cases.js";

describe("归一化（与 Python _norm_question 一致）", () => {
  it("NFKC + 小写 + 去空白", () => {
    expect(normalizeQuestion("挑战怎么玩？")).toBe(normalizeQuestion("挑战怎么玩？"));
    expect(normalizeQuestion("ＡＢＣ １２３")).toBe("abc123"); // 全角→半角 + 去空格
    expect(normalizeQuestion("你好\n\t 世界")).toBe("你好世界");
  });
});

describe("similarityRatio 与 CPython difflib 对拍", () => {
  it("22 组用例逐位一致（|diff| < 1e-12）", () => {
    for (const testCase of DIFFLIB_CASES) {
      const actual = similarityRatio(normalizeQuestion(testCase.a), normalizeQuestion(testCase.b));
      expect(actual, `a=${JSON.stringify(testCase.a)} b=${JSON.stringify(testCase.b)}`).toBeCloseTo(
        testCase.ratio,
        12,
      );
    }
  });

  it("固件里的归一化列与我们的一致", () => {
    for (const testCase of DIFFLIB_CASES) {
      expect(normalizeQuestion(testCase.a)).toBe(testCase.na);
      expect(normalizeQuestion(testCase.b)).toBe(testCase.nb);
    }
  });
});

describe("阈值语义（缓存命中判据 ≥0.90）", () => {
  it("命中：仅近乎同形的改写；不命中：换说法的问法（与 Python 版行为一致，刻意保守）", () => {
    // 实测结论（固件真值）：阈值 0.90 只抓「同形改写」，同义改写基本抓不到——
    // 这是 Python 版的既有行为，移植时保持不动（改阈值 = 改缓存命中率 = 行为漂移）。
    const hits: [string, string][] = [
      ["挑战怎么玩？", "挑战怎么玩"], // 0.909
      ["我背包里有什么", "我背包里有什么？"], // 0.933
      ["我背包里有什么", "我背包里有些什么"], // 0.933
    ];
    for (const [a, b] of hits) {
      const ratio = similarityRatio(normalizeQuestion(a), normalizeQuestion(b));
      expect(ratio, `a=${a} b=${b}`).toBeGreaterThanOrEqual(0.9);
    }

    const misses: [string, string][] = [
      ["你好", "最近怎么样"],
      ["副本怪物多久刷新", "副本里怪物多久刷新一次"], // 0.842
      ["排行榜第一名是谁", "排行榜的第一名是谁呀"], // 0.889
      ["邮件附件怎么领", "怎么领取邮件里的附件"], // 0.353（同义但换形）
    ];
    for (const [a, b] of misses) {
      const ratio = similarityRatio(normalizeQuestion(a), normalizeQuestion(b));
      expect(ratio, `a=${a} b=${b}`).toBeLessThan(0.9);
    }
  });
});
