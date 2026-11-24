using System;
using System.Linq;
using System.Text;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Milestones (#38): what the player has reached, and when, and what's left to try.</summary>
public partial class MilestonesPanel : PanelContainer
{
    private const double RefreshSeconds = 1;

    private readonly Func<SectorState> _sector;
    private Label _list = null!;
    private double _sinceRefresh;

    public MilestonesPanel(Func<SectorState> sector)
    {
        _sector = sector;
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -412;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "MILESTONES", InputActions.Milestones);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(new Label
        {
            Text = "Nothing here ends the game: they're things to try.", Modulate = new Color(1, 1, 1, 0.65f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(370, 0),
        });
        _list = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(370, 0) };
        box.AddChild(_list);
    }

    public MilestonesPanel() : this(() => new SectorState(0)) { }

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
        var text = new StringBuilder($"{Milestones.All.Count(m => reached.ContainsKey(m.Id))} of {Milestones.All.Count} reached\n\n");
        foreach (var milestone in Milestones.All)
        {
            if (!reached.TryGetValue(milestone.Id, out var tick)) { text.Append($"[ ] {milestone.Title}\n      {milestone.Hint}\n"); continue; }
            var seconds = (long)(tick * FixedStepClock.TickSeconds);
            text.Append($"[x] {milestone.Title}{(tick > 0 ? $"   at {seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : "")}\n");
        }
        _list.Text = text.ToString();
    }
}
