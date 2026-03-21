using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Flavor;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace FastDrawImg.Patches;

public partial class FastDrawImageScanner : Node2D
{
    private readonly record struct PixelInterval(int StartX, int EndXExclusive);
    private readonly record struct FinalRowSegment(int Y, int StartX, int EndXExclusive);

    public enum DrawDispatchResult
    {
        Failed = 0,
        PreviewOnly = 1,
        NetworkSent = 2
    }

    public enum DrawRegionMode
    {
        Black = 0,
        White = 1
    }

    public const string NodeName = "FastDrawImageScanner";
    public const string UiLayerName = "FastDrawUiLayer";
    public const string PreviewSpriteName = "FastDrawPreviewSprite";
    private const float MinDrawAreaSize = 16f;
    private const float LuminanceThreshold = 0.5f;
    private const float MinContentLuminanceRange = 0.02f;
    private static readonly Rect2 DefaultDrawArea = new(new Vector2(120f, 80f), new Vector2(640f, 480f));

    private NMapDrawings _mapDrawings = null!;
    private DrawAreaOverlay _overlay = null!;
    private Sprite2D _previewSprite = null!;
    private ImageTexture? _previewTex;
    private FileDialog _fileDialog = null!;
    private CanvasLayer _uiLayer = null!;
    private Label _statusLabel = null!;
    private OptionButton _modeOption = null!;
    private Window? _dropWindow;

    private Color _drawColor = Colors.White;
    private Image? _sourceImage;
    private Image? _binaryImage;
    private Image? _contentMask;
    private string? _currentImagePath;
    private readonly List<FinalRowSegment> _finalSegments = new();
    private int _blackPixelCount;
    private int _whitePixelCount;
    private ulong? _localPlayerId;
    private bool _dropConnected;
    private bool _previewVisible;
    private bool _hasAreaSelectionStart;
    private bool _suppressNextMapClearReset;
    private Vector2 _areaSelectionStart;
    private Rect2 _drawArea = DefaultDrawArea;
    private Rect2 _displayRect = DefaultDrawArea;
    private Rect2I _displayPixelBounds = new(120, 80, 640, 480);
    private DrawRegionMode _drawMode = DrawRegionMode.Black;

    public bool IsSelectionModeActive => _overlay.IsSelectionMode;

    public void Initialize(NMapDrawings drawings)
    {
        _mapDrawings = drawings;
        ResolvePlayerDrawColor();
        BuildPreview();
        BuildOverlay();
        BuildUi();
        TryConnectFileDrop();
        Visible = true;
        FastDrawLog.Debug($"Scanner initialized: mapSize={FormatVector(_mapDrawings.Size)}, drawArea={FormatRect(_drawArea)}, drawColor={_drawColor}, parent={GetParent()?.Name}");
        SetStatus(BuildShortcutSummary());
    }

    public override void _ExitTree()
    {
        if (_dropConnected && _dropWindow != null)
        {
            _dropWindow.FilesDropped -= OnFilesDropped;
            _dropConnected = false;
            _dropWindow = null;
        }

        if (IsInstanceValid(_mapDrawings))
            _mapDrawings.Resized -= OnMapDrawingsResized;

        if (IsInstanceValid(_overlay))
            _overlay.QueueFree();

        if (IsInstanceValid(_previewSprite))
            _previewSprite.QueueFree();

        if (IsInstanceValid(_uiLayer))
            _uiLayer.QueueFree();

        base._ExitTree();
    }

    public void OpenImportDialog() => _fileDialog.PopupCenteredRatio(0.7f);

    public void NotifySelectionModeBlocked()
        => SetStatus("请先完成或取消区域选择");

