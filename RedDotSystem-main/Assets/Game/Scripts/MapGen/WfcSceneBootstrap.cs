using UnityEngine;

namespace Game.MapGen
{
    /// <summary>
    /// wfc 场景的落点：等框架启动后把场景自带的 Canvas 交给 UIManager 接管，
    /// 让 WFC 地图画布和右侧控件走框架 UI 相机与统一缩放。
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class WfcSceneBootstrap : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;

        private async void Start()
        {
            await MieMieFrameWork.ModuleHubBootstrap.EnsureAsync();

            var ui = SkierFramework.UIManager.Instance;
            if (ui == null)
            {
                Debug.LogWarning("[WFC] UIManager 未就绪，画布保持场景默认渲染");
                return;
            }

            ui.AdoptCanvas(canvas != null ? canvas : GetComponent<Canvas>());
        }
    }
}
