using Godot;
using System;

namespace FastDrawImg.Patches;

public partial class DrawAreaOverlay : Control
{
    private static readonly Color AreaFillColor = new(0.20f, 0.70f, 0.95f, 0.08f);
    private static readonly Color AreaOutlineColor = new(0.20f, 0.70f, 0.95f, 0.90f);
    private static readonly Color SelectionFillColor = new(0.95f, 0.80f, 0.20f, 0.18f);
    private static readonly Color SelectionOutlineColor = new(0.95f, 0.80f, 0.20f, 0.95f);
    private const float MarkerRadius = 5f;
    private const float MarkerCrossHalfSize = 8f;

    private bool _selectionMode;
    private Rect2 _selectionRect = default;
    private Texture2D? _previewTexture;
    private bool _previewVisible;

    public Rect2 DrawArea { get; set; } = default;

    public bool IsSelectionMode => _selectionMode;

    public event Action? SelectionCanceled;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void SetDrawArea(Rect2 area)
    {
        DrawArea = area;
        QueueRedraw();
    }

    public void SetPreviewTexture(Texture2D? texture)
    {
        _previewTexture = texture;
        QueueRedraw();
    }

    public void SetPreviewVisible(bool visible)
    {
        _previewVisible = visible;
        QueueRedraw();
    }

    public void EnterSelectionMode()
    {
        _selectionMode = true;
        _selectionRect = default;
        QueueRedraw();
    }

    public void CancelSelectionMode(bool notify = false)
    {
        if (!_selectionMode)
            return;

        _selectionMode = false;
        _selectionRect = default;
        QueueRedraw();

        if (notify)
            SelectionCanceled?.Invoke();
    }

    public void SetSelectionRect(Rect2 rect)
    {
        _selectionRect = rect;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (DrawArea.Size.X > 0f && DrawArea.Size.Y > 0f)
        {
            if (_previewVisible && _previewTexture != null)
                DrawTextureRect(_previewTexture, DrawArea, false, null, false);
            else
                DrawRect(DrawArea, AreaFillColor, true);

            DrawRect(DrawArea, AreaOutlineColor, false, 2f);
            DrawMarker(DrawArea.Position, AreaOutlineColor);
            DrawMarker(DrawArea.End, AreaOutlineColor);
        }

        if (_selectionMode && _selectionRect.Size.X > 0f && _selectionRect.Size.Y > 0f)
        {
            DrawRect(_selectionRect, SelectionFillColor, true);
            DrawRect(_selectionRect, SelectionOutlineColor, false, 2f);
        }
    }

    private void DrawMarker(Vector2 point, Color color)
    {
        DrawCircle(point, MarkerRadius, color);
        DrawLine(point + new Vector2(-MarkerCrossHalfSize, 0f), point + new Vector2(MarkerCrossHalfSize, 0f), color, 2f);
        DrawLine(point + new Vector2(0f, -MarkerCrossHalfSize), point + new Vector2(0f, MarkerCrossHalfSize), color, 2f);
    }
}
