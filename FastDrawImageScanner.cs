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

    private Color _drawColor = Colors.White;
    private Image? _sourceImage;
    private Image? _binaryImage;
    private string? _currentImagePath;
    private ulong? _localPlayerId;
    private bool _dropConnected;
    private bool _previewVisible;
    private Rect2 _drawArea = DefaultDrawArea;

    public void Initialize(NMapDrawings drawings)
    {
        _mapDrawings = drawings;
        ResolvePlayerDrawColor();
        BuildOverlay();
        BuildUi();
        TryConnectFileDrop();
        Visible = true;
        SetStatus("Ctrl+U 导入图片 / Ctrl+V 粘贴路径 / U 重绘 / Shift+U 清空 / 选择区域重框");
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

        if (IsInstanceValid(_overlay))
            _overlay.QueueFree();

        base._ExitTree();
    }

    public void OpenImportDialog() => _fileDialog.PopupCenteredRatio(0.7f);

    public bool HandleShortcutKey(InputEventKey keyEvent)
    {
        if (!_overlay.IsSelectionMode)
            return false;

        if (keyEvent.Keycode == Key.Escape)
        {
            CancelAreaSelection();
            return true;
        }

        if (keyEvent.Keycode is Key.U or Key.V)
            return true;

        return false;
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
        ResetPreviewState("已清空当前图像", forgetLoadedImage: true);
        SendClearToNetwork();
    }

    public void OnMapCleared()
    {
        if (_binaryImage == null && !_previewVisible)
            return;

        ResetPreviewState("地图绘制已清空，按 U 可重绘当前图像");
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
        SendImageToNetwork();
        SetStatus($"已绘制: {(_currentImagePath ?? "剪贴板路径")}");
    }

    private void ResolvePlayerDrawColor()
    {
        try
        {
            var nsField = typeof(NMapDrawings).GetField("_netService", BindingFlags.NonPublic | BindingFlags.Instance);
            var pcField = typeof(NMapDrawings).GetField("_playerCollection", BindingFlags.NonPublic | BindingFlags.Instance);
            dynamic? ns = nsField?.GetValue(_mapDrawings);
            dynamic? pc = pcField?.GetValue(_mapDrawings);
            if (ns != null && pc != null)
            {
                _localPlayerId = (ulong)ns.NetId;
                var player = pc.GetPlayer(_localPlayerId.Value);
                if (player != null)
                    _drawColor = player.Character.MapDrawingColor;
            }
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
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.SetDrawArea(_drawArea);
        _overlay.AreaSelected += OnAreaSelected;
        _overlay.SelectionCanceled += OnAreaSelectionCanceled;
        _mapDrawings.AddChild(_overlay);
    }

    private void BuildUi()
    {
        _uiLayer = new CanvasLayer();

        var panel = new PanelContainer();
        panel.Position = new Vector2(24, 24);
        panel.Size = new Vector2(480, 126);

        var vbox = new VBoxContainer();
        panel.AddChild(vbox);

        _statusLabel = new Label { Text = "未载入图像", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        vbox.AddChild(_statusLabel);

        var buttonRow = new HBoxContainer();
        vbox.AddChild(buttonRow);

        var importButton = new Button { Text = "导入图像" };
        importButton.Pressed += OpenImportDialog;
        buttonRow.AddChild(importButton);

        var selectAreaButton = new Button { Text = "选择区域" };
        selectAreaButton.Pressed += StartAreaSelection;
        buttonRow.AddChild(selectAreaButton);

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

    private void StartAreaSelection()
    {
        _overlay.EnterSelectionMode();
        SetStatus("左键拖拽选择绘制区域，右键或 Esc 取消");
    }

    private void CancelAreaSelection()
    {
        _overlay.CancelSelectionMode();
        SetStatus("已取消区域选择");
    }

    private void OnAreaSelectionCanceled() => SetStatus("已取消区域选择");

    private void OnAreaSelected(Rect2 area)
    {
        if (area.Size.X < MinDrawAreaSize || area.Size.Y < MinDrawAreaSize)
        {
            SetStatus("选区太小，至少需要 16x16");
            _overlay.SetDrawArea(_drawArea);
            return;
        }

        _drawArea = area;
        _overlay.SetDrawArea(_drawArea);
        if (_sourceImage != null)
            RefreshRenderedImage(_previewVisible);

        SetStatus($"已更新绘制区域: {Mathf.RoundToInt(area.Size.X)}x{Mathf.RoundToInt(area.Size.Y)}");
    }

    private void OnFileSelected(string path) => TryLoadImage(path);

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
            SetStatus($"已载入: {Path.GetFileName(path)}，按 U 绘制");
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
        Image work = CreateFittedBinaryCanvas(image, renderSize);

        for (int y = 0; y < renderSize.Y; y++)
        for (int x = 0; x < renderSize.X; x++)
        {
            Color px = work.GetPixel(x, y);
            work.SetPixel(x, y, px.Luminance > LuminanceThreshold ? Colors.White : Colors.Black);
        }

        return work;
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

    private Image CreateFittedBinaryCanvas(Image source, Vector2I renderSize)
    {
        Image canvas = Image.CreateEmpty(renderSize.X, renderSize.Y, false, Image.Format.Rgba8);
        canvas.Fill(Colors.Black);

        int sourceWidth = source.GetWidth();
        int sourceHeight = source.GetHeight();
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return canvas;

        float scale = Math.Min(renderSize.X / (float)sourceWidth, renderSize.Y / (float)sourceHeight);
        int fittedWidth = Math.Max(1, Mathf.RoundToInt(sourceWidth * scale));
        int fittedHeight = Math.Max(1, Mathf.RoundToInt(sourceHeight * scale));

        Image resized = (Image)source.Duplicate();
        resized.Resize(fittedWidth, fittedHeight, Image.Interpolation.Nearest);

        Vector2I offset = new((renderSize.X - fittedWidth) / 2, (renderSize.Y - fittedHeight) / 2);
        Rect2I sourceRect = new(0, 0, fittedWidth, fittedHeight);
        canvas.BlitRect(resized, sourceRect, offset);
        return canvas;
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
            preview.SetPixel(x, y, _binaryImage.GetPixel(x, y).R > 0.5f ? _drawColor : transparent);

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

    private void SendImageToNetwork()
    {
        if (_binaryImage == null)
            return;

        var ns = RunManager.Instance?.NetService;
        if (ns == null || ns.Type == NetGameType.Singleplayer)
            return;

        var segments = BuildSegments(_binaryImage);
        if (segments.Count == 0)
        {
            SetStatus("图像里没有可绘制的白色区域");
            return;
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
                bool on = frame.GetPixel(x, y).R > 0.5f;
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

    private void ResetPreviewState(string status, bool forgetLoadedImage = false)
    {
        if (forgetLoadedImage)
        {
            _sourceImage = null;
            _binaryImage = null;
            _currentImagePath = null;
            _previewTex = null;
            _overlay.SetPreviewTexture(null);
        }

        _previewVisible = false;
        _overlay.SetPreviewVisible(false);
        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
        GD.Print("[FastDrawImg] " + text);
    }
}
