using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Sets up and watches the job of the craft you're viewing (key J). Three kinds:
/// Mining (source body, material, destination, return-at %), Freight on a fixed route (pickup station,
/// material, destination, minimum pickup, keep-stocked target), and Freight serving station requests
/// (minimum load only: it picks trips itself, #1). Shows state, wait reason, cycles and delivered
/// mass. Starting goes through SectorState.StartJob, which refuses with the reason if it can't work.
/// </summary>
public partial class JobPanel : PanelContainer
{
    private const double RefreshSeconds = 0.25;
    private static readonly double[] ReturnFractions = { 0.25, 0.5, 0.75, 1.0 };
    private static readonly long[] MinPickups = { 1_000, 5_000, 10_000, 20_000 };
    private static readonly long[] KeepStocked = { 0, 5_000, 10_000, 20_000, 50_000 };
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _craft;
    private readonly MaterialCatalog _materials;
    private Label _title = null!;
    private OptionButton _kind = null!;
    private GridContainer _miningRows = null!;
    private GridContainer _freightRows = null!;
    private GridContainer _serveRows = null!;
    private OptionButton _serveMinLoad = null!;
    private OptionButton _source = null!, _mineMaterial = null!, _mineDestination = null!, _returnAt = null!;
    private OptionButton _pickup = null!, _haulMaterial = null!, _haulDestination = null!, _minPickup = null!, _keepStocked = null!;
    private Label _state = null!;
    private Label _message = null!;
    private List<BodyId> _sources = new();
    private List<string> _deposits = new();
    private List<CraftId> _stations = new();
    private double _sinceRefresh;

    public JobPanel(Func<SectorState> sector, Func<CraftState?> craft, MaterialCatalog materials)
    {
        _sector = sector;
        _craft = craft;
        _materials = materials;
        Build();
    }

    public JobPanel() : this(() => new SectorState(0), () => null, new MaterialCatalog(Array.Empty<MaterialDefinition>())) { }

    private void Build()
    {
        // Right side, full height, scrolling (settings, state and recent history don't fit under the HUD).
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -450;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_title);

        var kindRow = new HBoxContainer();
        kindRow.AddChild(new Label { Text = "Job" });
        _kind = new OptionButton { FocusMode = FocusModeEnum.None };
        _kind.AddItem("Mining");
        _kind.AddItem("Freight: fixed route");
        _kind.AddItem("Freight: serve requests");
        _kind.ItemSelected += _ => ShowKind();
        kindRow.AddChild(_kind);
        box.AddChild(kindRow);

        _miningRows = new GridContainer { Columns = 2 };
        box.AddChild(_miningRows);
        _source = AddRow(_miningRows, "Mine at");
        _source.ItemSelected += _ => RefreshDeposits();
        _mineMaterial = AddRow(_miningRows, "Material");
        _mineDestination = AddRow(_miningRows, "Deliver to");
        _returnAt = AddRow(_miningRows, "Return when hold is");
        foreach (var f in ReturnFractions) _returnAt.AddItem($"{f * 100:0}% full");

        _freightRows = new GridContainer { Columns = 2 };
        box.AddChild(_freightRows);
        _pickup = AddRow(_freightRows, "Pick up at");
        _haulMaterial = AddRow(_freightRows, "Material");
        _haulDestination = AddRow(_freightRows, "Deliver to");
        _minPickup = AddRow(_freightRows, "Wait for at least");
        foreach (var kg in MinPickups) _minPickup.AddItem($"{kg / 1000} t");
        _keepStocked = AddRow(_freightRows, "Keep destination at");
        foreach (var kg in KeepStocked) _keepStocked.AddItem(kg == 0 ? "no limit (deliver all)" : $"{kg / 1000} t");

