using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Laws and civilizations (L, #36): switch laws on or off (with what each one does and what every
/// civilization you've met thinks of it), each civilization's attitude and where it's heading, and the
/// latest events. Attitude moves slowly towards the target, so a change of law takes a while to tell.
/// </summary>
public partial class LawsPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private readonly Func<SectorState> _sector;
    private readonly Action _changed;
    private VBoxContainer _laws = null!, _factions = null!;
    private Label _events = null!, _status = null!;
    private readonly List<(string LawId, CheckBox Box, Label Views)> _lawRows = new();
    private double _sinceRefresh;
    private bool _updating;

    public LawsPanel(Func<SectorState> sector, Action changed)
    {
        _sector = sector;
        _changed = changed;
        Build();
    }

    public LawsPanel() : this(() => new SectorState(0), () => { }) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -470;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "LAWS AND CIVILIZATIONS", InputActions.Laws);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(Dim("Your laws shape how the civilizations you meet see you. Each one drifts slowly towards what it thinks of the laws in force; "
                         + "mining in a civilization's system angers it. Hostile ones raid your gate in their system."));

        box.AddChild(Heading("Laws"));
        _laws = new VBoxContainer();
        box.AddChild(_laws);
        foreach (var law in Laws.All)
        {
            var check = new CheckBox { Text = law.Name, FocusMode = FocusModeEnum.None, TooltipText = law.Effect };
            var id = law.Id;
            check.Toggled += on =>
            {
                if (_updating) return;
                var result = _sector().SetLaw(id, on);
                _status.Text = result.Message;
                _changed();
                Refresh();
            };
            _laws.AddChild(check);
            _laws.AddChild(Dim("   " + law.Effect));
            var views = Dim("");
            _laws.AddChild(views);
            _lawRows.Add((id, check, views));
        }
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0) };
        box.AddChild(_status);

        box.AddChild(Heading("Civilizations"));
        _factions = new VBoxContainer();
        box.AddChild(_factions);

        box.AddChild(Heading("Recent events"));
        _events = Dim("");
        box.AddChild(_events);
    }

    private static Label Heading(string text)
    {
        var label = new Label { Text = text, Modulate = new Color(0.75f, 0.85f, 1f) };
        label.AddThemeFontSizeOverride("font_size", 16);
        return label;
    }

    private static Label Dim(string text) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0), Modulate = new Color(1, 1, 1, 0.65f),
    };

    public void Open()
    {
        Visible = true;
        _status.Text = "";
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds) return;
        _sinceRefresh = 0;
        Refresh();
    }

    /// <summary>"Hostile (−42, heading to −15)" with a word, not only a colour (#68).</summary>
    private static string AttitudeText(Faction f, IReadOnlyCollection<string> laws) =>
        $"{f.Disposition} ({f.Attitude:+0;-0;0}, heading to {f.TargetAttitude(laws):+0;-0;0})";

    private static Color DispositionColor(Disposition d) => d switch
    {
        Disposition.Hostile => Palette.Bad,
        Disposition.Wary => Palette.Warning,
        Disposition.Friendly => Palette.Good,
        _ => Colors.White,
    };

    private void Refresh()
    {
        var sector = _sector();
        _updating = true;
        foreach (var (lawId, box, views) in _lawRows)
        {
            box.ButtonPressed = sector.LawActive(lawId);
            var opinions = sector.Factions
                .Select(f => (f.Name, Stance: f.Culture.StanceOn(lawId)))
                .Where(o => o.Stance != 0)
                .Select(o => $"{o.Name} {(o.Stance > 0 ? "like" : "dislike")} it{(Math.Abs(o.Stance) > 1 ? " strongly" : "")}")
                .ToList();
            views.Text = opinions.Count == 0 ? "" : "   " + string.Join("; ", opinions);
            views.Visible = opinions.Count > 0;
        }
        _updating = false;

        foreach (var child in _factions.GetChildren()) child.QueueFree();
        if (sector.Factions.Count == 0) _factions.AddChild(Dim("None met yet. A warp gate's first jump into a new system may find one."));
        foreach (var f in sector.Factions)
        {
            var star = sector.Bodies.FirstOrDefault(b => b.Id == f.HomeStarId)?.Name ?? "?";
            var line = new Label
            {
                Text = $"{f.Name}  ({f.Culture.Name}, {star} system):  {AttitudeText(f, sector.ActiveLaws)}"
                       + (f.Grievance >= 1 ? $"\n   grievance {f.Grievance:0} from mining in their system (fades with time)" : ""),
                AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0),
                Modulate = DispositionColor(f.Disposition),
            };
            _factions.AddChild(line);
        }

        var recent = sector.Events.Reverse().Take(12)
            .Select(e => $"[{TimeText(e.Tick)}] {e.Text}").ToList();
        _events.Text = recent.Count == 0 ? "Nothing yet." : string.Join("\n", recent);
    }

    private static string TimeText(long tick)
    {
        var s = (long)(tick * FixedStepClock.TickSeconds);
        return $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}";
    }
}
