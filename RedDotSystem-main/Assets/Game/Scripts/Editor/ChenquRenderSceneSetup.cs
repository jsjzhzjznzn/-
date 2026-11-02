// 陈曲（贝洛伯格街区）预览场景的编辑器辅助脚本。
//
// 背景：该建筑群世界尺寸约 3863 x 765 x 4201（对角 5758 单位），
// 而工程 Ultra 画质的 QualitySettings.shadowDistance 只有 150，
// 对这个尺度等于完全没有阴影。直接把 shadowDistance 改大又会影响正式游戏，
// 所以这里只在打开预览场景期间临时放大，关闭/切换场景时还原。
//
// 注意：QualitySettings.renderPipeline（逐画质档绑定）优先级高于
// GraphicsSettings.defaultRenderPipeline，所以阴影距离必须通过 QualitySettings 设置。

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 进入 ChenquRender 预览场景时临时提高阴影距离，离开时还原。
    /// 仅影响编辑器内的预览，不影响打包与运行时。
    /// </summary>
    [InitializeOnLoad]
    public static class ChenquRenderSceneSetup
    {
        private const string PreviewScenePath = "Assets/Game/Scenes/ChenquRender.scene";

        // 5758 单位对角线上，2000 约等于把整条主街罩进阴影；再大阴影贴图精度就不够了。
        private const float PreviewShadowDistance = 2000f;

        private static bool _applied;
        private static float _savedShadowDistance;

        static ChenquRenderSceneSetup()
        {
            Apply(SceneManager.GetActiveScene());
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            Apply(scene);
        }

        private static void OnSceneClosed(Scene scene)
        {
            if (_applied && scene.path == PreviewScenePath)
            {
                Restore();
            }
        }

        private static void Apply(Scene scene)
        {
            if (scene.path != PreviewScenePath)
            {
                if (_applied)
                {
                    Restore();
                }

                return;
            }

            if (_applied)
            {
                return;
            }

            _savedShadowDistance = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = PreviewShadowDistance;
            _applied = true;
        }

        private static void Restore()
        {
            QualitySettings.shadowDistance = _savedShadowDistance;
            _applied = false;
        }
    }
}