    public bool CancelAreaSelectionShortcut()
    {
        if (!_overlay.IsSelectionMode)
            return false;

        CancelAreaSelection();
        return true;
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

    private bool ShouldIgnoreShortcutInput(out string context)
    {
        Viewport? inputViewport = _uiLayer?.GetViewport() ?? NGame.Instance?.GetViewport() ?? GetTree()?.Root;
        Control? focusOwner = inputViewport?.GuiGetFocusOwner();
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

        _previewVisible = true;
        UpdatePreviewTexture();
        FastDrawLog.Debug($"开始绘制当前图像: drawArea={FormatRect(_drawArea)}, displayRect={FormatRect(_displayRect)}, sourceSize={GetImageSizeText(_sourceImage)}, binarySize={GetImageSizeText(_binaryImage)}, drawMode={_drawMode}, finalSegments={_finalSegments.Count}");

        DrawDispatchResult result = SendImageToNetwork();
        if (result == DrawDispatchResult.Failed)
            return;

        string source = _currentImagePath ?? "剪贴板路径";
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
        catch (Exception ex)
        {
            _drawColor = Colors.White;
            FastDrawLog.Debug($"解析绘制颜色失败，回退为白色: {ex.Message}");
        }
    }

    private void BuildPreview()
    {
        _previewSprite = new Sprite2D
        {
            Name = PreviewSpriteName,
            Centered = false,
            Visible = false,
            TextureFilter = TextureFilterEnum.Nearest
        };
        _mapDrawings.AddChild(_previewSprite);
    }

    private void BuildOverlay()
    {
        _overlay = new DrawAreaOverlay
        {
            Name = DrawAreaOverlay.NodeName
        };
        _mapDrawings.AddChild(_overlay);
        _overlay.SetDrawArea(_drawArea);
        _overlay.SelectionCanceled += OnAreaSelectionCanceled;
        _mapDrawings.Resized += OnMapDrawingsResized;
        SyncOverlayLayout();
        _overlay.MoveToFront();
    }

    private void BuildUi()
    {
        _uiLayer = new CanvasLayer
        {
            Name = UiLayerName
        };

        var panel = new PanelContainer
        {
            Position = new Vector2(24f, 24f),
            Size = new Vector2(400f, 160f)
        };

        var vbox = new VBoxContainer();
        panel.AddChild(vbox);

        _statusLabel = new Label
        {
            Text = "未载入图像",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        vbox.AddChild(_statusLabel);

        var modeRow = new HBoxContainer();
        vbox.AddChild(modeRow);

        var modeLabel = new Label
        {
            Text = "绘制区域",
            CustomMinimumSize = new Vector2(72f, 0f)
        };
        modeRow.AddChild(modeLabel);

        _modeOption = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None
        };
        _modeOption.AddItem("黑色部分", (int)DrawRegionMode.Black);
        _modeOption.AddItem("白色部分", (int)DrawRegionMode.White);
        _modeOption.Select((int)DrawRegionMode.Black);
        _modeOption.ItemSelected += OnModeSelected;
        modeRow.AddChild(_modeOption);

        var buttonRow = new HBoxContainer();
        vbox.AddChild(buttonRow);

        var importButton = new Button
        {
            Text = "导入图像",
            FocusMode = Control.FocusModeEnum.None
        };
        importButton.Pressed += () =>
        {
            if (_overlay.IsSelectionMode)
                NotifySelectionModeBlocked();
            else
                OpenImportDialog();
        };
        buttonRow.AddChild(importButton);

        var drawButton = new Button
        {
            Text = "绘制当前图像",
            FocusMode = Control.FocusModeEnum.None
        };
        drawButton.Pressed += DrawCurrentImage;
        buttonRow.AddChild(drawButton);

        var clearButton = new Button
        {
            Text = "清空",
            FocusMode = Control.FocusModeEnum.None
        };
        clearButton.Pressed += () =>
        {
            if (_overlay.IsSelectionMode)
                NotifySelectionModeBlocked();
            else
                ClearCurrentImage();
        };
        buttonRow.AddChild(clearButton);

        _fileDialog = new FileDialog
        {
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Title = "选择黑白图像",
            Filters = new[] { "*.png ; PNG 图片", "*.jpg, *.jpeg ; JPEG 图片", "*.bmp ; BMP 图片", "*.webp ; WebP 图片" }
        };
        _fileDialog.FileSelected += OnFileSelected;

        _uiLayer.AddChild(panel);
        _uiLayer.AddChild(_fileDialog);

        if (NGame.Instance != null)
            NGame.Instance.AddChild(_uiLayer);
        else
            AddChild(_uiLayer);
    }

    private void TryConnectFileDrop()
    {
        _dropWindow = NGame.Instance?.GetWindow() ?? GetWindow();
        if (_dropWindow == null)
            return;

        _dropWindow.FilesDropped += OnFilesDropped;
        _dropConnected = true;
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

    private void OnFilesDropped(string[] files)
    {
        if (files == null || files.Length == 0)
            return;

        FastDrawLog.Debug($"拖入文件: {string.Join(", ", files)}");
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
        FastDrawLog.Debug($"记录选区起点: point={FormatVector(point)}, drawArea={FormatRect(_drawArea)}");
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
        FastDrawLog.Debug($"记录选区终点: start={FormatVector(startPoint)}, end={FormatVector(point)}");
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
            RebuildDisplayOutput();

        FastDrawLog.Debug($"更新绘制区域: area={FormatRect(_drawArea)}, displayRect={FormatRect(_displayRect)}, pixelBounds={FormatRectI(_displayPixelBounds)}");
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

            Image image = Image.LoadFromFile(path);
            if (image == null || image.IsEmpty())
                return false;

            FastDrawLog.Debug($"载入图像: path={path}, size={image.GetWidth()}x{image.GetHeight()}, format={image.GetFormat()}");
            _currentImagePath = path;
            _sourceImage = PrepareSourceImage(image);
            _drawMode = DrawRegionMode.Black;
            _modeOption.Select((int)_drawMode);
            RefreshRenderedImage(showPreview: true);
            SetStatus(BuildLoadedStatus(Path.GetFileName(path)));
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
            _finalSegments.Clear();
            _blackPixelCount = 0;
            _whitePixelCount = 0;
            _previewVisible = false;
            UpdateDisplayLayout();
            UpdatePreviewTexture();
            FastDrawLog.Debug("刷新渲染图像时 source 为空，已清空预览");
            return;
        }

        _binaryImage = PrepareBinaryImage(_sourceImage);
        _previewVisible = showPreview;
        RebuildDisplayOutput();
        FastDrawLog.Debug($"刷新渲染图像: source={GetImageSizeText(_sourceImage)}, binary={GetImageSizeText(_binaryImage)}, mask={GetImageSizeText(_contentMask)}, drawArea={FormatRect(_drawArea)}, displayRect={FormatRect(_displayRect)}, pixelBounds={FormatRectI(_displayPixelBounds)}, previewVisible={_previewVisible}, blackPixels={_blackPixelCount}, whitePixels={_whitePixelCount}, finalSegments={_finalSegments.Count}");
    }

    private void RebuildDisplayOutput()
    {
        UpdateDisplayLayout();
        _finalSegments.Clear();

        if (_binaryImage == null)
        {
            UpdatePreviewTexture();
            return;
        }

        List<FinalRowSegment> resolvedSegments = BuildFinalRowSegments(_binaryImage, _drawMode, out int rawSegments, out int dedupedSegments, out int mergedSegments);
        _finalSegments.AddRange(resolvedSegments);
        UpdatePreviewTexture();
        FastDrawLog.Debug($"重建最终输出: displayRect={FormatRect(_displayRect)}, pixelBounds={FormatRectI(_displayPixelBounds)}, rawSegments={rawSegments}, dedupedSegments={dedupedSegments}, mergedSegments={mergedSegments}, drawablePixels={CountDrawablePixels(_drawMode)}");
    }

    private Image PrepareBinaryImage(Image image)
    {
        Image work = (Image)image.Duplicate();
        if (work.GetFormat() != Image.Format.Rgba8)
            work.Convert(Image.Format.Rgba8);

        Image contentMask = CreateContentMask(work);
        _contentMask = contentMask;
        ApplyBinaryThreshold(work, contentMask, out _blackPixelCount, out _whitePixelCount);
        FastDrawLog.Debug($"原图阈值完成: source={image.GetWidth()}x{image.GetHeight()}, maskPixels={CountMaskPixels(contentMask)}, blackPixels={_blackPixelCount}, whitePixels={_whitePixelCount}");
        return work;
    }

    private Image CreateContentMask(Image image)
    {
        Image contentMask = Image.CreateEmpty(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8);
        contentMask.Fill(Colors.Black);

        for (int y = 0; y < image.GetHeight(); y++)
        for (int x = 0; x < image.GetWidth(); x++)
            if (image.GetPixel(x, y).A > 0.01f)
                contentMask.SetPixel(x, y, Colors.White);

        return contentMask;
    }

    private void UpdateDisplayLayout()
    {
        if (_sourceImage == null)
        {
            _displayRect = _drawArea;
            _displayPixelBounds = new Rect2I(
                Mathf.FloorToInt(_drawArea.Position.X),
                Mathf.FloorToInt(_drawArea.Position.Y),
                Mathf.Max(1, Mathf.CeilToInt(_drawArea.Size.X)),
                Mathf.Max(1, Mathf.CeilToInt(_drawArea.Size.Y)));
            return;
        }

        Vector2 imageSize = new(_sourceImage.GetWidth(), _sourceImage.GetHeight());
        _displayRect = CalculateDisplayRect(_drawArea, imageSize);
        _displayPixelBounds = CalculateDisplayPixelBounds(_displayRect);
    }

    private static Rect2 CalculateDisplayRect(Rect2 area, Vector2 imageSize)
    {
        if (imageSize.X <= 0f || imageSize.Y <= 0f || area.Size.X <= 0f || area.Size.Y <= 0f)
            return area;

        float scale = Mathf.Min(area.Size.X / imageSize.X, area.Size.Y / imageSize.Y);
        Vector2 displaySize = imageSize * scale;
        Vector2 displayPosition = area.Position + (area.Size - displaySize) * 0.5f;
        return new Rect2(displayPosition, displaySize);
    }

    private Rect2I CalculateDisplayPixelBounds(Rect2 displayRect)
    {
        int minX = Mathf.Clamp(Mathf.FloorToInt(displayRect.Position.X), 0, Mathf.CeilToInt(_mapDrawings.Size.X));
        int minY = Mathf.Clamp(Mathf.FloorToInt(displayRect.Position.Y), 0, Mathf.CeilToInt(_mapDrawings.Size.Y));
        int maxX = Mathf.Clamp(Mathf.CeilToInt(displayRect.End.X), minX + 1, Mathf.CeilToInt(_mapDrawings.Size.X));
        int maxY = Mathf.Clamp(Mathf.CeilToInt(displayRect.End.Y), minY + 1, Mathf.CeilToInt(_mapDrawings.Size.Y));
        return new Rect2I(minX, minY, maxX - minX, maxY - minY);
    }

    private void ApplyBinaryThreshold(Image image, Image contentMask, out int blackPixels, out int whitePixels)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        (float minLuminance, float maxLuminance, int contentPixels) = MeasureContentLuminanceRange(image, contentMask);
        float luminanceRange = maxLuminance - minLuminance;
        bool useNormalizedRange = contentPixels > 0 && luminanceRange >= MinContentLuminanceRange;
        blackPixels = 0;
        whitePixels = 0;

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

            bool isWhite = luminance > LuminanceThreshold;
            image.SetPixel(x, y, isWhite ? Colors.White : Colors.Black);
            if (isWhite)
                whitePixels++;
            else
                blackPixels++;
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

            Color px = image.GetPixel(x, y);
            float luminance = px.Luminance * px.A;
            minLuminance = Mathf.Min(minLuminance, luminance);
            maxLuminance = Mathf.Max(maxLuminance, luminance);
            contentPixels++;
        }

