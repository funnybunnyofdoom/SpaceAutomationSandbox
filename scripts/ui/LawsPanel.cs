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
    private Label _status = null!;
    private InfoList _events = null!;
    private readonly Dictionary<int, InfoList> _factionRows = new();
    private string _factionKey = "-";
    private readonly List<(string LawId, CheckBox Box, InfoList Views)> _lawRows = new();
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
        box.AddChild(UiTabs.Row(("laws", "Laws", null), ("trade", "Trade", () => DiplomacyChosen?.Invoke(-1))));
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
            var views = new InfoList();
            _laws.AddChild(views);
            _lawRows.Add((id, check, views));
        }
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0) };
        box.AddChild(_status);

        box.AddChild(Heading("Civilizations"));
        _factions = new VBoxContainer();
        box.AddChild(_factions);

        box.AddChild(Heading("Recent events"));
        _events = new InfoList();
        box.AddChild(_events);
    }

    /// <summary>Raised by a civilization's button (#89).</summary>
    public event System.Action<int>? DiplomacyChosen;

    private static Label Heading(string text)
    {
        return UiTheme.Header(text);
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

    private void Refresh()
    {
        var sector = _sector();
        _updating = true;
        foreach (var (lawId, box, views) in _lawRows)
        {
            box.ButtonPressed = sector.LawActive(lawId);
            var blocked = sector.LawBlocker(lawId);
            box.Disabled = blocked is not null && !sector.LawActive(lawId);
            box.TooltipText = Laws.Get(lawId).Effect + (blocked is null ? "" : $"\n{blocked}");
            box.Text = Laws.Get(lawId).Name + (blocked is null ? "" : $"  (not for you: {sector.Race.Name})");
            var opinions = sector.Factions
                .Select(f => (f.Name, Stance: f.Culture.StanceOn(lawId)))
                .Where(o => o.Stance != 0)
                .Select(o => new HudRow(o.Stance > 0 ? "ok" : "warn", $"{o.Name} {(o.Stance > 0 ? "like" : "dislike")} it{(Math.Abs(o.Stance) > 1 ? " strongly" : "")}", o.Stance > 0 ? Severity.Good : Severity.Warning))
                .ToList();
            views.Set(opinions);
            views.Visible = opinions.Count > 0;
        }
        _updating = false;

        // Built only when a civilization is met, so its button can be pressed; the rows update in place.
        var key = string.Join(",", sector.Factions.Select(f => $"{f.Id} {f.Name} {f.RaceId}"));
        if (key != _factionKey)
        {
            _factionKey = key;
            _factionRows.Clear();
            foreach (var child in _factions.GetChildren()) { _factions.RemoveChild(child); child.QueueFree(); }
            if (sector.Factions.Count == 0) _factions.AddChild(Dim("None met yet. A warp gate's first jump into a new system may find one."));
            foreach (var f in sector.Factions)
            {
                var rows = new InfoList();
                var across = new HBoxContainer();
                across.AddThemeConstantOverride("separation", 8);
                across.AddChild(CivilizationRows.Portrait(f)); // #118
                across.AddChild(rows);
                _factions.AddChild(across);
                _factionRows[f.Id] = rows;
                var id = f.Id;
                var deal = new Button { Text = "Trade and diplomacy", Icon = UiIcons.Get("trade"), FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
                deal.Pressed += () => DiplomacyChosen?.Invoke(id);
                _factions.AddChild(deal);
            }
        }
        foreach (var f in sector.Factions)
        {
            if (!_factionRows.TryGetValue(f.Id, out var list)) continue;
            var star = sector.Bodies.FirstOrDefault(b => b.Id == f.HomeStarId)?.Name ?? "?";
            var rows = new List<HudRow> { new("star", $"{f.Name}  ({f.Culture.Name}, {star} system)", Link: new TargetRef(CraftId.None, f.HomeStarId)) };
            rows.AddRange(CivilizationRows.For(sector, f));
            if (!f.RaidGateId.IsNone) rows.Add(new("danger", $"RAIDING {sector.Crafts.FirstOrDefault(c => c.Id == f.RaidGateId)?.Name ?? "your gate"}", Severity.Bad, Link: new TargetRef(f.RaidGateId, BodyId.None)));
            rows.Add(new("weapon", $"{sector.Crafts.Count(c => c.IsPatrol && c.FactionId == f.Id && !c.IsWreck)} patrol ships in their system"
                + (sector.Factions.Where(o => SectorState.AreEnemies(f, o)).Select(o => o.Name).ToList() is { Count: > 0 } foes ? $"; enemies of the {string.Join(", ", foes)}" : ""), Severity.Muted));
            list.Set(rows);
        }

        var recent = sector.Events.Reverse().Take(12).Select(LogPanel.Row).ToList(); // the rest is in the log (#100)
        _events.Set(recent.Count == 0 ? new[] { new HudRow("message", "Nothing yet.", Severity.Muted) } : recent.ToArray());
    }
}
