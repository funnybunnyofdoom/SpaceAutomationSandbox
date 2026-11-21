using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Fleet manager (key F) for many craft: a summary (how many are automated, how many are waiting),
/// search by name or id, filters (ships, stations, automated, waiting, idle), sorting, and per-row
/// Fly / Target buttons. Rows are rebuilt only when the visible set changes; texts update in place.
/// </summary>
public partial class FleetPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private static readonly string[] Filters = { "All", "Ships", "Stations", "Automated", "Waiting", "Idle ships" };
    private static readonly string[] Sorts = { "Id", "Name", "Distance", "State" };

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftId?> _piloted;
    private Label _title = null!;
    private Label _summary = null!;
    private LineEdit _search = null!;
    private OptionButton _filter = null!;
    private OptionButton _sort = null!;
    private VBoxContainer _rows = null!;
    private double _sinceRefresh;
    private List<CraftId> _shown = new();
    private string _signature = "";

    /// <summary>Raised when the player picks a craft to fly.</summary>
    public event Action<CraftId>? CraftChosen;

    /// <summary>Raised when the player picks a craft as the piloted ship's target.</summary>
    public event Action<CraftId>? CraftTargeted;

    public FleetPanel(Func<SectorState> sector, Func<CraftId?> piloted)
    {
        _sector = sector;
        _piloted = piloted;
        Build();
    }

    public FleetPanel() : this(() => new SectorState(0), () => null) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.BottomLeft);
        OffsetLeft = 12;
        OffsetRight = 492;
        OffsetTop = -340; // below the HUD text
        OffsetBottom = -12;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_title);
        _summary = new Label { Modulate = new Color(1, 1, 1, 0.75f) };
        box.AddChild(_summary);

        var controls = new HBoxContainer();
        box.AddChild(controls);
        _search = new LineEdit { PlaceholderText = "Search name or id", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _search.TextChanged += _ => Refresh(force: true);
        controls.AddChild(_search);
        _filter = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var f in Filters) _filter.AddItem(f);
        _filter.ItemSelected += _ => Refresh(force: true);
        controls.AddChild(_filter);
        _sort = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var s in Sorts) _sort.AddItem($"by {s}");
        _sort.ItemSelected += _ => Refresh(force: true);
        controls.AddChild(_sort);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rows);
    }

    public void Open()
    {
        Visible = true;
        Refresh(force: true);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private static bool IsShip(CraftState c) => c.Stats.TotalThrustN > 0;
    private static bool IsWaiting(CraftState c) => c.Job is { Active: true } job && job.Status.StartsWith("waiting");

    private bool Matches(CraftState c, string search) =>
        (search.Length == 0 || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Id.ToString().Contains(search))
        && _filter.Selected switch
        {
            1 => IsShip(c),
            2 => !IsShip(c),
            3 => c.IsAutomated,
            4 => IsWaiting(c),
            5 => IsShip(c) && !c.IsAutomated && c.Id != _piloted(),
            _ => true,
        };

    private void Refresh(bool force = false)
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var crafts = sector.Crafts;
        var pilot = _piloted() is { } id && crafts.Any(c => c.Id == id) ? sector.GetCraft(id) : null;

        _title.Text = $"FLEET ({crafts.Count})  (F to close)";
        _summary.Text = $"{crafts.Count(IsShip)} ships, {crafts.Count(c => !IsShip(c))} stations   "
                        + $"{crafts.Count(c => c.IsAutomated)} automated, {crafts.Count(IsWaiting)} waiting";

        double Distance(CraftState c) => pilot is null ? 0 : pilot.Position.DistanceTo(c.Position);
        var search = _search.Text.Trim();
        var visible = crafts.Where(c => Matches(c, search));
        visible = _sort.Selected switch
        {
            1 => visible.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id.Value),
            2 => visible.OrderBy(Distance).ThenBy(c => c.Id.Value),
            3 => visible.OrderBy(c => State(sector, c)).ThenBy(c => c.Id.Value),
            _ => visible.OrderBy(c => c.Id.Value),
        };
        var shown = visible.Select(c => c.Id).ToList();

        var signature = string.Join(",", shown.Select(i => i.Value)) + "|" + _piloted();
        if (force || signature != _signature)
        {
            _signature = signature;
            _shown = shown;
            foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
            foreach (var craftId in _shown) _rows.AddChild(MakeRow(craftId));
            if (_shown.Count == 0) _rows.AddChild(new Label { Text = "  No craft match.", Modulate = new Color(1, 1, 1, 0.6f) });
        }

        // Update texts in place (positions, speeds and job states change all the time).
        for (var i = 0; i < _shown.Count && i < _rows.GetChildCount(); i++)
        {
            var craft = sector.GetCraft(_shown[i]);
            var row = _rows.GetChild(i);
            row.GetNode<Label>("Text").Text = Describe(sector, craft, pilot);
        }
    }

    private HBoxContainer MakeRow(CraftId id)
    {
        var row = new HBoxContainer();
        var you = _piloted() == id;
        row.AddChild(new Label
        {
            Name = "Text", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true,
            Modulate = you ? new Color(0.6f, 1f, 0.7f) : new Color(1, 1, 1),
        });
        var fly = new Button { Text = you ? "Flying" : "Fly", Disabled = you, FocusMode = FocusModeEnum.None };
        fly.Pressed += () => { CraftChosen?.Invoke(id); Refresh(force: true); };
        row.AddChild(fly);
        var target = new Button { Text = "Target", Disabled = you, FocusMode = FocusModeEnum.None };
        target.Pressed += () => CraftTargeted?.Invoke(id);
        row.AddChild(target);
        return row;
    }

    public static string Where(SectorState sector, CraftState craft) =>
        craft.IsDocked ? $"docked at {sector.GetCraft(craft.DockedToCraftId).Name}, port {craft.DockPortIndex + 1}"
        : craft.IsLanded ? $"{(craft.IsAnchored ? "anchored to" : "landed on")} {sector.GetBody(craft.LandedOnBodyId).Name}"
        : craft.RailOrbit is { } rail ? $"{(craft.IsShipyard ? "orbiting" : "parked in orbit around")} {sector.GetBody(rail.Parent).Name}"
        : $"flying, {craft.SpeedMps:0} m/s";

    /// <summary>Sort key and one-word state: waiting / working / building / idle.</summary>
    private static string State(SectorState sector, CraftState c) =>
        IsWaiting(c) ? "0 waiting" : c.IsAutomated ? "1 working" : c.BuildQueue.Count > 0 ? "2 building" : "3 idle";

    private static string Describe(SectorState sector, CraftState craft, CraftState? pilot)
    {
        var role = craft.IsShipyard ? "shipyard" : IsShip(craft) ? "ship" : "station";
        var distance = pilot is null || pilot == craft ? "" : $", {FormatDistance(pilot.Position.DistanceTo(craft.Position))} away";
        var second = Where(sector, craft);
        if (craft.BuildQueue.Count > 0) second += $"   building {craft.BuildQueue[0].Fraction * 100:0}%";
        if (craft.Job is { Active: true } job) second += $"\n   {job.Kind}: {job.Status} ({job.CompletedCycles} cycles)";
        else if (IsShip(craft) && pilot != craft) second += "   (idle)";
        return $"{craft.Name}  ({craft.Id}, {role}{distance})\n   {second}";
    }

    private static string FormatDistance(double m) => m >= 1000 ? $"{m / 1000:0.0} km" : $"{m:0} m";
}
