using Godot;
using System;

namespace FastDrawImg.Patches;

public partial class DrawAreaOverlay : Control
{
    private static readonly Color AreaFillColor = new(0.20f, 0.70f, 0.95f, 0.08f);
    private static readonly Color AreaOutlineColor = new(0.20f, 0.70f, 0.95f, 0.90f);
    private static readonly Color SelectionFillColor = new(0.95f, 0.80f, 0.20f, 0.18f);
    private static readonly Color SelectionOutlineColor = new(0.95f, 0.80f, 0.20f, 0.95f);

    private bool _selectionMode;
    private bool _dragging;
    private Vector2 _dragStart;
    private Rect2 _selectionRect = default;

    public Rect2 DrawArea { get; set; } = default;

    public bool IsSelectionMode => _selectionMode;

    public event Action<Rect2>? AreaSelected;
    public event Action? SelectionCanceled;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.All;
    }

    public void EnterSelectionMode()
    {
        _selectionMode = true;
        _dragging = false;
        _selectionRect = default;
        MouseFilter = MouseFilterEnum.Stop;
        GrabFocus();
        QueueRedraw();
    }

    public void CancelSelectionMode(bool notify = false)
    {
        if (!_selectionMode && !_dragging)
            return;

        _selectionMode = false;
        _dragging = false;
        _selectionRect = default;
        MouseFilter = MouseFilterEnum.Ignore;
        ReleaseFocus();
        QueueRedraw();

        if (notify)
            SelectionCanceled?.Invoke();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (!_selectionMode)
            return;

        if (@event is InputEventMouseButton mouseButton)
        {
            Vector2 point = ClampPoint(mouseButton.Position);
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed)
                {
                    _dragging = true;
                    _dragStart = point;
                    _selectionRect = new Rect2(point, Vector2.Zero);
                    QueueRedraw();
                    AcceptEvent();
                    return;
                }

                if (_dragging)
                {
                    _dragging = false;
                    _selectionMode = false;
                    MouseFilter = MouseFilterEnum.Ignore;
                    ReleaseFocus();
                    _selectionRect = MakeRect(_dragStart, point);
                    QueueRedraw();
                    AreaSelected?.Invoke(_selectionRect);
                    AcceptEvent();
                    return;
                }
            }

            if (mouseButton.Pressed && mouseButton.ButtonIndex == MouseButton.Right)
            {
                CancelSelectionMode(notify: true);
                AcceptEvent();
            }

            return;
        }

        if (@event is InputEventMouseMotion mouseMotion && _dragging)
        {
            _selectionRect = MakeRect(_dragStart, ClampPoint(mouseMotion.Position));
            QueueRedraw();
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        if (DrawArea.Size.X > 0f && DrawArea.Size.Y > 0f)
        {
            DrawRect(DrawArea, AreaFillColor, true);
            DrawRect(DrawArea, AreaOutlineColor, false, 2f);
        }

        if (_selectionMode && _selectionRect.Size.X > 0f && _selectionRect.Size.Y > 0f)
        {
            DrawRect(_selectionRect, SelectionFillColor, true);
            DrawRect(_selectionRect, SelectionOutlineColor, false, 2f);
        }
    }

    private Vector2 ClampPoint(Vector2 point)
        => new(Mathf.Clamp(point.X, 0f, Size.X), Mathf.Clamp(point.Y, 0f, Size.Y));

    private static Rect2 MakeRect(Vector2 start, Vector2 end)
    {
        float minX = Mathf.Min(start.X, end.X);
        float minY = Mathf.Min(start.Y, end.Y);
        float maxX = Mathf.Max(start.X, end.X);
        float maxY = Mathf.Max(start.Y, end.Y);
        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }
}
