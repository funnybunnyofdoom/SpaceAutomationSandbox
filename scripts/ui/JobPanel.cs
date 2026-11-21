using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Sets up and watches the mining job of the craft you're viewing (key J): source body, material,
/// destination station and "return when the hold is X% full", plus Start/Stop. Shows the job's
/// state, why it's waiting, completed cycles and delivered mass. Starting goes through
/// <see cref="SectorState.StartJob"/>, which refuses with the reason if the ship or job can't work.
/// </summary>
public partial class JobPanel : PanelContainer
{
    private const double RefreshSeconds = 0.25;
    private static readonly double[] ReturnFractions = { 0.25, 0.5, 0.75, 1.0 };
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _craft;
    private Label _title = null!;
    private OptionButton _source = null!;
    private OptionButton _material = null!;
    private OptionButton _destination = null!;
    private OptionButton _returnAt = null!;
    private Label _state = null!;
    private Label _message = null!;
    private List<BodyId> _sources = new();
    private List<string> _materials = new();
    private List<CraftId> _destinations = new();
    private double _sinceRefresh;

    public JobPanel(Func<SectorState> sector, Func<CraftState?> craft)
    {
        _sector = sector;
        _craft = craft;
        Build();
    }

    public JobPanel() : this(() => new SectorState(0), () => null) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.CenterTop);
        OffsetLeft = -210;
        OffsetRight = 210;
        OffsetTop = 370; // below the HUD text
        OffsetBottom = 700;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_title);

        var grid = new GridContainer { Columns = 2 };
        box.AddChild(grid);
        _source = AddRow(grid, "Mine at");
        _source.ItemSelected += _ => RefreshMaterials();
        _material = AddRow(grid, "Material");
        _destination = AddRow(grid, "Deliver to");
        _returnAt = AddRow(grid, "Return when hold is");
        foreach (var f in ReturnFractions) _returnAt.AddItem($"{f * 100:0}% full");
        _returnAt.Select(0);

        var buttons = new HBoxContainer();
        var start = new Button { Text = "Start job", FocusMode = FocusModeEnum.None };
        start.Pressed += Start;
        buttons.AddChild(start);
        var stop = new Button { Text = "Stop job", FocusMode = FocusModeEnum.None };
        stop.Pressed += () => { if (_craft() is { } c) Show(_sector().StopJob(c.Id)); };
        buttons.AddChild(stop);
        box.AddChild(buttons);

        _state = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(390, 0) };
        box.AddChild(_state);
        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(390, 0) };
        box.AddChild(_message);
        box.AddChild(new Label
        {
            Text = "While a job runs it flies the ship; your flight keys do nothing until you stop it.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(390, 0),
            Modulate = new Color(1, 1, 1, 0.55f),
        });
    }

    private static OptionButton AddRow(GridContainer grid, string label)
    {
        grid.AddChild(new Label { Text = label });
        var option = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(240, 0) };
        grid.AddChild(option);
        return option;
    }

    public void Open()
    {
        Visible = true;
        _message.Text = "";
        RefreshChoices();
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private void RefreshChoices()
    {
        var sector = _sector();
        var craft = _craft();
        _sources = sector.Bodies.Where(b => b.IsMineable).Select(b => b.Id).ToList();
        _source.Clear();
        foreach (var id in _sources)
        {
            var body = sector.GetBody(id);
            _source.AddItem($"{body.Name} ({body.DepositsKg.Values.Sum() / 1000.0:0} t left)");
        }
        _destinations = sector.Crafts.Where(c => c.Stats.DockingPorts.Count > 0 && c.Id != craft?.Id).Select(c => c.Id).ToList();
        _destination.Clear();
        foreach (var id in _destinations) _destination.AddItem($"{sector.GetCraft(id).Name} ({id})");

        // Preselect the current job's settings, if any.
        if (craft?.Job is { } job)
        {
            Select(_source, _sources.IndexOf(job.SourceBodyId));
            Select(_destination, _destinations.IndexOf(job.DestinationId));
            Select(_returnAt, Array.IndexOf(ReturnFractions, job.ReturnFraction));
        }
        else
        {
            Select(_source, 0);
            Select(_destination, 0);
        }
        RefreshMaterials();
        if (craft?.Job is { } j) Select(_material, _materials.IndexOf(j.MaterialId));
    }

    private void RefreshMaterials()
    {
        _materials = _source.Selected >= 0 && _source.Selected < _sources.Count
            ? _sector().GetBody(_sources[_source.Selected]).DepositsKg.Keys.ToList()
            : new List<string>();
        _material.Clear();
        foreach (var m in _materials) _material.AddItem(m);
        Select(_material, 0);
    }

    private static void Select(OptionButton option, int index)
    {
        if (index >= 0 && index < option.ItemCount) option.Select(index);
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var craft = _craft();
        _title.Text = craft is null ? "MINING JOB" : $"MINING JOB: {craft.Name}  (J to close)";
        if (craft?.Job is not { } job)
        {
            _state.Text = "No job.";
            return;
        }
        var sector = _sector();
        var source = sector.Bodies.FirstOrDefault(b => b.Id == job.SourceBodyId)?.Name ?? "?";
        var destination = sector.Crafts.FirstOrDefault(c => c.Id == job.DestinationId)?.Name ?? "?";
        _state.Text = $"{job.MaterialId} from {source} to {destination}, return at {job.ReturnFraction * 100:0}%\n"
                      + $"State: {job.Status}\n"
                      + $"Cycles: {job.CompletedCycles}    Delivered: {job.DeliveredKg / 1000.0:0.0} t    "
                      + $"Hold: {craft.Inventory.TotalKg / 1000.0:0.0} / {craft.CargoCapacityKg / 1000.0:0} t";
        _state.AddThemeColorOverride("font_color", job.State == MiningJobState.Waiting ? ErrorColor : new Color(1, 1, 1));
    }

    private void Start()
    {
        var craft = _craft();
        if (craft is null) return;
        if (_source.Selected < 0 || _source.Selected >= _sources.Count) { Show(new ActionResult(false, "No mineable body.")); return; }
        if (_material.Selected < 0 || _material.Selected >= _materials.Count) { Show(new ActionResult(false, "That body has nothing left to mine.")); return; }
        if (_destination.Selected < 0 || _destination.Selected >= _destinations.Count) { Show(new ActionResult(false, "No station with a docking port.")); return; }

        var job = new MiningJob(_sources[_source.Selected], _materials[_material.Selected], _destinations[_destination.Selected],
            ReturnFractions[Math.Max(0, _returnAt.Selected)]);
        Show(_sector().StartJob(craft.Id, job));
    }

    private void Show(ActionResult result)
    {
        _message.Text = result.Message;
        _message.AddThemeColorOverride("font_color", result.Done ? OkColor : ErrorColor);
        Refresh();
    }
}
