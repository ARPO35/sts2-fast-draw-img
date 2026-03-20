using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Flavor;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace FastDrawImg.Patches;

public partial class FastDrawImageScanner : Node2D
{
    private enum DrawRegionMode
    {
        Black = 0,
        White = 1
    }

    public const string NodeName = "FastDrawImageScanner";
    private const float MinDrawAreaSize = 16f;
    private const float RenderScaleDivisor = 4f;
    private const int MinRenderDimension = 32;
    private const int MaxRenderDimension = 320;
    private const int LineDensity = 2;
    private const float LuminanceThreshold = 0.5f;
    private static readonly Rect2 DefaultDrawArea = new(new Vector2(120f, 80f), new Vector2(640f, 480f));

    private NMapDrawings _mapDrawings = null!;
    private DrawAreaOverlay _overlay = null!;
    private ImageTexture? _previewTex;
    private FileDialog _fileDialog = null!;
    private CanvasLayer _uiLayer = null!;
    private Label _statusLabel = null!;
    private OptionButton _modeOption = null!;

    private Color _drawColor = Colors.White;
    private Image? _sourceImage;
    private Image? _binaryImage;
    private Image? _contentMask;
    private string? _currentImagePath;
    private ulong? _localPlayerId;
    private bool _dropConnected;
    private bool _previewVisible;
    private bool _hasAreaSelectionStart;
    private Vector2 _areaSelectionStart;
    private Rect2 _drawArea = DefaultDrawArea;
    private DrawRegionMode _drawMode = DrawRegionMode.Black;

    public void Initialize(NMapDrawings drawings)
    {
        _mapDrawings = drawings;
        ResolvePlayerDrawColor();
        BuildOverlay();
        BuildUi();
        TryConnectFileDrop();
        Visible = true;
        SetStatus(BuildShortcutSummary());
    }

    public override void _ExitTree()
    {
        if (_dropConnected)
        {
            var window = GetWindow();
            if (window != null)
                window.FilesDropped -= OnFilesDropped;
            _dropConnected = false;
        }

        if (IsInstanceValid(_mapDrawings))
            _mapDrawings.Resized -= OnMapDrawingsResized;

        if (IsInstanceValid(_overlay))
            _overlay.QueueFree();

        base._ExitTree();
    }

    public void OpenImportDialog() => _fileDialog.PopupCenteredRatio(0.7f);

    public bool IsSelectionModeActive => _overlay.IsSelectionMode;

    public void NotifySelectionModeBlocked()
        => SetStatus("请先完成或取消区域选择");

    public bool CancelAreaSelectionShortcut()
    {
        if (!_overlay.IsSelectionMode)
            return false;

        CancelAreaSelection();
        return true;
    }

    public void PasteFromClipboard()
    {
        string text = DisplayServer.ClipboardGet().StripEdges();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("剪贴板中没有可用路径");
            return;
        }

