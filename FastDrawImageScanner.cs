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

    private NMapDrawings _mapDrawings = null!;
    private Sprite2D _previewSprite = null!;
    private ImageTexture? _previewTex;
    private FileDialog _fileDialog = null!;
    private CanvasLayer _uiLayer = null!;
    private Label _statusLabel = null!;

    private readonly Vector2I _renderRes = new(160, 120);
    private const float PixelScale = 2.0f;
    private readonly Vector2 _drawOffset = new(60, 40);
    private const float LuminanceThreshold = 0.5f;
    private const int LineDensity = 2;

    private Color _drawColor = Colors.White;
    private Image? _binaryImage;
    private string? _currentImagePath;
    private bool _dropConnected;

    public void Initialize(NMapDrawings drawings)
    {
        _mapDrawings = drawings;
        ResolvePlayerDrawColor();
        BuildPreview();
        BuildUi();
        TryConnectFileDrop();
        Visible = true;
        SetStatus("Ctrl+U 导入图片 / Ctrl+V 粘贴路径 / U 重绘 / Shift+U 清空");
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
        base._ExitTree();
    }

    public void OpenImportDialog() => _fileDialog.PopupCenteredRatio(0.7f);

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
        _binaryImage = null;
        _currentImagePath = null;
        _previewSprite.Visible = false;
        SendClearToNetwork();
        SetStatus("已清空当前图像");
    }

    public void DrawCurrentImage()
    {
        if (_binaryImage == null)
        {
            SetStatus("还没有载入图像");
            return;
        }

        UpdatePreviewTexture();
        SendImageToNetwork();
        _previewSprite.Visible = true;
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
                var player = pc.GetPlayer((ulong)ns.NetId);
                if (player != null)
                    _drawColor = player.Character.MapDrawingColor;
            }
        }
        catch
        {
            _drawColor = Colors.White;
        }
    }

    private void BuildPreview()
    {
        _previewSprite = new Sprite2D
        {
            Centered = false,
            Position = _drawOffset,
            Scale = new Vector2(PixelScale, PixelScale),
            Visible = false
        };
        AddChild(_previewSprite);
    }

    private void BuildUi()
    {
        _uiLayer = new CanvasLayer();

        var panel = new PanelContainer();
        panel.Position = new Vector2(24, 24);
        panel.Size = new Vector2(360, 126);

        var vbox = new VBoxContainer();
        panel.AddChild(vbox);

        _statusLabel = new Label { Text = "未载入图像", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        vbox.AddChild(_statusLabel);

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
            _binaryImage = PrepareBinaryImage(image);
            UpdatePreviewTexture();
            _previewSprite.Visible = true;
            SetStatus($"已载入: {Path.GetFileName(path)}，按 U 绘制");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("载入失败: " + ex.Message);
            return false;
        }
    }

    private Image PrepareBinaryImage(Image image)
    {
        Image work = image.Duplicate();
        if (work.GetFormat() != Image.Format.Rgba8)
            work.Convert(Image.Format.Rgba8);

        work.Resize(_renderRes.X, _renderRes.Y, Image.Interpolation.Nearest);

        for (int y = 0; y < _renderRes.Y; y++)
        for (int x = 0; x < _renderRes.X; x++)
        {
            Color px = work.GetPixel(x, y);
            work.SetPixel(x, y, px.Luminance > LuminanceThreshold ? Colors.White : Colors.Black);
        }

        return work;
    }

    private void UpdatePreviewTexture()
    {
        if (_binaryImage == null)
            return;

        Image preview = Image.CreateEmpty(_renderRes.X, _renderRes.Y, false, Image.Format.Rgba8);
        Color transparent = new(0, 0, 0, 0);

        for (int y = 0; y < _renderRes.Y; y++)
        for (int x = 0; x < _renderRes.X; x++)
            preview.SetPixel(x, y, _binaryImage.GetPixel(x, y).R > 0.5f ? _drawColor : transparent);

        if (_previewTex == null)
        {
            _previewTex = ImageTexture.CreateFromImage(preview);
            _previewSprite.Texture = _previewTex;
        }
        else
        {
            _previewTex.Update(preview);
        }
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
        float subStep = PixelScale / LineDensity;

        for (int y = 0; y < _renderRes.Y; y++)
        {
            int? runStart = null;
            for (int x = 0; x < _renderRes.X; x++)
            {
                bool on = frame.GetPixel(x, y).R > 0.5f;
                if (on)
                    runStart ??= x;
                else if (runStart.HasValue)
                {
                    for (int sub = 0; sub < LineDensity; sub++)
                        AddSegment(segments, runStart.Value, x, y * PixelScale + sub * subStep);
                    runStart = null;
                }
            }

            if (runStart.HasValue)
                for (int sub = 0; sub < LineDensity; sub++)
                    AddSegment(segments, runStart.Value, _renderRes.X, y * PixelScale + sub * subStep);
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

    private void AddSegment(List<(Vector2, Vector2)> list, int x1, int x2, float drawY)
    {
        Vector2 start = (_drawOffset + new Vector2(x1 * PixelScale, drawY)) * 2f;
        Vector2 end = (_drawOffset + new Vector2(x2 * PixelScale, drawY)) * 2f;
        list.Add((start, end));
    }

    private void SetStatus(string text)
    {
        _statusLabel.Text = text;
        GD.Print("[FastDrawImg] " + text);
    }
}
