using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>What the left mouse button does on the grid. All but Draw belong to large-ship building (#112).</summary>
public enum GridTool { Draw, Line, Rectangle, Outline, Fill, Select }

/// <summary>
/// The editable grid. Left mouse uses the tool (draw the selected part at the current rotation, to begin with), right mouse erases; a
/// drag is one undo step. With large-ship building open (#111) the wheel zooms, the middle button pans and a small map shows the whole
/// grid. Draws only from <see cref="BlueprintEditor"/>; holds no design state of its own.
/// </summary>
public partial class BlueprintGridCanvas : Control
{
    /// <summary>The smallest cell of the small grid: 21 fit beside the palette and the stats at 1280 x 720. Bigger windows get bigger cells (#102).</summary>
    public const int MinCellPx = 25;

    private const int Top = 18; // room for the line above the grid
    private const int SmallCount = 2 * BlueprintEditor.HalfExtent + 1;
    private const float MinZoomPx = 4, MaxZoomPx = 64;

    private int H => _model.Extent;
    private int Count => 2 * H + 1;

    /// <summary>The large grid: zoom and pan.</summary>
    private bool Large => _model.Extent > BlueprintEditor.HalfExtent;

    private float _zoomPx = 16;
    private bool _fitted; // not zoomed or moved since showing the whole design
    private Vector2 _pan; // where the grid's corner is, from this control's corner below the top line

    /// <summary>Pixels per cell now.</summary>
    private float CellPx => Large ? _zoomPx : Mathf.Max(MinCellPx, Mathf.FloorToInt(Mathf.Min(Size.X, Size.Y - Top) / Count));
    private float Side => Count * CellPx;
    private Vector2 Origin => Large ? new Vector2(Mathf.Round(_pan.X), Top + Mathf.Round(_pan.Y)) : new(Mathf.Floor(Mathf.Max(0, (Size.X - Side) / 2)), Top);

    /// <summary>Cells with something wrong, and what (#102).</summary>
    public IReadOnlyDictionary<GridPos, string> Problems { get; set; } = new Dictionary<GridPos, string>();

    public GridTool Tool { get; set; }

    /// <summary>The drawing brush, 1 to 5 cells across.</summary>
    public int BrushSize { get; set; } = 1;

    /// <summary>Cells waiting to be placed at the pointer (a paste), with their corner at (0, 0); null when there are none.</summary>
    public IReadOnlyList<DesignCell>? Stamp { get; private set; }

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
    private readonly CellLayer _layer;
    private readonly Overview _overview;
    private GridPos? _hover;
    private GridPos _from;
    private MouseButton _dragButton = MouseButton.None;
    private bool _movingSelection;
    private bool _byKeys;

    /// <summary>The placed part under the mouse and its cell, or null (also while drawing).</summary>
    public event Action<string?, Rect2>? PartPointed;

    /// <summary>Raised after any edit so the panel can refresh stats and errors.</summary>
    public event Action? Edited;

    /// <summary>Raised when the selection or the stamp changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>Something can't be done, and why.</summary>
    public event Action<string>? Refused;

    public BlueprintGridCanvas(BlueprintEditor model, IReadOnlyDictionary<string, Color> colors, Func<string?> selectedPart, Func<int> rotation)
    {
        _model = model;
        _colors = colors;
        _selectedPart = selectedPart;
        _rotation = rotation;
        CustomMinimumSize = new Vector2(SmallCount * MinCellPx, SmallCount * MinCellPx + Top);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        TextureFilter = TextureFilterEnum.Nearest;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        MouseExited += () => { if (_dragButton == MouseButton.None) _hover = null; PartPointed?.Invoke(null, default); QueueRedraw(); };
        _layer = new CellLayer(this) { ShowBehindParent = true, MouseFilter = MouseFilterEnum.Ignore };
        _layer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_layer);
        _overview = new Overview(this) { CustomMinimumSize = new Vector2(122, 122) };
        _overview.SetAnchorsPreset(LayoutPreset.BottomRight);
        (_overview.OffsetLeft, _overview.OffsetTop, _overview.OffsetRight, _overview.OffsetBottom) = (-130, -130, -8, -8);
        AddChild(_overview);
        Resized += () => { if (_fitted) Fit(); Redraw(); };
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

    /// <summary>The part under the pointer, for the eyedropper.</summary>
    public DesignCell? CellAtPointer => _hover is { } at && _model.Cells.TryGetValue(at, out var cell) ? cell : null;

    private Rect2 CellRect(GridPos pos) => new(Origin + CellOrigin(pos), new Vector2(CellPx, CellPx));

    /// <summary>Draws everything anew: the cells changed, or where they are.</summary>
    public void Redraw()
    {
        QueueRedraw();
        _layer.QueueRedraw();
        _overview.Visible = Large;
        _overview.Changed();
    }

    // --- The view (#111) ---

    /// <summary>Shows the whole design, or a good part of the grid when there's nothing.</summary>
    public void Fit()
    {
        if (!Large) return;
        _fitted = true;
        var cells = _model.Cells.Keys;
        var (x0, y0, x1, y1) = cells.Any() ? (cells.Min(c => c.X), cells.Min(c => c.Y), cells.Max(c => c.X), cells.Max(c => c.Y)) : (-10, -10, 10, 10);
        var room = new Vector2(Mathf.Max(100, Size.X), Mathf.Max(100, Size.Y - Top));
        _zoomPx = Mathf.Clamp(Mathf.Floor(Mathf.Min(room.X / (x1 - x0 + 5), room.Y / (y1 - y0 + 5))), MinZoomPx, 40);
        _pan = room / 2 - new Vector2((x0 + x1 + 1) / 2f + H, (y0 + y1 + 1) / 2f + H) * _zoomPx;
        Redraw();
    }

    public void ZoomBy(float factor, Vector2? at = null)
    {
        if (!Large) return;
        _fitted = false;
        var point = (at ?? (_hover is { } cell ? CellRect(cell).GetCenter() : Size / 2)) - new Vector2(0, Top);
        var before = _zoomPx;
        _zoomPx = Mathf.Clamp(Mathf.Round(_zoomPx * factor), MinZoomPx, MaxZoomPx);
        if (Mathf.IsEqualApprox(before, _zoomPx)) _zoomPx = Mathf.Clamp(before + Mathf.Sign(factor - 1), MinZoomPx, MaxZoomPx); // always a step
        _pan = point - (point - _pan) * (_zoomPx / before);
        Redraw();
    }

    private void CentreOn(Vector2 cell)
    {
        _fitted = false;
        _pan = new Vector2(Size.X, Size.Y - Top) / 2 - (cell + new Vector2(H, H)) * CellPx;
        Redraw();
    }

    /// <summary>Moves the pointer by keys or a controller; the view follows.</summary>
    public void MoveCursor(int dx, int dy)
    {
        var at = _hover ?? new GridPos(0, 0);
        at = new GridPos(Mathf.Clamp(at.X + dx, -H, H), Mathf.Clamp(at.Y + dy, -H, H));
        _byKeys = true;
        PointAt(at);
        var rect = CellRect(at);
        if (Large && !new Rect2(CellPx, Top + CellPx, Size.X - 2 * CellPx, Size.Y - Top - 2 * CellPx).HasPoint(rect.GetCenter())) CentreOn(new Vector2(at.X + 0.5f, at.Y + 0.5f));
    }

    /// <summary>Like pressing and letting go of a mouse button at the pointer; for tools that drag, the first press starts and the next ends.</summary>
    public void PressByKeys(bool erase)
    {
        if (_hover is not { } at) return;
        _byKeys = true;
        var button = erase ? MouseButton.Right : MouseButton.Left;
        if (_dragButton != MouseButton.None) { Release(at); return; }
        Press(at, button, false);
        if (_dragButton != MouseButton.None && (Tool is GridTool.Draw or GridTool.Fill || Stamp is not null || erase && Tool is GridTool.Draw)) Release(at);
    }

    /// <summary>Drops what's being drawn or pasted. True if there was something.</summary>
    public bool Cancel()
    {
        var any = Stamp is not null || _dragButton != MouseButton.None;
        if (_dragButton != MouseButton.None) _model.EndStroke();
        _dragButton = MouseButton.None;
        _movingSelection = false;
        Stamp = null;
        if (any) SelectionChanged?.Invoke();
        QueueRedraw();
        return any;
    }

    public void SetStamp(IReadOnlyList<DesignCell>? stamp)
    {
        Cancel();
        Stamp = stamp is { Count: > 0 } ? stamp : null;
        SelectionChanged?.Invoke();
        QueueRedraw();
    }

    // --- Input ---

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel when Large:
                ZoomBy(wheel.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f, wheel.Position);
                AcceptEvent();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } button:
                _byKeys = false;
                if (button.Pressed && _dragButton == MouseButton.None) Press(ToCell(button.Position), button.ButtonIndex, button.ShiftPressed);
                else if (!button.Pressed && button.ButtonIndex == _dragButton) Release(ToCell(button.Position));
                AcceptEvent();
                break;

            case InputEventMouseMotion motion when Large && (motion.ButtonMask & MouseButtonMask.Middle) != 0:
                _fitted = false;
                _pan += motion.Relative;
                Redraw();
                break;

            case InputEventMouseMotion motion:
                _byKeys = false;
                PointAt(ToCell(motion.Position));
                break;
        }
    }

    private void PointAt(GridPos cell)
    {
        var was = _hover;
        _hover = _model.InBounds(cell) || _dragButton != MouseButton.None ? cell : null;
        if (_dragButton != MouseButton.None)
        {
            if (Tool == GridTool.Draw && Stamp is null && was is { } last) // no gaps when the mouse is quick
                foreach (var step in BlueprintTools.Line(last, cell)) Apply(step);
        }
        else if (!_byKeys) PartPointed?.Invoke(_model.Cells.TryGetValue(cell, out var under) ? under.PartId : null, CellRect(cell));
        QueueRedraw();
    }

    private bool Refuse()
    {
        if (!_model.IsLocked) return false;
        Refused?.Invoke(_model.LockedReason!);
        return true;
    }

    private void Press(GridPos cell, MouseButton button, bool add)
    {
        PartPointed?.Invoke(null, default);
        _hover = cell;
        if (Refuse()) return;
        if (Stamp is not null)
        {
            if (button == MouseButton.Right) { SetStamp(null); return; }
            var placed = StampAt(cell);
            if (!_model.PlaceAll(placed)) { Refused?.Invoke("It doesn't fit the grid there, or nothing would change."); return; }
            _model.Selection.Clear();
            _model.Selection.UnionWith(placed.Select(c => c.Pos));
            SelectionChanged?.Invoke();
            Edited?.Invoke();
            return;
        }
        if (Tool == GridTool.Select)
        {
            if (button == MouseButton.Right) { _model.Selection.Clear(); SelectionChanged?.Invoke(); Redraw(); return; }
            _movingSelection = !add && _model.Selection.Contains(cell);
            if (!_movingSelection && !add) _model.Selection.Clear();
            (_dragButton, _from) = (button, cell);
            Redraw();
            return;
        }
        if (Tool == GridTool.Fill)
        {
            var region = BlueprintTools.Region(_model.Cells, cell, H);
            if (region is null) { Refused?.Invoke("That space is open to the edge of the grid: nothing to fill."); return; }
            if (button == MouseButton.Right ? _model.Erase(region) : _selectedPart() is { } part && _model.Draw(region, part, _rotation())) Edited?.Invoke();
            Redraw();
            return;
        }
        (_dragButton, _from) = (button, cell);
        if (Tool != GridTool.Draw) { QueueRedraw(); return; }
        _model.BeginStroke();
        Apply(cell);
    }

    private void Release(GridPos cell)
    {
        var button = _dragButton;
        _dragButton = MouseButton.None;
        if (Tool == GridTool.Draw)
        {
            _model.EndStroke();
        }
        else if (Tool == GridTool.Select)
        {
            if (_movingSelection)
            {
                var (dx, dy) = (cell.X - _from.X, cell.Y - _from.Y);
                if ((dx, dy) != (0, 0) && !_model.MoveSelection(dx, dy)) Refused?.Invoke("It doesn't fit the grid there.");
            }
            else _model.Selection.UnionWith(BlueprintTools.Rectangle(_from, cell).Where(_model.Cells.ContainsKey));
            _movingSelection = false;
            SelectionChanged?.Invoke();
        }
        else
        {
            var shape = Shape(_from, cell);
            if (button == MouseButton.Right) _model.Erase(shape);
            else if (_selectedPart() is { } part) _model.Draw(shape, part, _rotation());
        }
        Edited?.Invoke();
        Redraw();
    }

    private IReadOnlyList<GridPos> Shape(GridPos a, GridPos b) => Tool switch
    {
        GridTool.Line => BlueprintTools.Line(a, b),
        GridTool.Rectangle => BlueprintTools.Rectangle(a, b),
        GridTool.Outline => BlueprintTools.Outline(a, b),
        _ => BlueprintTools.Brush(b, BrushSize),
    };

    private void Apply(GridPos cell)
    {
        var where = BlueprintTools.Brush(cell, BrushSize);
        var changed = _dragButton == MouseButton.Right ? _model.Erase(where) : _selectedPart() is { } part && _model.Draw(where, part, _rotation());
        if (!changed) return;
        Edited?.Invoke();
        _layer.QueueRedraw();
    }

    private IReadOnlyList<DesignCell> StampAt(GridPos cell) =>
        Stamp is { } stamp ? stamp.Select(c => c with { Pos = new GridPos(cell.X + c.Pos.X, cell.Y + c.Pos.Y) }).ToList() : Array.Empty<DesignCell>();

    /// <summary>What letting go (or clicking) here would do: parts placed and cells emptied.</summary>
    private (IReadOnlyList<DesignCell> Placed, IReadOnlyList<GridPos> Removed) Pending()
    {
        var nothing = (Array.Empty<DesignCell>(), Array.Empty<GridPos>());
        if (_hover is not { } hover || _model.IsLocked) return nothing;
        if (Stamp is not null) return (StampAt(hover), Array.Empty<GridPos>());
        if (Tool == GridTool.Select)
        {
            if (!_movingSelection || _dragButton == MouseButton.None) return nothing;
            var (dx, dy) = (hover.X - _from.X, hover.Y - _from.Y);
            var selected = _model.Selected;
            return (selected.Select(c => c with { Pos = new GridPos(c.Pos.X + dx, c.Pos.Y + dy) }).ToList(), selected.Select(c => c.Pos).ToList());
        }
        IReadOnlyList<GridPos> shape = Tool == GridTool.Fill ? _model.InBounds(hover) ? BlueprintTools.Region(_model.Cells, hover, H)?.ToList() ?? new List<GridPos>() : new List<GridPos>() : _dragButton != MouseButton.None ? Shape(_from, hover) : BlueprintTools.Brush(hover, Tool == GridTool.Draw ? BrushSize : 1);
        if (_dragButton == MouseButton.Right)
            return (Array.Empty<DesignCell>(), shape.SelectMany(p => BlueprintTools.Mirrored(new DesignCell(p, ""), _model.MirrorLeftRight, _model.MirrorFrontBack)).Select(c => c.Pos).Distinct().ToList());
        return _selectedPart() is { } part
            ? (shape.SelectMany(p => BlueprintTools.Mirrored(new DesignCell(p, part, _rotation()), _model.MirrorLeftRight, _model.MirrorFrontBack)).DistinctBy(c => c.Pos).ToList(), Array.Empty<GridPos>())
            : nothing;
    }

    // --- Drawing: the cells on a layer of their own, drawn only when they change; everything that follows the mouse over them ---

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        DrawSetTransform(Origin);
        if (_model.MirrorLeftRight) DrawLine(new Vector2((H + 0.5f) * CellPx, 0), new Vector2((H + 0.5f) * CellPx, Side), Palette.Warning with { A = 0.5f }, 2);
        if (_model.MirrorFrontBack) DrawLine(new Vector2(0, (H + 0.5f) * CellPx), new Vector2(Side, (H + 0.5f) * CellPx), Palette.Warning with { A = 0.5f }, 2);
        foreach (var pos in _model.Selection) DrawRect(new Rect2(CellOrigin(pos), new Vector2(CellPx, CellPx)), UiTheme.Accent with { A = 0.3f });

        if (_model.Stats is { } stats) // centre of mass (cell centres are at integer coordinates)
        {
            var com = new Vector2((float)((stats.CenterOfMassX + H + 0.5) * CellPx), (float)((stats.CenterOfMassY + H + 0.5) * CellPx));
            DrawCircle(com, 5, ComColor);
            DrawArc(com, 9, 0, Mathf.Tau, 24, ComColor, 1.5f);
        }

        var (placed, removed) = Pending();
        var off = false;
        foreach (var pos in removed) if (_model.Cells.ContainsKey(pos)) Cross(pos);
        foreach (var cell in placed)
        {
            var fits = _model.InBounds(cell.Pos);
            off |= !fits;
            if (fits && !(_dragButton == MouseButton.Left && Tool == GridTool.Draw)) DrawPartSprite(this, cell.PartId, cell.Rotation, CellOrigin(cell.Pos), new Color(1, 1, 1, 0.45f));
            if (CellPx >= 8 || !fits) DrawRect(new Rect2(CellOrigin(cell.Pos), new Vector2(CellPx, CellPx)), fits ? (_model.Cells.ContainsKey(cell.Pos) && Stamp is not null ? Palette.Warning with { A = 0.9f } : HoverPlace) : HoverErase, false, 2);
            if (!fits) Cross(cell.Pos);
        }
        if (_hover is { } hover)
        {
            if (Tool == GridTool.Select && _dragButton != MouseButton.None && !_movingSelection && Stamp is null)
            {
                var (a, b) = (CellOrigin(new GridPos(Math.Min(_from.X, hover.X), Math.Min(_from.Y, hover.Y))), CellOrigin(new GridPos(Math.Max(_from.X, hover.X) + 1, Math.Max(_from.Y, hover.Y) + 1)));
                DrawRect(new Rect2(a, b - a), UiTheme.Accent, false, 2);
            }
            else if (placed.Count == 0) DrawRect(new Rect2(CellOrigin(hover), new Vector2(CellPx, CellPx)), _dragButton == MouseButton.Right ? HoverErase : UiTheme.Accent, false, 2);
            if (placed.Count == 1 && CellPx >= 12) DrawFacing(this, CellOrigin(placed[0].Pos), placed[0].Rotation, HoverPlace); // shows the rotation that will be placed
        }

        // What it would do, before it's done (#112).
        if (placed.Count + removed.Count > 1)
        {
            var preview = _model.Preview(placed, removed);
            var com = new Vector2((float)((preview.ComX + H + 0.5) * CellPx), (float)((preview.ComY + H + 0.5) * CellPx));
            DrawArc(com, 9, 0, Mathf.Tau, 24, Palette.Warning, 2);
            DrawSetTransform(Vector2.Zero);
            var text = off ? "Off the grid" : $"{(placed.Count > 0 ? $"{placed.Count:N0} parts placed" : $"{removed.Count(_model.Cells.ContainsKey):N0} parts removed")}, {preview.DeltaKg / 1000:+#,0.#;-#,0.#;0} t; balance to ({preview.ComX:0.#}, {preview.ComY:0.#})";
            var cost = off || placed.Count == 0 || removed.Count > 0 ? "" : _model.CostText(placed).TrimStart(';', ' '); // a move costs nothing new
            var lines = cost == "" ? new[] { text } : new[] { text, "Cost " + cost };
            for (var i = 0; i < lines.Length; i++)
            {
                var y = Size.Y - 28 * (lines.Length - i) - 2;
                DrawRect(new Rect2(6, y, font.GetStringSize(lines[i], HorizontalAlignment.Left, -1, 14).X + 14, 24), new Color(0.03f, 0.05f, 0.09f, 0.95f));
                DrawString(font, new Vector2(13, y + 17), lines[i], HorizontalAlignment.Left, -1, 14, off ? Palette.Bad : Colors.White);
            }
        }

        // Forward indicator above the grid.
        DrawSetTransform(Vector2.Zero);
        DrawRect(new Rect2(0, 0, Size.X, Top), new Color(0.03f, 0.04f, 0.07f));
        DrawString(font, new Vector2(Size.X / 2 - 36, 13), "^ FORWARD ^", HorizontalAlignment.Left, -1, 12, new Color(1, 1, 1, 0.6f));
        DrawString(font, new Vector2(Size.X - 136, 13), $"placing rotation: {_rotation() * 90} deg", HorizontalAlignment.Left, -1, 12, new Color(1, 1, 1, 0.6f));
    }

    private void Cross(GridPos pos)
    {
        var o = CellOrigin(pos);
        DrawRect(new Rect2(o, new Vector2(CellPx, CellPx)), HoverErase, false, 2);
        DrawLine(o + new Vector2(2, 2), o + new Vector2(CellPx - 2, CellPx - 2), HoverErase, 2); // an × marks erasing, not just the colour (#68)
        DrawLine(o + new Vector2(CellPx - 2, 2), o + new Vector2(2, CellPx - 2), HoverErase, 2);
    }

    private void DrawCells(Control on)
    {
        var font = ThemeDB.FallbackFont;
        on.DrawSetTransform(Origin);
        on.DrawRect(new Rect2(0, 0, Side, Side), Background);
        for (var i = 0; i <= Count; i++)
        {
            var axis = i == H || i == H + 1;
            if (!axis && CellPx < 8 && i % 10 != H % 10) continue;
            on.DrawLine(new Vector2(i * CellPx, 0), new Vector2(i * CellPx, Side), axis ? AxisLine : GridLine);
            on.DrawLine(new Vector2(0, i * CellPx), new Vector2(Side, i * CellPx), axis ? AxisLine : GridLine);
        }
        var view = new Rect2(-Origin - new Vector2(CellPx, CellPx), Size + new Vector2(2, 2) * CellPx);
        foreach (var (pos, cell) in _model.Cells)
        {
            var origin = CellOrigin(pos);
            if (!view.HasPoint(origin)) continue;
            if (!DrawPartSprite(on, cell.PartId, cell.Rotation, origin, Colors.White)) // the part's pixel art (#46)
            {
                on.DrawRect(new Rect2(origin + new Vector2(2, 2), new Vector2(CellPx - 4, CellPx - 4)), _colors.TryGetValue(cell.PartId, out var c) ? c : UnknownPart);
                // Two-letter tag so parts with similar colours stay distinguishable.
                if (CellPx >= 16)
                {
                    var id = cell.PartId;
                    on.DrawString(font, origin + new Vector2(5, CellPx - 9), id.Length >= 2 ? id[..2].ToUpperInvariant() : id.ToUpperInvariant(), HorizontalAlignment.Left, -1, 11, new Color(0, 0, 0, 0.75f));
                    DrawFacing(on, origin, cell.Rotation, new Color(0, 0, 0, 0.6f));
                }
            }
            if (!Problems.ContainsKey(pos)) continue; // not connected (#35), or not allowed; the "!" says so without colour (#68)
            on.DrawRect(new Rect2(origin, new Vector2(CellPx, CellPx)), LooseCell, false, CellPx >= 12 ? 3 : 1);
            if (CellPx >= 12) on.DrawString(font, origin + new Vector2(CellPx - 11, 14), "!", HorizontalAlignment.Left, -1, 14, LooseCell);
        }
    }

    /// <summary>Draws a part's sprite in a cell, turned by its rotation; false if the part has none.</summary>
    private bool DrawPartSprite(Control on, string partId, int rotation, Vector2 origin, Color modulate)
    {
        if (PartSprites.Icon(partId) is not { } texture) return false;
        if (rotation == 0) { on.DrawTextureRect(texture, new Rect2(origin, new Vector2(CellPx, CellPx)), false, modulate); return true; }
        on.DrawSetTransform(Origin + origin + new Vector2(CellPx, CellPx) / 2, rotation * Mathf.Pi / 2, Vector2.One);
        on.DrawTextureRect(texture, new Rect2(-new Vector2(CellPx, CellPx) / 2, new Vector2(CellPx, CellPx)), false, modulate);
        on.DrawSetTransform(Origin);
        return true;
    }

    /// <summary>Small triangle near the cell edge the part faces (thrusters push that way; ports open that way).</summary>
    private void DrawFacing(Control on, Vector2 origin, int rotation, Color color)
    {
        var (fx, fy) = DesignCell.Facing(rotation);
        var dir = new Vector2(fx, fy);
        var side = new Vector2(-fy, fx);
        var centre = origin + new Vector2(CellPx / 2f, CellPx / 2f);
        var tip = centre + dir * (CellPx / 2f - 2);
        on.DrawColoredPolygon(new[] { tip, tip - dir * 6 + side * 5, tip - dir * 6 - side * 5 }, color);
    }

    private sealed partial class CellLayer : Control
    {
        private readonly BlueprintGridCanvas? _grid;
        public CellLayer(BlueprintGridCanvas grid) => _grid = grid;
        public CellLayer() { }
        public override void _Draw() => _grid?.DrawCells(this);
    }

    /// <summary>The whole grid, small (#111): where the design is and what's in view; click to go there.</summary>
    private sealed partial class Overview : Control
    {
        private readonly BlueprintGridCanvas? _grid;
        private ImageTexture? _picture;
        private ShipDesign? _of;

        public Overview(BlueprintGridCanvas grid)
        {
            _grid = grid;
            MouseFilter = MouseFilterEnum.Stop;
            TextureFilter = TextureFilterEnum.Nearest;
            TooltipText = "The whole grid: click to go there";
        }

        public Overview() { }

        public void Changed() => QueueRedraw();

        public override void _GuiInput(InputEvent @event)
        {
            var pressed = @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || @event is InputEventMouseMotion motion && (motion.ButtonMask & MouseButtonMask.Left) != 0;
            if (!pressed || _grid is null || @event is not InputEventMouse mouse) return;
            _grid.CentreOn(mouse.Position / Size * _grid.Count - new Vector2(_grid.H, _grid.H));
            AcceptEvent();
        }

        public override void _Draw()
        {
            if (_grid is not { Large: true } grid) return;
            var design = grid._model.ToDesign();
            if (!ReferenceEquals(design, _of) || _picture is null)
            {
                _of = design;
                var image = Image.CreateEmpty(grid.Count, grid.Count, false, Image.Format.Rgba8);
                foreach (var cell in design.Cells)
                    if (grid._model.InBounds(cell.Pos)) image.SetPixel(cell.Pos.X + grid.H, cell.Pos.Y + grid.H, grid._colors.GetValueOrDefault(cell.PartId, UnknownPart));
                _picture = ImageTexture.CreateFromImage(image);
            }
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.02f, 0.03f, 0.05f, 0.95f));
            DrawTextureRect(_picture, new Rect2(Vector2.Zero, Size), false);
            var scale = Size.X / grid.Side;
            var seen = new Rect2((new Vector2(0, Top) - grid.Origin) * scale, new Vector2(grid.Size.X, grid.Size.Y - Top) * scale).Intersection(new Rect2(Vector2.Zero, Size));
            if (seen.HasArea()) DrawRect(seen, UiTheme.Accent, false, 1);
            DrawRect(new Rect2(Vector2.Zero, Size), UiTheme.Edge, false, 1);
        }
    }
}
