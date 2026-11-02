using System;

namespace Game.MapGen
{
    /// <summary>
    /// 多倍频柏林噪声采样，产出归一化到 0~1 的二维噪声图。
    /// 与 pcg-master 的 Utils/index.ts 里的 generateNoiseMap / invertLerp 对应。
    /// </summary>
    public static class NoiseMapGenerator
    {
        /// <summary>根据百分比得出值</summary>
        public static float Lerp(float min, float max, float rate)
        {
            return min + (max - min) * rate;
        }

        /// <summary>根据值得出百分比</summary>
        public static float InvertLerp(float min, float max, float value)
        {
            return (value - min) / (max - min);
        }

        /// <summary>
        /// 生成噪声图，返回值范围 [0,1]，下标为 [x, y]。
        /// </summary>
        public static float[,] GenerateNoiseMap(
            int mapWidth,
            int mapHeight,
            int seed,
            float scale,
            int octaves,
            float persistance,
            float lacunarity,
            float offsetX,
            float offsetY)
        {
            var perlinNoise = new PerlinNoise().Seed(seed);

            float maxNoiseHeight = float.NegativeInfinity;
            float minNoiseHeight = float.PositiveInfinity;

            var noiseMap = new float[mapWidth, mapHeight];

            float halfWidth = mapWidth / 2f;
            float halfHeight = mapHeight / 2f;

            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    float amplitude = 1;
                    float frequency = 1;
                    float noiseHeight = 0;

                    for (int i = 0; i < octaves; i++)
                    {
                        float sampleX = ((x - halfWidth + offsetX * mapWidth) / scale) * frequency;
                        float sampleY = ((y - halfHeight + offsetY * mapHeight) / scale) * frequency;
                        float perlinValue = perlinNoise.Noise(sampleX, sampleY) * 2 - 1;
                        noiseHeight += perlinValue * amplitude;
                        amplitude *= persistance;
                        frequency *= lacunarity;
                    }

                    if (noiseHeight > maxNoiseHeight)
                    {
                        maxNoiseHeight = noiseHeight;
                    }
                    else if (noiseHeight < minNoiseHeight)
                    {
                        minNoiseHeight = noiseHeight;
                    }
                    noiseMap[x, y] = noiseHeight;
                }
            }

            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    // 原 Demo 保留三位小数，这里保持一致，便于和原版对照同一 seed 的结果
                    noiseMap[x, y] = (float)Math.Round(InvertLerp(minNoiseHeight, maxNoiseHeight, noiseMap[x, y]), 3);
                }
            }

            return noiseMap;
        }
    }
}
