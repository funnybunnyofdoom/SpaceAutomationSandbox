using System;
using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The editable grid. Left mouse paints the selected part at the current rotation (R in the panel),
/// right mouse erases; a drag is one undo step. Every part shows a small arrow for its facing.
///  Draws only from <see cref="BlueprintEditor"/>; holds no design state of its own.
/// </summary>
public partial class BlueprintGridCanvas : Control
{
    /// <summary>The smallest cell: 21 fit beside the palette and the stats at 1280 x 720. Bigger windows get bigger cells (#102).</summary>
    public const int MinCellPx = 25;
    private const int H = BlueprintEditor.HalfExtent;
    private const int Count = 2 * H + 1;
    private const int Top = 18; // room for the line above the grid

    /// <summary>Pixels per cell now.</summary>
    private int CellPx => Mathf.Max(MinCellPx, Mathf.FloorToInt(Mathf.Min(Size.X, Size.Y - Top) / Count));
    private int Side => Count * CellPx;
    private Vector2 Origin => new(Mathf.Floor(Mathf.Max(0, (Size.X - Side) / 2)), Top);

    /// <summary>Cells with something wrong, and what (#102).</summary>
    public IReadOnlyDictionary<GridPos, string> Problems { get; set; } = new Dictionary<GridPos, string>();

    private static readonly Color Background = new(0.06f, 0.07f, 0.10f);
    private static readonly Color GridLine = new(1f, 1f, 1f, 0.07f);
    private static readonly Color AxisLine = new(1f, 1f, 1f, 0.18f);
    private static Color HoverPlace => Palette.Good with { A = 0.9f }; // colourblind-safe option (#68)
    private static Color HoverErase => Palette.Bad with { A = 0.9f };
    private static readonly Color ComColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color UnknownPart = new(1f, 0f, 1f);
    private static Color LooseCell => Palette.Bad with { A = 0.95f };

    private readonly BlueprintEditor _model;
    private readonly IReadOnlyDictionary<string, Color> _colors;
    private readonly Func<string?> _selectedPart;
    private readonly Func<int> _rotation;
    private GridPos? _hover;
    private MouseButton _dragButton = MouseButton.None;

    /// <summary>The placed part under the mouse and its cell, or null (also while drawing).</summary>
    public event Action<string?, Rect2>? PartPointed;

    /// <summary>Raised after any edit so the panel can refresh stats and errors.</summary>
    public event Action? Edited;

    public BlueprintGridCanvas(BlueprintEditor model, IReadOnlyDictionary<string, Color> colors, Func<string?> selectedPart, Func<int> rotation)
    {
        _model = model;
        _colors = colors;
        _selectedPart = selectedPart;
        _rotation = rotation;
        CustomMinimumSize = new Vector2(Count * MinCellPx, Count * MinCellPx + Top);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        TextureFilter = TextureFilterEnum.Nearest;
        MouseFilter = MouseFilterEnum.Stop;
        MouseExited += () => { _hover = null; PartPointed?.Invoke(null, default); QueueRedraw(); };
    }

    // Parameterless constructor required by Godot for script instances; not used.
    public BlueprintGridCanvas() : this(new BlueprintEditor(new PartCatalog(Array.Empty<PartDefinition>(), new MaterialCatalog(Array.Empty<MaterialDefinition>()))),
        new Dictionary<string, Color>(), () => null, () => 0) { }

    private GridPos ToCell(Vector2 local) =>
        new(Mathf.FloorToInt((local.X - Origin.X) / CellPx) - H, Mathf.FloorToInt((local.Y - Origin.Y) / CellPx) - H);

    /// <summary>A cell's corner inside the grid (drawing is moved to the grid's corner).</summary>
    private Vector2 CellOrigin(GridPos pos) => new((pos.X + H) * CellPx, (pos.Y + H) * CellPx);

    /// <summary>What's wrong with the cell under the mouse, if anything.</summary>
    public string? ProblemAtPointer => _hover is { } at && Problems.TryGetValue(at, out var why) ? why : null;

