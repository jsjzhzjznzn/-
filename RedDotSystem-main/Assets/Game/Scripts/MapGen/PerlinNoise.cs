using System;

namespace Game.MapGen
{
    /// <summary>
    /// 经典柏林噪声（Ken Perlin 的 2D/3D 梯度噪声实现，固定置换表 + seed 打乱）。
    /// 与 pcg-master 的 Script/Utils/PerlinNoise.ts 逐行对应。
    /// </summary>
    public class PerlinNoise
    {
        private readonly Grad[] grad3;
        private readonly int[] p;
        private readonly int[] perm = new int[512];
        private readonly Grad[] gradP = new Grad[512];

        private class Grad
        {
            public readonly float x;
            public readonly float y;
            public readonly float z;

            public Grad(float x, float y, float z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public float Dot2(float vx, float vy) => x * vx + y * vy;

            public float Dot3(float vx, float vy, float vz) => x * vx + y * vy + z * vz;
        }

        public PerlinNoise()
        {
            grad3 = new[]
            {
                new Grad(1, 1, 0), new Grad(-1, 1, 0), new Grad(1, -1, 0), new Grad(-1, -1, 0),
                new Grad(1, 0, 1), new Grad(-1, 0, 1), new Grad(1, 0, -1), new Grad(-1, 0, -1),
                new Grad(0, 1, 1), new Grad(0, -1, 1), new Grad(0, 1, -1), new Grad(0, -1, -1),
            };

            p = new[]
            {
                151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142, 8, 99, 37, 240,
                21, 10, 23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32, 57, 177, 33, 88,
                237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175, 74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83,
                111, 229, 122, 60, 211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216,
                80, 73, 209, 76, 132, 187, 208, 89, 18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186,
                3, 64, 52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58,
                17, 182, 189, 28, 42, 223, 183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9,
                129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104, 218, 246, 97, 228, 251, 34, 242, 193,
                238, 210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157,
                184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128,
                195, 78, 66, 215, 61, 156, 180,
            };

            Seed(0);
        }

        private static float Fade(float t)
        {
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        private static float Lerp(float a, float b, float t)
        {
            return (1 - t) * a + t * b;
        }

        public PerlinNoise Seed(int seed)
        {
            if (seed > 0 && seed < 1)
            {
                seed *= 65536;
            }
            seed = (int)Math.Floor((double)seed);
            if (seed < 256)
            {
                seed |= seed << 8;
            }
            for (int i = 0; i < 256; i++)
            {
                int v;
                if ((i & 1) == 1)
                {
                    v = p[i] ^ (seed & 255);
                }
                else
                {
                    v = p[i] ^ ((seed >> 8) & 255);
                }
                perm[i] = perm[i + 256] = v;
                gradP[i] = gradP[i + 256] = grad3[v % 12];
            }

            return this;
        }

        public float Noise(float x, float y)
        {
            int ix = (int)Math.Floor(x);
            int iy = (int)Math.Floor(y);
            x = x - ix;
            y = y - iy;
            int X = ix & 255;
            int Y = iy & 255;
            float n00 = gradP[X + perm[Y]].Dot2(x, y);
            float n01 = gradP[X + perm[Y + 1]].Dot2(x, y - 1);
            float n10 = gradP[X + 1 + perm[Y]].Dot2(x - 1, y);
            float n11 = gradP[X + 1 + perm[Y + 1]].Dot2(x - 1, y - 1);
            float u = Fade(x);
            return Lerp(Lerp(n00, n10, u), Lerp(n01, n11, u), Fade(y));
        }
    }
}
