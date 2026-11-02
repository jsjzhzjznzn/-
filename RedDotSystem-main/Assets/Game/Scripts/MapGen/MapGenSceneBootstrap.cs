using UnityEngine;

namespace Game.MapGen
{
    /// <summary>
    /// mapgen 场景的落点：等框架启动（ModuleHubBootstrap 早于场景自举）后，把场景自带的 Canvas 交给 UIManager 接管，
    /// 让地图画布和滑杆走框架 UI 相机与统一缩放。框架未就绪前 Canvas 保持 ScreenSpaceOverlay，画面仍然可见。
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class MapGenSceneBootstrap : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;

        private async void Start()
        {
            await MieMieFrameWork.ModuleHubBootstrap.EnsureAsync();

            var ui = SkierFramework.UIManager.Instance;
            if (ui == null)
            {
                Debug.LogWarning("[MapGen] UIManager 未就绪，画布保持场景默认渲染");
                return;
            }

            ui.AdoptCanvas(canvas != null ? canvas : GetComponent<Canvas>());
        }
    }
}
