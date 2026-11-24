using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Opens what a name in a panel stands for (#100). Set by the game; panels only say what was clicked.</summary>
public static class UiLinks
{
    public static Action<TargetRef> Open { get; set; } = _ => { };
}

/// <summary>Buttons joining panels that belong together (#100): the one you're in is pressed.</summary>
public static class UiTabs
{
    public static HBoxContainer Row(params (string Icon, string Text, Action? Press)[] tabs)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);
        foreach (var (icon, text, press) in tabs)
        {
            var button = new Button { Text = text, Icon = UiIcons.Get(icon), FocusMode = Control.FocusModeEnum.None, ToggleMode = press is null, ButtonPressed = press is null, TextureFilter = CanvasItem.TextureFilterEnum.Nearest };
            if (press is null) button.Toggled += _ => button.SetPressedNoSignal(true); // where you are: stays pressed
            else button.Pressed += press;
            row.AddChild(button);
        }
        return row;
    }
}

/// <summary>The message log (#100): what happened, newest first, by kind; a line about a craft or body opens it when clicked.</summary>
public partial class LogPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private const int MostRows = 200;

    private static readonly (EventKind? Kind, string Name, string Icon)[] Filters =
    {
        (null, "Everything", "map"), (EventKind.Danger, "Danger", "danger"), (EventKind.Civilization, "Civilizations", "trade"), (EventKind.Life, "Native life", "life"),
        (EventKind.Fleet, "Your craft", "fleet"), (EventKind.Milestone, "Milestones", "milestones"), (EventKind.Message, "Messages (this session)", "message"),
    };

    private readonly Func<SectorState> _sector;
    private readonly Func<IReadOnlyList<SectorEvent>> _messages;
    private readonly List<Button> _buttons = new();
    private readonly InfoList _rows = new();
    private LineEdit _search = null!;
    private int _filter;
    private string _key = "";
    private SectorState? _of;
    private SectorEvent? _seen;
    private double _sinceRefresh;

    public LogPanel(Func<SectorState> sector, Func<IReadOnlyList<SectorEvent>> messages)
    {
        _sector = sector;
        _messages = messages;
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -482;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "MESSAGE LOG", InputActions.Log);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        var filters = new HFlowContainer();
        box.AddChild(filters);
        var group = new ButtonGroup();
        for (var i = 0; i < Filters.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                ToggleMode = true, ButtonGroup = group, ButtonPressed = i == 0, FocusMode = FocusModeEnum.None, TooltipText = Filters[i].Name,
                Icon = Filters[i].Kind is null ? null : UiIcons.Get(Filters[i].Icon), TextureFilter = TextureFilterEnum.Nearest,
            };
            button.Pressed += () => { _filter = index; _key = ""; Refresh(); };
            filters.AddChild(button);
            _buttons.Add(button);
        }
        _search = new LineEdit { PlaceholderText = "Search the log", ClearButtonEnabled = true };
        _search.TextChanged += _ => { _key = ""; Refresh(); };
        box.AddChild(_search);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        scroll.AddChild(_rows);
    }

    public LogPanel() : this(() => new SectorState(0), () => Array.Empty<SectorEvent>()) { }

    /// <summary>A line of the log; also used by other panels that show events.</summary>
    public static HudRow Row(SectorEvent e)
    {
        var seconds = (long)(e.Tick * FixedStepClock.TickSeconds);
        var icon = e.Kind switch
        {
            EventKind.Danger => "danger", EventKind.Civilization => "trade", EventKind.Life => "life", EventKind.Fleet => "fleet", EventKind.Milestone => "milestones", _ => "message",
        };
        return new HudRow(icon, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}  {e.Text}", e.Kind == EventKind.Danger ? Severity.Bad : e.Kind == EventKind.Message ? Severity.Muted : Severity.Normal,
            Tip: e.Target.IsNone ? "" : "Click to go there", Link: e.Target.IsNone ? null : e.Target);
    }

    public void Open()
    {
        Visible = true;
        _key = "";
        Refresh();
    }

    /// <summary>Events since the log was last open; what a loaded game already had isn't new.</summary>
    public int NewEvents()
    {
        var events = _sector().Events;
        if (!ReferenceEquals(_of, _sector()) || Visible)
        {
            _of = _sector();
            _seen = events.Count > 0 ? events[^1] : null;
            return 0;
        }
        for (var i = events.Count - 1; i >= 0; i--) if (ReferenceEquals(events[i], _seen)) return events.Count - 1 - i;
        return events.Count;
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
        NewEvents();
        var all = _sector().Events.Concat(_messages()).ToList();
        var key = $"{all.Count}|{(all.Count > 0 ? all.Max(e => e.Tick) : 0)}|{_messages().Count}|{_sector().Events.Count}";
        if (key == _key) return;
        _key = key;
        for (var i = 0; i < Filters.Length; i++)
        {
            var count = Filters[i].Kind is { } kind ? all.Count(e => e.Kind == kind) : all.Count;
            _buttons[i].Text = Filters[i].Kind is null ? $"All {count}" : $"{count}";
        }
        var search = _search.Text.Trim();
        var shown = all.Select((e, i) => (e, i)).Where(x => (Filters[_filter].Kind is not { } kind || x.e.Kind == kind) && (search.Length == 0 || x.e.Text.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.e.Tick).ThenByDescending(x => x.i).Take(MostRows).Select(x => Row(x.e)).ToList();
        if (shown.Count == 0) shown.Add(new HudRow("message", "Nothing here yet.", Severity.Muted));
        _rows.Set(shown);
    }
}
