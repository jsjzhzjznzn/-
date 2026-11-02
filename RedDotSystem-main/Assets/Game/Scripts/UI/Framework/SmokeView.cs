using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SkierFramework
{
    /// <summary>
    /// P1 冒烟视图：验证 UIManager 加载预制体、分层、UIView 生命周期、关闭流程。
    /// 后续可以删除，或保留作为框架自检面板（编辑器快捷键打开）。
    /// </summary>
    public class SmokeView : UIView
    {
        public TMP_Text label;
        public Button closeButton;

        public override void OnAddListener()
        {
            base.OnAddListener();
            if (closeButton != null)
                closeButton.onClick.AddListener(OnCloseClicked);
        }

        public override void OnRemoveListener()
        {
            if (closeButton != null)
                closeButton.onClick.RemoveListener(OnCloseClicked);
            base.OnRemoveListener();
        }

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);
            if (label != null)
                label.text = "SkierFramework 冒烟测试\nUIManager / UIView / UIConfig 正常";
            Debug.Log("[SmokeView] OnOpen");
        }

        private void OnCloseClicked()
        {
            UIManager.Instance.Close(UIType.SmokeView).Forget();
        }
    }
}
