#if UNITY_EDITOR
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GameEditor.Diagnostics
{
    /// <summary>
    /// 诊断 "Resource ID out of range in SetResource (max is 1048575)"。
    /// 该报错说明进程内创建的 GPU 资源（纹理/RT/材质/Mesh/Buffer）累计超过 20 位 ID 上限，
    /// 也就是有东西在运行时持续创建资源却不释放。本工具每 2 秒采样一次，写 CSV，用来看是哪一类在涨。
    /// 用法：菜单 Tools/DSH/GPU Leak Watch (toggle) 打开，进 Play Mode 复现，再看 Logs/gpu-watch.csv。
    /// 定位完成后可以整个文件删掉。
    /// </summary>
    [InitializeOnLoad]
    public static class GpuLeakWatch
    {
        private const string PrefKey = "DSH.GpuLeakWatch.Enabled";
        private const string MenuPath = "Tools/DSH/GPU Leak Watch (toggle)";
        private const double IntervalSec = 2.0;

        private static double _nextSample;
        private static string _csvPath;
        private static double _lastConsoleLog;

        static GpuLeakWatch()
        {
            EditorApplication.update += Tick;
        }

        private static bool Enabled
        {
            get { return EditorPrefs.GetBool(PrefKey, false); }
        }

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            bool enable = !Enabled;
            EditorPrefs.SetBool(PrefKey, enable);
            if (enable)
            {
                ResetFile();
                Debug.Log("[GPU-WATCH] 已开启 → " + _csvPath);
            }
            else
            {
                Debug.Log("[GPU-WATCH] 已关闭");
            }
        }

        private static void ResetFile()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string dir = Path.Combine(root, "Logs");
            Directory.CreateDirectory(dir);
            _csvPath = Path.Combine(dir, "gpu-watch.csv");
            File.WriteAllText(_csvPath,
                "time,frame,playing,gpuMB,textures,renderTextures,materials,meshes,gameObjects,monoMB\n",
                new UTF8Encoding(false));
            _nextSample = 0;
            _lastConsoleLog = 0;
        }

        private static void Tick()
        {
            if (!Enabled) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextSample) return;
            _nextSample = now + IntervalSec;

            if (string.IsNullOrEmpty(_csvPath) || !File.Exists(_csvPath)) ResetFile();

            long gpuBytes = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
            long monoBytes = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int textures = Resources.FindObjectsOfTypeAll<Texture>().Length;
            int renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            int objects = Resources.FindObjectsOfTypeAll<GameObject>().Length;

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:HH:mm:ss},{1},{2},{3:F1},{4},{5},{6},{7},{8},{9:F1}\n",
                System.DateTime.Now,
                Time.frameCount,
                Application.isPlaying ? 1 : 0,
                gpuBytes / 1048576f,
                textures, renderTextures, materials, meshes, objects,
                monoBytes / 1048576f);

            try
            {
                File.AppendAllText(_csvPath, line, new UTF8Encoding(false));
            }
            catch
            {
                // 文件被占用时跳过本次采样，不影响编辑器
            }

            if (now - _lastConsoleLog > 15)
            {
                _lastConsoleLog = now;
                Debug.Log("[GPU-WATCH] " + line.TrimEnd());
            }
        }
    }
}
#endif
