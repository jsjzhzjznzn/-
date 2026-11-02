using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 大世界地图（全屏）：烘焙底图 + 玩家标记 + 滚轮缩放 / 拖拽平移。
///
/// 底图与坐标换算全部走 <see cref="DashijieMinimap"/> 的世界契约，本类只做显示与交互。
/// 地图内容（地图像素 = UI 单位）挂在 RectMask2D 窗口下，缩放改 sizeDelta、平移改 anchoredPosition，
/// 玩家标记随内容一起缩放，不需要第二套坐标换算。
/// </summary>
public class DashijieMapView : UIView
{
    /// <summary>地图内容（底图 RawImage 所在节点，缩放/平移都改它）</summary>
    [ControlBinding] public RectTransform mapContent;
    /// <summary>底图显示（与 mapContent 同一节点）</summary>
    [ControlBinding("MapContent")] public RawImage mapImage;
    [ControlBinding] public RectTransform playerMark;
    /// <summary>任务目标标记（追踪中任务的当前目标 NPC，与底图同一坐标系，随缩放/平移一起动）</summary>
    [ControlBinding] public RectTransform questMarker;
    [ControlBinding] public Button closeButton;
    [ControlBinding("Backdrop")] public Button backdropButton;
    [ControlBinding] public TMP_Text hintText;

    private const float MinZoom = 0.5f;
    private const float MaxZoom = 2f;
    private const float WheelStep = 0.1f;

    private DashijieMinimap minimap;
    private Player player;
    private WorldQuestDirector questDirector;

    private float zoom = 1f;
    private float fitScale = 1f;
    private Vector2 pan;
    private Vector2 lastWindowSize;
    private Vector3 lastMousePosition;

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        minimap = new DashijieMinimap();
        if (!minimap.Load())
        {
            Debug.LogWarning($"[DashijieMapView] 底图缺失：Resources/{DashijieMinimap.ResourcePath}");
            minimap = null;
        }
        else if (mapImage != null)
        {
            mapImage.texture = minimap.Texture;
            mapImage.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        player = FindObjectOfType<Player>();
        questDirector = FindObjectOfType<WorldQuestDirector>();
        zoom = 1f;
        pan = Vector2.zero;
        ApplyLayout();

        if (hintText != null) hintText.text = "M / Esc 关闭 · 滚轮缩放 · 拖拽平移";
    }

    public override void OnClose()
    {
        if (mapImage != null) mapImage.texture = null;
        minimap?.Destroy();
        minimap = null;
        player = null;
        base.OnClose();
    }

    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (backdropButton != null) backdropButton.onClick.AddListener(OnCloseClicked);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveAllListeners();
        if (backdropButton != null) backdropButton.onClick.RemoveAllListeners();
    }

    private void OnCloseClicked() => OnCancel();

    private void Update()
    {
        if (minimap == null || mapContent == null) return;

        RefreshLayoutIfWindowChanged();
        HandleZoom();
        HandleDrag();
        RefreshPlayerMark();
        RefreshQuestMarker();
    }

    /// <summary>窗口铺满屏幕：首帧布局解析完、或分辨率变化时才重算，不要每帧写 sizeDelta</summary>
    private void RefreshLayoutIfWindowChanged()
    {
        var window = mapContent.parent as RectTransform;
        if (window == null) return;
        if (window.rect.size == lastWindowSize) return;

        lastWindowSize = window.rect.size;
        ApplyLayout();
    }

    private void HandleZoom()
    {
        float wheel = Input.mouseScrollDelta.y;
        if (Mathf.Abs(wheel) < 0.01f) return;

        float next = Mathf.Clamp(zoom + wheel * WheelStep, MinZoom, MaxZoom);
        if (Mathf.Approximately(next, zoom)) return;

        zoom = next;
        ApplyLayout();
    }

    private void HandleDrag()
    {
        if (!Input.GetMouseButton(0))
        {
            lastMousePosition = Input.mousePosition;
            return;
        }

        Vector3 delta = Input.mousePosition - lastMousePosition;
        lastMousePosition = Input.mousePosition;
        if (delta.sqrMagnitude <= 0.01f) return;

        pan += new Vector2(delta.x, delta.y);
        ClampPan();
        mapContent.anchoredPosition = pan;
    }

    private void ApplyLayout()
    {
        if (mapContent == null) return;

        var window = mapContent.parent as RectTransform;
        if (window == null) return;

        // 按“覆盖”适配（取长边）：方图铺满非方形窗口，任何缩放/拖拽下都不会露出黑边
        fitScale = Mathf.Max(window.rect.width, window.rect.height) / DashijieMinimap.TextureSize;
        float size = DashijieMinimap.TextureSize * fitScale * zoom;
        mapContent.sizeDelta = new Vector2(size, size);

        ClampPan();
        mapContent.anchoredPosition = pan;
        RefreshPlayerMark();
    }

    private void ClampPan()
    {
        if (mapContent == null) return;
        var window = mapContent.parent as RectTransform;
        if (window == null) return;

        float maxX = Mathf.Max(0f, (mapContent.sizeDelta.x - window.rect.width) * 0.5f);
        float maxY = Mathf.Max(0f, (mapContent.sizeDelta.y - window.rect.height) * 0.5f);
        pan = new Vector2(Mathf.Clamp(pan.x, -maxX, maxX), Mathf.Clamp(pan.y, -maxY, maxY));
    }

    private void RefreshPlayerMark()
    {
        if (playerMark == null) return;

        if (player == null)
        {
            player = FindObjectOfType<Player>();
            if (player == null) return;
        }

        Vector3 position = player.transform.position;
        Vector2 mapPixel = DashijieMinimap.WorldToMap(new Vector2(position.x, position.z));

        // 内容矩形的本地单位 = 底图像素 × fitScale × zoom
        float scale = fitScale * zoom;
        playerMark.anchoredPosition = (mapPixel - new Vector2(DashijieMinimap.TextureSize * 0.5f, DashijieMinimap.TextureSize * 0.5f)) * scale;

        Transform cameraTransform = player.CameraTransform;
        float yaw = cameraTransform != null ? cameraTransform.eulerAngles.y : player.transform.eulerAngles.y;
        playerMark.localEulerAngles = new Vector3(0f, 0f, -yaw);
    }

    /// <summary>
    /// 任务标记：与玩家标记同一坐标系（内容矩形本地单位 = 底图像素 × fitScale × zoom），
    /// 因此缩放/平移时不需要额外换算，跟随底图一起动。
    /// </summary>
    private void RefreshQuestMarker()
    {
        if (questMarker == null) return;

        if (questDirector == null) questDirector = FindObjectOfType<WorldQuestDirector>();

        Vector3 markerWorld = Vector3.zero;
        bool show = questDirector != null && questDirector.TryGetTrackedMarker(out markerWorld, out _);

        if (questMarker.gameObject.activeSelf != show) questMarker.gameObject.SetActive(show);
        if (!show) return;

        Vector2 mapPixel = DashijieMinimap.WorldToMap(new Vector2(markerWorld.x, markerWorld.z));
        float scale = fitScale * zoom;
        Vector2 center = new(DashijieMinimap.TextureSize * 0.5f, DashijieMinimap.TextureSize * 0.5f);
        questMarker.anchoredPosition = (mapPixel - center) * scale;
    }
}
