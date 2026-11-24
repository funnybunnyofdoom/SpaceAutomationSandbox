using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Native life (I, #94): the living worlds you've found, how they're doing, their plants and animals by site, what your machines
/// there are doing with them, and the orders for the craft you fly (cull, load and release animals).
/// </summary>
public partial class LifePanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _craft;
    private readonly Action _changed;
    private Label _message = null!;
    private InfoList _worlds = null!, _here = null!;
    private Button _cull = null!, _load = null!, _release = null!;
    private double _sinceRefresh;

    public LifePanel(Func<SectorState> sector, Func<CraftState?> craft, Action changed)
    {
        _sector = sector;
        _craft = craft;
        _changed = changed;
        Build();
    }

    public LifePanel() : this(() => new SectorState(0), () => null, () => { }) { }

    private static Label Text() => new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(430, 0) };

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
        InputActions.TitleWithKey(title, "NATIVE LIFE", InputActions.Life);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        _here = new InfoList();
        box.AddChild(_here);
        var buttons = new HBoxContainer();
        _cull = Add(buttons, "Cull here", "Weapons on this anchored craft hunt the site's animals, for biomass; those who care for life resent it",
            c => _sector().SetCulling(c.Id, !c.Culling));
        _load = Add(buttons, "Load animals", "Fill the pens with this site's animals", c => _sector().LoadAnimals(c.Id));
        _release = Add(buttons, "Release animals", "Let the animals in the pens out here", c => _sector().ReleaseAnimals(c.Id));
        box.AddChild(buttons);
        _message = Text();
        box.AddChild(_message);
        box.AddChild(UiTheme.Header("Living worlds"));
        _worlds = new InfoList();
        box.AddChild(_worlds);
    }

    private Button Add(HBoxContainer row, string text, string tip, Func<CraftState, ActionResult> act)
    {
        var button = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None };
        button.Pressed += () =>
        {
            if (_craft() is not { } craft) return;
            var result = act(craft);
            if (result.Done) _changed();
            _message.Text = result.Message;
            _message.AddThemeColorOverride("font_color", result.Done ? OkColor : ErrorColor);
            Refresh();
        };
        row.AddChild(button);
        return button;
    }

    public void Open()
    {
        Visible = true;
        _message.Text = "";
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private static string Temper(AnimalTemper temper) => temper switch
    {
        AnimalTemper.Crowding => "damage machines when crowded",
        AnimalTemper.Hungry => "damage machines when plants are thin",
        _ => "harmless",
    };

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var craft = _craft();
        _cull.Text = craft is { Culling: true } ? "Stop culling" : "Cull here";
        _cull.Disabled = craft is null || craft.Stats.WeaponList.Count == 0;
        _load.Disabled = craft is null || craft.Stats.PenCapacityKg <= 0;
        _release.Disabled = craft is null || craft.PenKg <= 0;
        var site = craft is null ? null : sector.SiteUnder(craft);
        var here = new List<HudRow>();
        if (craft is null) here.Add(new("idle", "You aren't flying a craft.", Severity.Muted));
        else if (site is null) here.Add(new("landed", $"{craft.Name} isn't on a surface site.", Severity.Muted));
        else
        {
            here.Add(new("landed", $"{craft.Name} is at {site.Name}"));
            here.Add(new("life", $"plants {site.Flora * 100:0}%", Severity.Good, site.Flora));
            here.Add(new("crew", $"animals {site.Fauna * 100:0}%", Severity.Normal, site.Fauna));
        }
        if (craft is { PenKg: > 0 }) here.Add(new("cargo", $"pens: {craft.PenKg / 1000.0:0.##} of {craft.Stats.PenCapacityKg / 1000:0.#} t, {craft.PenAnimal}s", Severity.Normal, craft.PenKg / Math.Max(1, craft.Stats.PenCapacityKg)));
        _here.Set(here);

        var found = sector.Bodies.Where(b => b.LifeFound).ToList();
        if (found.Count == 0)
        {
            _worlds.Set(new[] { new HudRow("life", "No living worlds found yet. Survey temperate planets in other systems: about one in eight has life.", Severity.Muted) });
            return;
        }
        var rows = new List<HudRow>();
        foreach (var body in found)
        {
            var health = SectorState.HealthOf(body);
            rows.Add(new(UiIcons.BodyIcon(body), $"{body.Name}: {health}" + (health == LifeHealth.Thriving ? " (growing faster)" : health == LifeHealth.Stressed ? " (growing slower: let it recover)" : ""),
                health == LifeHealth.Lost ? Severity.Bad : health == LifeHealth.Stressed ? Severity.Warning : health == LifeHealth.Thriving ? Severity.Good : Severity.Normal, Link: TargetRef.Of(body)));
            if (health == LifeHealth.Lost) { rows.Add(new("danger", "its native life has died out", Severity.Muted)); continue; }
            var plants = body.Sites.Average(s => s.Flora);
            rows.Add(new("life", $"plants {plants * 100:0}% on average{(body.FloraHazard == FloraHazard.None ? "" : $", {body.FloraHazard.ToString().ToLowerInvariant()}")}", Severity.Good, plants));
            rows.Add(body.HasAnimals ? new("crew", $"{body.AnimalName}s: {Temper(body.AnimalTemper)}", Severity.Muted) : new("idle", "no animals", Severity.Muted));
            foreach (var s in body.Sites.Where(s => s.Surveyed))
            {
                var angry = s.Fauna > 0 && SectorState.AnimalsAngry(body, s);
                rows.Add(new("life", $"{SurfaceSite.Letter(s.Index)}: plants {s.Flora * 100:0}%", Severity.Good, s.Flora));
                if (s.Fauna > 0) rows.Add(new(angry ? "warn" : "crew", $"{SurfaceSite.Letter(s.Index)}: animals {s.Fauna * 100:0}%{(angry ? "  ANGRY" : "")}", angry ? Severity.Bad : Severity.Normal, s.Fauna));
            }
            foreach (var c in sector.Crafts.Where(c => c.LandedOnBodyId == body.Id && (c.AnimalStatus is not null || c.RepellerOn)))
                rows.Add(new("orbit", $"{c.Name}: {c.AnimalStatus ?? "repelling animals"}", Link: TargetRef.Of(c)));
        }
        _worlds.Set(rows);
    }
}