        _serveRows = new GridContainer { Columns = 2 };
        box.AddChild(_serveRows);
        _serveMinLoad = AddRow(_serveRows, "Smallest trip");
        foreach (var kg in MinPickups) _serveMinLoad.AddItem($"{kg / 1000} t");
        _serveRows.AddChild(new Label { Text = "" });
        _serveRows.AddChild(new Label
        {
            Text = "Serves stations' requests (set them in the cargo panel, C, while docked).",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0), Modulate = new Color(1, 1, 1, 0.6f),
        });

        var buttons = new HBoxContainer();
        var start = new Button { Text = "Start job", FocusMode = FocusModeEnum.None };
        start.Pressed += Start;
        buttons.AddChild(start);
        var stop = new Button { Text = "Stop job", FocusMode = FocusModeEnum.None };
        stop.Pressed += () => { if (_craft() is { } c) Show(_sector().StopJob(c.Id)); };
        buttons.AddChild(stop);
        box.AddChild(buttons);

        _state = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(410, 0) };
        box.AddChild(_state);
        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(410, 0) };
        box.AddChild(_message);
        box.AddChild(new Label
        {
            Text = "While a job runs it flies the ship; your flight keys do nothing until you stop it.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(410, 0),
            Modulate = new Color(1, 1, 1, 0.55f),
        });
    }

    private static OptionButton AddRow(GridContainer grid, string label)
    {
        grid.AddChild(new Label { Text = label });
        var option = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(250, 0) };
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

    private void ShowKind()
    {
        _miningRows.Visible = _kind.Selected == 0;
        _freightRows.Visible = _kind.Selected == 1;
        _serveRows.Visible = _kind.Selected == 2;
    }

    private void RefreshChoices()
    {
        var sector = _sector();
        var craft = _craft();

        // Unsurveyed bodies are listed (so you know they exist) but jobs there are refused with the reason.
        _sources = sector.Bodies.Where(b => b.IsMineable).Select(b => b.Id).ToList();
        _source.Clear();
        foreach (var id in _sources)
        {
            var body = sector.GetBody(id);
            _source.AddItem(body.Surveyed ? $"{body.Name} ({body.DepositsKg.Values.Sum() / 1000.0:0} t left)" : $"{body.Name} (unsurveyed)");
        }

        _stations = sector.Crafts.Where(c => c.Stats.DockingPorts.Count > 0 && c.Id != craft?.Id).Select(c => c.Id).ToList();
        foreach (var option in new[] { _mineDestination, _pickup, _haulDestination })
        {
            option.Clear();
            foreach (var id in _stations) option.AddItem($"{sector.GetCraft(id).Name} ({id})");
        }
        _haulMaterial.Clear();
        foreach (var m in _materials.Materials) _haulMaterial.AddItem(m.Name);

        Select(_source, 0);
        Select(_mineDestination, 0);
        Select(_returnAt, 0);
        Select(_pickup, 0);
        Select(_haulDestination, Math.Min(1, _stations.Count - 1));
        Select(_haulMaterial, 0);
        Select(_minPickup, 1);
        Select(_keepStocked, 0);
        Select(_serveMinLoad, 1);

        // Show the current job's settings, if any.
        switch (craft?.Job)
        {
            case MiningJob mining:
                _kind.Select(0);
                Select(_source, _sources.IndexOf(mining.SourceBodyId));
                Select(_mineDestination, _stations.IndexOf(mining.DestinationId));
                Select(_returnAt, Array.IndexOf(ReturnFractions, mining.ReturnFraction));
                RefreshDeposits();
                Select(_mineMaterial, _deposits.IndexOf(mining.MaterialId));
                break;
            case HaulJob haul:
                _kind.Select(1);
                Select(_pickup, _stations.IndexOf(haul.PickupId));
                Select(_haulDestination, _stations.IndexOf(haul.DestinationId));
                Select(_haulMaterial, _materials.Materials.ToList().FindIndex(m => m.Id == haul.MaterialId));
                Select(_minPickup, Array.IndexOf(MinPickups, haul.MinPickupKg));
                Select(_keepStocked, Array.IndexOf(KeepStocked, haul.KeepStockedKg));
                RefreshDeposits();
                break;
            case ServeRequestsJob serve:
                _kind.Select(2);
                Select(_serveMinLoad, Array.IndexOf(MinPickups, serve.MinLoadKg));
                RefreshDeposits();
                break;
            default:
                RefreshDeposits();
                break;
        }
        ShowKind();
    }

    private void RefreshDeposits()
    {
        var body = _source.Selected >= 0 && _source.Selected < _sources.Count ? _sector().GetBody(_sources[_source.Selected]) : null;
        _deposits = body is { Surveyed: true } ? body.DepositsKg.Keys.ToList() : new List<string>();
        _mineMaterial.Clear();
        foreach (var m in _deposits) _mineMaterial.AddItem(_materials.Contains(m) ? _materials.Get(m).Name : m);
        if (_deposits.Count == 0) _mineMaterial.AddItem(body is { Surveyed: false } ? "(unknown: survey it first)" : "(nothing left)");
        Select(_mineMaterial, 0);
    }

    private static void Select(OptionButton option, int index)
    {
        if (index >= 0 && index < option.ItemCount) option.Select(index);
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var craft = _craft();
        _title.Text = craft is null ? "JOB" : $"JOB: {craft.Name}  (J to close)";
        if (craft?.Job is not { } job)
        {
            _state.Text = "No job.";
            return;
        }
        var sector = _sector();
        string Station(CraftId id) => sector.Crafts.FirstOrDefault(c => c.Id == id)?.Name ?? "?";
        var what = job switch
        {
            MiningJob m => $"Mining {job.MaterialId} at {sector.Bodies.FirstOrDefault(b => b.Id == m.SourceBodyId)?.Name ?? "?"} for "
                           + $"{Station(m.DestinationId)}, return at {m.ReturnFraction * 100:0}%",
            HaulJob h => $"Freight: {job.MaterialId} from {Station(h.PickupId)} to {Station(h.DestinationId)}"
                         + (h.KeepStockedKg > 0 ? $", keep {h.KeepStockedKg / 1000} t there" : ""),
            ServeRequestsJob s => "Freight: serving station requests"
                                  + (s.Contract is { } c ? $" (now {c.AmountKg / 1000.0:0.0} t {c.MaterialId}, {Station(c.PickupId)} → {Station(c.DestinationId)})" : ""),
            _ => job.Kind,
        };
        _state.Text = $"{what}\nState: {job.Status}{(craft.PowerShort && !craft.LegacyFreePower ? "  (NO POWER)" : "")}{(craft.CrewShortReason is { } why ? $"  (STOPPED: {why})" : "")}{(craft.GivingWayTo.IsNone ? "" : "  (giving way to traffic)")}{(craft.Transit is { } leg ? $"  (far away: in transit, arrives in {(leg.ArriveTick - sector.Tick) * FixedStepClock.TickSeconds:0} s)" : "")}\n"
                      + $"Cycles: {job.CompletedCycles}    Delivered: {job.DeliveredKg / 1000.0:0.0} t    "
                      + $"Hold: {craft.CargoKg / 1000.0:0.0} / {craft.CargoCapacityKg / 1000.0:0} t"
                      + (job.History.Count == 0 ? "" : "\nRecent (newest first):\n" + string.Join("\n", job.History.Reverse().Take(6).Select(e =>
                          $"  {FormatTime(e.Tick)}  {e.Text}")));
        _state.AddThemeColorOverride("font_color", job.Status.StartsWith("waiting") ? ErrorColor : new Color(1, 1, 1));
    }

    private static string FormatTime(long tick)
    {
        var seconds = (long)(tick * FixedStepClock.TickSeconds);
        return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    private void Start()
    {
        var craft = _craft();
        if (craft is null) return;
        if (_stations.Count == 0 && _kind.Selected != 2) { Show(new ActionResult(false, "No station with a docking port.")); return; }

        if (_kind.Selected == 0)
        {
            if (_source.Selected < 0 || _source.Selected >= _sources.Count) { Show(new ActionResult(false, "No mineable body.")); return; }
            var source = _sector().GetBody(_sources[_source.Selected]);
            if (!source.Surveyed)
            {
                Show(new ActionResult(false, $"{source.Name} hasn't been surveyed: fly a ship you pilot within {SectorState.SurveyRangeM / 1000:0} km of it first."));
                return;
            }
            if (_mineMaterial.Selected < 0 || _mineMaterial.Selected >= _deposits.Count) { Show(new ActionResult(false, $"{source.Name} has nothing left to mine.")); return; }
            Show(_sector().StartJob(craft.Id, new MiningJob(source.Id, _deposits[_mineMaterial.Selected],
                _stations[Math.Max(0, _mineDestination.Selected)], ReturnFractions[Math.Max(0, _returnAt.Selected)])));
        }
        else if (_kind.Selected == 2)
        {
            Show(_sector().StartJob(craft.Id, new ServeRequestsJob(MinPickups[Math.Max(0, _serveMinLoad.Selected)])));
        }
        else
        {
            var material = _materials.Materials[Math.Max(0, _haulMaterial.Selected)].Id;
            Show(_sector().StartJob(craft.Id, new HaulJob(_stations[Math.Max(0, _pickup.Selected)], material,
                _stations[Math.Max(0, _haulDestination.Selected)], MinPickups[Math.Max(0, _minPickup.Selected)],
                KeepStocked[Math.Max(0, _keepStocked.Selected)])));
        }
    }

    private void Show(ActionResult result)
    {
        _message.Text = result.Message;
        _message.AddThemeColorOverride("font_color", result.Done ? OkColor : ErrorColor);
        Refresh();
    }
}
