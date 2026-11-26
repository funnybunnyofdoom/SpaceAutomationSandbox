using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Milestones (#38) as badges (#104): what the player has reached, and when, and what's left to try.</summary>
public partial class MilestonesPanel : PanelContainer
{
    private const double RefreshSeconds = 1;

    private static readonly Dictionary<string, string> Icons = new()
    {
        ["automated"] = "work", ["ships_10"] = "fleet", ["researched"] = "research", ["new_star"] = "gate", ["stars_5"] = "star", ["contact"] = "message",
        ["friend"] = "ok", ["trade"] = "trade", ["victory"] = "weapon", ["life"] = "life", ["thriving"] = "planet", ["swarm_1000"] = "swarm", ["ships_1000"] = "fleet",
        ["giant_ship"] = "shipyard", ["fusion"] = "power", ["antimatter"] = "mat_antimatter", ["quantum"] = "speed", ["dismantled"] = "mining", ["megastructure"] = "mega",
    };

    private readonly Func<SectorState> _sector;
    private readonly List<(Milestone Milestone, PanelContainer Badge, StyleBoxFlat Style, Label When)> _badges = new();
    private readonly InfoList _count = new();
    private double _sinceRefresh;

    public MilestonesPanel(Func<SectorState> sector)
    {
        _sector = sector;
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -452;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        var title = new Label { MouseFilter = MouseFilterEnum.Pass, TooltipText = "Nothing here ends the game: they're things to try." };
        InputActions.TitleWithKey(title, "MILESTONES", InputActions.Milestones);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(_count);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 5);
        grid.AddThemeConstantOverride("v_separation", 5);
        box.AddChild(grid);
        foreach (var milestone in Milestones.All)
        {
            var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.3f), ContentMarginLeft = 5, ContentMarginRight = 5, ContentMarginTop = 6, ContentMarginBottom = 5 };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(4);
            var badge = new PanelContainer { CustomMinimumSize = new Vector2(132, 128), TooltipText = $"{milestone.Title}\n{milestone.Hint}" };
            badge.AddThemeStyleboxOverride("panel", style);
            var inside = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            inside.AddThemeConstantOverride("separation", 2);
            badge.AddChild(inside);
            var icon = UiTheme.Icon(Icons.GetValueOrDefault(milestone.Id, "milestones"), 48);
            icon.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            inside.AddChild(icon);
            inside.AddChild(Text(milestone.Title, 14));
            var when = Text("", 12);
            when.Modulate = new Color(1, 1, 1, 0.7f);
            inside.AddChild(when);
            grid.AddChild(badge);
            _badges.Add((milestone, badge, style, when));
        }
    }

    public MilestonesPanel() : this(() => new SectorState(0)) { }

    private static Label Text(string text, int size)
    {
        var label = new Label
        {
            Text = text, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(120, 0),
        };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    public void Open()
    {
        Visible = true;
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
        var reached = _sector().MilestonesReached;
        var count = Milestones.All.Count(m => reached.ContainsKey(m.Id));
        _count.Set(new[] { new HudRow("milestones", $"{count} of {Milestones.All.Count} reached", Severity.Normal, count / (double)Milestones.All.Count) });
        foreach (var (milestone, badge, style, when) in _badges)
        {
            var done = reached.TryGetValue(milestone.Id, out var tick);
            var seconds = (long)(tick * FixedStepClock.TickSeconds);
            when.Text = !done ? milestone.Hint : tick > 0 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : "reached"; // a word or a time, not only the colour (#68)
            badge.Modulate = new Color(1, 1, 1, done ? 1 : 0.5f);
            style.BorderColor = done ? UiTheme.Accent : new Color(1, 1, 1, 0.15f);
            style.BgColor = done ? new Color(0.1f, 0.35f, 0.5f, 0.45f) : new Color(0, 0, 0, 0.3f);
        }
    }
}
