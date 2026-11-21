using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Full-screen blueprint editor panel: part palette (keys 1–9), the grid, live stats, errors and
/// warnings, name, and save/load/new/undo. A view over <see cref="BlueprintEditor"/>. It edits
/// designs only and never changes a flying craft (building one is Phase 2 fabrication).
/// </summary>
public partial class BlueprintEditorView : Control
{
    private static readonly Color PanelColor = new(0.02f, 0.02f, 0.05f, 0.92f);
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    private static readonly Color WarningColor = new(1f, 0.85f, 0.4f);
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);

    private readonly BlueprintEditor _model;
    private readonly PartCatalog _catalog;
    private readonly IReadOnlyDictionary<string, Color> _colors;
    private readonly List<PartDefinition> _palette;
    private readonly List<Button> _paletteButtons = new();
    private readonly Func<ShipDesign?> _currentShipDesign;

    private BlueprintGridCanvas _grid = null!;
    private LineEdit _name = null!;
    private Label _stats = null!;
    private Label _bill = null!;
    private Label _problems = null!;
    private Label _status = null!;
    private OptionButton _loadList = null!;
    private List<BlueprintEntry> _entries = new();
    private string? _selectedPart;
    private string? _loadedFromId; // user design this edit started from (overwriting it needs no confirm)
    private string? _pendingConfirm; // second click on the same action confirms it

    public event Action? CloseRequested;

    public BlueprintEditorView(PartCatalog catalog, IReadOnlyDictionary<string, Color> colors, Func<ShipDesign?> currentShipDesign)
    {
        _catalog = catalog;
        _colors = colors;
        _currentShipDesign = currentShipDesign;
        _model = new BlueprintEditor(catalog);
        // Stable palette order: the order parts appear in parts.json would be nicer, but the catalog is a set.
        _palette = new[] { "hull", "cockpit", "thruster", "battery", "cargo", "drill" }
            .Where(id => catalog.TryGet(id, out _)).Select(catalog.Get)
            .Concat(catalog.Parts.Where(p => p.Id is not ("hull" or "cockpit" or "thruster" or "battery" or "cargo" or "drill")).OrderBy(p => p.Id))
            .ToList();
        _selectedPart = _palette.FirstOrDefault()?.Id;
        Build();
    }

    public BlueprintEditorView() : this(new PartCatalog(Array.Empty<PartDefinition>(), new MaterialCatalog(Array.Empty<MaterialDefinition>())), new Dictionary<string, Color>(), () => null) { }

    public BlueprintEditor Model => _model;

    /// <summary>Shows the editor. With no unsaved edits it starts from the player's current ship.</summary>
    public void Open()
    {
        if (!_model.IsDirty && _model.Cells.Count == 0 && _currentShipDesign() is { } design)
        {
            _model.Load(design);
            _loadedFromId = null;
        }
        RefreshLoadList();
        Visible = true;
        Refresh();
    }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop; // the map behind doesn't get clicks/wheel while open

        var bg = new ColorRect { Color = PanelColor, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 24);
        AddChild(margin);

        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 28);
        margin.AddChild(columns);

        // --- left: palette ---
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        columns.AddChild(left);
        left.AddChild(Heading("BLUEPRINT EDITOR"));
        left.AddChild(new Label { Text = "Parts (keys 1-9)" });
        var group = new ButtonGroup();
        for (var i = 0; i < _palette.Count; i++)
        {
            var part = _palette[i];
            var row = new HBoxContainer();
            row.AddChild(new ColorRect
            {
                Color = _colors.TryGetValue(part.Id, out var c) ? c : Colors.Magenta,
                CustomMinimumSize = new Vector2(18, 18),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            });
            var button = new Button
            {
                Text = $"{i + 1}  {part.Name}  {part.MassKg / 1000:0.#} t{PartSummary(part)}",
                ToggleMode = true,
                ButtonGroup = group,
                ButtonPressed = part.Id == _selectedPart,
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                FocusMode = FocusModeEnum.None,
            };
            var id = part.Id;
            button.Pressed += () => _selectedPart = id;
            _paletteButtons.Add(button);
            row.AddChild(button);
            left.AddChild(row);
        }
        left.AddChild(new Label
        {
            Text = "\nLeft drag: place\nRight drag: erase\nCtrl+Z: undo   Ctrl+S: save\nB or Esc: close editor",
            Modulate = new Color(1, 1, 1, 0.7f),
        });

        // --- centre: grid ---
        var centre = new VBoxContainer();
        columns.AddChild(centre);
        centre.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) }); // room for the FORWARD label
        _grid = new BlueprintGridCanvas(_model, _colors, () => _selectedPart);
        _grid.Edited += () => { _pendingConfirm = null; Refresh(); };
        centre.AddChild(_grid);

        // --- right: name, stats, problems, file actions ---
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 10);
        columns.AddChild(right);

        right.AddChild(new Label { Text = "Name" });
        _name = new LineEdit { PlaceholderText = "Design name" };
        _name.TextChanged += text => { _model.Name = text; _pendingConfirm = null; Refresh(updateName: false); };
        right.AddChild(_name);

        _stats = new Label();
        right.AddChild(_stats);
        _bill = new Label();
        right.AddChild(_bill);
        _problems = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(330, 0) };
        right.AddChild(_problems);

        var actions = new HBoxContainer();
        actions.AddChild(ActionButton("Save", Save));
        actions.AddChild(ActionButton("Undo", () => { if (_model.Undo()) Refresh(); }));
        actions.AddChild(ActionButton("New", NewDesign));
        actions.AddChild(ActionButton("Close", () => CloseRequested?.Invoke()));
        right.AddChild(actions);

        var loadRow = new HBoxContainer();
        _loadList = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        loadRow.AddChild(_loadList);
        loadRow.AddChild(ActionButton("Load", LoadSelected));
        right.AddChild(loadRow);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(330, 0) };
        right.AddChild(_status);
        right.AddChild(new Label
        {
            Text = "Designs are saved to your user folder. Editing a design does not change the ship you are flying.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(330, 0),
            Modulate = new Color(1, 1, 1, 0.55f),
        });
    }

    private static string PartSummary(PartDefinition p) =>
        p.ThrustN > 0 ? $", {p.ThrustN / 1000:0} kN"
        : p.CargoCapacityKg > 0 ? $", {p.CargoCapacityKg / 1000:0} t cargo"
        : p.EnergyCapacityKJ > 0 ? $", {p.EnergyCapacityKJ / 1000:0} MJ"
        : p.MiningRateKgPerS > 0 ? $", {p.MiningRateKgPerS:0} kg/s"
        : p.IsControlSource ? ", control" : "";

    private static Label Heading(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 20);
        return label;
    }

    private static Button ActionButton(string text, Action onPressed)
    {
        var button = new Button { Text = text, FocusMode = FocusModeEnum.None };
        button.Pressed += onPressed;
        return button;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (key.CtrlPressed && key.PhysicalKeycode == Key.Z) { if (_model.Undo()) Refresh(); }
        else if (key.CtrlPressed && key.PhysicalKeycode == Key.S) Save();
        else if (key.PhysicalKeycode is >= Key.Key1 and <= Key.Key9 && !key.CtrlPressed)
        {
            var index = (int)(key.PhysicalKeycode - Key.Key1);
            if (index >= _palette.Count) return;
            _selectedPart = _palette[index].Id;
            _paletteButtons[index].ButtonPressed = true;
        }
        else return;

        GetViewport().SetInputAsHandled();
    }

    private void Refresh(bool updateName = true)
    {
        if (updateName && _name.Text != _model.Name) _name.Text = _model.Name;

        var s = _model.Stats;
        _stats.Text = s is null
            ? "Empty design."
            : $"Id: {_model.Id}{(_model.IsDirty ? "   (unsaved)" : "")}\n"
              + $"Cells {s.CellCount}    Mass {s.TotalMassKg / 1000:0.0} t\n"
              + $"Thrust {s.TotalThrustN / 1000:0} kN    Accel {s.MaxAccelerationMps2:0.0} m/s²\n"
              + $"Cargo {s.CargoCapacityKg / 1000:0} t    Battery {s.EnergyCapacityKJ / 1000:0} MJ\n"
              + $"Collision radius {s.BoundingRadiusM:0.0} m";

        _bill.Text = BillText();

        var errors = _model.Errors;
        var warnings = _model.Warnings;
        if (errors.Count == 0 && warnings.Count == 0)
        {
            _problems.Text = "Valid: this design can fly.";
            _problems.AddThemeColorOverride("font_color", OkColor);
        }
        else
        {
            _problems.Text = string.Join("\n", errors.Select(e => "Error: " + e).Concat(warnings.Select(w => "Warning: " + w)));
            _problems.AddThemeColorOverride("font_color", errors.Count > 0 ? ErrorColor : WarningColor);
        }
        _grid.QueueRedraw();
    }

    /// <summary>Bill of materials in material-file order, e.g. "Metal 12.6 t".</summary>
    private string BillText()
    {
        var bill = _model.BillOfMaterials;
        if (bill.TotalKg == 0) return "";
        var materials = _catalog.Materials;
        var lines = bill.Kg.OrderBy(kv => materials.OrderOf(kv.Key))
            .Select(kv => $"  {(materials.Contains(kv.Key) ? materials.Get(kv.Key).Name : kv.Key)}  {kv.Value / 1000:0.0##} t");
        return "Bill of materials:\n" + string.Join("\n", lines);
    }

    private void SetStatus(string text, Color? color = null)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color ?? new Color(1, 1, 1));
    }

    /// <summary>Returns true if the action may run now; otherwise asks for a second click.</summary>
    private bool Confirmed(string action, string question)
    {
        if (_pendingConfirm == action) { _pendingConfirm = null; return true; }
        _pendingConfirm = action;
        SetStatus(question, WarningColor);
        return false;
    }

    private void Save()
    {
        var id = _model.Id;
        if (string.IsNullOrWhiteSpace(_model.Name)) { SetStatus("Give the design a name first.", ErrorColor); return; }
        if (BlueprintLibrary.IsBuiltInId(id))
        {
            SetStatus($"'{id}' is a built-in design. Pick another name to save your own copy.", ErrorColor);
            return;
        }
        if (BlueprintLibrary.UserDesignExists(id) && id != _loadedFromId
            && !Confirmed("save:" + id, $"A design '{id}' already exists. Press Save again to overwrite it."))
            return;

        try
        {
            var path = BlueprintLibrary.Save(_model.ToDesign());
            _model.MarkSaved();
            _loadedFromId = id;
            var errors = _model.Errors.Count;
            SetStatus(errors == 0 ? $"Saved {path}" : $"Saved {path} - it still has {errors} error(s), so it can't fly.",
                errors == 0 ? OkColor : WarningColor);
            RefreshLoadList(selectId: id);
        }
        catch (Exception e)
        {
            SetStatus($"Save failed: {e.Message}", ErrorColor);
        }
        Refresh();
    }

    private void NewDesign()
    {
        if (_model.IsDirty && !Confirmed("new", "Unsaved changes. Press New again to discard them.")) return;
        _model.New();
        _loadedFromId = null;
        SetStatus("New empty design.");
        Refresh();
    }

    private void LoadSelected()
    {
        var index = _loadList.Selected;
        if (index < 0) return;
        if (_model.IsDirty && !Confirmed("load:" + index, "Unsaved changes. Press Load again to discard them.")) return;

        try
        {
            if (index == 0)
            {
                if (_currentShipDesign() is not { } current) { SetStatus("No ship to copy from.", ErrorColor); return; }
                _model.Load(current);
                _loadedFromId = null;
                SetStatus($"Loaded the design of the ship you are flying ('{current.Id}').");
            }
            else
            {
                var entry = _entries[index - 1];
                _model.Load(BlueprintLibrary.Load(entry));
                _loadedFromId = entry.IsBuiltIn ? null : entry.Id;
                SetStatus($"Loaded {entry.Label}.");
            }
        }
        catch (Exception e)
        {
            SetStatus($"Load failed: {e.Message}", ErrorColor);
        }
        Refresh();
    }

    private void RefreshLoadList(string? selectId = null)
    {
        _entries = BlueprintLibrary.List();
        _loadList.Clear();
        _loadList.AddItem("My current ship");
        foreach (var entry in _entries) _loadList.AddItem(entry.Label);
        var match = selectId is null ? -1 : _entries.FindIndex(e => e.Id == selectId && !e.IsBuiltIn);
        _loadList.Select(match >= 0 ? match + 1 : 0);
    }
}
