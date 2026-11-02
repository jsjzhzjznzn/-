namespace MieMieFrameWork
{
    using System;
    using UnityEngine;
    using UnityEngine.Audio;

    public partial class AudioManager
    {
        /// <summary>
        /// 音频组件与可调播放预算（挂在 FrameRoot 预制体上序列化）
        /// </summary>
        [Serializable]
        public sealed class AudioManagerConfig
        {
            [Header("音频组件配置")]
            [Tooltip("特效声音播放器预制体（带 AudioSource，会被池化）")]
            [SerializeField]
            private GameObject efPlayerES;

            [Tooltip("BGM 播放器")]
            [SerializeField]
            private AudioSource bgmSource;

            [Tooltip("环境音播放器")]
            [SerializeField]
            private AudioSource ambienceSource;

            [Tooltip("特效音对象池根节点")]
            [SerializeField]
            private Transform effectClipRoot;

            [Tooltip("音频混音器")]
            [SerializeField]
            private AudioMixer mixer;

            [Tooltip("特效音混音组")]
            [SerializeField]
            private AudioMixerGroup effectMixerGroup;

            [Header("音频音量设置")]
            [SerializeField, Range(0, 1), Tooltip("全局音量系数")]
            private float globalVolumeFactor = 1;

            [SerializeField, Range(0, 1), Tooltip("BGM 基准音量")]
            private float bgVolumeBaseNum = 1;

            [SerializeField, Range(0, 1), Tooltip("环境音基准音量")]
            private float ambienceVolumeBaseNum = 0.8f;

            [SerializeField, Range(0, 1), Tooltip("特效音基准音量")]
            private float effectVolumeBaseNum = 1;

            [Header("音频全局控制")]
            [SerializeField, Tooltip("静音")]
            private bool isMute;

            [SerializeField, Tooltip("循环播放（仅 BGM 通道）")]
            private bool isLoop;

            [SerializeField, Tooltip("暂停所有")]
            private bool isPause;

            [Header("音效播放预算")]
            [SerializeField, Min(1), Tooltip("同时占用的音效数量上限，含加载中与暂停中的请求")]
            private int maxConcurrentEffects = 32;

            [SerializeField, Min(1), Tooltip("每帧接受的音效请求数量上限")]
            private int maxEffectsPerFrame = 10;

            [SerializeField, Min(1), Tooltip("同一片段或同一路径的同时音效数量上限")]
            private int maxSameClipEffects = 4;

            [SerializeField, Min(0), Tooltip("初始化时预热的特效播放器数量")]
            private int effectPrewarmCount = 8;

            public GameObject EfPlayerES => efPlayerES;
            public AudioSource BgmSource => bgmSource;
            public AudioSource AmbienceSource => ambienceSource;
            public Transform EffectClipRoot => effectClipRoot;
            public AudioMixer Mixer => mixer;
            public AudioMixerGroup EffectMixerGroup => effectMixerGroup;
            public float GlobalVolumeFactor => globalVolumeFactor;
            public float BgVolumeBaseNum => bgVolumeBaseNum;
            public float AmbienceVolumeBaseNum => ambienceVolumeBaseNum;
            public float EffectVolumeBaseNum => effectVolumeBaseNum;
            public bool IsMute => isMute;
            public bool IsLoop => isLoop;
            public bool IsPause => isPause;
            public int MaxConcurrentEffects => maxConcurrentEffects;
            public int MaxEffectsPerFrame => maxEffectsPerFrame;
            public int MaxSameClipEffects => maxSameClipEffects;
            public int EffectPrewarmCount => effectPrewarmCount;
        }
    }
}
