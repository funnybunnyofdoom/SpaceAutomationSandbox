using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The editable grid. Left mouse paints the selected part at the current rotation (R in the panel),
/// right mouse erases; a drag is one undo step. Every part shows a small arrow for its facing.
///  Draws only from <see cref="BlueprintEditor"/>; holds no design state of its own.
/// </summary>
public partial class BlueprintGridCanvas : Control
{
    public const int CellPx = 26; // 21 cells fit beside the palette and the stats panel
    private const int H = BlueprintEditor.HalfExtent;
    private const int Side = (2 * H + 1) * CellPx;

    private static readonly Color Background = new(0.06f, 0.07f, 0.10f);
    private static readonly Color GridLine = new(1f, 1f, 1f, 0.07f);
    private static readonly Color AxisLine = new(1f, 1f, 1f, 0.18f);
    private static readonly Color HoverPlace = new(0.4f, 1f, 0.6f, 0.9f);
    private static readonly Color HoverErase = new(1f, 0.35f, 0.3f, 0.9f);
    private static readonly Color ComColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color UnknownPart = new(1f, 0f, 1f);

    private readonly BlueprintEditor _model;
    private readonly IReadOnlyDictionary<string, Color> _colors;
    private readonly Func<string?> _selectedPart;
    private readonly Func<int> _rotation;
    private GridPos? _hover;
    private MouseButton _dragButton = MouseButton.None;

    /// <summary>Raised after any edit so the panel can refresh stats and errors.</summary>
    public event Action? Edited;

    public BlueprintGridCanvas(BlueprintEditor model, IReadOnlyDictionary<string, Color> colors, Func<string?> selectedPart, Func<int> rotation)
    {
        _model = model;
        _colors = colors;
        _selectedPart = selectedPart;
        _rotation = rotation;
        CustomMinimumSize = new Vector2(Side, Side);
        MouseFilter = MouseFilterEnum.Stop;
        MouseExited += () => { _hover = null; QueueRedraw(); };
    }

    // Parameterless constructor required by Godot for script instances; not used.
    public BlueprintGridCanvas() : this(new BlueprintEditor(new PartCatalog(Array.Empty<PartDefinition>(), new MaterialCatalog(Array.Empty<MaterialDefinition>()))),
        new Dictionary<string, Color>(), () => null, () => 0) { }

    private static GridPos ToCell(Vector2 local) =>
        new(Mathf.FloorToInt(local.X / CellPx) - H, Mathf.FloorToInt(local.Y / CellPx) - H);

    private static Vector2 CellOrigin(GridPos pos) => new((pos.X + H) * CellPx, (pos.Y + H) * CellPx);

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } button:
                if (button.Pressed && _dragButton == MouseButton.None)
                {
                    _dragButton = button.ButtonIndex;
                    _model.BeginStroke();
                    Apply(ToCell(button.Position));
                }
                else if (!button.Pressed && button.ButtonIndex == _dragButton)
                {
                    _dragButton = MouseButton.None;
                    _model.EndStroke();
                    Edited?.Invoke();
                }
                AcceptEvent();
                break;

            case InputEventMouseMotion motion:
                var cell = ToCell(motion.Position);
                _hover = BlueprintEditor.InBounds(cell) ? cell : null;
                if (_dragButton != MouseButton.None) Apply(cell);
                QueueRedraw();
                break;
        }
    }

    private void Apply(GridPos cell)
    {
        var changed = _dragButton == MouseButton.Right
            ? _model.Remove(cell)
            : _selectedPart() is { } part && _model.Place(cell, part, _rotation());
        if (changed) { Edited?.Invoke(); QueueRedraw(); }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, Side, Side), Background);
        for (var i = 0; i <= 2 * H + 1; i++)
        {
            var line = i == H || i == H + 1 ? AxisLine : GridLine;
            DrawLine(new Vector2(i * CellPx, 0), new Vector2(i * CellPx, Side), line);
            DrawLine(new Vector2(0, i * CellPx), new Vector2(Side, i * CellPx), line);
        }

        var font = ThemeDB.FallbackFont;
        foreach (var (pos, cell) in _model.Cells)
        {
            var origin = CellOrigin(pos);
            DrawRect(new Rect2(origin + new Vector2(2, 2), new Vector2(CellPx - 4, CellPx - 4)),
                _colors.TryGetValue(cell.PartId, out var c) ? c : UnknownPart);
            // Two-letter tag so parts with similar colours stay distinguishable.
            var id = cell.PartId;
            var tag = id.Length >= 2 ? id[..2].ToUpperInvariant() : id.ToUpperInvariant();
            DrawString(font, origin + new Vector2(5, CellPx - 9), tag, HorizontalAlignment.Left, -1, 11, new Color(0, 0, 0, 0.75f));
            DrawFacing(origin, cell.Rotation, new Color(0, 0, 0, 0.6f));
        }

        if (_model.Stats is { } stats) // centre of mass (cell centres are at integer coordinates)
        {
            var com = new Vector2((float)((stats.CenterOfMassX + H + 0.5) * CellPx), (float)((stats.CenterOfMassY + H + 0.5) * CellPx));
            DrawCircle(com, 5, ComColor);
            DrawArc(com, 9, 0, Mathf.Tau, 24, ComColor, 1.5f);
        }

        if (_hover is { } hover)
        {
            var erase = _dragButton == MouseButton.Right;
            DrawRect(new Rect2(CellOrigin(hover), new Vector2(CellPx, CellPx)), erase ? HoverErase : HoverPlace, false, 2);
            if (!erase) DrawFacing(CellOrigin(hover), _rotation(), HoverPlace); // shows the rotation that will be placed
        }

        // Forward indicator above the grid centre.
        DrawString(font, new Vector2(Side - 150, -6), $"placing rotation: {_rotation() * 90} deg", HorizontalAlignment.Left, -1, 12, new Color(1, 1, 1, 0.6f));
        DrawString(font, new Vector2(Side / 2f - 30, -6), "^ FORWARD ^", HorizontalAlignment.Left, -1, 12, new Color(1, 1, 1, 0.6f));
    }

    /// <summary>Small triangle near the cell edge the part faces (thrusters push that way; ports open that way).</summary>
    private void DrawFacing(Vector2 origin, int rotation, Color color)
    {
        var (fx, fy) = DesignCell.Facing(rotation);
        var dir = new Vector2(fx, fy);
        var side = new Vector2(-fy, fx);
        var centre = origin + new Vector2(CellPx / 2f, CellPx / 2f);
        var tip = centre + dir * (CellPx / 2f - 2);
        DrawColoredPolygon(new[] { tip, tip - dir * 6 + side * 5, tip - dir * 6 - side * 5 }, color);
    }
}
