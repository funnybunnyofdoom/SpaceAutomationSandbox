using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
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
    private Label _surveyNote = null!;
    private GridContainer _salvageRows = null!;
    private GridContainer _defendRows = null!;
    private GridContainer _tradeRows = null!;
    private OptionButton _tradeHome = null!, _tradeGive = null!, _tradeWith = null!, _tradeGet = null!;
    private List<CraftId> _traders = new();
    private List<string> _tradeGoods = new();
    private OptionButton _defendWhat = null!;
    private List<CraftId> _posts = new();
    private List<BodyId> _systems = new();
    private OptionButton _salvageDestination = null!;
    private OptionButton _serveMinLoad = null!;
    private OptionButton _source = null!, _mineMaterial = null!, _mineDestination = null!, _returnAt = null!;
    private OptionButton _pickup = null!, _haulMaterial = null!, _haulDestination = null!, _minPickup = null!, _keepStocked = null!;
    private InfoList _state = null!;
    private RouteView _route = null!;
    private readonly List<Button> _kindButtons = new();
    private Label _kindName = null!;
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

        _title.AddThemeColorOverride("font_color", UiTheme.Accent);
        // The kinds as icons; the list behind them keeps the choice (#103).
        var kindRow = new HFlowContainer();
        _kind = new OptionButton { FocusMode = FocusModeEnum.None, Visible = false };
        _kind.AddItem("Mining");
        _kind.AddItem("Freight: fixed route");
        _kind.AddItem("Freight: serve requests");
        _kind.AddItem("Survey: scout unsurveyed bodies");
        _kind.AddItem("Salvage: cut up wrecks");
        _kind.AddItem("Guard a station");
        _kind.AddItem("Escort a ship");
        _kind.AddItem("Patrol a system");
        _kind.AddItem("Trade route with a civilization");
        kindRow.AddChild(_kind);
        var group = new ButtonGroup();
        var icons = new[] { "mining", "freight", "cargo", "survey", "salvage", "shield", "fleet", "orbit", "trade" };
        for (var i = 0; i < icons.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(42, 38), Icon = UiIcons.Get(icons[i]),
                IconAlignment = HorizontalAlignment.Center, ExpandIcon = true, TextureFilter = TextureFilterEnum.Nearest, TooltipText = _kind.GetItemText(i),
            };
            button.AddThemeConstantOverride("icon_max_width", 32);
            button.Pressed += () => { _kind.Select(index); ShowKind(); };
            kindRow.AddChild(button);
            _kindButtons.Add(button);
        }
        box.AddChild(kindRow);
        box.AddChild(_kindName = UiTheme.Header(""));
        box.AddChild(_route = new RouteView());

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

        _surveyNote = new Label
        {
            Text = "Flies to the nearest unsurveyed body, surveys it (within 2 km, as when you fly there), then the next. Lets jobs and depots use bodies you haven't visited.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(410, 0), Modulate = new Color(1, 1, 1, 0.6f),
        };
        box.AddChild(_surveyNote);

        _tradeRows = new GridContainer { Columns = 2 };
        box.AddChild(_tradeRows);
        _tradeHome = AddRow(_tradeRows, "Home station");
        _tradeGive = AddRow(_tradeRows, "Take them");
        foreach (var m in _materials.Materials) _tradeGive.AddIconItem(UiIcons.Material(m.Id), m.Name);
        _tradeWith = AddRow(_tradeRows, "Trade with");
        _tradeWith.ItemSelected += _ => RefreshTradeGoods();
        _tradeGet = AddRow(_tradeRows, "Bring back");
        _defendRows = new GridContainer { Columns = 2 };
        box.AddChild(_defendRows);
        _defendWhat = AddRow(_defendRows, "Protect");
        _defendRows.AddChild(new Label { Text = "" });
        _defendRows.AddChild(new Label
        {
            Text = "Needs weapons. Attacks enemies that come near, goes to a shipyard when badly damaged and to a station for ammunition.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0), Modulate = new Color(1, 1, 1, 0.6f),
        });
        _salvageRows = new GridContainer { Columns = 2 };
        box.AddChild(_salvageRows);
        _salvageDestination = AddRow(_salvageRows, "Deliver to");
        _salvageRows.AddChild(new Label { Text = "" });
        _salvageRows.AddChild(new Label
        {
            Text = "Needs a salvage arm. Cuts up the nearest wreck (through warp gates too), delivers, and goes on.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0), Modulate = new Color(1, 1, 1, 0.6f),
        });

        var buttons = new HBoxContainer();
        var start = new Button { Text = "Start job", Icon = UiIcons.Get("ok"), FocusMode = FocusModeEnum.None, TooltipText = "The craft runs this job on its own until you stop it; the panel shows what it's waiting for" };
        start.Pressed += Start;
        buttons.AddChild(start);
        var stop = new Button { Text = "Stop job", FocusMode = FocusModeEnum.None, TooltipText = "Stop the job; the craft stays where it is and you can fly it again" };
        stop.Pressed += () => { if (_craft() is { } c) Show(_sector().StopJob(c.Id)); };
        buttons.AddChild(stop);
        box.AddChild(buttons);

        box.AddChild(_state = new InfoList());
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
        for (var i = 0; i < _kindButtons.Count; i++) _kindButtons[i].SetPressedNoSignal(i == _kind.Selected);
        _kindName.Text = _kind.GetItemText(Math.Max(0, _kind.Selected));
        _miningRows.Visible = _kind.Selected == 0;
        _freightRows.Visible = _kind.Selected == 1;
        _serveRows.Visible = _kind.Selected == 2;
        _surveyNote.Visible = _kind.Selected == 3;
        _salvageRows.Visible = _kind.Selected == 4;
        _defendRows.Visible = _kind.Selected is >= 5 and <= 7;
        if (_kind.Selected is >= 5 and <= 7) RefreshPosts();
        _tradeRows.Visible = _kind.Selected == 8;
        if (_kind.Selected == 8) RefreshTraders();
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
            // Only what the surveyed sites hold counts (#70).
            var surveyed = body.Sites.Where(s => s.Surveyed).ToList();
            _source.AddIconItem(UiIcons.Get(UiIcons.BodyIcon(body)), !body.AnySiteSurveyed ? $"{body.Name} (unsurveyed)"
                : $"{body.Name} ({surveyed.Sum(s => s.DepositsKg.Values.Sum()) / 1000.0:0} t of ore in {surveyed.Count} of {body.Sites.Count} sites{(body.BulkKg > 0 ? $", {body.BulkKg / 1e9:0.###} Mt of {body.BulkMaterialId}" : "")})");
        }

        _stations = sector.Crafts.Where(c => c.Stats.DockingPorts.Count > 0 && !c.IsWreck && SectorState.IsPlayers(c) && c.Id != craft?.Id).Select(c => c.Id).ToList();
        foreach (var option in new[] { _mineDestination, _pickup, _haulDestination, _salvageDestination, _tradeHome })
        {
            option.Clear();
            foreach (var id in _stations) option.AddIconItem(UiIcons.Get(sector.GetCraft(id).IsShipyard ? "shipyard" : SectorState.IsGate(sector.GetCraft(id)) ? "gate" : "orbit"), $"{sector.GetCraft(id).Name} ({id})");
        }
        _haulMaterial.Clear();
        foreach (var m in _materials.Materials) _haulMaterial.AddIconItem(UiIcons.Material(m.Id), m.Name);

        Select(_source, 0);
        Select(_mineDestination, 0);
        Select(_salvageDestination, 0);
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
            case TradeJob:
                _kind.Select(8);
                RefreshDeposits();
                break;
            case DefendJob defend:
                _kind.Select(5 + (int)defend.Mode);
                RefreshDeposits();
                break;
            case SalvageJob salvage:
                _kind.Select(4);
                Select(_salvageDestination, _stations.IndexOf(salvage.DestinationId));
                RefreshDeposits();
                break;
            case SurveyJob:
                _kind.Select(3);
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

    /// <summary>The trade stations there are (#89), and the route's current choices.</summary>
    private void RefreshTraders()
    {
        var sector = _sector();
        _traders = sector.Crafts.Where(c => c.IsTradeStation && !c.IsWreck).Select(c => c.Id).ToList();
        _tradeWith.Clear();
        foreach (var id in _traders) _tradeWith.AddItem(sector.GetCraft(id).Name);
        if (_traders.Count == 0) _tradeWith.AddItem("(no trade station known)");
        if (_craft()?.Job is TradeJob job)
        {
            Select(_tradeHome, _stations.IndexOf(job.HomeId));
            Select(_tradeGive, _materials.Materials.ToList().FindIndex(m => m.Id == job.GiveMaterial));
            Select(_tradeWith, _traders.IndexOf(job.TraderId));
        }
        RefreshTradeGoods();
        if (_craft()?.Job is TradeJob current) Select(_tradeGet, _tradeGoods.IndexOf(current.GetMaterial));
    }

    private void RefreshTradeGoods()
    {
        var sector = _sector();
        _tradeGoods = _tradeWith.Selected >= 0 && _tradeWith.Selected < _traders.Count && sector.FactionById(sector.GetCraft(_traders[_tradeWith.Selected]).FactionId) is { } faction
            ? sector.SellsOf(faction).ToList() : new List<string>();
        _tradeGet.Clear();
        foreach (var m in _tradeGoods) _tradeGet.AddIconItem(UiIcons.Material(m), _materials.Contains(m) ? _materials.Get(m).Name : m);
    }

    /// <summary>What the chosen fleet order (#88) can protect: stations, ships or star systems.</summary>
    private void RefreshPosts()
    {
        var sector = _sector();
        var craft = _craft();
        var mine = sector.Crafts.Where(c => SectorState.IsPlayers(c) && !c.IsWreck && c.Id != craft?.Id);
        _posts = (_kind.Selected == 5 ? mine.Where(c => c.Stats.TotalThrustN <= 0 || c.IsOnRails) : mine.Where(c => c.Stats.TotalThrustN > 0)).Select(c => c.Id).ToList();
        _systems = sector.Stars.Select(s => s.Id).ToList();
        _defendWhat.Clear();
        if (_kind.Selected == 7) foreach (var id in _systems) _defendWhat.AddItem($"{sector.GetBody(id).Name} system");
        else foreach (var id in _posts) _defendWhat.AddItem($"{sector.GetCraft(id).Name} ({id})");
        if (craft?.Job is DefendJob job) Select(_defendWhat, _kind.Selected == 7 ? _systems.IndexOf(job.StarId) : _posts.IndexOf(job.PostId));
    }

    private void RefreshDeposits()
    {
        var body = _source.Selected >= 0 && _source.Selected < _sources.Count ? _sector().GetBody(_sources[_source.Selected]) : null;
        // Ore in the surveyed sites (#70) and the body's bulk (#27).
        _deposits = body is { AnySiteSurveyed: true } ? body.MineableMaterials.Where(m => body.SurveyedDeposit(m) > 0).ToList() : new List<string>();
        _mineMaterial.Clear();
        foreach (var m in _deposits) _mineMaterial.AddIconItem(UiIcons.Material(m), _materials.Contains(m) ? _materials.Get(m).Name : m);
        if (_deposits.Count == 0) _mineMaterial.AddItem(body is { AnySiteSurveyed: false } ? "(unknown: survey it first)" : "(nothing in the surveyed sites)");
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
        _title.Text = craft is null ? "JOB" : $"JOB: {craft.Name}  ({InputActions.Key1(InputActions.Job)} to close)";
        if (craft?.Job is not { } job)
        {
            _state.Set(new[] { new HudRow("idle", "No job: choose one and start it.", Severity.Muted) });
            ShowRoute(craft);
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
            TradeJob t => $"Trading {t.GiveMaterial} from {Station(t.HomeId)} for {t.GetMaterial} at {Station(t.TraderId)}",
            DefendJob d => d.Mode == DefendMode.Patrol ? $"Patrolling the {sector.Bodies.FirstOrDefault(b => b.Id == d.StarId)?.Name ?? "?"} system, {d.CompletedCycles} fights"
                : $"{(d.Mode == DefendMode.Guard ? "Guarding" : "Escorting")} {Station(d.PostId)}, {d.CompletedCycles} fights",
            SalvageJob s => $"Salvaging for {Station(s.DestinationId)}: {(s.WreckId.IsNone ? "no wreck picked" : Station(s.WreckId))}, {sector.Crafts.Count(c => c.IsWreck)} wrecks in all",
            SurveyJob => $"Surveying: {job.CompletedCycles} bodies surveyed, {sector.Bodies.Count(b => !b.Surveyed && b.Kind != BodyKind.Star)} left",
            ServeRequestsJob s => "Freight: serving station requests"
                                  + (s.Contract is { } c ? $" (now {c.AmountKg / 1000.0:0.0} t {c.MaterialId}, {Station(c.PickupId)} → {Station(c.DestinationId)})" : ""),
            _ => job.Kind,
        };
        var waiting = job.Status.StartsWith("waiting");
        var rows = new List<HudRow>
        {
            new(FleetPanel.JobIcon(job.Kind), what),
            new(!job.Active ? "idle" : waiting ? "wait" : "work", job.Active ? job.Status : $"stopped: {job.Status}", waiting ? Severity.Warning : Severity.Normal),
        };
        if (craft.PowerShort && !craft.LegacyFreePower) rows.Add(new("warn", "NO POWER", Severity.Bad));
        if (craft.CrewShortReason is { } why) rows.Add(new("warn", $"STOPPED: {why}", Severity.Bad));
        if (!craft.GivingWayTo.IsNone) rows.Add(new("fleet", "giving way to traffic", Severity.Muted));
        if (craft.Transit is { } leg) rows.Add(new("clock", $"far away: in transit, arrives in {(leg.ArriveTick - sector.Tick) * FixedStepClock.TickSeconds:0} s", Severity.Muted));
        if (craft.HasCargoLimit && craft.CargoCapacityKg > 0)
            rows.Add(new("cargo", $"{craft.CargoKg / 1000.0:0.0} / {craft.CargoCapacityKg / 1000.0:0} t in the hold", Severity.Normal, craft.CargoKg / (double)craft.CargoCapacityKg));
        rows.Add(new("ok", $"{job.CompletedCycles} cycles    {job.DeliveredKg / 1000.0:N1} t delivered", Severity.Muted));
        foreach (var e in job.History.Reverse().Take(6)) rows.Add(new("message", $"{FormatTime(e.Tick)}  {e.Text}", Severity.Muted));
        _state.Set(rows);
        ShowRoute(craft);
    }

    /// <summary>The running job as a picture; without one, the job being set up.</summary>
    private void ShowRoute(CraftState? craft)
    {
        var sector = _sector();
        RouteStop Station(CraftId id) => sector.Crafts.FirstOrDefault(c => c.Id == id) is { } c ? new(c.Name, c.Design, "orbit") : new("?", null, "help");
        RouteStop Body(BodyId id) => sector.Bodies.FirstOrDefault(b => b.Id == id) is { } b ? new(b.Name, null, UiIcons.BodyIcon(b)) : new("?", null, "help");
        RouteStop Ship() => craft is null ? new("?", null, "fleet") : new(craft.Name, craft.Design, "fleet");
        CraftId At(OptionButton option, List<CraftId> ids) => option.Selected >= 0 && option.Selected < ids.Count ? ids[option.Selected] : CraftId.None;
        var stops = new List<RouteStop>();
        var goods = new List<string>();
        switch (craft?.Job is { Active: true } running ? running : null)
        {
            case MiningJob m: stops.Add(Body(m.SourceBodyId)); goods.Add("mat_" + m.MaterialId); stops.Add(Station(m.DestinationId)); break;
            case HaulJob h: stops.Add(Station(h.PickupId)); goods.Add("mat_" + h.MaterialId); stops.Add(Station(h.DestinationId)); break;
            case TradeJob t: stops.Add(Station(t.HomeId)); goods.Add("mat_" + t.GiveMaterial); stops.Add(Station(t.TraderId)); goods.Add("mat_" + t.GetMaterial); stops.Add(Station(t.HomeId)); break;
            case DefendJob d: stops.Add(Ship()); goods.Add("shield"); stops.Add(d.Mode == DefendMode.Patrol ? Body(d.StarId) : Station(d.PostId)); break;
            case SalvageJob s: stops.Add(s.WreckId.IsNone ? new("nearest wreck", null, "danger") : Station(s.WreckId)); goods.Add("salvage"); stops.Add(Station(s.DestinationId)); break;
            case SurveyJob s: stops.Add(Ship()); goods.Add("survey"); stops.Add(s.TargetBodyId.IsNone ? new("next body", null, "planet") : Body(s.TargetBodyId)); break;
            case ServeRequestsJob { Contract: { } c }: stops.Add(Station(c.PickupId)); goods.Add("mat_" + c.MaterialId); stops.Add(Station(c.DestinationId)); break;
            case ServeRequestsJob: stops.Add(new("any station", null, "orbit")); goods.Add("cargo"); stops.Add(new("who asks", null, "orbit")); break;
            default:
                switch (_kind.Selected)
                {
                    case 1:
                        stops.Add(Station(At(_pickup, _stations)));
                        goods.Add(_haulMaterial.Selected >= 0 && _haulMaterial.Selected < _materials.Materials.Count ? "mat_" + _materials.Materials[_haulMaterial.Selected].Id : "cargo");
                        stops.Add(Station(At(_haulDestination, _stations)));
                        break;
                    case 2: stops.Add(new("any station", null, "orbit")); goods.Add("cargo"); stops.Add(new("who asks", null, "orbit")); break;
                    case 3: stops.Add(Ship()); goods.Add("survey"); stops.Add(new("next body", null, "planet")); break;
                    case 4: stops.Add(new("nearest wreck", null, "danger")); goods.Add("salvage"); stops.Add(Station(At(_salvageDestination, _stations))); break;
                    case 5 or 6: stops.Add(Ship()); goods.Add("shield"); stops.Add(Station(At(_defendWhat, _posts))); break;
                    case 7: stops.Add(Ship()); goods.Add("shield"); stops.Add(_defendWhat.Selected >= 0 && _defendWhat.Selected < _systems.Count ? Body(_systems[_defendWhat.Selected]) : new("?", null, "star")); break;
                    case 8:
                        stops.Add(Station(At(_tradeHome, _stations)));
                        goods.Add(_tradeGive.Selected >= 0 && _tradeGive.Selected < _materials.Materials.Count ? "mat_" + _materials.Materials[_tradeGive.Selected].Id : "cargo");
                        stops.Add(Station(At(_tradeWith, _traders)));
                        goods.Add(_tradeGet.Selected >= 0 && _tradeGet.Selected < _tradeGoods.Count ? "mat_" + _tradeGoods[_tradeGet.Selected] : "cargo");
                        stops.Add(Station(At(_tradeHome, _stations)));
                        break;
                    default:
                        stops.Add(_source.Selected >= 0 && _source.Selected < _sources.Count ? Body(_sources[_source.Selected]) : new("?", null, "asteroid"));
                        goods.Add(_mineMaterial.Selected >= 0 && _mineMaterial.Selected < _deposits.Count ? "mat_" + _deposits[_mineMaterial.Selected] : "mining");
                        stops.Add(Station(At(_mineDestination, _stations)));
                        break;
                }
                break;
        }
        _route.Set(stops, goods);
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
        if (_kind.Selected == 3) { Show(_sector().StartJob(craft.Id, new SurveyJob())); return; }
        if (_kind.Selected == 8) // trade routes (#89)
        {
            if (_tradeWith.Selected < 0 || _tradeWith.Selected >= _traders.Count || _tradeGet.Selected < 0 || _tradeGet.Selected >= _tradeGoods.Count || _stations.Count == 0)
                Show(new ActionResult(false, "No trade station to trade with, or no station of yours."));
            else Show(_sector().StartJob(craft.Id, new TradeJob(_stations[Math.Max(0, _tradeHome.Selected)], _materials.Materials[Math.Max(0, _tradeGive.Selected)].Id,
                _traders[_tradeWith.Selected], _tradeGoods[_tradeGet.Selected])));
            return;
        }
        if (_kind.Selected >= 5) // fleet orders (#88)
        {
            var what = Math.Max(0, _defendWhat.Selected);
            if (_kind.Selected == 7) Show(_sector().StartJob(craft.Id, new DefendJob(_systems[what])));
            else if (what >= _posts.Count) Show(new ActionResult(false, _kind.Selected == 5 ? "No other station to guard." : "No other ship to escort."));
            else Show(_sector().StartJob(craft.Id, new DefendJob(_kind.Selected == 5 ? DefendMode.Guard : DefendMode.Escort, _posts[what])));
            return;
        }
        if (_stations.Count == 0 && _kind.Selected != 2) { Show(new ActionResult(false, "No station with a docking port.")); return; }

        if (_kind.Selected == 4)
        {
            Show(_sector().StartJob(craft.Id, new SalvageJob(_stations[Math.Max(0, _salvageDestination.Selected)])));
            return;
        }
        if (_kind.Selected == 0)
        {
            if (_source.Selected < 0 || _source.Selected >= _sources.Count) { Show(new ActionResult(false, "No mineable body.")); return; }
            var source = _sector().GetBody(_sources[_source.Selected]);
            if (!source.AnySiteSurveyed)
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
