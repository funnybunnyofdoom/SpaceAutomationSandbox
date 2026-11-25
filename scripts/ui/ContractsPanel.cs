using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>Contracts (#124): what's offered, what's under way, and how the last ones went.</summary>
public partial class ContractsPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _pilot;
    private readonly VBoxContainer _box = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly List<Action> _live = new();
    private string _key = "";
    private double _sinceRefresh;

    /// <summary>Something was done: what came of it.</summary>
    public event Action<ActionResult>? Acted;

    /// <summary>A freight job for a delivery: the trade station and the material.</summary>
    public event Action<CraftId, string>? HaulChosen;

    public ContractsPanel(Func<SectorState> sector, Func<CraftState?> pilot)
    {
        (_sector, _pilot) = (sector, pilot);
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -482;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        AddChild(outer);
        var title = new Label();
        InputActions.TitleWithKey(title, "CONTRACTS", InputActions.Contracts);
        title.AddThemeFontSizeOverride("font_size", 18);
        outer.AddChild(title);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        _box.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(_box);
    }

    public ContractsPanel() : this(() => new SectorState(0), () => null) { }

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

    private static string Time(long ticks)
    {
        var seconds = Math.Max(0, ticks / FixedStepClock.TicksPerSecond);
        return seconds >= 3600 ? $"{seconds / 3600} h {seconds / 60 % 60:00} min" : $"{seconds / 60} min {seconds % 60:00} s";
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var key = $"{sector.Options[WorldOptions.Contracts]}|{sector.Factions.Count}|{_pilot()?.Id}|" + string.Join(";", sector.CivilizationContracts.Select(c => $"{c.Id}{c.State}"));
        if (key != _key) Rebuild(sector, key);
        foreach (var live in _live) live();
    }

    private void Rebuild(SectorState sector, string key)
    {
        _key = key;
        _live.Clear();
        foreach (var child in _box.GetChildren()) { _box.RemoveChild(child); child.QueueFree(); }
        var all = sector.CivilizationContracts;
        if (sector.Options[WorldOptions.Contracts] == WorldAmount.Off && all.Count == 0)
        {
            Say("Contracts are off in this game. The pause menu's world options can turn them on.");
            return;
        }
        if (all.Count == 0)
        {
            Say(sector.Factions.Count == 0 ? "No civilization knows you yet: they offer work once you've met them through a warp gate."
                : "No offers yet. Civilizations that aren't hostile offer work now and then; the log says when.");
            return;
        }
        var under = all.Where(c => c.State == ContractState.Accepted).ToList();
        if (under.Count > 0) _box.AddChild(UiTheme.Header("Under way"));
        foreach (var contract in under) Card(sector, contract);
        var offered = all.Where(c => c.State == ContractState.Offered).ToList();
        if (offered.Count > 0) _box.AddChild(UiTheme.Header("Offered"));
        foreach (var contract in offered) Card(sector, contract);
        var done = all.Where(c => !c.IsOpen).Reverse().Take(12).ToList();
        if (done.Count == 0) return;
        _box.AddChild(UiTheme.Header("Lately"));
        foreach (var contract in done)
        {
            var row = new InfoRow();
            var who = sector.FactionById(contract.FactionId)?.Name ?? "?";
            row.Set(new HudRow(contract.State == ContractState.Done ? "ok" : contract.State == ContractState.Failed ? "warn" : "idle",
                $"{who}: {sector.Describe(contract)}. {contract.Result}", contract.State == ContractState.Failed ? Severity.Warning : Severity.Muted));
            _box.AddChild(row);
        }
    }

    private void Card(SectorState sector, Contract contract)
    {
        var id = contract.Id;
        var faction = sector.FactionById(contract.FactionId);
        var panel = new PanelContainer();
        var back = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.05f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
        back.SetCornerRadiusAll(4);
        if (faction is not null) { back.BorderColor = new Color(CivilizationRows.Accent(faction), 0.6f); back.BorderWidthLeft = 3; }
        panel.AddThemeStyleboxOverride("panel", back);
        _box.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        panel.AddChild(box);
        var head = new InfoRow();
        head.Set(new HudRow(contract.Kind switch { ContractKind.Delivery => "freight", ContractKind.Survey => "survey", _ => "crew" },
            $"{faction?.Name ?? "?"}: {sector.Describe(contract)}", Link: Where(sector, contract)));
        box.AddChild(head);
        box.AddChild(Text($"Pays {sector.RewardText(contract)}."));
        var status = new InfoRow();
        box.AddChild(status);
        var buttons = new HFlowContainer();
        box.AddChild(buttons);
        Button? hand = null;
        if (contract.State == ContractState.Offered)
        {
            Add(buttons, "Accept", () => _sector().AcceptContract(id));
            Add(buttons, "Refuse", () => _sector().RefuseContract(id));
        }
        else
        {
            if (contract.Kind is ContractKind.Delivery or ContractKind.Rescue) hand = Add(buttons, contract.Kind == ContractKind.Delivery ? "Hand over" : $"Hand over {contract.Kg} kg {contract.Material}", () => _pilot() is { } p ? _sector().HandOver(p.Id, id) : new ActionResult(false, "Fly a craft first."));
            if (contract.Kind == ContractKind.Delivery && faction is not null && sector.TradeStationOf(faction) is { } station)
            {
                var haul = new Button { Text = "Haul it with a freight job...", FocusMode = FocusModeEnum.None, TooltipText = "Opens the job panel for the ship you fly, delivering this to their station" };
                haul.Pressed += () => HaulChosen?.Invoke(station.Id, contract.Material);
                buttons.AddChild(haul);
            }
            Add(buttons, "Give up", () => _sector().GiveUpContract(id)).TooltipText = $"Costs {SectorState.FailGoodwill:0} goodwill with them";
        }
        _live.Add(() =>
        {
            var s = _sector();
            if (s.ContractById(id) is not { IsOpen: true } c) return;
            if (c.State == ContractState.Offered) { status.Set(new HudRow("clock", $"Offer open for {Time(c.OfferUntilTick - s.Tick)}; then {Time(c.DurationTicks)} to do it.", Severity.Muted)); return; }
            var left = Time(c.DeadlineTick - s.Tick);
            status.Set(c.Kind switch
            {
                ContractKind.Delivery => new HudRow("cargo", $"{c.DoneKg / 1000.0:0.#} of {c.Kg / 1000.0:0.#} t delivered; {left} left", c.DeadlineTick - s.Tick < 600 * FixedStepClock.TicksPerSecond ? Severity.Warning : Severity.Normal, (double)c.DoneKg / c.Kg),
                ContractKind.Survey => new HudRow("survey", $"{c.Bodies.Count(b => s.Exists(new TargetRef(CraftId.None, b)) && s.GetBody(b).Surveyed)} of {c.Bodies.Count} surveyed; {left} left", Severity.Normal,
                    c.Bodies.Count(b => s.Exists(new TargetRef(CraftId.None, b)) && s.GetBody(b).Surveyed) / (double)Math.Max(1, c.Bodies.Count)),
                _ => new HudRow("target", $"{(s.Exists(new TargetRef(c.Stranded, BodyId.None)) ? s.GetCraft(c.Stranded).Name : "Their ship")} waits; {left} left", Link: s.Exists(new TargetRef(c.Stranded, BodyId.None)) ? new TargetRef(c.Stranded, BodyId.None) : null),
            });
            if (hand is null || _pilot() is not { } pilot) return;
            var near = c.Kind == ContractKind.Rescue ? s.Exists(new TargetRef(c.Stranded, BodyId.None)) && s.GetCraft(c.Stranded).Position.DistanceTo(pilot.Position) < 2_000
                : pilot.IsDocked && s.FactionById(c.FactionId) is { } f && s.TradeStationOf(f)?.Id == pilot.DockedToCraftId;
            hand.Disabled = !near;
            hand.TooltipText = near ? "" : c.Kind == ContractKind.Rescue ? "Fly beside their ship first" : "Dock at their trade station first";
        });
    }

    private static TargetRef? Where(SectorState sector, Contract contract)
    {
        var target = contract.Kind switch
        {
            ContractKind.Delivery => sector.FactionById(contract.FactionId) is { } f && sector.TradeStationOf(f) is { } station ? TargetRef.Of(station) : TargetRef.None,
            ContractKind.Survey => contract.Bodies.Select(b => new TargetRef(CraftId.None, b)).FirstOrDefault(t => sector.Exists(t) && !sector.GetBody(t.Body).Surveyed),
            _ => !contract.Stranded.IsNone ? new TargetRef(contract.Stranded, BodyId.None) : new TargetRef(CraftId.None, contract.Place),
        };
        return !target.IsNone && sector.Exists(target) ? target : null;
    }

    private Button Add(Container row, string text, Func<ActionResult> act)
    {
        var button = new Button { Text = text, FocusMode = FocusModeEnum.None };
        button.Pressed += () => { Acted?.Invoke(act()); _key = ""; Refresh(); };
        row.AddChild(button);
        return button;
    }

    private void Say(string text) => _box.AddChild(Text(text));

    private static Label Text(string text) => new() { Text = text, ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0), Modulate = new Color(1, 1, 1, 0.75f) };
}
