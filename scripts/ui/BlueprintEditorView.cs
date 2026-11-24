using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Full-screen blueprint editor panel: part palette (keys 1–9), the grid, live stats, errors and
/// warnings, name, and save/load/new/undo. A view over <see cref="BlueprintEditor"/>. It edits
/// designs only and never changes a flying craft (building one is Phase 2 fabrication).
/// </summary>
public partial class BlueprintEditorView : Control
{
    private static readonly Color PanelColor = new(0.02f, 0.02f, 0.05f, 0.75f);
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
    private int _rotation; // quarter turns clockwise applied to newly placed parts (R)
    private string? _loadedFromId; // user design this edit started from (overwriting it needs no confirm)
    private string? _pendingConfirm; // second click on the same action confirms it

    public event Action? CloseRequested;

    /// <summary>The tech a part still needs (#37), or null if it can be built. Locked parts can still be designed with.</summary>
    public Func<string, string?> LockedBy { get; set; } = _ => null;

    public BlueprintEditorView(PartCatalog catalog, IReadOnlyDictionary<string, Color> colors, Func<ShipDesign?> currentShipDesign)
    {
        _catalog = catalog;
        _colors = colors;
        _currentShipDesign = currentShipDesign;
        _model = new BlueprintEditor(catalog);
        _palette = catalog.Parts.ToList(); // the order of data/parts/parts.json
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
        // Research changes over time: mark the parts that can't be built yet (they can still be planned with).
        for (var i = 0; i < _palette.Count && i < _paletteButtons.Count; i++)
        {
            var part = _palette[i];
            var tech = LockedBy(part.Id);
            _paletteButtons[i].Text = $"{KeyLabel(i)}  {part.Name}  {part.MassKg / 1000:0.#} t{PartSummary(part)}" + (tech is null ? "" : $"  [needs {tech}]");
            _paletteButtons[i].Modulate = tech is null ? Colors.White : new Color(1, 1, 1, 0.55f);
        }
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
        left.AddChild(new Label { Text = "Parts (1-9, 0; Shift for 11+)" });
        var paletteScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        left.AddChild(paletteScroll);
        var paletteList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        paletteScroll.AddChild(paletteList);
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
                Text = $"{KeyLabel(i)}  {part.Name}  {part.MassKg / 1000:0.#} t{PartSummary(part)}",
                ToggleMode = true,
                ButtonGroup = group,
                ButtonPressed = part.Id == _selectedPart,
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                FocusMode = FocusModeEnum.None,
                TooltipText = PartTooltip(part), // #65
                Icon = PartSprites.Icon(part.Id), // its pixel art (#46)
            };
            var id = part.Id;
            button.Pressed += () => _selectedPart = id;
            _paletteButtons.Add(button);
            row.AddChild(button);
            paletteList.AddChild(row);
        }
        left.AddChild(new Label
        {
            Text = "\nLeft drag: place\nRight drag: erase\nR: rotate the part to place\nCtrl+Z: undo   Ctrl+S: save\nB or Esc: close editor",
            Modulate = new Color(1, 1, 1, 0.7f),
        });

        // --- centre: grid ---
        var centre = new VBoxContainer();
        columns.AddChild(centre);
        centre.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) }); // room for the FORWARD label
        _grid = new BlueprintGridCanvas(_model, _colors, () => _selectedPart, () => _rotation);
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

    /// <summary>Key hint for palette slot <paramref name="index"/>: 1–9, 0, then ⇧1–⇧0, then none.</summary>
    private static string KeyLabel(int index) => index switch
    {
        < 9 => $"{index + 1}",
        9 => "0",
        < 19 => $"⇧{index - 9}",
        19 => "⇧0",
        _ => " ",
    };

    private static string PartSummary(PartDefinition p) =>
        p.ThrustN > 0 ? $", {p.ThrustN / 1000:0} kN"
        : p.CargoCapacityKg > 0 ? $", {p.CargoCapacityKg / 1000:0} t cargo"
        : p.EnergyCapacityKJ > 0 ? $", {p.EnergyCapacityKJ / 1000:0} MJ"
        : p.MiningRateKgPerS > 0 ? $", {p.MiningRateKgPerS:0} kg/s"
        : p.PowerGenerationKW > 0 ? $", +{p.PowerGenerationKW:0} kW"
        : p.EjectionRateKgPerS > 0 ? $", ejects {p.EjectionRateKgPerS:0} kg/s"
        : p.CrewCapacity > 0 ? $", {p.CrewCapacity} crew"
        : p.RobotCount > 0 ? $", {p.RobotCount} robots, {p.PowerUseKW:0} kW"
        : p.IsWarpGate ? ", warp gate"
        : p.ResearchRatePerS > 0 ? $", {p.ResearchRatePerS * 60:0.#} research/min"
        : p.FoodRateKgPerS > 0 ? $", {p.FoodRateKgPerS * 3600:0} kg food/h"
        : p.IsControlSource ? ", control" : "";

    /// <summary>Everything a part does, for its palette tooltip (#65).</summary>
    private static string PartTooltip(PartDefinition p)
    {
        var lines = new System.Collections.Generic.List<string> { $"{p.Name}: {p.MassKg / 1000:0.##} t" };
        if (p.IsControlSource) lines.Add("Control: a craft needs one to be flown or automated.");
        if (p.ThrustN > 0) lines.Add($"Thrust {p.ThrustN / 1000:0} kN, pushing the way it faces (place it facing back to go forward).");
        if (p.CargoCapacityKg > 0) lines.Add($"Cargo {p.CargoCapacityKg / 1000:0.#} t.");
        if (p.EnergyCapacityKJ > 0) lines.Add($"Stores {p.EnergyCapacityKJ / 1000:0.#} MJ of power.");
        if (p.PowerGenerationKW > 0) lines.Add($"Makes {p.PowerGenerationKW:0} kW of power.");
        if (p.PowerUseKW > 0) lines.Add($"Uses {p.PowerUseKW:0} kW while working.");
        if (p.MiningRateKgPerS > 0) lines.Add($"Drill: mines {p.MiningRateKgPerS:0.#} kg/s while landed.");
        if (p.RefiningRateKgPerS > 0) lines.Add($"Refinery: {p.RefiningRateKgPerS:0.#} kg/s of ore into materials.");
        if (p.FabricationRateKgPerS > 0) lines.Add($"Fabricator: builds ships at {p.FabricationRateKgPerS:0.#} kg/s.");
        if (p.EjectionRateKgPerS > 0) lines.Add($"Throws out slag at {p.EjectionRateKgPerS:0.#} kg/s.");
        if (p.CrewCapacity > 0) lines.Add($"Houses {p.CrewCapacity} crew (they need food and water).");
        if (p.RobotCount > 0) lines.Add($"{p.RobotCount} robots: work like crew, need only power.");
        if (p.FoodRateKgPerS > 0) lines.Add($"Farm: {p.FoodRateKgPerS * 3600:0} kg of food per hour.");
        if (p.ResearchRatePerS > 0) lines.Add($"Lab: {p.ResearchRatePerS * 60:0.#} research points per minute.");
        if (p.IsDockingPort) lines.Add("Docking port: other craft dock here.");
        if (p.IsAnchorClamp) lines.Add("Anchor clamp: holds the craft to a surface.");
        if (p.IsFoundation) lines.Add("Foundation: the craft stays fixed where it's deployed.");
        if (p.IsWarpGate) lines.Add("Warp gate: jumps to another star system.");
        if (p.MaterialsKg is { Count: > 0 } materials)
            lines.Add("Made of: " + string.Join(", ", materials.Select(m => $"{m.Value / 1000:0.##} t {m.Key}")));
        return string.Join("\n", lines);
    }

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
        else if (key.PhysicalKeycode == Key.R && !key.CtrlPressed) { _rotation = (_rotation + 1) % 4; _grid.QueueRedraw(); }
        else if (key.CtrlPressed && key.PhysicalKeycode == Key.S) Save();
        else if (key.PhysicalKeycode is >= Key.Key0 and <= Key.Key9 && !key.CtrlPressed)
        {
            // 1..9 then 0 pick parts 1–10; with Shift, parts 11–20.
            var digit = key.PhysicalKeycode == Key.Key0 ? 10 : (int)(key.PhysicalKeycode - Key.Key0);
            var index = digit - 1 + (key.ShiftPressed ? 10 : 0);
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
              + $"Thrust {s.TotalThrustN / 1000:0} kN forward    Accel {s.MaxAccelerationMps2:0.0} m/s²\n"
              + (s.NonForwardThrustN > 0
                  ? $"Reverse {s.ReverseThrustN / 1000:0} kN    Strafe left {s.LeftThrustN / 1000:0} / right {s.RightThrustN / 1000:0} kN\n"
                  : "")
              + (s.DockingPorts.Count > 0 || s.FabricationRateKgPerS > 0 ? $"Docking ports {s.DockingPorts.Count}    Fabrication {s.FabricationRateKgPerS:0} kg/s\n" : "")
              + $"Cargo {s.CargoCapacityKg / 1000:0} t    Battery {s.EnergyCapacityKJ / 1000:0} MJ\n"
              + $"Power: solar {s.PowerGenerationKW:0} kW, use up to {s.TotalPowerUseKW:0} kW\n(thrust {s.ThrustPowerKW:0} kW)"
              + (s.TotalPowerUseKW <= s.PowerGenerationKW ? "\n"
                  : s.EnergyCapacityKJ > 0 ? $"  battery: {s.EnergyCapacityKJ / (s.TotalPowerUseKW - s.PowerGenerationKW):0} s at max\n"
                  : "  NO BATTERY\n")
              + (s.CrewRequired > 0 || s.CrewCapacity > 0 || s.RobotCount > 0
                  ? $"Crew: needs {s.CrewRequired}, houses {s.CrewCapacity}" + (s.RobotCount > 0 ? $", robots {s.RobotCount}" : "")
                    + (s.CrewRequired > s.CrewCapacity + s.RobotCount ? "  - can't house its own crew\n" : "\n")
                  : $"Crew: none needed (up to {ShipStats.UncrewedMaxParts} parts)\n")
              + $"Collision radius {s.BoundingRadiusM:0.0} m";

        _bill.Text = BillText();

        var errors = _model.Errors;
        var warnings = _model.Warnings;
        if (errors.Count == 0 && warnings.Count == 0)
        {
            _problems.Text = _model.Role == DesignRole.Ship
                ? "Valid ship: this design can fly."
                : $"Valid station{(_model.Stats?.IsFixedStructure == true ? " (fixed structure: foundation)" : "")}: it can be built at a shipyard but can't fly.";
            _problems.AddThemeColorOverride("font_color", OkColor);
        }
        else
        {
            var role = _model.Role == DesignRole.Ship ? "As a ship" : "As a station";
            _problems.Text = (errors.Count > 0 ? role + ":\n" : "")
                             + string.Join("\n", errors.Select(e => "Error: " + e).Concat(warnings.Select(w => "Warning: " + w)));
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
        if (_model.ToDesign().LooseCells() is { Count: > 0 } loose)
        {
            SetStatus($"{loose.Count} part(s) (outlined red) don't touch the rest. Connect every part edge to edge before saving.", ErrorColor);
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
