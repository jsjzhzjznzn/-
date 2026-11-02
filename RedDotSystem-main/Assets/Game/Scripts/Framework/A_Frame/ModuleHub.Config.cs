namespace MieMieFrameWork
{
    using UnityEngine;

    /// <summary>
    /// ModuleHub 的 Inspector 配置（挂在 FrameRoot 预制体上）
    /// </summary>
    public partial class ModuleHub
    {
        /// <summary>
        /// 启动时是否初始化 YooAsset。关闭时路径加载的音频会失败，但直接传 AudioClip 仍可播放
        /// </summary>
        [Header("资源")]
        [SerializeField, Tooltip("启动时初始化 YooAsset（关闭后按路径加载资源会失败）")]
        private bool initializeResource = true;

        public bool InitializeResource => initializeResource;

        /// <summary>
        /// 音频管理器配置：不填则 ModuleHub 启动时报错（音频是必需的框架能力）
        /// </summary>
        [Header("音频")]
        [SerializeField, Tooltip("音频组件引用与播放预算")]
        private AudioManager.AudioManagerConfig audioManagerConfig;

        public AudioManager.AudioManagerConfig AudioManagerConfig => audioManagerConfig;
    }
}
