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

    private int _soundCount; // cells when a sound last played (#129)
    private readonly BlueprintEditor _model;
    private readonly PartCatalog _catalog;
    private readonly IReadOnlyDictionary<string, Color> _colors;
    private readonly List<PartDefinition> _palette;
    private readonly List<Button> _paletteButtons = new();
    private readonly Func<ShipDesign?> _currentShipDesign;

    private BlueprintGridCanvas _grid = null!;
    private LineEdit _name = null!;
    private InfoList _stats = null!, _bill = null!, _problems = null!;
    private Label _summary = null!, _billTitle = null!;
    private LineEdit _search = null!;
    private string _category = "";
    private List<int> _shown = new(); // palette indices in view, in order: what the number keys pick
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
        _soundCount = 0;
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
        FilterPalette();
        if (_modulesBox.Visible) ListModules();
        Visible = true;
        if (_usingButtons) UseButtons(false);
        RefreshLarge();
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
        _left = left;
        left.AddChild(Heading("BLUEPRINT EDITOR"));
        BuildModules(left);
        // Categories (#102): all, then one icon each.
        var categories = new HFlowContainer();
        categories.AddThemeConstantOverride("h_separation", 3);
        _partsBox.AddChild(categories);
        var categoryGroup = new ButtonGroup();
        foreach (var (name, icon) in PartFacts.Categories.Prepend(("", "map")))
        {
            var button = new Button
            {
                ToggleMode = true, ButtonGroup = categoryGroup, ButtonPressed = name == "", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(29, 29),
                TooltipText = name == "" ? "All parts" : name, Icon = name == "" ? null : UiIcons.Get(icon), Text = name == "" ? "All" : "",
                IconAlignment = HorizontalAlignment.Center, TextureFilter = TextureFilterEnum.Nearest,
            };
            button.Pressed += () => { _category = name; FilterPalette(); };
            categories.AddChild(button);
        }
        _search = new LineEdit { PlaceholderText = "Search parts", ClearButtonEnabled = true };
        _search.TextChanged += _ => FilterPalette();
        _search.TextSubmitted += _ => _search.ReleaseFocus();
        _partsBox.AddChild(_search);
        var paletteScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _partsBox.AddChild(paletteScroll);
        var paletteList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        paletteList.AddThemeConstantOverride("separation", 2);
        paletteScroll.AddChild(paletteList);
        var group = new ButtonGroup();
        for (var i = 0; i < _palette.Count; i++)
        {
            var part = _palette[i];
            var button = new Button
            {
                ToggleMode = true,
                ButtonGroup = group,
                ButtonPressed = part.Id == _selectedPart,
                Alignment = HorizontalAlignment.Left,
                ClipText = true, // a narrow window shortens it; the tooltip has it all
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                FocusMode = FocusModeEnum.None,
                Icon = PartSprites.Icon(part.Id), // its pixel art (#46)
            };
            var id = part.Id;
            button.AddThemeConstantOverride("icon_max_width", 32);
            button.CustomMinimumSize = new Vector2(0, 36);
            button.TextureFilter = TextureFilterEnum.Nearest;
            button.Pressed += () => { _selectedPart = id; _search.ReleaseFocus(); };
            button.MouseEntered += () => Point(id, button, 0.15);
            button.MouseExited += () => { if (_pointed == id) StopPointing(); };
            _paletteButtons.Add(button);
            paletteList.AddChild(button);
        }
        left.AddChild(new Label
        {
            Text = "Left drag: place    Right drag: erase    R: rotate\nCtrl+S: save    B or Esc: close    Arrows, Enter: without a mouse\nNumber keys: pick (Shift for 11 to 20)",
            ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.7f),
        });

        // --- centre: grid ---
        var centre = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddChild(centre);
        _grid = new BlueprintGridCanvas(_model, _colors, () => _selectedPart, () => _rotation);
        _grid.Edited += () =>
        {
            _pendingConfirm = null;
            _refreshDue = true; // once a frame, however many cells changed
            var count = _model.Cells.Count; // #129: placed or erased, by ear
            UiSounds.Play(count >= _soundCount ? "place" : "erase");
            _soundCount = count;
        };
        BuildToolbar(centre);
        centre.AddChild(_grid);
        _grid.PartPointed += (id, cell) =>
        {
            if (id is null) StopPointing();
            else Point(id, _grid, 0.5, cell);
        };

        AddChild(_card); // over the columns

        // --- right: name, stats, problems, file actions ---
        var rightScroll = new ScrollContainer { CustomMinimumSize = new Vector2(350, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        columns.AddChild(rightScroll);
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        rightScroll.AddChild(right);

        right.AddChild(new Label { Text = "Name" });
        _name = new LineEdit { PlaceholderText = "Design name" };
        _name.TextChanged += text => { _model.Name = text; _pendingConfirm = null; Refresh(updateName: false); };
        right.AddChild(_name);

        _summary = new Label { ThemeTypeVariation = "HudText", Modulate = new Color(1, 1, 1, 0.65f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        right.AddChild(_summary);
        right.AddChild(_problems = new InfoList());
        right.AddChild(_stats = new InfoList());
        right.AddChild(_billTitle = UiTheme.Header("Materials"));
        right.AddChild(_bill = new InfoList());

        var actions = new HBoxContainer();
        actions.AddChild(ActionButton("Save", Save));
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

    // --- Part tooltips: a card of its own, opaque, on the palette and on placed parts ---

    private readonly PartCard _card = new();
    private string? _pointed;
    private Control? _pointedIn;
    private VBoxContainer? _left;
    private Rect2? _pointedArea;
    private double _pointedForS, _pointDelayS;

    private void Point(string partId, Control where, double delayS, Rect2? area = null)
    {
        if (_pointed == partId && _pointedIn == where && _pointedArea == area) return;
        _card.Visible = false;
        (_pointed, _pointedIn, _pointedArea, _pointDelayS, _pointedForS) = (partId, where, area, delayS, 0);
    }

    private void StopPointing()
    {
        _pointed = null;
        _card.Visible = false;
    }

    /// <summary>Dev/test hook: <c>--part-tip=id</c>.</summary>
    public void DevPoint(string partId)
    {
        var i = _palette.FindIndex(p => p.Id == partId);
        if (i >= 0) Point(partId, _paletteButtons[i], 0);
    }

    public override void _Process(double delta)
    {
        if (_refreshDue)
        {
            _refreshDue = false;
            Refresh();
        }
        // The palette takes what the grid and the numbers leave.
        var width = Mathf.Clamp(Size.X * 0.22f, 290, 420);
        if (_left is { } left && !Mathf.IsEqualApprox(left.CustomMinimumSize.X, width)) left.CustomMinimumSize = new Vector2(width, 0);
        if (_pointed is not { } id || _pointedIn is not { } where) return;
        if (!IsVisibleInTree()) { StopPointing(); return; }
        _pointedForS += delta;
        if (_pointedForS < _pointDelayS || !_catalog.TryGet(id, out var part) || part is null) return;
        var area = _pointedArea is { } a ? new Rect2(where.GlobalPosition + a.Position * GetGlobalTransform().Scale, a.Size * GetGlobalTransform().Scale) : where.GetGlobalRect();
        var back = GetGlobalTransform().AffineInverse();
        _card.Show(part, _catalog.Materials, LockedBy(id), new Rect2(back * area.Position, area.Size / GetGlobalTransform().Scale),
            where == _grid ? _grid.ProblemAtPointer : null, PartTiers.Previous(_catalog, id));
    }

    /// <summary>Shows the parts of the category that match the search, with the keys that pick them.</summary>
    private void FilterPalette()
    {
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _shown = new List<int>();
        for (var i = 0; i < _palette.Count && i < _paletteButtons.Count; i++)
        {
            var part = _palette[i];
            var show = (_category == "" || PartFacts.Category(part) == _category)
                       && words.All(w => part.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                         || PartFacts.Category(part).Contains(w, StringComparison.OrdinalIgnoreCase)
                                         || PartFacts.Of(part, _catalog.Materials).Any(f => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
            _paletteButtons[i].Visible = show;
            if (!show) continue;
            // Research changes over time: parts that can't be built yet are marked (they can still be planned with).
            var locked = LockedBy(part.Id) is not null;
            _paletteButtons[i].Text = $"{KeyLabel(_shown.Count)} {part.Name}  {part.MassKg / 1000:0.#} t{PartSummary(part)}{(locked ? "  [locked]" : "")}";
            _paletteButtons[i].Modulate = locked ? new Color(1, 1, 1, 0.55f) : Colors.White;
            _shown.Add(i);
        }
    }

    /// <summary>The design's numbers as bars (#102).</summary>
    private static List<HudRow> StatRows(ShipStats s)
    {
        var rows = new List<HudRow> { new("hull", $"{s.CellCount} parts    {s.TotalMassKg / 1000:0.0} t    radius {s.BoundingRadiusM:0.0} m") };
        if (s.TotalThrustN > 0)
            rows.Add(new("speed", $"{s.TotalThrustN / 1000:0} kN    {s.MaxAccelerationMps2:0.0} m/s²", s.MaxAccelerationMps2 < 2 ? Severity.Warning : Severity.Normal, s.MaxAccelerationMps2 / 20,
                Tip: "Forward thrust and acceleration, empty. Full bar: 20 m/s²"));
        if (s.NonForwardThrustN > 0)
            rows.Add(new("speed", $"reverse {s.ReverseThrustN / 1000:0} kN    strafe {s.LeftThrustN / 1000:0} / {s.RightThrustN / 1000:0} kN", Severity.Muted));
        var made = s.PowerGenerationKW + s.GeneratorKW;
        if (made > 0 || s.TotalPowerUseKW > 0)
        {
            var enough = made >= s.TotalPowerUseKW;
            rows.Add(new("power", $"{s.PowerGenerationKW:0} kW solar{(s.GeneratorKW > 0 ? $" + {s.GeneratorKW:0} made" : "")} / {s.TotalPowerUseKW:0} kW used",
                enough ? Severity.Good : s.EnergyCapacityKJ > 0 ? Severity.Warning : Severity.Bad, s.TotalPowerUseKW > 0 ? made / s.TotalPowerUseKW : 1,
                Tip: $"Power made against the most it can use at once ({s.ThrustPowerKW:0} kW of it for thrust)"));
        }
        if (s.EnergyCapacityKJ > 0)
            rows.Add(new("battery", $"{s.EnergyCapacityKJ / 1000:0} MJ stored" + (s.TotalPowerUseKW > made ? $", lasts {s.EnergyCapacityKJ / (s.TotalPowerUseKW - made):0} s at most use" : "")));
        else if (s.TotalPowerUseKW > made) rows.Add(new("warn", "No battery: it can't run everything at once", Severity.Warning));
        if (s.CargoCapacityKg > 0) rows.Add(new("cargo", $"{s.CargoCapacityKg / 1000:0.#} t cargo"));
        if (s.CrewRequired > 0 || s.CrewCapacity > 0 || s.RobotCount > 0)
        {
            var housed = s.CrewCapacity + s.RobotCount;
            rows.Add(new("crew", $"needs {s.CrewRequired} crew, houses {s.CrewCapacity}{(s.RobotCount > 0 ? $" + {s.RobotCount} robots" : "")}", housed < s.CrewRequired ? Severity.Warning : Severity.Good,
                s.CrewRequired > 0 ? housed / (double)s.CrewRequired : 1, Tip: housed < s.CrewRequired ? "It can't house the crew it needs" : "Crew or robots to run it"));
        }
        else rows.Add(new("crew", $"no crew needed (up to {ShipStats.UncrewedMaxParts} parts)", Severity.Muted));
        if (s.DockingPorts.Count > 0) rows.Add(new("docked", $"{s.DockingPorts.Count} docking port{(s.DockingPorts.Count == 1 ? "" : "s")}"));
        if (s.FabricationRateKgPerS > 0) rows.Add(new("shipyard", $"builds {s.FabricationRateKgPerS:0} kg/s"));
        if (s.MiningRateKgPerS > 0) rows.Add(new("mining", $"mines {s.MiningRateKgPerS:0} kg/s"));
        if (s.ShieldCapacity > 0) rows.Add(new("shield", $"shield takes {s.ShieldCapacity:0.#} parts' worth"));
        if (s.WeaponList.Count > 0) rows.Add(new("weapon", $"{s.WeaponList.Count} weapon{(s.WeaponList.Count == 1 ? "" : "s")}"));
        return rows;
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
        if (!Visible) return;
        if (ToolKey(@event)) { GetViewport().SetInputAsHandled(); return; }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (key.PhysicalKeycode is >= Key.Key0 and <= Key.Key9 && !key.CtrlPressed)
        {
            // 1..9 then 0 pick parts 1–10; with Shift, parts 11–20.
            var digit = key.PhysicalKeycode == Key.Key0 ? 10 : (int)(key.PhysicalKeycode - Key.Key0);
            var index = digit - 1 + (key.ShiftPressed ? 10 : 0);
            if (index >= _shown.Count) return;
            _selectedPart = _palette[_shown[index]].Id;
            _paletteButtons[_shown[index]].ButtonPressed = true;
        }
        else return;

        GetViewport().SetInputAsHandled();
    }

    private void Refresh(bool updateName = true)
    {
        if (updateName && _name.Text != _model.Name) _name.Text = _model.Name;

        var s = _model.Stats;
        _summary.Text = s is null ? "Empty design." : $"Id: {_model.Id}{(_model.IsDirty ? "   (unsaved)" : "")}";
        _stats.Set(s is null ? Array.Empty<HudRow>() : StatRows(s));

        // Materials, biggest share first by the order of the materials file.
        var bill = _model.BillOfMaterials;
        var materials = _catalog.Materials;
        _billTitle.Visible = bill.TotalKg > 0;
        _bill.Set(bill.Kg.OrderBy(kv => materials.OrderOf(kv.Key))
            .Select(kv => new HudRow("mat_" + kv.Key, $"{(materials.Contains(kv.Key) ? materials.Get(kv.Key).Name : kv.Key)}  {kv.Value / 1000:0.0##} t", Severity.Normal, kv.Value / bill.TotalKg)).ToList());

        var errors = _model.Errors;
        var warnings = _model.Warnings;
        if (_model.IsLocked) warnings = warnings.Append(_model.LockedReason!).ToList();
        _problems.Set(errors.Count == 0 && warnings.Count == 0
            ? new[]
            {
                new HudRow("ok", _model.Role == DesignRole.Ship
                    ? "Valid ship: this design can fly."
                    : $"Valid station{(s?.IsFixedStructure == true ? " (fixed structure: foundation)" : "")}: it can be built at a shipyard but can't fly.", Severity.Good),
            }
            : errors.Select(e => new HudRow("danger", $"{(_model.Role == DesignRole.Ship ? "Ship" : "Station")}: {e}", Severity.Bad))
                .Concat(warnings.Select(w => new HudRow("warn", w, Severity.Warning))).ToArray());
        _grid.Problems = _model.ToDesign().ProblemCells(_catalog, _model.Role);
        RefreshTools();
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
        _grid.Fit();
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
        _grid.Fit();
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
