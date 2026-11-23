using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Research panel (R, #37): points to spend and how fast labs make them, then every tech in the tree
/// with what it unlocks, its cost, and whether it's done, can be bought now, or what it still needs.
/// </summary>
public partial class ResearchPanel : PanelContainer
{
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    private const double RefreshSeconds = 0.5;

    private readonly Func<SectorState> _sector;
    private readonly PartCatalog _catalog;
    private Label _header = null!;
    private Label _status = null!;
    private VBoxContainer _rows = null!;
    private readonly List<(string TechId, Label Label, Button Button)> _techRows = new();
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
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -450;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "RESEARCH", InputActions.Research);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(new Label
        {
            Text = $"Research labs make points from power and electronics ({SectorState.LabElectronicsKgPerPoint:0} kg per point). "
                   + "Spend them on techs to unlock parts. Locked parts can still be drawn in the blueprint editor.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(410, 0), Modulate = new Color(1, 1, 1, 0.65f),
        });
        _header = new Label();
        box.AddChild(_header);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(410, 0) };
        box.AddChild(_status);
        _rows = new VBoxContainer();
        box.AddChild(_rows);

        foreach (var tech in _catalog.Research.Techs)
        {
            var row = new VBoxContainer();
            var label = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(410, 0) };
            row.AddChild(label);
            var button = new Button { Text = $"Research ({tech.Cost:0} points)", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            var id = tech.Id;
            button.Pressed += () =>
            {
                var result = _sector().TryResearch(id);
                _status.Text = result.Message;
                _status.AddThemeColorOverride("font_color", result.Done ? OkColor : ErrorColor);
                Refresh();
            };
            row.AddChild(button);
            _rows.AddChild(row);
            _techRows.Add((id, label, button));
        }
        if (_techRows.Count == 0) _rows.AddChild(new Label { Text = "(no tech tree loaded: everything is open)" });
    }

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
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        _header.Text = $"Points: {sector.ResearchPoints:0.0}   labs working: +{sector.ResearchRateNow * 60:0.#} per minute"
                       + (sector.ResearchAllUnlocked ? "\n(This game started before research: everything is already unlocked.)" : "");
        foreach (var (id, label, button) in _techRows)
        {
            if (!_catalog.Research.TryGet(id, out var tech)) continue;
            var parts = string.Join(", ", (tech.Parts ?? new List<string>()).Select(p => _catalog.TryGet(p, out var part) ? part.Name : p));
            var done = sector.IsTechUnlocked(id);
            var blocker = done ? null : sector.ResearchBlocker(id);
            label.Text = $"{tech.Name}{(done ? "  - done" : "")}\n  {tech.Description}\n  Unlocks: {parts}"
                         + ((tech.Requires?.Count ?? 0) > 0 ? $"\n  Needs: {string.Join(", ", tech.Requires!.Select(r => _catalog.Research.TryGet(r, out var t) ? t.Name : r))}" : "")
                         + (blocker is not null && !done ? $"\n  {blocker}" : "");
            label.Modulate = done ? new Color(0.6f, 1f, 0.7f) : blocker is null ? Colors.White : new Color(1, 1, 1, 0.7f);
            button.Visible = !done;
            button.Disabled = blocker is not null;
        }
    }
}
