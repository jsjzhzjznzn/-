using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 表情气泡播放器（挂在气泡模板里的贴纸 Image 上）
/// 气泡是虚拟列表池化复用的，所以播放/停止由 FriendUIController 在每次绑定时显式驱动：
/// 绑到表情消息就 Play，绑到文字消息或气泡被回收就 Stop。
/// 用 unscaledDeltaTime 计时：聊天面板属于界面表现，跟随项目的 UI 时间约定（暂停中也照常动）。
/// </summary>
[RequireComponent(typeof(Image))]
public class StickerAnimation : MonoBehaviour
{
    /// <summary>要换图的 Image（就是本物体）</summary>
    private Image target;

    /// <summary>当前播放的帧序列（null=没在播）</summary>
    private Sprite[] frames;

    /// <summary>单帧时长（秒）</summary>
    private float frameSeconds;

    /// <summary>距下一帧的累计时间</summary>
    private float elapsed;

    /// <summary>当前帧下标</summary>
    private int index;

    /// <summary>是否正在播放多帧动画（静态表情不需要每帧 Update）</summary>
    public bool IsPlaying => frames != null && frames.Length > 1;

    private void Awake()
    {
        target = GetComponent<Image>();
    }

    /// <summary>播放指定表情（静态表情等于只显示一张图）</summary>
    public void Play(StickerInfo sticker)
    {
        if (sticker == null) return;
        Play(sticker.Frames, sticker.Fps);
    }

    /// <summary>
    /// 播放帧序列的方法
    /// </summary>
    /// <param name="stickerFrames">参数名：stickerFrames，类型：Sprite[]，用途：帧序列（旧→新）</param>
    /// <param name="fps">参数名：fps，类型：float，用途：播放帧率</param>
    public void Play(Sprite[] stickerFrames, float fps)
    {
        if (target == null) target = GetComponent<Image>();
        frames = stickerFrames;
        index = 0;
        elapsed = 0f;
        frameSeconds = fps > 0.01f ? 1f / fps : 1f / StickerCatalog.DefaultFps;
        if (frames != null && frames.Length > 0) target.sprite = frames[0];
    }

    /// <summary>停止播放（气泡被回收或改绑文字消息时调用，避免池化复用的旧帧残留）</summary>
    public void Stop()
    {
        frames = null;
        elapsed = 0f;
        index = 0;
    }

    /// <summary>气泡被隐藏/回收时自动停下，池里不再有隐藏的物体空转</summary>
    private void OnDisable()
    {
        Stop();
    }

    private void Update()
    {
        if (!IsPlaying) return;

        elapsed += Time.unscaledDeltaTime;
        if (elapsed < frameSeconds) return;

        // 掉帧（单帧耗时跨过多帧）时直接对齐到当前应显示的帧，不做逐帧补播
        elapsed -= frameSeconds;
        if (elapsed > frameSeconds) elapsed = 0f;

        index = (index + 1) % frames.Length;
        target.sprite = frames[index];
    }
}
