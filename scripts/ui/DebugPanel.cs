using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Development debug menu (F3): game speed, simulation tuning multipliers (saved with the game),
/// and cheats (add stock, survey everything). For playtesting pacing without editing data files.
/// </summary>
public partial class DebugPanel : PanelContainer
{
    private static readonly int[] Speeds = { 1, 2, 5, 10, 25, 50 };
    private static readonly long[] StockAmounts = { 1_000, 10_000, 50_000 };

    private readonly Func<SectorState> _sector;
    private readonly Func<int> _getSpeed;
    private readonly Action<int> _setSpeed;
    private readonly MaterialCatalog _materials;
    private OptionButton _speed = null!;
    private SpinBox _drill = null!, _refining = null!, _fabrication = null!;
    private OptionButton _station = null!, _material = null!, _amount = null!;
    private Label _status = null!;
    private List<CraftId> _stations = new();
    private bool _updating;

    public DebugPanel(Func<SectorState> sector, Func<int> getSpeed, Action<int> setSpeed, MaterialCatalog materials)
    {
        _sector = sector;
        _getSpeed = getSpeed;
        _setSpeed = setSpeed;
        _materials = materials;
        Build();
    }

    public DebugPanel() : this(() => new SectorState(0), () => 1, _ => { }, new MaterialCatalog(Array.Empty<MaterialDefinition>())) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.CenterLeft);
        OffsetLeft = 12;
        OffsetRight = 372;
        OffsetTop = -72; // just below the HUD text
        OffsetBottom = 350;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var title = new Label { Text = "DEBUG  (F3 to close)" };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);

        var grid = new GridContainer { Columns = 2 };
        box.AddChild(grid);
        grid.AddChild(new Label { Text = "Game speed" });
        _speed = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var s in Speeds) _speed.AddItem($"{s}×");
        _speed.ItemSelected += i => _setSpeed(Speeds[i]);
        grid.AddChild(_speed);

        _drill = AddMultiplier(grid, "Drill rate ×", v => _sector().Tuning.DrillRateMultiplier = v);
        _refining = AddMultiplier(grid, "Refinery rate ×", v => _sector().Tuning.RefiningRateMultiplier = v);
        _fabrication = AddMultiplier(grid, "Fabrication rate ×", v => _sector().Tuning.FabricationRateMultiplier = v);

        var reset = new Button
        {
            Text = "Reset tuning",
            FocusMode = FocusModeEnum.None,
            TooltipText = "Tuning is saved with the game. Game speed isn't (it only runs ticks faster).",
        };
        reset.Pressed += () => { _sector().Tuning.Reset(); Refresh(); };
        box.AddChild(reset);

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "Cheats (create mass from nothing)" });
        var cheat = new GridContainer { Columns = 2 };
        box.AddChild(cheat);
        cheat.AddChild(new Label { Text = "Station" });
        _station = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(200, 0) };
        cheat.AddChild(_station);
        cheat.AddChild(new Label { Text = "Material" });
        _material = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var m in _materials.Materials) _material.AddItem(m.Name);
        cheat.AddChild(_material);
        cheat.AddChild(new Label { Text = "Amount" });
        _amount = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var kg in StockAmounts) _amount.AddItem($"{kg / 1000} t");
        _amount.Select(1);
        cheat.AddChild(_amount);

        var buttons = new HBoxContainer();
        var add = new Button { Text = "Add stock", FocusMode = FocusModeEnum.None };
        add.Pressed += AddStock;
        buttons.AddChild(add);
        var survey = new Button { Text = "Survey all bodies", FocusMode = FocusModeEnum.None };
        survey.Pressed += () => { _sector().DebugSurveyAll(); _status.Text = "Every body is now surveyed."; };
        buttons.AddChild(survey);
        box.AddChild(buttons);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(330, 0) };
        box.AddChild(_status);
    }

    private SpinBox AddMultiplier(GridContainer grid, string label, Action<double> apply)
    {
        grid.AddChild(new Label { Text = label });
        var spin = new SpinBox
        {
            MinValue = SimulationTuning.Min, MaxValue = SimulationTuning.Max, Step = 0.25, Value = 1,
            AllowGreater = false, CustomArrowStep = 1, CustomMinimumSize = new Vector2(120, 0),
        };
        spin.ValueChanged += v => { if (!_updating) apply(v); };
        grid.AddChild(spin);
        return spin;
    }

    public void Open()
    {
        Visible = true;
        _status.Text = "";
        Refresh();
    }

    private void Refresh()
    {
        _updating = true;
        var tuning = _sector().Tuning;
        _drill.Value = tuning.DrillRateMultiplier;
        _refining.Value = tuning.RefiningRateMultiplier;
        _fabrication.Value = tuning.FabricationRateMultiplier;
        _speed.Select(Math.Max(0, Array.IndexOf(Speeds, _getSpeed())));
        _updating = false;

        // Stations first (shipyards, ports), then everything else.
        var sector = _sector();
        _stations = sector.Crafts.OrderByDescending(c => c.IsShipyard || c.Stats.DockingPorts.Count > 0).Select(c => c.Id).ToList();
        _station.Clear();
        foreach (var id in _stations) _station.AddItem($"{sector.GetCraft(id).Name} ({id})");
        if (_stations.Count > 0) _station.Select(0);
    }

    private void AddStock()
    {
        if (_station.Selected < 0 || _station.Selected >= _stations.Count) return;
        var material = _materials.Materials[Math.Max(0, _material.Selected)];
        var kg = StockAmounts[Math.Max(0, _amount.Selected)];
        var sector = _sector();
        sector.DebugAddStock(_stations[_station.Selected], material.Id, kg);
        _status.Text = $"Added {kg / 1000} t of {material.Name} to {sector.GetCraft(_stations[_station.Selected]).Name}.";
    }
}
