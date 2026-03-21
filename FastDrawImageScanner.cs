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
    private enum DrawDispatchResult
    {
        Failed = 0,
        PreviewOnly = 1,
        NetworkSent = 2
    }

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
    private const float MinContentLuminanceRange = 0.02f;
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
    private bool _suppressNextMapClearReset;
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
        FastDrawLog.Debug($"Scanner initialized: mapSize={FormatVector(_mapDrawings.Size)}, drawArea={FormatRect(_drawArea)}, drawColor={_drawColor}");
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

    public bool HandleShortcutKey(InputEventKey keyEvent)
    {
        FastDrawShortcuts shortcuts = FastDrawShortcutConfig.Current;

        if (shortcuts.Matches(FastDrawShortcutAction.CancelSelection, keyEvent))
        {
            FastDrawLog.Debug("匹配快捷键: cancelSelection");
            return CancelAreaSelectionShortcut();
        }

        if (shortcuts.Matches(FastDrawShortcutAction.CaptureSelectionStart, keyEvent))
        {
            FastDrawLog.Debug("匹配快捷键: captureSelectionStart");
            CaptureSelectionStart();
            return true;
        }

        if (shortcuts.Matches(FastDrawShortcutAction.CaptureSelectionEnd, keyEvent))
        {
            FastDrawLog.Debug("匹配快捷键: captureSelectionEnd");
            CaptureSelectionEnd();
            return true;
        }

        if (shortcuts.Matches(FastDrawShortcutAction.ImportImage, keyEvent))
        {
            FastDrawLog.Debug("匹配快捷键: importImage");
            if (_overlay.IsSelectionMode)
                NotifySelectionModeBlocked();
            else
                OpenImportDialog();

            return true;
        }

        if (shortcuts.Matches(FastDrawShortcutAction.PasteImagePath, keyEvent))
        {
            FastDrawLog.Debug("匹配快捷键: pasteImagePath");
            if (_overlay.IsSelectionMode)
                NotifySelectionModeBlocked();
            else
                PasteFromClipboard();

            return true;
        }

        if (shortcuts.Matches(FastDrawShortcutAction.ClearCurrentImage, keyEvent))
        {
            FastDrawLog.Debug("匹配快捷键: clearCurrentImage");
            if (_overlay.IsSelectionMode)
                NotifySelectionModeBlocked();
            else
                ClearCurrentImage();

            return true;
        }

        if (shortcuts.Matches(FastDrawShortcutAction.DrawCurrentImage, keyEvent))
        {
            FastDrawLog.Debug("匹配快捷键: drawCurrentImage");
            if (_overlay.IsSelectionMode)
                NotifySelectionModeBlocked();
            else
                DrawCurrentImage();

            return true;
        }

        return false;
    }

    public bool ProcessShortcutInput(InputEventKey keyEvent)
    {
        if (ShouldIgnoreShortcutInput(out string context))
        {
            FastDrawLog.Debug($"忽略按键输入: {DescribeKeyEvent(keyEvent)}, context={context}");
            return false;
        }

        FastDrawLog.Debug($"收到按键输入: {DescribeKeyEvent(keyEvent)}, context={context}");
        return HandleShortcutKey(keyEvent);
    }

    private bool ShouldIgnoreShortcutInput(out string context)
    {
        Control? focusOwner = GetViewport()?.GuiGetFocusOwner();
        bool fileDialogVisible = _fileDialog != null && _fileDialog.Visible;
        bool textInputFocused = focusOwner is LineEdit or TextEdit;
        context = $"selectionMode={_overlay.IsSelectionMode}, fileDialogVisible={fileDialogVisible}, focusOwner={(focusOwner == null ? "none" : $"{focusOwner.GetType().Name}:{focusOwner.Name}")}";
        return fileDialogVisible || textInputFocused;
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
        FastDrawLog.Debug($"从剪贴板读取路径: {text}");
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
        if (_suppressNextMapClearReset)
        {
            _suppressNextMapClearReset = false;
            FastDrawLog.Debug("忽略一次由重绘流程触发的地图清空回调");
            return;
        }

        if (_binaryImage == null && !_previewVisible)
            return;

        FastDrawLog.Debug("处理地图清空回调，重置当前预览状态");
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
        FastDrawLog.Debug($"开始绘制当前图像: drawArea={FormatRect(_drawArea)}, binarySize={GetImageSizeText(_binaryImage)}, drawMode={_drawMode}");

        DrawDispatchResult result = SendImageToNetwork();
        if (result == DrawDispatchResult.Failed)
            return;

        string source = _currentImagePath ?? "剪贴板路径";
        if (result == DrawDispatchResult.PreviewOnly)
        {
            SetStatus($"已更新预览{GetSelectedRegionText()}，当前模式不会发送地图绘制: {source}");
            return;
        }

        SetStatus($"已绘制{GetSelectedRegionText()}: {source}");
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

            _drawColor.A = 1f;
            FastDrawLog.Debug($"解析绘制颜色成功: playerId={_localPlayerId}, color={_drawColor}");
        }
        catch
        {
            _drawColor = Colors.White;
            FastDrawLog.Debug("解析绘制颜色失败，回退为白色");
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
        FastDrawLog.Debug($"同步覆盖层布局: mapSize={FormatVector(_mapDrawings.Size)}");
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
        _modeOption.FocusMode = Control.FocusModeEnum.None;
        _modeOption.AddItem("黑色部分", (int)DrawRegionMode.Black);
        _modeOption.AddItem("白色部分", (int)DrawRegionMode.White);
        _modeOption.Select((int)_drawMode);
        _modeOption.ItemSelected += OnModeSelected;
        modeRow.AddChild(_modeOption);

        var buttonRow = new HBoxContainer();
        vbox.AddChild(buttonRow);

        var importButton = new Button { Text = "导入图像" };
        importButton.FocusMode = Control.FocusModeEnum.None;
        importButton.Pressed += OpenImportDialog;
        buttonRow.AddChild(importButton);

        var drawButton = new Button { Text = "绘制当前图像" };
        drawButton.FocusMode = Control.FocusModeEnum.None;
        drawButton.Pressed += DrawCurrentImage;
        buttonRow.AddChild(drawButton);

        var clearButton = new Button { Text = "清空" };
        clearButton.FocusMode = Control.FocusModeEnum.None;
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
        {
            FastDrawLog.Debug($"拖入文件: {string.Join(", ", files)}");
            TryLoadImage(files[0]);
        }
    }

    public void CaptureSelectionStart()
    {
        if (!_overlay.IsSelectionMode)
            _overlay.EnterSelectionMode();

        Vector2 point = ClampToDrawings(_mapDrawings.GetLocalMousePosition());
        _areaSelectionStart = point;
        _hasAreaSelectionStart = true;
        _overlay.SetSelectionRect(CreatePointMarker(point));
        FastDrawLog.Debug($"记录选区起点: point=({point.X:0.##}, {point.Y:0.##}), drawArea={_drawArea}");
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
        FastDrawLog.Debug($"记录选区终点: start=({startPoint.X:0.##}, {startPoint.Y:0.##}), end=({point.X:0.##}, {point.Y:0.##})");
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

        FastDrawLog.Debug($"更新绘制区域: area={_drawArea}");
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

            FastDrawLog.Debug($"载入图像: path={path}, size={image.GetWidth()}x{image.GetHeight()}, format={image.GetFormat()}");
            _currentImagePath = path;
            _sourceImage = PrepareSourceImage(image);
            RefreshRenderedImage(showPreview: true);
            bool autoSwitchedMode = AutoSwitchDrawModeIfNeeded();
            if (autoSwitchedMode)
            {
                SetStatus($"已载入: {Path.GetFileName(path)}，检测到当前图像只有{GetSelectedRegionText()}可绘制，已自动切换，按 {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 绘制");
                return true;
            }

            SetStatus($"已载入: {Path.GetFileName(path)}，当前绘制{GetSelectedRegionText()}，按 {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 绘制");
            return true;
        }
        catch (Exception ex)
        {
            FastDrawLog.Error($"载入图像失败 path={path}", ex);
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
            FastDrawLog.Debug("刷新渲染图像时 source 为空，已清空预览");
            return;
        }

        _binaryImage = PrepareBinaryImage(_sourceImage);
        _previewVisible = showPreview;
        UpdatePreviewTexture();
        _overlay.SetPreviewVisible(_previewVisible);
        FastDrawLog.Debug($"刷新渲染图像: source={GetImageSizeText(_sourceImage)}, binary={GetImageSizeText(_binaryImage)}, drawArea={FormatRect(_drawArea)}, previewVisible={_previewVisible}, drawablePixels={CountDrawablePixels(_binaryImage)}");
    }

    private Image PrepareBinaryImage(Image image)
    {
        Vector2I renderSize = CalculateRenderSize(_drawArea.Size);
        FastDrawLog.Debug($"重采样图像: source={image.GetWidth()}x{image.GetHeight()}, render={renderSize.X}x{renderSize.Y}, drawArea={_drawArea}");
        Image work = CreateFittedBinaryCanvas(image, renderSize, out Image contentMask);
        _contentMask = contentMask;
        ApplyBinaryThreshold(work, contentMask);
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

    private void ApplyBinaryThreshold(Image image, Image contentMask)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        (float minLuminance, float maxLuminance, int contentPixels) = MeasureContentLuminanceRange(image, contentMask);
        float luminanceRange = maxLuminance - minLuminance;
        bool useNormalizedRange = contentPixels > 0 && luminanceRange >= MinContentLuminanceRange;

        FastDrawLog.Debug($"二值化前景统计: contentPixels={contentPixels}, minLum={minLuminance:0.###}, maxLum={maxLuminance:0.###}, range={luminanceRange:0.###}, normalized={useNormalizedRange}");

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (contentMask.GetPixel(x, y).R <= 0.5f)
            {
                image.SetPixel(x, y, Colors.Black);
                continue;
            }

            Color px = image.GetPixel(x, y);
            float luminance = px.Luminance * px.A;
            if (useNormalizedRange)
                luminance = Mathf.Clamp((luminance - minLuminance) / luminanceRange, 0f, 1f);

            image.SetPixel(x, y, luminance > LuminanceThreshold ? Colors.White : Colors.Black);
        }
    }

    private static (float minLuminance, float maxLuminance, int contentPixels) MeasureContentLuminanceRange(Image image, Image contentMask)
    {
        float minLuminance = 1f;
        float maxLuminance = 0f;
        int contentPixels = 0;

        for (int y = 0; y < image.GetHeight(); y++)
        for (int x = 0; x < image.GetWidth(); x++)
        {
            if (contentMask.GetPixel(x, y).R <= 0.5f)
                continue;

            float luminance = image.GetPixel(x, y).Luminance * image.GetPixel(x, y).A;
            minLuminance = Mathf.Min(minLuminance, luminance);
            maxLuminance = Mathf.Max(maxLuminance, luminance);
            contentPixels++;
        }

        if (contentPixels == 0)
            return (0f, 0f, 0);

        return (minLuminance, maxLuminance, contentPixels);
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
            FastDrawLog.Debug("更新预览贴图时 binary 为空");
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
        FastDrawLog.Debug($"预览贴图已更新: size={width}x{height}, drawablePixels={CountDrawablePixels(_binaryImage)}");
    }

    private void SendClearToNetwork()
    {
        var ns = RunManager.Instance?.NetService;
        if (ns == null || ns.Type == NetGameType.Singleplayer)
        {
            FastDrawLog.Debug($"跳过清空网络绘制: netService={(ns == null ? "null" : ns.Type.ToString())}");
            return;
        }

        FastDrawLog.Debug("发送 ClearMapDrawingsMessage");
        ns.SendMessage(default(ClearMapDrawingsMessage));
    }

    private DrawDispatchResult SendImageToNetwork()
    {
        if (_binaryImage == null)
            return DrawDispatchResult.Failed;

        var ns = RunManager.Instance?.NetService;
        if (ns == null || ns.Type == NetGameType.Singleplayer)
        {
            FastDrawLog.Debug($"跳过网络绘制: netService={(ns == null ? "null" : ns.Type.ToString())}, previewVisible={_previewVisible}");
            return DrawDispatchResult.PreviewOnly;
        }

        var segments = BuildSegments(_binaryImage);
        if (segments.Count == 0)
        {
            int alternateCount = CountDrawablePixels(_binaryImage, GetAlternateDrawMode());
            if (alternateCount > 0)
                SetStatus($"图像里没有可绘制的{GetDrawableRegionText()}，检测到可绘制的{GetAlternateDrawableRegionText()}，请切换后再试");
            else
                SetStatus($"图像里没有可绘制的{GetDrawableRegionText()}");
            return DrawDispatchResult.Failed;
        }

        FastDrawLog.Debug($"发送网络绘制: segments={segments.Count}, drawArea={_drawArea}, mapSize={_mapDrawings.Size}, firstSegment={(segments.Count > 0 ? $"{segments[0].start} -> {segments[0].end}" : "none")}");
        _suppressNextMapClearReset = true;
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

        return DrawDispatchResult.NetworkSent;
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
        FastDrawLog.Debug("状态更新: " + text);
    }

    private int CountDrawablePixels(Image? image)
        => CountDrawablePixels(image, _drawMode);

    private int CountDrawablePixels(Image? image, DrawRegionMode mode)
    {
        if (image == null)
            return 0;

        int count = 0;
        for (int y = 0; y < image.GetHeight(); y++)
        for (int x = 0; x < image.GetWidth(); x++)
            if (IsTargetPixel(image, x, y, mode))
                count++;

        return count;
    }

    private static int CountMaskPixels(Image image)
    {
        int count = 0;
        for (int y = 0; y < image.GetHeight(); y++)
        for (int x = 0; x < image.GetWidth(); x++)
            if (image.GetPixel(x, y).R > 0.5f)
                count++;

        return count;
    }

    private static string GetImageSizeText(Image? image)
        => image == null ? "null" : $"{image.GetWidth()}x{image.GetHeight()}";

    private bool AutoSwitchDrawModeIfNeeded()
    {
        if (_binaryImage == null)
            return false;

        int currentCount = CountDrawablePixels(_binaryImage, _drawMode);
        if (currentCount > 0)
            return false;

        DrawRegionMode alternateMode = GetAlternateDrawMode();
        int alternateCount = CountDrawablePixels(_binaryImage, alternateMode);
        if (alternateCount <= 0)
            return false;

        _drawMode = alternateMode;
        _modeOption.Select((int)_drawMode);
        UpdatePreviewTexture();
        _previewVisible = true;
        _overlay.SetPreviewVisible(true);
        FastDrawLog.Debug($"自动切换绘制模式: mode={_drawMode}, drawablePixels={alternateCount}");
        return true;
    }

    private static string FormatRect(Rect2 rect)
        => $"({rect.Position.X:0.##}, {rect.Position.Y:0.##}, {rect.Size.X:0.##}, {rect.Size.Y:0.##})";

    private static string FormatVector(Vector2 vector)
        => $"({vector.X:0.##}, {vector.Y:0.##})";

    private static string DescribeKeyEvent(InputEventKey keyEvent)
        => $"keycode={keyEvent.Keycode}, keyLabel={keyEvent.KeyLabel}, physicalKeycode={keyEvent.PhysicalKeycode}, ctrl={keyEvent.CtrlPressed}, shift={keyEvent.ShiftPressed}, alt={keyEvent.AltPressed}, unicode={keyEvent.Unicode}";

    private bool IsTargetPixel(Image frame, int x, int y, DrawRegionMode mode)
    {
        if (!IsContentPixel(x, y))
            return false;

        bool isWhite = frame.GetPixel(x, y).R > 0.5f;
        return mode == DrawRegionMode.White ? isWhite : !isWhite;
    }

    private DrawRegionMode GetAlternateDrawMode()
        => _drawMode == DrawRegionMode.Black ? DrawRegionMode.White : DrawRegionMode.Black;

    private string GetAlternateDrawableRegionText()
        => GetDrawableRegionText(GetAlternateDrawMode());

    private string GetDrawableRegionText(DrawRegionMode mode)
        => mode == DrawRegionMode.Black ? "黑色区域" : "白色区域";
}
