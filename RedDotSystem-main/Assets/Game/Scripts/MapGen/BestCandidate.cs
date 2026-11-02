using System;
using UnityEngine;

namespace Game.MapGen
{
    /// <summary>
    /// Mitchell 最佳候选采样：每次生成一批候选点，取「离已选点与边界最远」的那个，
    /// 从而得到近似均匀分布的资源点。与 pcg-master 的 Script/Utils/BestCandidate.ts 对应。
    /// </summary>
    public static class BestCandidate
    {
        /// <summary>生成 n 个均匀分布的点（坐标范围 [0,mapWidth] × [0,mapHeight]）</summary>
        /// <summary>
        /// 在给定区域内用最佳候选算法撒点。isValid 返回 false 的位置视为不可放置，
        /// 传 null 即在整张矩形区域内均匀撒点。
        /// </summary>
        public static Vector2[] Generate(int mapWidth, int mapHeight, int n, Func<Vector2, bool> isValid)
        {
            if (n < 1)
            {
                throw new ArgumentException("Greater than 0");
            }

            // 已选点
            var result = new Vector2[n];
            int count = 0;

            // 第一个点也在候选里挑，避免落在不可放置区域
            int candidateNum = mapWidth * 10;
            for (int retry = 0; count < 1 && retry < 200; retry++)
            {
                for (int i = 0; i < candidateNum; i++)
                {
                    Vector2 point = RandomPoint(mapWidth, mapHeight);
                    if (isValid == null || isValid(point))
                    {
                        result[count++] = point;
                        break;
                    }
                }
            }
            if (count == 0)
            {
                throw new ArgumentException("找不到可放置的位置");
            }

            // 边界点也参与距离比较，防止点扎满整个地图边缘
            var edgePoints = new[]
            {
                new Vector2(0, 0),
                new Vector2(0, mapHeight / 2f),
                new Vector2(0, mapHeight),
                new Vector2(mapWidth / 2f, 0),
                new Vector2(mapWidth / 2f, mapHeight),
                new Vector2(mapWidth, 0),
                new Vector2(mapWidth, mapHeight / 2f),
                new Vector2(mapWidth, mapHeight),
            };

            int emptyRounds = 0;
            while (count < n && emptyRounds < 200)
            {
                float maxDistance = float.NegativeInfinity;
                Vector2 best = Vector2.zero;
                bool found = false;

                for (int i = 0; i < candidateNum; i++)
                {
                    Vector2 point = RandomPoint(mapWidth, mapHeight);
                    if (isValid != null && !isValid(point))
                    {
                        continue;
                    }

                    // 本候选点到「所有已选点 + 边界点」的最小距离
                    float minDistance = float.PositiveInfinity;
                    for (int j = 0; j < count; j++)
                    {
                        minDistance = Mathf.Min(minDistance, CalculateDistance(point, result[j]));
                    }
                    for (int j = 0; j < edgePoints.Length; j++)
                    {
                        minDistance = Mathf.Min(minDistance, CalculateDistance(point, edgePoints[j]));
                    }

                    if (minDistance > maxDistance)
                    {
                        maxDistance = minDistance;
                        best = point;
                        found = true;
                    }
                }

                // 一整轮都没有合法候选就收手，避免死循环；数量按实际结果截断返回
                if (!found)
                {
                    emptyRounds++;
                    continue;
                }

                emptyRounds = 0;
                result[count++] = best;
            }

            if (count == n)
            {
                return result;
            }

            var trimmed = new Vector2[count];
            Array.Copy(result, trimmed, count);
            return trimmed;
        }

        private static Vector2 RandomPoint(int mapWidth, int mapHeight)
        {
            return new Vector2(Mathf.Floor(mapWidth * UnityEngine.Random.value), Mathf.Floor(mapHeight * UnityEngine.Random.value));
        }

        private static float CalculateDistance(Vector2 a, Vector2 b)
        {
            return (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y);
        }
    }
}