        if (contentPixels == 0)
            return (0f, 0f, 0);

        return (minLuminance, maxLuminance, contentPixels);
    }

    private void UpdatePreviewTexture()
    {
        if (_binaryImage == null || _displayPixelBounds.Size.X <= 0 || _displayPixelBounds.Size.Y <= 0)
        {
            _previewTex = null;
            _previewSprite.Texture = null;
            _previewSprite.Visible = false;
            FastDrawLog.Debug("更新预览贴图时没有可用输出");
            return;
        }

        int width = _displayPixelBounds.Size.X;
        int height = _displayPixelBounds.Size.Y;
        Image preview = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        Color transparent = new(0f, 0f, 0f, 0f);
        preview.Fill(transparent);

        foreach (FinalRowSegment segment in _finalSegments)
        {
            int row = segment.Y - _displayPixelBounds.Position.Y;
            if (row < 0 || row >= height)
                continue;

            int startX = Mathf.Clamp(segment.StartX - _displayPixelBounds.Position.X, 0, width);
            int endX = Mathf.Clamp(segment.EndXExclusive - _displayPixelBounds.Position.X, 0, width);
            for (int x = startX; x < endX; x++)
                preview.SetPixel(x, row, _drawColor);
        }

        if (_previewTex == null || _previewTex.GetWidth() != width || _previewTex.GetHeight() != height)
            _previewTex = ImageTexture.CreateFromImage(preview);
        else
            _previewTex.Update(preview);

        _previewSprite.Texture = _previewTex;
        SyncPreviewSpriteTransform();
        _previewSprite.Visible = _previewVisible;
        FastDrawLog.Debug($"预览贴图已更新: size={width}x{height}, finalSegments={_finalSegments.Count}, previewVisible={_previewVisible}");
    }

    private void SyncPreviewSpriteTransform()
    {
        if (!IsInstanceValid(_previewSprite))
            return;

        _previewSprite.Position = _displayPixelBounds.Position;
        _previewSprite.Scale = Vector2.One;
    }

    private void SendClearToNetwork()
    {
        INetGameService? ns = RunManager.Instance?.NetService;
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

        INetGameService? ns = RunManager.Instance?.NetService;
        if (ns == null || ns.Type == NetGameType.Singleplayer)
        {
            FastDrawLog.Debug($"跳过网络绘制: netService={(ns == null ? "null" : ns.Type.ToString())}, previewVisible={_previewVisible}");
            return DrawDispatchResult.PreviewOnly;
        }

        if (_finalSegments.Count == 0)
        {
            int alternateCount = CountDrawablePixels(GetAlternateDrawMode());
            if (alternateCount > 0)
                SetStatus($"图像里没有可绘制的{GetDrawableRegionText()}，可切换{GetSelectedRegionText(GetAlternateDrawMode())}后再试");
            else
                SetStatus($"图像里没有可绘制的{GetDrawableRegionText()}");
            return DrawDispatchResult.Failed;
        }

        string firstSegment = _finalSegments.Count > 0
            ? $"({_finalSegments[0].StartX}, {_finalSegments[0].Y}) -> ({_finalSegments[0].EndXExclusive}, {_finalSegments[0].Y})"
            : "none";
        FastDrawLog.Debug($"发送网络绘制: segments={_finalSegments.Count}, drawArea={FormatRect(_drawArea)}, displayRect={FormatRect(_displayRect)}, pixelBounds={FormatRectI(_displayPixelBounds)}, mapSize={FormatVector(_mapDrawings.Size)}, firstSegment={firstSegment}");
        _suppressNextMapClearReset = true;
        ns.SendMessage(default(ClearMapDrawingsMessage));

        var msg = new MapDrawingMessage
        {
            drawingMode = DrawingMode.Drawing
        };

        foreach (FinalRowSegment segment in _finalSegments)
        {
            float drawY = segment.Y + 0.5f;
            Vector2 start = new(segment.StartX, drawY);
            Vector2 end = new(segment.EndXExclusive, drawY);
            SendEvent(ns, ref msg, new NetMapDrawingEvent
            {
                type = MapDrawingEventType.BeginLine,
                position = ToNetPos(start),
                overrideDrawingMode = DrawingMode.Drawing
            });
            SendEvent(ns, ref msg, new NetMapDrawingEvent
            {
                type = MapDrawingEventType.ContinueLine,
                position = ToNetPos(end),
                overrideDrawingMode = DrawingMode.Drawing
            });
            SendEvent(ns, ref msg, new NetMapDrawingEvent
            {
                type = MapDrawingEventType.EndLine
            });
        }

        if (msg.Events.Count > 0)
            ns.SendMessage(msg);

        return DrawDispatchResult.NetworkSent;
    }

    private List<FinalRowSegment> BuildFinalRowSegments(Image frame, DrawRegionMode mode, out int rawSegments, out int dedupedSegments, out int mergedSegments)
    {
        var rowBuckets = new Dictionary<int, List<PixelInterval>>();
        int width = frame.GetWidth();
        int height = frame.GetHeight();
        rawSegments = 0;
        dedupedSegments = 0;
        mergedSegments = 0;
        if (width <= 0 || height <= 0)
            return new List<FinalRowSegment>();

        float scale = _displayRect.Size.X / width;

        for (int y = 0; y < height; y++)
        {
            int? runStart = null;
            for (int x = 0; x < width; x++)
            {
                bool on = IsTargetPixel(frame, x, y, mode);
                if (on)
                {
                    runStart ??= x;
                }
                else if (runStart.HasValue)
                {
                    AddProjectedRun(rowBuckets, runStart.Value, x, y, scale, ref rawSegments);
                    runStart = null;
                }
            }

            if (runStart.HasValue)
                AddProjectedRun(rowBuckets, runStart.Value, width, y, scale, ref rawSegments);
        }

        List<FinalRowSegment> merged = MergeProjectedRuns(rowBuckets, out dedupedSegments);
        mergedSegments = merged.Count;
        return merged;
    }

    private void SendEvent(INetGameService ns, ref MapDrawingMessage msg, NetMapDrawingEvent ev)
    {
        if (msg.TryAddEvent(ev))
            return;

        ns.SendMessage(msg);
        msg = new MapDrawingMessage
        {
            drawingMode = DrawingMode.Drawing
        };
        msg.TryAddEvent(ev);
    }

    private Vector2 ToNetPos(Vector2 pos)
    {
        Vector2 size = _mapDrawings.Size;
        pos.X -= size.X * 0.5f;
        pos /= new Vector2(960f, size.Y);
        return pos;
    }

    private void AddProjectedRun(Dictionary<int, List<PixelInterval>> rowBuckets, int sourceStartX, int sourceEndXExclusive, int sourceRow, float scale, ref int rawSegments)
    {
        int localStartX = Mathf.Clamp(Mathf.FloorToInt(_displayRect.Position.X + sourceStartX * scale), _displayPixelBounds.Position.X, _displayPixelBounds.End.X);
        int localEndX = Mathf.Clamp(Mathf.CeilToInt(_displayRect.Position.X + sourceEndXExclusive * scale), _displayPixelBounds.Position.X, _displayPixelBounds.End.X);
        int localRowStart = Mathf.Clamp(Mathf.FloorToInt(_displayRect.Position.Y + sourceRow * scale), _displayPixelBounds.Position.Y, _displayPixelBounds.End.Y);
        int localRowEnd = Mathf.Clamp(Mathf.CeilToInt(_displayRect.Position.Y + (sourceRow + 1) * scale), _displayPixelBounds.Position.Y, _displayPixelBounds.End.Y);

        if (localEndX <= localStartX || localRowEnd <= localRowStart)
            return;

        for (int localRow = localRowStart; localRow < localRowEnd; localRow++)
        {
            if (!rowBuckets.TryGetValue(localRow, out List<PixelInterval>? intervals))
            {
                intervals = new List<PixelInterval>();
                rowBuckets.Add(localRow, intervals);
            }

            intervals.Add(new PixelInterval(localStartX, localEndX));
            rawSegments++;
        }
    }

    private static List<FinalRowSegment> MergeProjectedRuns(Dictionary<int, List<PixelInterval>> rowBuckets, out int dedupedSegments)
    {
        var merged = new List<FinalRowSegment>();
        dedupedSegments = 0;
        var rows = new List<int>(rowBuckets.Keys);
        rows.Sort();

        foreach (int row in rows)
        {
            List<PixelInterval> intervals = rowBuckets[row];
            intervals.Sort((left, right) =>
            {
                int startCompare = left.StartX.CompareTo(right.StartX);
                return startCompare != 0 ? startCompare : left.EndXExclusive.CompareTo(right.EndXExclusive);
            });

            var unique = new List<PixelInterval>(intervals.Count);
            PixelInterval? lastUnique = null;
            foreach (PixelInterval interval in intervals)
            {
                if (lastUnique.HasValue && lastUnique.Value.Equals(interval))
                    continue;

                unique.Add(interval);
                lastUnique = interval;
                dedupedSegments++;
            }

            if (unique.Count == 0)
                continue;

            int currentStart = unique[0].StartX;
            int currentEnd = unique[0].EndXExclusive;

            for (int i = 1; i < unique.Count; i++)
            {
                PixelInterval interval = unique[i];
                if (interval.StartX <= currentEnd)
                {
                    currentEnd = Math.Max(currentEnd, interval.EndXExclusive);
                    continue;
                }

                merged.Add(new FinalRowSegment(row, currentStart, currentEnd));
                currentStart = interval.StartX;
                currentEnd = interval.EndXExclusive;
            }

            merged.Add(new FinalRowSegment(row, currentStart, currentEnd));
        }

        return merged;
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
            _finalSegments.Clear();
            _blackPixelCount = 0;
            _whitePixelCount = 0;
            _previewTex = null;
            _previewSprite.Texture = null;
            UpdateDisplayLayout();
        }

        _previewVisible = false;
        _previewSprite.Visible = false;
        _hasAreaSelectionStart = false;
        _overlay.CancelSelectionMode();
        SetStatus(status);
    }

    private void SetDrawMode(DrawRegionMode mode)
    {
        if (_drawMode == mode)
            return;

        _drawMode = mode;

        if (_binaryImage != null)
        {
            _previewVisible = true;
            RebuildDisplayOutput();
            _previewSprite.Visible = true;
            SetStatus($"已切换为绘制{GetSelectedRegionText()}，预览已更新，按 {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 绘制");
            return;
        }

        SetStatus($"当前绘制模式: {GetSelectedRegionText()}，默认黑色，可在面板切换");
    }

    private bool IsTargetPixel(Image frame, int x, int y)
        => IsTargetPixel(frame, x, y, _drawMode);

    private bool IsTargetPixel(Image frame, int x, int y, DrawRegionMode mode)
    {
        if (!IsContentPixel(x, y))
            return false;

        bool isWhite = frame.GetPixel(x, y).R > 0.5f;
        return mode == DrawRegionMode.White ? isWhite : !isWhite;
    }

    private bool IsContentPixel(int x, int y)
        => _contentMask != null && _contentMask.GetPixel(x, y).R > 0.5f;

    private string BuildLoadedStatus(string fileName)
    {
        string hint = GetDrawableHintForMode(_drawMode);
        string hintText = string.IsNullOrEmpty(hint) ? string.Empty : $"，{hint}";
        return $"已载入: {fileName}，当前绘制{GetSelectedRegionText()}{hintText}，按 {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 绘制";
    }

    private string GetDrawableHintForMode(DrawRegionMode mode)
    {
        if (_binaryImage == null)
            return string.Empty;

        int currentCount = CountDrawablePixels(mode);
        if (currentCount > 0)
            return string.Empty;

        DrawRegionMode alternateMode = mode == DrawRegionMode.Black ? DrawRegionMode.White : DrawRegionMode.Black;
        int alternateCount = CountDrawablePixels(alternateMode);
        if (alternateCount > 0)
            return $"{GetDrawableRegionText(mode)}为空，可切换{GetSelectedRegionText(alternateMode)}";

        return $"{GetDrawableRegionText(mode)}为空";
    }

    private string GetSelectedRegionText()
        => GetSelectedRegionText(_drawMode);

    private static string GetSelectedRegionText(DrawRegionMode mode)
        => mode == DrawRegionMode.Black ? "黑色部分" : "白色部分";

    private string GetDrawableRegionText()
        => GetDrawableRegionText(_drawMode);

    private static string GetDrawableRegionText(DrawRegionMode mode)
        => mode == DrawRegionMode.Black ? "黑色区域" : "白色区域";

    private string GetShortcutText(FastDrawShortcutAction action)
        => FastDrawShortcutConfig.Current.Describe(action);

    private string BuildShortcutSummary()
        => $"{GetShortcutText(FastDrawShortcutAction.ImportImage)} 导入图片 / {GetShortcutText(FastDrawShortcutAction.PasteImagePath)} 粘贴路径 / {GetShortcutText(FastDrawShortcutAction.DrawCurrentImage)} 绘制 / {GetShortcutText(FastDrawShortcutAction.ClearCurrentImage)} 清空 / 用 {GetShortcutText(FastDrawShortcutAction.CaptureSelectionStart)} 和 {GetShortcutText(FastDrawShortcutAction.CaptureSelectionEnd)} 记录选区两点";

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
        GD.Print("[FastDrawImg] " + text);
        FastDrawLog.Debug("状态更新: " + text);
    }

    private int CountDrawablePixels(DrawRegionMode mode)
        => mode == DrawRegionMode.Black ? _blackPixelCount : _whitePixelCount;

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

    private DrawRegionMode GetAlternateDrawMode()
        => _drawMode == DrawRegionMode.Black ? DrawRegionMode.White : DrawRegionMode.Black;

    private static string FormatRect(Rect2 rect)
        => $"({rect.Position.X:0.##}, {rect.Position.Y:0.##}, {rect.Size.X:0.##}, {rect.Size.Y:0.##})";

    private static string FormatRectI(Rect2I rect)
        => $"({rect.Position.X}, {rect.Position.Y}, {rect.Size.X}, {rect.Size.Y})";

    private static string FormatVector(Vector2 vector)
        => $"({vector.X:0.##}, {vector.Y:0.##})";

    private static string DescribeKeyEvent(InputEventKey keyEvent)
        => $"keycode={keyEvent.Keycode}, keyLabel={keyEvent.KeyLabel}, physicalKeycode={keyEvent.PhysicalKeycode}, ctrl={keyEvent.CtrlPressed}, shift={keyEvent.ShiftPressed}, alt={keyEvent.AltPressed}, unicode={keyEvent.Unicode}";
}
