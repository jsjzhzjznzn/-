/**
 * difflib 对拍固件（**冻结的 oracle，勿手改数值**）。
 *
 * 这些 ratio 由 CPython 的 `difflib.SequenceMatcher(None, na, nb).ratio()` 计算得出
 * （2026-10-18 一次性生成），用于验证 `src/core/distance.ts` 的移植实现是否**逐位一致**。
 * 因为要对比的是 CPython 的行为，oracle 必须来自 CPython——因此它是**数据资产**而不是可再生成的产物：
 *  - 不要用我们自己的 similarityRatio 重新生成（那是自证，等于把测试废掉）；
 *  - 想扩样例时，在任意有 CPython 的机器上跑同样的 (a, b) 对，把结果按格式追加进来即可；
 *  - 22 组用例已覆盖：空串、重复字符、≥200 字符触发 autojunk 的长串、中日文、版本号。
 */
export interface DifflibCase {
  a: string;
  b: string;
  /** 归一化后的 a（NFKC + 小写 + 去空白） */
  na: string;
  nb: string;
  /** CPython difflib 的 ratio */
  ratio: number;
}

export const DIFFLIB_CASES: DifflibCase[] = [
  { a: "挑战怎么玩？", b: "挑战怎么玩", na: "挑战怎么玩?", nb: "挑战怎么玩", ratio: 0.9090909090909091 },
  { a: "挑战怎么玩", b: "挑战怎么玩？", na: "挑战怎么玩", nb: "挑战怎么玩?", ratio: 0.9090909090909091 },
  { a: "我背包里有什么", b: "我背包里有什么？", na: "我背包里有什么", nb: "我背包里有什么?", ratio: 0.9333333333333333 },
  { a: "我背包里有什么", b: "我背包里有些什么", na: "我背包里有什么", nb: "我背包里有些什么", ratio: 0.9333333333333333 },
  { a: "背包满了怎么办", b: "背包满的时候怎么办", na: "背包满了怎么办", nb: "背包满的时候怎么办", ratio: 0.75 },
  { a: "副本怪物多久刷新", b: "副本里怪物多久刷新一次", na: "副本怪物多久刷新", nb: "副本里怪物多久刷新一次", ratio: 0.8421052631578947 },
  { a: "你好", b: "你好呀", na: "你好", nb: "你好呀", ratio: 0.8 },
  { a: "你好", b: "最近怎么样", na: "你好", nb: "最近怎么样", ratio: 0.0 },
  { a: "", b: "你好", na: "", nb: "你好", ratio: 0.0 },
  { a: "你好", b: "", na: "你好", nb: "", ratio: 0.0 },
  { a: "", b: "", na: "", nb: "", ratio: 1.0 },
  { a: "重复重复重复", b: "重复重复重复重复", na: "重复重复重复", nb: "重复重复重复重复", ratio: 0.8571428571428571 },
  { a: "abc", b: "abc", na: "abc", nb: "abc", ratio: 1.0 },
  { a: "abc", b: "abd", na: "abc", nb: "abd", ratio: 0.6666666666666666 },
  { a: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", b: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", na: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", nb: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ratio: 1.0 },
  { a: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", b: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", na: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", nb: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ratio: 1.0 },
  { a: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", b: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaac", na: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", nb: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaac", ratio: 0.9960159362549801 },
  { a: "あいうえお", b: "あいうえおか", na: "あいうえお", nb: "あいうえおか", ratio: 0.9090909090909091 },
  { a: "1.2 版本改了什么", b: "v1.2版本改了什么", na: "1.2版本改了什么", nb: "v1.2版本改了什么", ratio: 0.9473684210526315 },
  { a: "排行榜第一名是谁", b: "排行榜的第一名是谁呀", na: "排行榜第一名是谁", nb: "排行榜的第一名是谁呀", ratio: 0.8888888888888888 },
  { a: "这个副本的怪物多久会刷新一次呢", b: "副本怪物刷新时间", na: "这个副本的怪物多久会刷新一次呢", nb: "副本怪物刷新时间", ratio: 0.5217391304347826 },
  { a: "邮件附件怎么领", b: "怎么领取邮件里的附件", na: "邮件附件怎么领", nb: "怎么领取邮件里的附件", ratio: 0.35294117647058826 },
];
