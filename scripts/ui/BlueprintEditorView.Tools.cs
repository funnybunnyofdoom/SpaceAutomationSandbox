using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The editor's toolbar and keys. Undo, redo and mirroring are for everyone; the rest appears with large-ship building (#111, #112) and
/// isn't shown at all before. Every key can be changed in Settings → Controls.
/// </summary>
public partial class BlueprintEditorView
{
    private static readonly (GridTool Tool, string Icon, string Name, string Action)[] Tools =
    {
        (GridTool.Draw, "tool_draw", "Draw", InputActions.EditDraw), (GridTool.Line, "tool_line", "Line", InputActions.EditLine),
        (GridTool.Rectangle, "tool_rect", "Filled rectangle", InputActions.EditRectangle), (GridTool.Outline, "tool_outline", "Rectangle outline", InputActions.EditOutline),
        (GridTool.Fill, "tool_fill", "Fill an enclosed space, or a region of one kind of part", InputActions.EditFill),
        (GridTool.Select, "tool_select", "Select: drag a box, drag the selection to move it, Shift adds", InputActions.EditSelect),
    };

    private readonly List<(Button Button, string Text, string? Action)> _tips = new();
    private readonly List<Button> _toolButtons = new(), _selectionButtons = new();
    private Button _undo = null!, _redo = null!, _mirrorH = null!, _mirrorV = null!;
    private HFlowContainer _toolbar = null!;
    private int _everyones; // the first tools in the bar are for everyone
    private bool _large, _usingButtons;
    private OptionButton _brush = null!, _replaceWith = null!;
    private IReadOnlyList<DesignCell> _clipboard = Array.Empty<DesignCell>();
    private bool _refreshDue;
    private bool _introduced;

    /// <summary>Large-ship building is open in this game.</summary>
    public Func<bool> LargeOpen { get; set; } = () => false;

    private void BuildToolbar(VBoxContainer centre)
    {
        var bar = _toolbar = new HFlowContainer { CustomMinimumSize = new Vector2(300, 0) };
        bar.AddThemeConstantOverride("h_separation", 2);
        centre.AddChild(bar);
        _undo = Tool(bar, "undo", "Undo", InputActions.EditUndo, Undo);
        _redo = Tool(bar, "redo", "Redo", InputActions.EditRedo, Redo);
        _mirrorH = Tool(bar, "mirror_h", "Mirror left to right while drawing", InputActions.EditMirror, () => ToggleMirror(true), toggle: true);
        _mirrorV = Tool(bar, "mirror_v", "Mirror front to back while drawing", InputActions.EditMirrorFront, () => ToggleMirror(false), toggle: true);

        _everyones = bar.GetChildCount();
        foreach (var (tool, icon, name, action) in Tools) _toolButtons.Add(Tool(_toolbar, icon, name, action, () => SetTool(tool), toggle: true));
        _brush = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Brush size" };
        foreach (var size in new[] { 1, 3, 5 }) _brush.AddItem($"{size}", size);
        _brush.ItemSelected += i => { _grid.BrushSize = _brush.GetItemId((int)i); _grid.QueueRedraw(); };
        _toolbar.AddChild(_brush);
        Tool(_toolbar, "pick", "Pick the part under the pointer", InputActions.EditPick, PickPart);
        Tool(_toolbar, "fit", "Show the whole design", InputActions.EditFit, () => _grid.Fit());
        _toolbar.AddChild(new VSeparator());
        Selected("copy", "Copy the selection", InputActions.EditCopy, Copy);
        Selected("cut", "Cut the selection", InputActions.EditCut, Cut);
        Tool(_toolbar, "paste", "Paste at the pointer: click to place, right click to stop", InputActions.EditPaste, () => _grid.SetStamp(_clipboard));
        Selected("turn", "Turn the selection a quarter", InputActions.EditTurn, () => Reshape(BlueprintTools.Turned));
        Selected("mirror_h", "Mirror the selection left to right", InputActions.EditFlip, () => Reshape(c => BlueprintTools.Flipped(c, true)));
        Selected("mirror_v", "Mirror the selection front to back", InputActions.EditFlipFront, () => Reshape(c => BlueprintTools.Flipped(c, false)));
        Selected("trash", "Delete the selection", InputActions.EditDelete, Delete);
        Tool(_toolbar, "swarm", "Select every part like the one under the pointer, or the chosen one", InputActions.EditSelectKind, SelectKind);
        _replaceWith = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Replace the chosen part with another: in the selection, or everywhere", CustomMinimumSize = new Vector2(44, 0), ClipText = true, Icon = UiIcons.Get("replace") };
        _replaceWith.GetPopup().AboutToPopup += FillReplace;
        _replaceWith.ItemSelected += Replace;
        _toolbar.AddChild(_replaceWith);

        _grid.SelectionChanged += RefreshTools;
        _grid.Refused += why => SetStatus(why, WarningColor);
    }