        text = text.Trim('"');
        if (!TryLoadImage(text))
            SetStatus("剪贴板内容不是可读取的图片路径");
    }

    public void ClearCurrentImage()
    {
        ResetPreviewState($"已清空当前图像，当前绘制{GetSelectedRegionText()}", forgetLoadedImage: true);
        SendClearToNetwork();
    }

    public void OnMapCleared()
    {
        if (_binaryImage == null && !_previewVisible)
            return;

        ResetPreviewState($"地图绘制已清空，当前绘制{GetSelectedRegionText()}，按 {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 可重绘当前图像");
    }

    public void OnPlayerMapCleared(ulong playerId)
    {
        if (!_localPlayerId.HasValue || _localPlayerId.Value != playerId)
            return;

        OnMapCleared();
    }

    public void DrawCurrentImage()
    {
        if (_overlay.IsSelectionMode)
        {
            SetStatus("请先完成或取消区域选择");
            return;
        }

        if (_binaryImage == null)
        {
            SetStatus("还没有载入图像");
            return;
        }

        UpdatePreviewTexture();
        _previewVisible = true;
        _overlay.SetPreviewVisible(true);

        if (!SendImageToNetwork())
            return;

        SetStatus($"已绘制{GetSelectedRegionText()}: {(_currentImagePath ?? "剪贴板路径")}");
    }

    private void ResolvePlayerDrawColor()
    {
        try
        {
            var nsField = typeof(NMapDrawings).GetField("_netService", BindingFlags.NonPublic | BindingFlags.Instance);
            var pcField = typeof(NMapDrawings).GetField("_playerCollection", BindingFlags.NonPublic | BindingFlags.Instance);

            object? netService = nsField?.GetValue(_mapDrawings);
            object? playerCollection = pcField?.GetValue(_mapDrawings);
            if (netService == null || playerCollection == null)
                return;

            dynamic ns = netService;
            dynamic pc = playerCollection;
            ulong localPlayerId = (ulong)ns.NetId;
            _localPlayerId = localPlayerId;

            var player = pc.GetPlayer(localPlayerId);
            if (player?.Character != null)
                _drawColor = player.Character.MapDrawingColor;
        }
        catch
        {
            _drawColor = Colors.White;
        }
    }

    private void BuildOverlay()
    {
        _overlay = new DrawAreaOverlay
        {
            Name = "FastDrawDrawAreaOverlay"
        };
        _mapDrawings.AddChild(_overlay);
        SyncOverlayLayout();
        _mapDrawings.Resized += OnMapDrawingsResized;

        _overlay.SetDrawArea(_drawArea);
        _overlay.SelectionCanceled += OnAreaSelectionCanceled;
        _overlay.MoveToFront();
    }

    private void OnMapDrawingsResized() => SyncOverlayLayout();

    private void SyncOverlayLayout()
    {
        if (!IsInstanceValid(_overlay))
            return;

        _overlay.Position = Vector2.Zero;
        _overlay.Size = _mapDrawings.Size;
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.QueueRedraw();
    }

    private void BuildUi()
    {
        _uiLayer = new CanvasLayer();

        var panel = new PanelContainer();
        panel.Position = new Vector2(24, 24);
        panel.Size = new Vector2(400, 160);

        var vbox = new VBoxContainer();
        panel.AddChild(vbox);

        _statusLabel = new Label { Text = "未载入图像", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        vbox.AddChild(_statusLabel);

        var modeRow = new HBoxContainer();
        vbox.AddChild(modeRow);

        var modeLabel = new Label { Text = "绘制区域", CustomMinimumSize = new Vector2(72, 0) };
        modeRow.AddChild(modeLabel);

        _modeOption = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _modeOption.AddItem("黑色部分", (int)DrawRegionMode.Black);
        _modeOption.AddItem("白色部分", (int)DrawRegionMode.White);
        _modeOption.Select((int)_drawMode);
        _modeOption.ItemSelected += OnModeSelected;
        modeRow.AddChild(_modeOption);

        var buttonRow = new HBoxContainer();
        vbox.AddChild(buttonRow);

        var importButton = new Button { Text = "导入图像" };
        importButton.Pressed += OpenImportDialog;
        buttonRow.AddChild(importButton);

        var drawButton = new Button { Text = "绘制当前图像" };
        drawButton.Pressed += DrawCurrentImage;
        buttonRow.AddChild(drawButton);

        var clearButton = new Button { Text = "清空" };
        clearButton.Pressed += ClearCurrentImage;
        buttonRow.AddChild(clearButton);

        _fileDialog = new FileDialog();
        _fileDialog.Access = FileDialog.AccessEnum.Filesystem;
        _fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
        _fileDialog.Title = "选择黑白图像";
        _fileDialog.Filters = new string[] { "*.png ; PNG 图片", "*.jpg, *.jpeg ; JPEG 图片", "*.bmp ; BMP 图片", "*.webp ; WebP 图片" };
        _fileDialog.FileSelected += OnFileSelected;

        _uiLayer.AddChild(panel);
        _uiLayer.AddChild(_fileDialog);
        AddChild(_uiLayer);
    }

    private void TryConnectFileDrop()
    {
        var window = GetWindow();
        if (window == null)
            return;

        window.FilesDropped += OnFilesDropped;
        _dropConnected = true;
    }

    private void OnFilesDropped(string[] files)
    {
        if (files != null && files.Length > 0)
            TryLoadImage(files[0]);
    }

    public void CaptureSelectionStart()
    {
        if (!_overlay.IsSelectionMode)
            _overlay.EnterSelectionMode();

        Vector2 point = ClampToDrawings(_mapDrawings.GetLocalMousePosition());
        _areaSelectionStart = point;
        _hasAreaSelectionStart = true;
        _overlay.SetSelectionRect(CreatePointMarker(point));
        SetStatus($"已记录第一个角点: ({Mathf.RoundToInt(point.X)}, {Mathf.RoundToInt(point.Y)})，移动鼠标后按 {GetShortcutText(FastDrawShortcutAction.CaptureSelectionEnd)} 记录第二点");
    }

    public void CaptureSelectionEnd()
    {
        if (!_overlay.IsSelectionMode || !_hasAreaSelectionStart)
        {
            SetStatus($"请先把鼠标移到第一个角点后按 {GetShortcutText(FastDrawShortcutAction.CaptureSelectionStart)}");
            return;
        }

        Vector2 point = ClampToDrawings(_mapDrawings.GetLocalMousePosition());
        Vector2 startPoint = _areaSelectionStart;
        _hasAreaSelectionStart = false;
        _overlay.CancelSelectionMode();
        ApplySelectedArea(startPoint, point);
    }

    private void CancelAreaSelection()
    {
        _hasAreaSelectionStart = false;
        _overlay.CancelSelectionMode();
        SetStatus("已取消区域选择");
    }

    private void OnAreaSelectionCanceled() => SetStatus("已取消区域选择");

    private void ApplySelectedArea(Vector2 startPoint, Vector2 endPoint)
    {
        Rect2 area = MakeRect(startPoint, endPoint);
        if (area.Size.X < MinDrawAreaSize || area.Size.Y < MinDrawAreaSize)
        {
            SetStatus($"选区太小: ({Mathf.RoundToInt(startPoint.X)}, {Mathf.RoundToInt(startPoint.Y)}) -> ({Mathf.RoundToInt(endPoint.X)}, {Mathf.RoundToInt(endPoint.Y)})，至少需要 16x16");
            _overlay.SetDrawArea(_drawArea);
            return;
        }

        _drawArea = area;
        _overlay.SetDrawArea(_drawArea);
        if (_sourceImage != null)
            RefreshRenderedImage(_previewVisible);

        SetStatus($"已更新绘制区域: ({Mathf.RoundToInt(startPoint.X)}, {Mathf.RoundToInt(startPoint.Y)}) -> ({Mathf.RoundToInt(endPoint.X)}, {Mathf.RoundToInt(endPoint.Y)})，尺寸 {Mathf.RoundToInt(area.Size.X)}x{Mathf.RoundToInt(area.Size.Y)}");
    }

    private void OnFileSelected(string path) => TryLoadImage(path);

    private void OnModeSelected(long index)
    {
        if (index < 0)
            return;

        SetDrawMode((DrawRegionMode)_modeOption.GetItemId((int)index));
    }

    private bool TryLoadImage(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            var image = Image.LoadFromFile(path);
            if (image == null || image.IsEmpty())
                return false;

            _currentImagePath = path;
            _sourceImage = PrepareSourceImage(image);
            RefreshRenderedImage(showPreview: true);
            SetStatus($"已载入: {Path.GetFileName(path)}，当前绘制{GetSelectedRegionText()}，按 {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 绘制");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("载入失败: " + ex.Message);
            return false;
        }
    }

    private Image PrepareSourceImage(Image image)
    {
        Image source = (Image)image.Duplicate();
        if (source.GetFormat() != Image.Format.Rgba8)
            source.Convert(Image.Format.Rgba8);
        return source;
    }

    private void RefreshRenderedImage(bool showPreview)
    {
        if (_sourceImage == null)
        {
            _binaryImage = null;
            _contentMask = null;
            _previewVisible = false;
            _overlay.SetPreviewTexture(null);
            _overlay.SetPreviewVisible(false);
            return;
        }

        _binaryImage = PrepareBinaryImage(_sourceImage);
        _previewVisible = showPreview;
        UpdatePreviewTexture();
        _overlay.SetPreviewVisible(_previewVisible);
    }

    private Image PrepareBinaryImage(Image image)
    {
        Vector2I renderSize = CalculateRenderSize(_drawArea.Size);
        Image work = CreateFittedBinaryCanvas(image, renderSize, out Image contentMask);
        _contentMask = contentMask;
        ApplyBinaryThreshold(work);
        return ApplyMorphologicalClose(work);
    }

    private Vector2I CalculateRenderSize(Vector2 areaSize)
    {
        int width = Math.Max(MinRenderDimension, Mathf.RoundToInt(areaSize.X / RenderScaleDivisor));
        int height = Math.Max(MinRenderDimension, Mathf.RoundToInt(areaSize.Y / RenderScaleDivisor));

        int longestEdge = Math.Max(width, height);
        if (longestEdge > MaxRenderDimension)
        {
            float scale = MaxRenderDimension / (float)longestEdge;
            width = Math.Max(MinRenderDimension, Mathf.RoundToInt(width * scale));
            height = Math.Max(MinRenderDimension, Mathf.RoundToInt(height * scale));
        }

        return new Vector2I(width, height);
    }

    private Image CreateFittedBinaryCanvas(Image source, Vector2I renderSize, out Image contentMask)
    {
        Image canvas = Image.CreateEmpty(renderSize.X, renderSize.Y, false, Image.Format.Rgba8);
        canvas.Fill(Colors.Black);
        contentMask = Image.CreateEmpty(renderSize.X, renderSize.Y, false, Image.Format.Rgba8);
        contentMask.Fill(Colors.Black);

        int sourceWidth = source.GetWidth();
        int sourceHeight = source.GetHeight();
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return canvas;

        float scale = Math.Min(renderSize.X / (float)sourceWidth, renderSize.Y / (float)sourceHeight);
        int fittedWidth = Math.Max(1, Mathf.RoundToInt(sourceWidth * scale));
        int fittedHeight = Math.Max(1, Mathf.RoundToInt(sourceHeight * scale));

        Image resized = (Image)source.Duplicate();
        resized.Resize(fittedWidth, fittedHeight, Image.Interpolation.Lanczos);

        Vector2I offset = new((renderSize.X - fittedWidth) / 2, (renderSize.Y - fittedHeight) / 2);
        Rect2I sourceRect = new(0, 0, fittedWidth, fittedHeight);
        canvas.BlitRect(resized, sourceRect, offset);

        for (int y = 0; y < fittedHeight; y++)
        for (int x = 0; x < fittedWidth; x++)
            if (resized.GetPixel(x, y).A > 0.01f)
                contentMask.SetPixel(offset.X + x, offset.Y + y, Colors.White);

        return canvas;
    }

    private void ApplyBinaryThreshold(Image image)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Color px = image.GetPixel(x, y);
            float luminance = px.Luminance * px.A;
            image.SetPixel(x, y, luminance > LuminanceThreshold ? Colors.White : Colors.Black);
        }
    }

    // A single close pass repairs tiny gaps without turning the whole image into a blob.
    private Image ApplyMorphologicalClose(Image image)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        Image dilated = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        Image closed = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            dilated.SetPixel(x, y, HasWhiteNeighbor(image, x, y) ? Colors.White : Colors.Black);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            closed.SetPixel(x, y, AllNeighborsWhite(dilated, x, y) ? Colors.White : Colors.Black);

        return closed;
    }

    private bool HasWhiteNeighbor(Image image, int x, int y)
    {
        for (int neighborY = Math.Max(0, y - 1); neighborY <= Math.Min(image.GetHeight() - 1, y + 1); neighborY++)
        for (int neighborX = Math.Max(0, x - 1); neighborX <= Math.Min(image.GetWidth() - 1, x + 1); neighborX++)
            if (image.GetPixel(neighborX, neighborY).R > 0.5f)
                return true;

        return false;
    }

    private bool AllNeighborsWhite(Image image, int x, int y)
    {
        for (int neighborY = Math.Max(0, y - 1); neighborY <= Math.Min(image.GetHeight() - 1, y + 1); neighborY++)
        for (int neighborX = Math.Max(0, x - 1); neighborX <= Math.Min(image.GetWidth() - 1, x + 1); neighborX++)
            if (image.GetPixel(neighborX, neighborY).R <= 0.5f)
                return false;

        return true;
    }

    private void UpdatePreviewTexture()
    {
        if (_binaryImage == null)
        {
            _overlay.SetPreviewTexture(null);
            return;
        }

        int width = _binaryImage.GetWidth();
        int height = _binaryImage.GetHeight();
        Image preview = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        Color transparent = new(0, 0, 0, 0);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            preview.SetPixel(x, y, IsTargetPixel(_binaryImage, x, y) ? _drawColor : transparent);

        if (_previewTex == null || _previewTex.GetWidth() != width || _previewTex.GetHeight() != height)
            _previewTex = ImageTexture.CreateFromImage(preview);
        else
            _previewTex.Update(preview);

        _overlay.SetPreviewTexture(_previewTex);
    }

    private void SendClearToNetwork()
    {
        var ns = RunManager.Instance?.NetService;
        if (ns == null || ns.Type == NetGameType.Singleplayer)
            return;
        ns.SendMessage(default(ClearMapDrawingsMessage));
    }

    private bool SendImageToNetwork()
    {
        if (_binaryImage == null)
            return false;

        var ns = RunManager.Instance?.NetService;
        if (ns == null || ns.Type == NetGameType.Singleplayer)
            return true;

        var segments = BuildSegments(_binaryImage);
        if (segments.Count == 0)
        {
            SetStatus($"图像里没有可绘制的{GetDrawableRegionText()}");
            return false;
        }

        ns.SendMessage(default(ClearMapDrawingsMessage));

        var msg = new MapDrawingMessage { drawingMode = DrawingMode.Drawing };
        foreach (var (start, end) in segments)
        {
            SendEvent(ns, ref msg, new NetMapDrawingEvent { type = MapDrawingEventType.BeginLine, position = ToNetPos(start), overrideDrawingMode = DrawingMode.Drawing });
            SendEvent(ns, ref msg, new NetMapDrawingEvent { type = MapDrawingEventType.ContinueLine, position = ToNetPos(end), overrideDrawingMode = DrawingMode.Drawing });
            SendEvent(ns, ref msg, new NetMapDrawingEvent { type = MapDrawingEventType.EndLine });
        }

        if (msg.Events.Count > 0)
            ns.SendMessage(msg);

        return true;
    }

    private List<(Vector2 start, Vector2 end)> BuildSegments(Image frame)
    {
        var segments = new List<(Vector2 start, Vector2 end)>();
        int width = frame.GetWidth();
        int height = frame.GetHeight();
        if (width <= 0 || height <= 0)
            return segments;

        float cellWidth = _drawArea.Size.X / width;
        float cellHeight = _drawArea.Size.Y / height;
        float subStep = cellHeight / LineDensity;

        for (int y = 0; y < height; y++)
        {
            int? runStart = null;
            for (int x = 0; x < width; x++)
            {
                bool on = IsTargetPixel(frame, x, y);
                if (on)
                    runStart ??= x;
                else if (runStart.HasValue)
                {
                    for (int sub = 0; sub < LineDensity; sub++)
                        AddSegment(segments, runStart.Value, x, y, cellWidth, cellHeight, sub * subStep);
                    runStart = null;
                }
            }

            if (runStart.HasValue)
                for (int sub = 0; sub < LineDensity; sub++)
                    AddSegment(segments, runStart.Value, width, y, cellWidth, cellHeight, sub * subStep);
        }

        return segments;
    }

    private void SendEvent(INetGameService ns, ref MapDrawingMessage msg, NetMapDrawingEvent ev)
    {
        if (msg.TryAddEvent(ev))
            return;

        ns.SendMessage(msg);
        msg = new MapDrawingMessage { drawingMode = DrawingMode.Drawing };
        msg.TryAddEvent(ev);
    }

    private Vector2 ToNetPos(Vector2 pos)
    {
        var size = _mapDrawings.Size;
        pos.X -= size.X * 0.5f;
        pos /= new Vector2(960f, size.Y);
        return pos;
    }

    private void AddSegment(List<(Vector2, Vector2)> list, int x1, int x2, int row, float cellWidth, float cellHeight, float rowOffset)
    {
        float y = _drawArea.Position.Y + row * cellHeight + rowOffset;
        Vector2 start = new(_drawArea.Position.X + x1 * cellWidth, y);
        Vector2 end = new(_drawArea.Position.X + x2 * cellWidth, y);
        list.Add((start, end));
    }

    private Vector2 ClampToDrawings(Vector2 point)
        => new(Mathf.Clamp(point.X, 0f, _mapDrawings.Size.X), Mathf.Clamp(point.Y, 0f, _mapDrawings.Size.Y));

    private static Rect2 CreatePointMarker(Vector2 point)
        => new(point - new Vector2(3f, 3f), new Vector2(6f, 6f));

    private static Rect2 MakeRect(Vector2 start, Vector2 end)
    {
        float minX = Mathf.Min(start.X, end.X);
        float minY = Mathf.Min(start.Y, end.Y);
        float maxX = Mathf.Max(start.X, end.X);
        float maxY = Mathf.Max(start.Y, end.Y);
        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    private void ResetPreviewState(string status, bool forgetLoadedImage = false)
    {
        if (forgetLoadedImage)
        {
            _sourceImage = null;
            _binaryImage = null;
            _contentMask = null;
            _currentImagePath = null;
            _previewTex = null;
            _overlay.SetPreviewTexture(null);
        }

        _previewVisible = false;
        _hasAreaSelectionStart = false;
        _overlay.SetPreviewVisible(false);
        SetStatus(status);
    }

    private void SetDrawMode(DrawRegionMode mode)
    {
        if (_drawMode == mode)
            return;

        _drawMode = mode;

        if (_binaryImage != null)
        {
            UpdatePreviewTexture();
            _previewVisible = true;
            _overlay.SetPreviewVisible(true);
            SetStatus($"已切换为绘制{GetSelectedRegionText()}，预览已更新，按 {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 绘制");
            return;
        }

        SetStatus($"当前绘制模式: {GetSelectedRegionText()}，默认黑色，可在面板切换");
    }

    private bool IsTargetPixel(Image frame, int x, int y)
    {
        if (!IsContentPixel(x, y))
            return false;

        bool isWhite = frame.GetPixel(x, y).R > 0.5f;
        return _drawMode == DrawRegionMode.White ? isWhite : !isWhite;
    }

    private bool IsContentPixel(int x, int y)
        => _contentMask != null && _contentMask.GetPixel(x, y).R > 0.5f;

    private string GetSelectedRegionText()
        => _drawMode == DrawRegionMode.Black ? "黑色部分" : "白色部分";

    private string GetDrawableRegionText()
        => _drawMode == DrawRegionMode.Black ? "黑色区域" : "白色区域";

    private string GetShortcutText(FastDrawShortcutAction action)
        => FastDrawShortcutConfig.Current.Describe(action);

    private string BuildShortcutSummary()
        => $"{GetShortcutText(FastDrawShortcutAction.ImportImage)} 导入图片 / {GetShortcutText(FastDrawShortcutAction.PasteImagePath)} 粘贴路径 / {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 重绘 / {GetShortcutText(FastDrawShortcutAction.ClearCurrentImage)} 清空 / 用 {GetShortcutText(FastDrawShortcutAction.CaptureSelectionStart)} 和 {GetShortcutText(FastDrawShortcutAction.CaptureSelectionEnd)} 记录选区两点";

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
        GD.Print("[FastDrawImg] " + text);
    }
}
