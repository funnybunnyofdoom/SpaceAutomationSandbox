using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Research panel (R, #37, drawn as a tree since #104): points and how fast labs make them, the chosen tech with what it unlocks, and
/// the whole tree, each tech under what it needs, with its parts and how near you are to affording it.
/// </summary>
public partial class ResearchPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private const float CardWidth = 126, CardHeight = 106, GapX = 10, GapY = 26;

    private readonly Func<SectorState> _sector;
    private readonly PartCatalog _catalog;
    private readonly List<(TechPlace Place, Button Card, StatBar Bar)> _cards = new();
    private InfoList _header = null!, _detail = null!;
    private Label _status = null!;
    private HFlowContainer _unlocks = null!;
    private Button _research = null!;
    private TreeCanvas _tree = null!;
    private string _chosen = "", _unlocksOf = "";
    private double _sinceRefresh;

    public ResearchPanel(Func<SectorState> sector, PartCatalog catalog)
    {
        _sector = sector;
        _catalog = catalog;
        Build();
    }

    public ResearchPanel() : this(() => new SectorState(0),
        new PartCatalog(Array.Empty<PartDefinition>(), new MaterialCatalog(Array.Empty<MaterialDefinition>()))) { }

    private void Build()
    {
        var places = TechTree.Layout(_catalog.Research);
        var columns = places.Count == 0 ? 1 : places.Max(p => p.Column) + 1;
        var rows = places.Count == 0 ? 1 : places.Max(p => p.Row) + 1;
        var treeWidth = columns * (CardWidth + GapX) - GapX;
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -(treeWidth + 46);
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var title = new Label
        {
            MouseFilter = MouseFilterEnum.Pass,
            TooltipText = $"Research labs make points from power and electronics ({SectorState.LabElectronicsKgPerPoint:0} kg per point).\nSpend them on techs to unlock parts. Locked parts can still be drawn in the blueprint editor.",
        };
        InputActions.TitleWithKey(title, "RESEARCH", InputActions.Research);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(_header = new InfoList());

        // The chosen tech.
        var card = new PanelContainer();
        box.AddChild(card);
        var detail = new VBoxContainer();
        card.AddChild(detail);
        detail.AddChild(_detail = new InfoList());
        detail.AddChild(_unlocks = new HFlowContainer());
        var act = new HBoxContainer();
        detail.AddChild(act);
        _research = new Button { FocusMode = FocusModeEnum.None, Icon = UiIcons.Get("research") };
        _research.Pressed += Research;
        act.AddChild(_research);
        _status = new Label { ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        act.AddChild(_status);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddChild(scroll);
        _tree = new TreeCanvas { CustomMinimumSize = new Vector2(treeWidth, rows * (CardHeight + GapY) - GapY) };
        scroll.AddChild(_tree);
        var group = new ButtonGroup();
        foreach (var place in places)
        {
            var inRow = places.Count(p => p.Row == place.Row);
            var button = new Button
            {
                ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None, ClipContents = true,
                Position = new Vector2((treeWidth - (inRow * (CardWidth + GapX) - GapX)) / 2 + place.Column * (CardWidth + GapX), place.Row * (CardHeight + GapY)),
                Size = new Vector2(CardWidth, CardHeight), TooltipText = place.Tech.Description,
            };
            var id = place.Tech.Id;
            button.Pressed += () => { _chosen = id; _status.Text = ""; Refresh(); };
            var inside = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            inside.SetAnchorsPreset(LayoutPreset.FullRect);
            inside.OffsetLeft = 5;
            inside.OffsetRight = -5;
            inside.OffsetTop = 4;
            inside.OffsetBottom = -5;
            inside.AddThemeConstantOverride("separation", 2);
            button.AddChild(inside);
            var name = new Label
            {
                Text = place.Tech.Name, HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MaxLinesVisible = 2, ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore,
            };
            name.AddThemeFontSizeOverride("font_size", 13);
            inside.AddChild(name);
            var sprites = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
            sprites.AddThemeConstantOverride("separation", 2);
            foreach (var part in (place.Tech.Parts ?? new List<string>()).Take(4)) sprites.AddChild(Sprite(part, 26));
            inside.AddChild(sprites);
            var bar = new StatBar();
            inside.AddChild(bar);
            _tree.AddChild(button);
            _cards.Add((place, button, bar));
        }
        _tree.Links = () => _cards.SelectMany(c => (c.Place.Tech.Requires ?? new List<string>())
            .Select(r => _cards.FirstOrDefault(p => p.Place.Tech.Id == r)).Where(p => p.Card is not null)
            .Select(p => (p.Card.Position + new Vector2(CardWidth / 2, CardHeight), c.Card.Position + new Vector2(CardWidth / 2, 0), _sector().IsTechUnlocked(p.Place.Tech.Id), c.Place.Tech.Id == _chosen)));
        if (_cards.Count == 0) _tree.AddChild(new Label { Text = "(no tech tree loaded: everything is open)" });
    }

    private static TextureRect Sprite(string part, int size) => new()
    {
        Texture = PartSprites.Icon(part), CustomMinimumSize = new Vector2(size, size), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest, MouseFilter = MouseFilterEnum.Ignore,
    };

    public void Open()
    {
        Visible = true;
        _status.Text = "";
        var sector = _sector();
        // Start on something that can be bought, else the first that isn't done.
        if (_chosen == "" || sector.IsTechUnlocked(_chosen))
            _chosen = (_cards.FirstOrDefault(c => sector.ResearchBlocker(c.Place.Tech.Id) is null).Place.Tech
                       ?? _cards.FirstOrDefault(c => !sector.IsTechUnlocked(c.Place.Tech.Id)).Place.Tech ?? _cards.FirstOrDefault().Place.Tech)?.Id ?? "";
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private void Research()
    {
        var result = _sector().TryResearch(_chosen);
        _status.Text = result.Message;
        _status.Modulate = UiTheme.Of(result.Done ? Severity.Good : Severity.Bad);
        Refresh();
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var rows = new List<HudRow>
        {
            new("research", $"{sector.ResearchPoints:0.0} points, +{sector.ResearchRateNow * 60:0.#} a minute from labs    {_cards.Count(c => sector.IsTechUnlocked(c.Place.Tech.Id))} of {_cards.Count} researched"),
        };
        if (sector.ResearchAllUnlocked) rows.Add(new("ok", "This game started before research: everything is already unlocked.", Severity.Good));
        _header.Set(rows);

        foreach (var (place, card, bar) in _cards)
        {
            var tech = place.Tech;
            var done = sector.IsTechUnlocked(tech.Id);
            var blocked = (tech.Requires ?? new List<string>()).Any(r => !sector.IsTechUnlocked(r));
            var affordable = !done && sector.ResearchBlocker(tech.Id) is null;
            bar.Set(done ? 1 : sector.ResearchPoints / Math.Max(1, tech.Cost), done ? "done" : affordable ? $"{tech.Cost:0} ready" : blocked ? $"{tech.Cost:0} locked" : $"{Math.Min(sector.ResearchPoints, tech.Cost):0} / {tech.Cost:0}",
                done || affordable ? Severity.Good : blocked ? Severity.Muted : Severity.Normal);
            card.Modulate = new Color(1, 1, 1, blocked && !done ? 0.6f : 1);
            card.SetPressedNoSignal(tech.Id == _chosen);
        }
        _tree.QueueRedraw();

        var chosen = _cards.FirstOrDefault(c => c.Place.Tech?.Id == _chosen).Place.Tech;
        _research.Visible = chosen is not null && !sector.IsTechUnlocked(chosen.Id);
        if (chosen is null) { _detail.Set(Array.Empty<HudRow>()); return; }
        var why = sector.IsTechUnlocked(chosen.Id) ? null : sector.ResearchBlocker(chosen.Id);
        var details = new List<HudRow> { new("research", $"{chosen.Name}: {chosen.Description}") };
        foreach (var need in chosen.Requires ?? new List<string>())
            if (_catalog.Research.TryGet(need, out var needed))
                details.Add(new(sector.IsTechUnlocked(need) ? "ok" : "wait", $"needs {needed.Name}", sector.IsTechUnlocked(need) ? Severity.Good : Severity.Warning));
        if (why is not null && sector.ResearchRateNow > 0 && sector.ResearchPoints < chosen.Cost)
            details.Add(new("clock", $"{(chosen.Cost - sector.ResearchPoints) / sector.ResearchRateNow / 60:0} min at this rate", Severity.Muted));
        _detail.Set(details);
        _research.Text = $"Research for {chosen.Cost:0} points";
        _research.Disabled = why is not null;
        _research.TooltipText = why ?? "";
        if (_unlocksOf == chosen.Id) return;
        _unlocksOf = chosen.Id;
        foreach (var child in _unlocks.GetChildren()) { _unlocks.RemoveChild(child); child.QueueFree(); }
        foreach (var id in chosen.Parts ?? new List<string>())
        {
            if (!_catalog.TryGet(id, out var part) || part is null) continue;
            var chip = new HBoxContainer
            {
                MouseFilter = MouseFilterEnum.Pass,
                TooltipText = $"{part.Name}, {part.MassKg / 1000:0.##} t\n" + string.Join("\n", PartFacts.Of(part, _catalog.Materials).Select(f => f.Text)),
            };
            chip.AddChild(Sprite(id, 32));
            chip.AddChild(new Label { Text = part.Name + "  ", ThemeTypeVariation = "HudText", MouseFilter = MouseFilterEnum.Ignore });
            _unlocks.AddChild(chip);
        }
    }

    /// <summary>Draws the lines from each tech to what's built on it.</summary>
    private sealed partial class TreeCanvas : Control
    {
        public Func<IEnumerable<(Vector2 From, Vector2 To, bool Done, bool Chosen)>> Links { get; set; } = Enumerable.Empty<(Vector2, Vector2, bool, bool)>;

        public override void _Draw()
        {
            foreach (var (from, to, done, chosen) in Links().OrderBy(l => l.Chosen))
            {
                // Down to just above the card, so lines past several rows run in the gaps as far as they can.
                var bend = to.Y - GapY / 2;
                var color = chosen ? UiTheme.Accent : done ? new Color(Palette.Good, 0.7f) : new Color(1, 1, 1, 0.25f);
                DrawPolyline(new[] { from, new Vector2(from.X, bend), new Vector2(to.X, bend), to }, color, chosen ? 3 : 2);
            }
            // Cards are translucent: lines passing behind one mustn't show through it.
            foreach (var card in GetChildren().OfType<Button>()) DrawRect(new Rect2(card.Position, card.Size), new Color(0.03f, 0.05f, 0.09f));
        }
    }
}