    private Button Tool(Container bar, string icon, string text, string? action, Action press, bool toggle = false)
    {
        var button = new Button
        {
            FocusMode = FocusModeEnum.None, Icon = UiIcons.Get(icon), ToggleMode = toggle, CustomMinimumSize = new Vector2(27, 28), IconAlignment = HorizontalAlignment.Center,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        button.Pressed += press;
        bar.AddChild(button);
        _tips.Add((button, text, action));
        return button;
    }

    private void Selected(string icon, string text, string action, Action press) => _selectionButtons.Add(Tool(_toolbar, icon, text, action, press));

    /// <summary>The grid and tools the game allows now; a short introduction the first time they're larger.</summary>
    private void RefreshLarge()
    {
        var open = LargeOpen();
        var extent = open ? BlueprintEditor.LargeHalfExtent : BlueprintEditor.HalfExtent;
        _large = open;
        for (var i = _everyones; i < _toolbar.GetChildCount(); i++) _toolbar.GetChild<Control>(i).Visible = open;
        if (!open && _grid.Tool != GridTool.Draw) SetTool(GridTool.Draw);
        if (!open) { _grid.BrushSize = 1; _brush.Select(0); _grid.SetStamp(null); _model.Selection.Clear(); }
        if (_model.Extent != extent)
        {
            _model.Extent = extent;
            if (open) _grid.Fit();
        }
        if (open && !_introduced)
        {
            _introduced = true;
            SetStatus($"Large-ship building is open: a {2 * extent + 1} x {2 * extent + 1} grid. Wheel zooms, middle drag moves, and the tools above the grid draw shapes and move whole groups of parts.", OkColor);
        }
        foreach (var (button, text, action) in _tips) button.TooltipText = action is null ? text : $"{text}  ({InputActions.Key1(action)})";
        RefreshTools();
    }

    private void RefreshTools()
    {
        _undo.Disabled = !_model.CanUndo;
        _redo.Disabled = !_model.CanRedo;
        _mirrorH.SetPressedNoSignal(_model.MirrorLeftRight);
        _mirrorV.SetPressedNoSignal(_model.MirrorFrontBack);
        for (var i = 0; i < _toolButtons.Count; i++) _toolButtons[i].SetPressedNoSignal(Tools[i].Tool == _grid.Tool && _grid.Stamp is null);
        foreach (var button in _selectionButtons) button.Disabled = _model.Selection.Count == 0;
        _grid.Redraw();
    }

    private void SetTool(GridTool tool)
    {
        _grid.Cancel();
        _grid.Tool = tool;
        RefreshTools();
    }

    private void ToggleMirror(bool leftRight)
    {
        if (leftRight) _model.MirrorLeftRight = !_model.MirrorLeftRight;
        else _model.MirrorFrontBack = !_model.MirrorFrontBack;
        RefreshTools();
    }

    private void Undo() { _grid.Cancel(); if (_model.Undo()) Refresh(); }

    private void Redo() { _grid.Cancel(); if (_model.Redo()) Refresh(); }

    private void Copy()
    {
        if (_model.Selection.Count == 0) return;
        _clipboard = BlueprintTools.ToCorner(_model.Selected);
        SetStatus($"{_clipboard.Count} parts copied.");
    }

    private void Cut()
    {
        Copy();
        Delete();
    }

    private void Delete()
    {
        if (_model.Selection.Count > 0 && _model.ReplaceSelection(Array.Empty<DesignCell>())) Refresh();
    }

    private void Reshape(Func<IEnumerable<DesignCell>, IReadOnlyList<DesignCell>> how)
    {
        if (_grid.Stamp is { } stamp) { _grid.SetStamp(how(stamp)); return; }
        if (_model.Selection.Count == 0) return;
        if (!_model.ReshapeSelection(how)) SetStatus("It doesn't fit the grid that way.", WarningColor);
        Refresh();
    }

    private void PickPart()
    {
        if (_grid.CellAtPointer is not { } cell) return;
        _rotation = cell.Rotation;
        Choose(cell.PartId);
        _grid.QueueRedraw();
    }

    private void Choose(string partId)
    {
        var i = _palette.FindIndex(p => p.Id == partId);
        if (i < 0) return;
        _selectedPart = partId;
        _paletteButtons[i].ButtonPressed = true;
    }

    /// <summary>Steps through the parts in view (controller shoulder buttons, page keys).</summary>
    private void StepPart(int step)
    {
        if (_shown.Count == 0) return;
        var at = _shown.FindIndex(i => _palette[i].Id == _selectedPart);
        Choose(_palette[_shown[((at < 0 ? 0 : at + step) % _shown.Count + _shown.Count) % _shown.Count]].Id);
        SetStatus($"Part: {(_catalog.TryGet(_selectedPart!, out var part) ? part!.Name : _selectedPart)}");
    }

    private void SelectKind()
    {
        var kind = _grid.CellAtPointer?.PartId ?? _selectedPart;
        _model.Selection.Clear();
        _model.Selection.UnionWith(_model.Cells.Values.Where(c => c.PartId == kind).Select(c => c.Pos));
        SetTool(GridTool.Select);
        SetStatus($"{_model.Selection.Count} selected.");
    }

    /// <summary>The kinds in the design (or the selection) that the chosen part can take the place of.</summary>
    private void FillReplace()
    {
        _replaceWith.Clear();
        var cells = _model.Selection.Count > 0 ? _model.Selected : _model.Cells.Values;
        foreach (var kind in cells.GroupBy(c => c.PartId).Where(g => g.Key != _selectedPart).OrderByDescending(g => g.Count()))
        {
            _replaceWith.AddIconItem(PartSprites.Icon(kind.Key), $"{kind.Count()} × {Name(kind.Key)} with {Name(_selectedPart ?? "")}");
            _replaceWith.SetItemMetadata(_replaceWith.ItemCount - 1, kind.Key);
        }
        if (_replaceWith.ItemCount == 0) { _replaceWith.AddItem("Nothing to replace with the chosen part"); _replaceWith.SetItemDisabled(0, true); }
        string Name(string id) => _catalog.TryGet(id, out var p) ? p!.Name : id;
    }

    private void Replace(long index)
    {
        var from = _replaceWith.GetItemMetadata((int)index);
        _replaceWith.Select(-1);
        if (from.VariantType != Variant.Type.String || _selectedPart is null) return;
        var count = _model.ReplaceParts(from.AsString(), _selectedPart);
        Refresh();
        SetStatus($"{count} parts replaced.");
    }

    /// <summary>Lets the keys or a controller step through the editor's buttons, and back to the grid.</summary>
    private void UseButtons(bool use)
    {
        _usingButtons = use;
        foreach (var node in FindChildren("*", "BaseButton", true, false)) ((BaseButton)node).FocusMode = use ? FocusModeEnum.All : FocusModeEnum.None;
        if (use) _mirrorH.GrabFocus();
        SetStatus(use ? "Using the buttons; the same key again goes back to the grid." : "");
    }

    /// <summary>The editor's keys; true when one was used. Actions that share a key differ by Ctrl or Shift, so they must match exactly.</summary>
    private bool ToolKey(InputEvent @event)
    {
        bool Is(string action) => @event.IsActionPressed(action, allowEcho: false, exactMatch: true);
        if (Is(InputActions.EditUndo)) Undo();
        else if (Is(InputActions.EditRedo)) Redo();
        else if (Is(InputActions.EditSave)) Save();
        else if (Is(InputActions.EditRotate))
        {
            if (_grid.Stamp is not null) Reshape(BlueprintTools.Turned);
            else { _rotation = (_rotation + 1) % 4; _grid.QueueRedraw(); }
        }
        else if (Is(InputActions.EditMirror)) ToggleMirror(true);
        else if (Is(InputActions.EditMirrorFront)) ToggleMirror(false);
        else if (Is(InputActions.EditNextPart)) StepPart(1);
        else if (Is(InputActions.EditPreviousPart)) StepPart(-1);
        else if (Is(InputActions.EditErase)) _grid.PressByKeys(erase: true);
        // Moving the pointer and pressing, by keys or a controller (held arrows repeat).
        else if (@event.IsActionPressed("ui_left", true)) _grid.MoveCursor(-1, 0);
        else if (@event.IsActionPressed("ui_right", true)) _grid.MoveCursor(1, 0);
        else if (@event.IsActionPressed("ui_up", true)) _grid.MoveCursor(0, -1);
        else if (@event.IsActionPressed("ui_down", true)) _grid.MoveCursor(0, 1);
        else if (@event.IsActionPressed("ui_accept")) _grid.PressByKeys(erase: false);
        else if (Is(InputActions.PanelFocus) || _usingButtons && @event.IsActionPressed("ui_cancel")) UseButtons(!_usingButtons);
        else if (@event.IsActionPressed("ui_cancel") && _grid.Cancel()) { }
        else if (!_large) return false;
        else if (Is(InputActions.EditCopy)) Copy();
        else if (Is(InputActions.EditCut)) Cut();
        else if (Is(InputActions.EditPaste)) _grid.SetStamp(_clipboard);
        else if (Is(InputActions.EditSelectAll))
        {
            _model.Selection.UnionWith(_model.Cells.Keys);
            SetTool(GridTool.Select);
        }
        else if (Is(InputActions.EditTurn)) Reshape(BlueprintTools.Turned);
        else if (Is(InputActions.EditFlip)) Reshape(c => BlueprintTools.Flipped(c, true));
        else if (Is(InputActions.EditFlipFront)) Reshape(c => BlueprintTools.Flipped(c, false));
        else if (Is(InputActions.EditDelete)) Delete();
        else if (Is(InputActions.EditSelectKind)) SelectKind();
        else if (Is(InputActions.EditPick)) PickPart();
        else if (Is(InputActions.EditFit)) _grid.Fit();
        else if (Is(InputActions.EditZoomIn)) _grid.ZoomBy(1.25f);
        else if (Is(InputActions.EditZoomOut)) _grid.ZoomBy(0.8f);
        else if (Is(InputActions.EditNextTool)) SetTool((GridTool)(((int)_grid.Tool + 1) % Tools.Length));
        else if (Tools.FirstOrDefault(t => Is(t.Action)) is { Name: not null } tool) SetTool(tool.Tool);
        else return false;
        return true;
    }
}