    private Rect2 CellRect(GridPos pos) => new(Origin + CellOrigin(pos), new Vector2(CellPx, CellPx));

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } button:
                if (button.Pressed && _dragButton == MouseButton.None)
                {
                    _dragButton = button.ButtonIndex;
                    PartPointed?.Invoke(null, default);
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
                else PartPointed?.Invoke(_model.Cells.TryGetValue(cell, out var under) ? under.PartId : null, CellRect(cell));
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
        DrawSetTransform(Origin);
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
            if (!DrawPartSprite(cell.PartId, cell.Rotation, origin, Colors.White)) // the part's pixel art (#46)
            {
                DrawRect(new Rect2(origin + new Vector2(2, 2), new Vector2(CellPx - 4, CellPx - 4)),
                    _colors.TryGetValue(cell.PartId, out var c) ? c : UnknownPart);
                // Two-letter tag so parts with similar colours stay distinguishable.
                var id = cell.PartId;
                var tag = id.Length >= 2 ? id[..2].ToUpperInvariant() : id.ToUpperInvariant();
                DrawString(font, origin + new Vector2(5, CellPx - 9), tag, HorizontalAlignment.Left, -1, 11, new Color(0, 0, 0, 0.75f));
                DrawFacing(origin, cell.Rotation, new Color(0, 0, 0, 0.6f));
            }
            if (Problems.ContainsKey(pos)) // not connected (#35), or not allowed; the "!" says so without colour (#68)
            {
                DrawRect(new Rect2(origin, new Vector2(CellPx, CellPx)), LooseCell, false, 3);
                DrawString(font, origin + new Vector2(CellPx - 11, 14), "!", HorizontalAlignment.Left, -1, 14, LooseCell);
            }
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
            // A faint copy of the part to place, turned as it will be (#46).
            if (!erase && _selectedPart() is { } placing && !_model.Cells.ContainsKey(hover))
                DrawPartSprite(placing, _rotation(), CellOrigin(hover), new Color(1, 1, 1, 0.45f));
            DrawRect(new Rect2(CellOrigin(hover), new Vector2(CellPx, CellPx)), erase ? HoverErase : HoverPlace, false, 2);
            if (!erase) DrawFacing(CellOrigin(hover), _rotation(), HoverPlace); // shows the rotation that will be placed
            else // an × marks erasing, not just the colour (#68)
            {
                var o = CellOrigin(hover);
                DrawLine(o + new Vector2(6, 6), o + new Vector2(CellPx - 6, CellPx - 6), HoverErase, 2);
                DrawLine(o + new Vector2(CellPx - 6, 6), o + new Vector2(6, CellPx - 6), HoverErase, 2);
            }
        }

        // Forward indicator above the grid centre.
        DrawString(font, new Vector2(Side - 150, -6), $"placing rotation: {_rotation() * 90} deg", HorizontalAlignment.Left, -1, 12, new Color(1, 1, 1, 0.6f));
        DrawString(font, new Vector2(Side / 2f - 30, -6), "^ FORWARD ^", HorizontalAlignment.Left, -1, 12, new Color(1, 1, 1, 0.6f));
    }

    /// <summary>Small triangle near the cell edge the part faces (thrusters push that way; ports open that way).</summary>
    /// <summary>Draws a part's sprite in a cell, turned by its rotation; false if the part has none.</summary>
    private bool DrawPartSprite(string partId, int rotation, Vector2 origin, Color modulate)
    {
        if (PartSprites.Icon(partId) is not { } texture) return false;
        DrawSetTransform(Origin + origin + new Vector2(CellPx, CellPx) / 2, rotation * Mathf.Pi / 2, Vector2.One);
        DrawTextureRect(texture, new Rect2(-new Vector2(CellPx, CellPx) / 2, new Vector2(CellPx, CellPx)), false, modulate);
        DrawSetTransform(Origin);
        return true;
    }

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
