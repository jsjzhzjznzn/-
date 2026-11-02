/**
 * 文本归一化与相似度（qa_cache 的「同问」判据）。
 *
 * 为什么移植 CPython 的 difflib：
 *   旧实现用 `difflib.SequenceMatcher(None, a, b).ratio() >= 0.90` 判定同问。
 *   这是一个**连续值**判据——换成编辑距离/Jaccard 会整体改变命中率（行为漂移）。
 *   所以这里逐句翻译 Ratcliff–Obershelp（含 autojunk 与 junk 扩展规则）。
 *
 * 归一化（app.py::_norm_question）：NFKC 全角→半角、小写、去空白——
 *   "挑战怎么玩？"与"挑战怎么玩"视为同问。
 */

/** 归一化：NFKC + 小写 + 去全部空白。 */
export function normalizeQuestion(text: string): string {
  return Array.from((text ?? "").normalize("NFKC").toLowerCase())
    .filter((ch) => !/\s/.test(ch))
    .join("");
}

/** 匹配块 [aIndex, bIndex, size] */
type Match = readonly [number, number, number];

interface ChainB {
  b2j: Map<string, number[]>;
  bjunk: Set<string>;
}

/** 等价 difflib.SequenceMatcher.__chain_b（junk 恒为空；autojunk 对 ≥200 元素生效）。 */
function chainB(b: string): ChainB {
  const b2j = new Map<string, number[]>();
  for (let i = 0; i < b.length; i += 1) {
    const element = b[i]!;
    const indices = b2j.get(element);
    if (indices) indices.push(i);
    else b2j.set(element, [i]);
  }

  const bjunk = new Set<string>();
  // isjunk 恒为 null（与旧实现一致），故只有 autojunk 生效
  const n = b.length;
  if (n >= 200) {
    const ntest = Math.floor(n / 100) + 1;
    for (const [element, indices] of [...b2j.entries()]) {
      if (indices.length > ntest) {
        bjunk.add(element);
        b2j.delete(element);
      }
    }
  }
  return { b2j, bjunk };
}

/** 等价 difflib.find_longest_match（junk 只影响两端的扩展步）。 */
function findLongestMatch(
  a: string,
  b: string,
  chain: ChainB,
  alo: number,
  ahi: number,
  blo: number,
  bhi: number,
): Match {
  let besti = alo;
  let bestj = blo;
  let bestsize = 0;
  let j2len = new Map<number, number>();

  for (let i = alo; i < ahi; i += 1) {
    const newj2len = new Map<number, number>();
    for (const j of chain.b2j.get(a[i]!) ?? []) {
      if (j < blo) continue;
      if (j >= bhi) break;
      const k = (j2len.get(j - 1) ?? 0) + 1;
      newj2len.set(j, k);
      if (k > bestsize) {
        besti = i - k + 1;
        bestj = j - k + 1;
        bestsize = k;
      }
    }
    j2len = newj2len;
  }

  const isBJunk = (index: number): boolean => chain.bjunk.has(b[index]!);

  // 扩展 best：先非 junk，再吞掉两侧的 junk
  while (
    besti > alo &&
    bestj > blo &&
    !isBJunk(bestj - 1) &&
    a[besti - 1] === b[bestj - 1]
  ) {
    besti -= 1;
    bestj -= 1;
    bestsize += 1;
  }
  while (
    besti + bestsize < ahi &&
    bestj + bestsize < bhi &&
    !isBJunk(bestj + bestsize) &&
    a[besti + bestsize] === b[bestj + bestsize]
  ) {
    bestsize += 1;
  }
  while (besti > alo && bestj > blo && isBJunk(bestj - 1) && a[besti - 1] === b[bestj - 1]) {
    besti -= 1;
    bestj -= 1;
    bestsize += 1;
  }
  while (
    besti + bestsize < ahi &&
    bestj + bestsize < bhi &&
    isBJunk(bestj + bestsize) &&
    a[besti + bestsize] === b[bestj + bestsize]
  ) {
    bestsize += 1;
  }

  return [besti, bestj, bestsize];
}

/** 等价 difflib.get_matching_blocks（末尾恒有一个 size=0 的哨兵块）。 */
function getMatchingBlocks(a: string, b: string, chain: ChainB): Match[] {
  const queue: [number, number, number, number][] = [[0, a.length, 0, b.length]];
  const matching: Match[] = [];

  while (queue.length > 0) {
    const [alo, ahi, blo, bhi] = queue.pop()!;
    const [i, j, k] = findLongestMatch(a, b, chain, alo, ahi, blo, bhi);
    if (k > 0) {
      matching.push([i, j, k]);
      if (alo < i && blo < j) queue.push([alo, i, blo, j]);
      if (i + k < ahi && j + k < bhi) queue.push([i + k, ahi, j + k, bhi]);
    }
  }

  matching.sort((left, right) => left[0] - right[0] || left[1] - right[1]);

  // 合并相邻块 + 追加哨兵（与 Python 一致）
  const merged: Match[] = [];
  let [i1, j1, k1] = [0, 0, 0];
  for (const [i2, j2, k2] of matching) {
    if (i1 + k1 === i2 && j1 + k1 === j2) {
      k1 += k2;
    } else {
      if (k1 > 0) merged.push([i1, j1, k1]);
      [i1, j1, k1] = [i2, j2, k2];
    }
  }
  if (k1 > 0) merged.push([i1, j1, k1]);
  merged.push([a.length, b.length, 0]);
  return merged;
}

/**
 * 等价 difflib.SequenceMatcher(None, a, b).ratio()：
 *   ratio = 2 * M / T（M = 匹配字符总数，T = 两边总长；两边都空 → 1.0）
 */
export function similarityRatio(a: string, b: string): number {
  if (a.length === 0 && b.length === 0) return 1;
  const chain = chainB(b);
  const total = a.length + b.length;
  let matches = 0;
  for (const [, , size] of getMatchingBlocks(a, b, chain)) matches += size;
  return (2 * matches) / total;
}
