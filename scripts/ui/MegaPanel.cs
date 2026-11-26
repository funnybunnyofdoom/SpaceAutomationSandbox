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
        scroll.AddChild(_box);
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
