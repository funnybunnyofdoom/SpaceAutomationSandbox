using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// System map target list (M, #53): every body (moons indented under their planet) and every
/// station, with distance and rough travel time from the craft you're flying, and a Target button.
/// Shown while the main view is zoomed out to the whole system.
/// </summary>
public partial class MapPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _pilot;
    private readonly Action<BodyId> _targetBody;
    private readonly Action<CraftId> _targetCraft;
    private readonly Action<BodyId> _viewSystem;
    private VBoxContainer _rows = null!;
    private Label _title = null!;
    private readonly List<(Label Label, Func<string> Text)> _labels = new();
    private string _layoutKey = "";
    private double _sinceRefresh;

    public MapPanel(Func<SectorState> sector, Func<CraftState?> pilot, Action<BodyId> targetBody, Action<CraftId> targetCraft, Action<BodyId>? viewSystem = null)
    {
        _viewSystem = viewSystem ?? (_ => { });
        _sector = sector;
        _pilot = pilot;
        _targetBody = targetBody;
        _targetCraft = targetCraft;
        Build();
    }

    public MapPanel() : this(() => new SectorState(0), () => null, _ => { }, _ => { }) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -400;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var box = new VBoxContainer();
        AddChild(box);
        _title = new Label { Text = "SYSTEM MAP  (M to close)" };
        _title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_title);
        box.AddChild(new Label
        {
            Text = "Distances and rough travel times from the craft you fly (cruise V for long trips). Wheel zooms. G: galaxy view.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(360, 0), Modulate = new Color(1, 1, 1, 0.6f),
        });
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rows);
    }

    public void Open()
    {
        Visible = true;
        _layoutKey = "";
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    /// <summary>Rough door-to-door time: cruise for long legs, the job autopilot's speed for short ones.</summary>
    public static string TravelTime(double distanceM)
    {
        var seconds = distanceM > SectorState.AutopilotCruiseMinDistanceM
            ? (distanceM - SectorState.AutopilotCruiseArriveM) / SectorState.CruiseSpeedMps + SectorState.AutopilotCruiseArriveM / SectorState.JobCruiseSpeedMps + 60
            : distanceM / SectorState.JobCruiseSpeedMps;
        return seconds < 90 ? $"{seconds:0} s" : $"{seconds / 60:0} min";
    }

    private static string Distance(double m) => m >= 1_000_000 ? $"{m / 1e6:0.00} Mm" : m >= 1000 ? $"{m / 1000:0} km" : $"{m:0} m";

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var pilot = _pilot();
        var stations = sector.Crafts.Where(c => c.Stats.DockingPorts.Count > 0).Select(c => c.Id).ToList();
        var key = $"{sector.Bodies.Count}|{string.Join(",", stations)}";
        if (key != _layoutKey) Rebuild(sector, stations, key);
        foreach (var (label, text) in _labels) label.Text = text();
        _title.Text = pilot is null ? "SYSTEM MAP  (M to close)" : $"SYSTEM MAP from {pilot.Name}  (M to close)";
    }

    private void Rebuild(SectorState sector, List<CraftId> stations, string key)
    {
        _layoutKey = key;
        _labels.Clear();
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }

        void AddBody(CelestialBodyState body, int depth)
        {
            var id = body.Id;
            AddRow(new string(' ', depth * 3), () =>
            {
                var s = _sector();
                var pilot = _pilot();
                var b = s.GetBody(id);
                var what = b.Kind == BodyKind.Asteroid ? "" : $" ({b.Kind})";
                if (pilot is null) return b.Name + what;
                if (s.StarOf(id)?.Id != s.StarAt(pilot.Position)?.Id) return $"{b.Name}{what}  (another system: reached by warp)";
                var d = Math.Max(0, s.GetPosition(id).DistanceTo(pilot.Position) - b.RadiusM);
                return $"{b.Name}{what}  {Distance(d)}, ~{TravelTime(d)}";
            }, () => _targetBody(id));
            foreach (var child in sector.Bodies.Where(b => b.Orbit?.Parent == body.Id && b.Kind != BodyKind.Star)) AddBody(child, depth + 1);
        }
        // One tree per star system (#54), yours first; other systems are reached by warp.
        var here = _pilot() is { } me ? sector.StarAt(me.Position) : null;
        foreach (var star in sector.Stars.OrderBy(s => s.Id == here?.Id ? 0 : 1).ThenBy(s => s.Id.Value))
        {
            if (sector.Stars.Count > 1)
            {
                var header = new HBoxContainer();
                var kind = star.StarClass switch { StarClass.Red => "red dwarf", StarClass.Blue => "blue star", _ => "yellow star" };
                header.AddChild(new Label
                {
                    Text = $"{star.Name} system, {kind}" + (star.Id == here?.Id ? " (you are here)" : " (by warp)"),
                    Modulate = new Color(0.75f, 0.85f, 1f), SizeFlagsHorizontal = SizeFlags.ExpandFill,
                });
                var view = new Button { Text = "View map", FocusMode = FocusModeEnum.None };
                var starId = star.Id;
                view.Pressed += () => _viewSystem(starId);
                header.AddChild(view);
                _rows.AddChild(header);
            }
            AddBody(star, 0);
        }

        if (stations.Count > 0) _rows.AddChild(new Label { Text = "Stations", Modulate = new Color(1, 1, 1, 0.6f) });
        foreach (var id in stations)
        {
            AddRow("", () =>
            {
                var s = _sector();
                var pilot = _pilot();
                var c = s.Crafts.FirstOrDefault(x => x.Id == id);
                if (c is null) return "(gone)";
                if (pilot is null || pilot.Id == id) return c.Name;
                if (s.StarAt(c.Position)?.Id != s.StarAt(pilot.Position)?.Id) return $"{c.Name}  ({s.StarAt(c.Position)?.Name} system, by warp)";
                var d = c.Position.DistanceTo(pilot.Position);
                return $"{c.Name}  {Distance(d)}, ~{TravelTime(d)}";
            }, () => _targetCraft(id));
        }
    }

    private void AddRow(string indent, Func<string> text, Action target)
    {
        var row = new HBoxContainer();
        var label = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        row.AddChild(new Label { Text = indent });
        row.AddChild(label);
        var button = new Button { Text = "Target", FocusMode = FocusModeEnum.None };
        button.Pressed += target;
        row.AddChild(button);
        _rows.AddChild(row);
        _labels.Add((label, text));
    }
}
