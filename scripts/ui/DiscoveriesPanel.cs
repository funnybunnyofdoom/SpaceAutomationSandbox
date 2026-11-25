using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Points of interest (#122): what can be decided where you are, what's been found, and the signs of what hasn't.</summary>
public partial class DiscoveriesPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _pilot;
    private readonly Action<int, string> _choose;
    private readonly VBoxContainer _box = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly List<Action> _live = new();
    private string _key = "";
    private double _sinceRefresh;

    /// <param name="choose">A point of interest and what's chosen there.</param>
    public DiscoveriesPanel(Func<SectorState> sector, Func<CraftState?> pilot, Action<int, string> choose)
    {
        (_sector, _pilot, _choose) = (sector, pilot, choose);
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -472;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        AddChild(outer);
        var title = new Label();
        InputActions.TitleWithKey(title, "DISCOVERIES", InputActions.Discoveries);
        title.AddThemeFontSizeOverride("font_size", 18);
        outer.AddChild(title);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        _box.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(_box);
    }

    public DiscoveriesPanel() : this(() => new SectorState(0), () => null, (_, _) => { }) { }

    public static string Icon(PointKind? kind) => kind?.Type switch { PointType.Derelict => "salvage", PointType.Ruin => "ruin", _ => "discovery" };

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

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var pilot = _pilot();
        var here = pilot is null ? null : sector.StarAt(pilot.Position);
        var found = sector.Points.Where(p => p.Found).OrderBy(p => p.Done).ThenBy(p => pilot is null || sector.PointBlocker(pilot, p) is not null).ThenBy(p => sector.StarOf(p.Parent)?.Id == here?.Id ? 0 : 1).ThenBy(p => p.Id).ToList();
        var key = $"{sector.Options.Summary()}|{sector.Points.Count(p => !p.Found)}|{pilot?.Id}|{here?.Id}|"
                  + string.Join(";", found.Select(p => $"{p.Id},{p.Studied},{p.Done},{(pilot is null ? "" : sector.PointBlocker(pilot, p) is null ? string.Join("/", sector.PointChoices(pilot, p).Select(c => c.Id + c.Detail)) : "away")}"));
        if (key != _key) Rebuild(sector, pilot, here, found, key);
        foreach (var live in _live) live();
    }

    private void Rebuild(SectorState sector, CraftState? pilot, CelestialBodyState? here, List<PointOfInterest> found, string key)
    {
        _key = key;
        _live.Clear();
        foreach (var child in _box.GetChildren()) { _box.RemoveChild(child); child.QueueFree(); }
        if (sector.Points.Count == 0)
        {
            Say(sector.Options[WorldOptions.PointsOfInterest] == WorldAmount.Off
                ? "Points of interest are off in this game. The pause menu can turn them on for star systems opened from then on."
                : "There's nothing here to discover yet. Star systems you open from now on have points of interest.");
            return;
        }
        var open = found.Where(p => !p.Done).ToList();
        if (open.Count > 0) _box.AddChild(UiTheme.Header("To explore"));
        foreach (var point in open) AddOpen(sector, pilot, point);

        var signs = sector.Points.Where(p => !p.Found && !p.Done && sector.KindOf(p) is not null).ToList();
        if (signs.Count > 0)
        {
            _box.AddChild(UiTheme.Header("Signs of something"));
            Say("Pass near to find it: the ship you fly and scouts with a survey job do. The map shows roughly where.");
            foreach (var group in signs.GroupBy(p => p.Parent).OrderBy(g => sector.StarOf(g.Key)?.Id == here?.Id ? 0 : 1).ThenBy(g => g.Key.Value))
            {
                if (!sector.Exists(new TargetRef(CraftId.None, group.Key))) continue;
                var body = sector.GetBody(group.Key);
                var system = sector.Stars.Count > 1 && sector.StarOf(body.Id) is { } star ? $" ({star.Name} system)" : "";
                var row = new InfoRow();
                row.Set(new HudRow("discovery", $"{group.Count()} near {body.Name}{system}", Severity.Muted, Tip: "Click to go there", Link: TargetRef.Of(body)));
                _box.AddChild(row);
            }
        }
        var done = found.Where(p => p.Done).ToList();
        if (done.Count > 0) _box.AddChild(UiTheme.Header("Done"));
        foreach (var point in done)
        {
            var target = sector.TargetOf(point);
            var row = new InfoRow();
            row.Set(new HudRow(Icon(sector.KindOf(point)), $"{point.Name}: {point.Result}", Severity.Muted, Link: target.IsNone ? null : target));
            _box.AddChild(row);
        }
    }

    private void AddOpen(SectorState sector, CraftState? pilot, PointOfInterest point)
    {
        var id = point.Id;
        var kind = sector.KindOf(point);
        var card = new PanelContainer();
        var back = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.05f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
        back.SetCornerRadiusAll(4);
        card.AddThemeStyleboxOverride("panel", back);
        _box.AddChild(card);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);
        var name = new InfoRow();
        box.AddChild(name);
        var body = sector.Exists(new TargetRef(CraftId.None, point.Parent)) ? sector.GetBody(point.Parent).Name : "?";
        var target = sector.TargetOf(point);
        name.Set(new HudRow(Icon(kind), $"{point.Name}: {(kind?.Name ?? "something unknown").ToLowerInvariant()} {(point.Orbit is null ? "on" : "near")} {body}", Tip: "Click to go there", Link: target.IsNone ? null : target));
        var about = sector.FoundIn(point)?.Text is { Length: > 0 } text ? text : kind?.Description ?? "This game's data doesn't have this kind.";
        box.AddChild(Text(about));
        var status = new InfoRow();
        box.AddChild(status);
        var choices = pilot is null || sector.PointBlocker(pilot, point) is not null ? Array.Empty<PointChoice>() : sector.PointChoices(pilot, point);
        var buttons = new List<(Button Button, Label Detail, PointChoice Choice)>();
        foreach (var choice in choices)
        {
            var button = new Button { Text = choice.Text, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            button.Pressed += () => { _choose(id, choice.Id); _key = ""; Refresh(); };
            box.AddChild(button);
            var detail = Text(choice.Detail);
            box.AddChild(detail);
            buttons.Add((button, detail, choice));
        }
        _live.Add(() =>
        {
            var s = _sector();
            if (s.PointById(id) is not { } p || _pilot() is not { } ship || s.KindOf(p) is not { } k) { status.Set(null); return; }
            var away = s.PointBlocker(ship, p);
            var distance = s.PointPosition(p, s.TimeS) is { } at && s.StarAt(at)?.Id == s.StarAt(ship.Position)?.Id ? MapPanel.Distance(at.DistanceTo(ship.Position)) + " away. " : "In another system. ";
            status.Set(k.Type != PointType.Derelict && !p.Studied
                ? new HudRow("research", away is null ? $"{(k.Type == PointType.Ruin ? "Studying" : "Scanning")}..." : distance + away, away is null ? Severity.Good : Severity.Normal, away is null || p.StudyTicks > 0 ? Progress(p, k) : null)
                : away is not null ? new HudRow("target", distance + away, Severity.Normal) : new HudRow("ok", "You're there: decide.", Severity.Good));
            var now = s.PointChoices(ship, p);
            foreach (var (button, detail, choice) in buttons)
            {
                var blocker = now.FirstOrDefault(c => c.Id == choice.Id)?.Blocker;
                button.Disabled = blocker is not null;
                detail.Text = choice.Detail + (blocker is null ? "" : $" {blocker}");
            }
        });
    }

    public static double Progress(PointOfInterest point, PointKind kind) => Math.Min(1, point.StudyTicks * FixedStepClock.TickSeconds / Math.Max(kind.StudySeconds, 1e-9));

    private void Say(string text) => _box.AddChild(Text(text));

    private static Label Text(string text) => new() { Text = text, ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0), Modulate = new Color(1, 1, 1, 0.75f) };
}
