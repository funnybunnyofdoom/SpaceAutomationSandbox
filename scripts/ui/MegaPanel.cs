using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Megastructures (#126): what can be founded where you are, and every project with its stage, what it waits for, and what it gives.</summary>
public partial class MegaPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _pilot;
    private readonly MaterialCatalog _materials;
    private readonly VBoxContainer _box = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly VBoxContainer _move = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly SpinBox _radius = new() { MinValue = 1, MaxValue = 5_000_000, Step = 1, Suffix = "km", CustomMinimumSize = new Vector2(140, 0) };
    private readonly InfoList _plan = new();
    private readonly Button _start = new() { Text = "Start the move", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
    private BodyId _moving = BodyId.None;

    /// <summary>Where a move would take the body (#127): its new orbit radius, or null when no move is planned in the panel.</summary>
    public (BodyId Body, double RadiusM)? Planned => Visible && !_moving.IsNone ? (_moving, _radius.Value * 1000) : null;
    private readonly List<Action> _live = new();
    private string _key = "";
    private double _sinceRefresh;

    public event Action<ActionResult>? Acted;

    public MegaPanel(Func<SectorState> sector, Func<CraftState?> pilot, MaterialCatalog materials)
    {
        (_sector, _pilot, _materials) = (sector, pilot, materials);
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -492;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        AddChild(outer);
        var title = new Label();
        InputActions.TitleWithKey(title, "MEGASTRUCTURES", InputActions.Megastructures);
        title.AddThemeFontSizeOverride("font_size", 18);
        outer.AddChild(title);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        _box.AddThemeConstantOverride("separation", 5);
        var inside = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        inside.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(inside);
        inside.AddChild(_box);
        inside.AddChild(_move); // #127
        _move.AddThemeConstantOverride("separation", 5);
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "New orbit radius" });
        row.AddChild(_radius);
        _move.AddChild(UiTheme.Header("Move a world"));
        _move.AddChild(row);
        _move.AddChild(_plan);
        _move.AddChild(_start);
        _radius.ValueChanged += _ => ShowPlan();
        _start.Pressed += () =>
        {
            if (_moving.IsNone) return;
            Acted?.Invoke(_sector().StartMove(_moving, _radius.Value * 1000));
            ShowPlan();
        };
    }

    public MegaPanel() : this(() => new SectorState(0), () => null, new MaterialCatalog(Array.Empty<MaterialDefinition>())) { }

    public void Open()
    {
        Visible = true;
        _key = "";
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private string Kg(IReadOnlyDictionary<string, long> kg) => string.Join(", ", kg.Select(m => $"{m.Value / 1000.0:0.#} t {(_materials.Contains(m.Key) ? _materials.Get(m.Key).Name.ToLowerInvariant() : m.Key)}"));

    /// <summary>Where a new one would go: the body you're targeting, else the nearest.</summary>
    private static CelestialBodyState? Place(SectorState sector, CraftState? pilot) =>
        pilot is null ? null : !pilot.TargetBodyId.IsNone && sector.Exists(new TargetRef(CraftId.None, pilot.TargetBodyId)) ? sector.GetBody(pilot.TargetBodyId) : sector.NearestBody(pilot.Position).Body;

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var place = Place(sector, _pilot());
        var key = $"{place?.Id}|{sector.UnlockedTech.Count}|" + string.Join(";", sector.Megas.Select(m => $"{m.Id}{m.Stages}{m.Working}{m.Paused}{m.Neglected}"));
        if (key != _key) Rebuild(sector, place, key);
        foreach (var live in _live) live();
        MoveSection(sector, place);
    }

    /// <summary>Moving the targeted body (#127): what it would take, or how its move is going.</summary>
    private void MoveSection(SectorState sector, CelestialBodyState? body)
    {
        if (body is null || body.Orbit is not { } orbit || body.Kind == BodyKind.Star || !sector.Exists(new TargetRef(CraftId.None, orbit.Parent)))
        {
            _moving = BodyId.None;
            _plan.Set(new[] { new HudRow("planet", "Target a rock, moon or planet to plan moving it to a new orbit round what it orbits.", Severity.Muted) });
            _radius.Visible = _start.Visible = false;
            return;
        }
        _radius.Visible = _start.Visible = true;
        if (_moving != body.Id)
        {
            _moving = body.Id;
            _radius.SetValueNoSignal(Math.Round((orbit.Move is null ? orbit.RadiusM : orbit.RadiusM) / 1000 * 0.8));
        }
        ShowPlan();
    }

    private void ShowPlan()
    {
        var sector = _sector();
        if (_moving.IsNone || !sector.Exists(new TargetRef(CraftId.None, _moving))) return;
        var body = sector.GetBody(_moving);
        var parent = sector.GetBody(body.Orbit!.Parent);
        var rows = new List<HudRow> { new("planet", $"{body.Name} orbits {parent.Name} at {body.Orbit.RadiusM / 1000:0.#} km") };
        if (sector.Moves.TryGetValue(_moving, out var move))
        {
            var share = (double)(sector.Tick - move.StartTick) / (move.EndTick - move.StartTick);
            rows.Add(new HudRow("speed", $"Moving: {(move.EndTick - sector.Tick) * FixedStepClock.TickSeconds / 3600:0.0} h left", Severity.Good, Math.Clamp(share, 0, 1)));
            rows.Add(new HudRow("mat_rock", $"{move.ThrownKg / 1000.0:N0} of {move.ReactionKg / 1000.0:N0} t of rock thrown", Severity.Muted));
            _plan.Set(rows);
            _start.Disabled = true;
            return;
        }
        var plan = sector.PlanMove(_moving, _radius.Value * 1000);
        rows.Add(new HudRow("speed", $"{plan.DeltaVMps:0.0} m/s of change: {plan.ReactionKg / 1000.0:N0} t of its rock thrown", Severity.Normal, Tip: $"Mass drivers throw rock at {SectorState.DriverExhaustMps / 1000:0} km/s"));
        rows.Add(new HudRow("power", plan.Drivers > 0 ? $"{plan.Drivers} mass drivers: {plan.ThrowKgPerS / 1000:0.#} t/s, {plan.PowerKW / 1000:0} MW while it moves" : "No mass drivers anchored on it", plan.Drivers > 0 ? Severity.Normal : Severity.Warning));
        if (!double.IsInfinity(plan.Seconds)) rows.Add(new HudRow("clock", $"{plan.Seconds / 3600:0.0} h to get there"));
        foreach (var problem in plan.Problems) rows.Add(new HudRow("warn", problem, Severity.Warning));
        _plan.Set(rows);
        _start.Disabled = !plan.CanStart;
        _start.TooltipText = plan.Problems.FirstOrDefault() ?? "Everything on it and round it comes along";
    }

    private void Rebuild(SectorState sector, CelestialBodyState? place, string key)
    {
        _key = key;
        _live.Clear();
        foreach (var child in _box.GetChildren()) { _box.RemoveChild(child); child.QueueFree(); }
        var kinds = sector.Parts?.Megastructures.Kinds ?? Array.Empty<MegaKind>();
        foreach (var project in sector.Megas) Project(sector, project);
        _box.AddChild(UiTheme.Header(place is null ? "Found one" : $"Found one at {place.Name}"));
        Say(place is null ? "Fly a craft first." : "Target a planet, moon or star (or fly near one) to found one there. Its hub appears in orbit and asks for each stage's materials: freight and serve-requests freighters bring them.");
        foreach (var kind in kinds)
        {
            var why = place is null ? "Fly a craft first." : sector.FoundBlocker(kind, place);
            var card = Card();
            var info = new InfoRow();
            info.Set(new HudRow("mega", $"{kind.Name}: {kind.Stages.Count} stages, {kind.TotalKg / 1000:N0} t in all", why is null ? Severity.Normal : Severity.Muted, Tip: kind.Description));
            card.AddChild(info);
            card.AddChild(Text(kind.Description + $" Upkeep: {Kg(kind.UpkeepKgPerHour)} an hour."));
            var found = new Button { Text = place is null ? "Found" : $"Found at {place.Name}", Disabled = why is not null, TooltipText = why ?? "", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            var (id, body) = (kind.Id, place?.Id ?? BodyId.None);
            found.Pressed += () => { Acted?.Invoke(_sector().FoundMega(id, body)); _key = ""; Refresh(); };
            card.AddChild(found);
            if (why is not null) card.AddChild(Text(why));
        }
    }

    private VBoxContainer Card()
    {
        var panel = new PanelContainer();
        var back = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.05f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
        back.SetCornerRadiusAll(4);
        panel.AddThemeStyleboxOverride("panel", back);
        _box.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        panel.AddChild(box);
        return box;
    }

    private void Project(SectorState sector, MegaProject project)
    {
        if (sector.KindOf(project) is not { } kind) return;
        var id = project.Id;
        var card = Card();
        var head = new InfoRow();
        var hub = sector.Exists(new TargetRef(project.HubId, BodyId.None)) ? new TargetRef(project.HubId, BodyId.None) : (TargetRef?)null;
        head.Set(new HudRow("mega", project.Name, Link: hub, Tip: "Click: its hub"));
        card.AddChild(head);
        var stages = new InfoRow();
        var now = new InfoRow();
        var gives = new InfoRow();
        card.AddChild(stages);
        card.AddChild(now);
        card.AddChild(gives);
        var pause = new Button { Text = project.Paused ? "Go on building" : "Pause", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin, TooltipText = "Paused, it keeps what's built and still works; it asks only for upkeep" };
        pause.Pressed += () => { Acted?.Invoke(_sector().PauseMega(id, !project.Paused)); _key = ""; Refresh(); };
        if (project.Stages < kind.Stages.Count) card.AddChild(pause);
        _live.Add(() =>
        {
            var s = _sector();
            if (s.Megas.FirstOrDefault(m => m.Id == id) is not { } p) return;
            stages.Set(new HudRow("mega", $"{p.Stages} of {kind.Stages.Count} stages{(p.Stages == kind.Stages.Count ? ": finished" : "")}", Severity.Normal, (double)p.Stages / kind.Stages.Count));
            if (!s.Exists(new TargetRef(p.HubId, BodyId.None))) { now.Set(new HudRow("danger", "Its hub is gone: it does nothing.", Severity.Bad)); gives.Set(null); return; }
            var station = s.GetCraft(p.HubId);
            if (p.Stages >= kind.Stages.Count) now.Set(null);
            else if (p.Paused) now.Set(new HudRow("wait", "Paused.", Severity.Muted));
            else if (p.Working)
                now.Set(new HudRow("shipyard", $"{kind.Stages[p.Stages].Name}: being built", Severity.Good, p.WorkTicks / (kind.Stages[p.Stages].WorkMinutes * 60 * FixedStepClock.TicksPerSecond),
                    Tip: station.PowerShort ? "Its hub is short of power: work waits" : ""));
            else
            {
                var missing = station.Inventory.Shortfall(kind.Stages[p.Stages].MaterialsKg);
                now.Set(new HudRow("wait", $"{kind.Stages[p.Stages].Name}: waiting for {Kg(missing)} at {station.Name}", Severity.Warning, Tip: "Its hub has requests for them: serve-requests freighters bring them, or a freight job"));
            }
            var share = s.Share(p);
            var e = kind.Effect;
            var what = new List<string>();
            if (e.StorageKg > 0) what.Add($"{e.StorageKg * share / 1e6:0.#} kt storage");
            if (e.PowerKW > 0) what.Add($"{e.PowerKW * share / 1000:0.#} MW{(e.PowerToSystem ? " to your stations in the system" : "")}");
            if (e.LiftKgPerMinute > 0) what.Add($"lifts {e.LiftKgPerMinute * share / 1000:0.#} t a minute");
            if (e.FabricationKgPerS > 0) what.Add($"builds {e.FabricationKgPerS * share:0} kg/s more");
            gives.Set(p.Neglected ? new HudRow("warn", $"Stopped: its hub lacks {Kg(kind.UpkeepKgPerHour)} an hour for upkeep", Severity.Bad)
                : new HudRow("ok", share > 0 ? "Gives " + string.Join(", ", what) : "Gives nothing until its first stage is done", share > 0 ? Severity.Good : Severity.Muted));
        });
    }

    private void Say(string text) => _box.AddChild(Text(text));

    private static Label Text(string text) => new() { Text = text, ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0), Modulate = new Color(1, 1, 1, 0.75f) };
}
