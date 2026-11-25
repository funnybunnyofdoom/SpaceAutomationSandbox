using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Trade and diplomacy with civilizations (#89). Opens with the cargo key while docked at a trade station, or from the laws
/// panel: barter and gifts when docked there, a peace offer when they're hostile.
/// </summary>
public partial class TradePanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _ship;
    private readonly MaterialCatalog _materials;
    private readonly Action _changed;
    private HBoxContainer _portrait = null!;
    private string _portraitOf = "";
    private OptionButton _faction = null!, _give = null!, _get = null!;
    private SpinBox _tonnes = null!;
    private Label _message = null!;
    private InfoList _state = null!, _quote = null!;
    private Button _trade = null!, _gift = null!, _peace = null!;
    private VBoxContainer _deal = null!;
    private List<int> _factions = new();
    private List<string> _gives = new(), _gets = new();
    private double _sinceRefresh;

    public TradePanel(Func<SectorState> sector, Func<CraftState?> ship, MaterialCatalog materials, Action changed)
    {
        _sector = sector;
        _ship = ship;
        _materials = materials;
        _changed = changed;
        Build();
    }

    public TradePanel() : this(() => new SectorState(0), () => null, new MaterialCatalog(Array.Empty<MaterialDefinition>()), () => { }) { }

    /// <summary>Raised by the Laws tab (#100).</summary>
    public event Action? LawsChosen;

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
        var head = new HBoxContainer();
        var title = new Label { Text = "TRADE AND DIPLOMACY", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.ThemeTypeVariation = "SectionHeader";
        head.AddChild(title);
        box.AddChild(head);
        box.AddChild(UiTabs.Row(("laws", "Laws", () => LawsChosen?.Invoke()), ("trade", "Trade", null)));
        _faction = new OptionButton { FocusMode = FocusModeEnum.None };
        _faction.ItemSelected += _ => { RefreshGoods(); Refresh(); };
        box.AddChild(_faction);
        var across = new HBoxContainer(); // #118
        across.AddThemeConstantOverride("separation", 8);
        box.AddChild(across);
        _portrait = new HBoxContainer();
        across.AddChild(_portrait);
        _state = new InfoList();
        across.AddChild(_state);

        _deal = new VBoxContainer();
        box.AddChild(_deal);
        var give = new HBoxContainer();
        give.AddChild(new Label { Text = "Give" });
        _give = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _give.ItemSelected += _ => Refresh();
        give.AddChild(_give);
        _tonnes = new SpinBox { MinValue = 0.1, MaxValue = 10_000, Step = 0.1, Value = 5, Suffix = "t" };
        _tonnes.ValueChanged += _ => Refresh();
        give.AddChild(_tonnes);
        _deal.AddChild(give);
        var get = new HBoxContainer();
        get.AddChild(new Label { Text = "For" });
        _get = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _get.ItemSelected += _ => Refresh();
        get.AddChild(_get);
        _deal.AddChild(get);
        _quote = new InfoList();
        _deal.AddChild(_quote);
        var buttons = new HBoxContainer();
        _trade = new Button { Text = "Trade", FocusMode = FocusModeEnum.None, TooltipText = "Swap the goods at their prices" };
        _trade.Pressed += () => Show(_ship() is { } s && Give() is { } g && Get() is { } t ? _sector().Barter(s.Id, g, Kg(s, g), t) : new ActionResult(false, "Nothing to trade."));
        buttons.AddChild(_trade);
        _gift = new Button { Text = "Give as a gift", FocusMode = FocusModeEnum.None, TooltipText = "Hand it over for nothing: raises goodwill and clears grievances" };
        _gift.Pressed += () => Show(_ship() is { } s && Give() is { } g ? _sector().Gift(s.Id, g, Kg(s, g)) : new ActionResult(false, "Nothing to give."));
        buttons.AddChild(_gift);
        _peace = new Button { Text = "Offer peace", FocusMode = FocusModeEnum.None, TooltipText = "Pay them in this material, from your hold, to end the war" };
        _peace.Pressed += () => Show(_ship() is { } s && Give() is { } g && Chosen() is { } f ? _sector().OfferPeace(s.Id, f.Id, g) : new ActionResult(false, "Nothing to offer."));
        buttons.AddChild(_peace);
        _deal.AddChild(buttons);
        _message = Text();
        box.AddChild(_message);
    }

    /// <summary>Opens on the civilization you're docked with, else the given one, else the first.</summary>
    public void Open(int? factionId = null)
    {
        Visible = true;
        _message.Text = "";
        var sector = _sector();
        _factions = sector.Factions.Select(f => f.Id).ToList();
        _faction.Clear();
        foreach (var f in sector.Factions) _faction.AddItem(f.Name);
        var wanted = factionId ?? (_ship() is { } s ? sector.DockedTrader(s)?.Id : null);
        if (wanted is { } id && _factions.IndexOf(id) is var index and >= 0) _faction.Select(index);
        RefreshGoods();
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private Faction? Chosen() => _faction.Selected >= 0 && _faction.Selected < _factions.Count ? _sector().FactionById(_factions[_faction.Selected]) : null;
    private string? Give() => _give.Selected >= 0 && _give.Selected < _gives.Count ? _gives[_give.Selected] : null;
    private string? Get() => _get.Selected >= 0 && _get.Selected < _gets.Count ? _gets[_get.Selected] : null;
    private long Kg(CraftState ship, string material) => Math.Min((long)Math.Round(_tonnes.Value * 1000), ship.Inventory[material]);
    private string MaterialName(string id) => _materials.Contains(id) ? _materials.Get(id).Name : id;

    private void RefreshGoods()
    {
        var sector = _sector();
        var ship = _ship();
        var keepGive = Give();
        var keepGet = Get();
        _give.Clear();
        _get.Clear();
        _gives = Chosen() is { } f && ship is not null
            ? _materials.Materials.Select(m => m.Id).Where(m => ship.Inventory[m] > 0 && sector.ValueTo(f, m) > 0).ToList() : new List<string>();
        _gets = Chosen() is { } seller ? sector.SellsOf(seller).ToList() : new List<string>();
        foreach (var m in _gives) _give.AddIconItem(UiIcons.Material(m), $"{MaterialName(m)} ({ship!.Inventory[m] / 1000.0:0.0##} t aboard){(SectorState.Favours(Chosen()!, m) ? ", they favour it" : "")}");
        foreach (var m in _gets) _get.AddIconItem(UiIcons.Material(m), MaterialName(m));
        if (keepGive is not null && _gives.IndexOf(keepGive) is var g and >= 0) _give.Select(g);
        if (keepGet is not null && _gets.IndexOf(keepGet) is var t and >= 0) _get.Select(t);
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        _deal.Visible = Chosen() is not null;
        var shown = Chosen() is { } who ? $"{who.Id} {who.Name} {who.RaceId}" : "";
        if (shown != _portraitOf)
        {
            _portraitOf = shown;
            foreach (var child in _portrait.GetChildren()) { _portrait.RemoveChild(child); child.QueueFree(); }
            if (Chosen() is { } now) _portrait.AddChild(CivilizationRows.Portrait(now, 96));
        }
        if (Chosen() is not { } f) { _state.Set(new[] { new HudRow("idle", "You haven't met a civilization yet. A warp gate's first jump into a new system may find one.", Severity.Muted) }); return; }
        var ship = _ship();
        var docked = ship is not null && sector.DockedTrader(ship) == f;
        var station = sector.TradeStationOf(f);
        var hostile = f.Disposition == Disposition.Hostile;
        var system = sector.Bodies.FirstOrDefault(b => b.Id == f.HomeStarId)?.Name ?? "?";
        var rows = new List<HudRow> { new("star", $"{f.Name}, a {f.Culture.Name} civilization in the {system} system", Link: new TargetRef(CraftId.None, f.HomeStarId)) };
        rows.AddRange(CivilizationRows.For(sector, f));
        if (f.Rules is { Favours.Count: > 0 } race) // #125
            rows.Add(new HudRow("trade", $"They pay {(CivilizationTraits.FavourFactor - 1) * 100:0}% more for {string.Join(", ", race.Favours.Select(m => MaterialName(m).ToLowerInvariant()))}", Severity.Good));
        rows.Add(hostile ? new("danger", $"They want goods worth {SectorState.PeacePrice(f):N0} for peace, from a ship of yours in their system.", Severity.Warning)
            : station is null ? new("idle", "They have no trade station at the moment.", Severity.Muted)
            : docked ? SectorState.WillTrade(f) ? new HudRow("docked", "Docked at their trade station.", Severity.Good) : new HudRow("warn", "They won't trade while wary, but accept gifts.", Severity.Warning)
            : new("docked", $"Dock at {station.Name} to trade or give gifts.", Severity.Muted, Link: TargetRef.Of(station)));
        _state.Set(rows);
        _trade.Disabled = !docked || !SectorState.WillTrade(f);
        _gift.Disabled = !docked;
        _peace.Visible = hostile;
        if (Give() is not { } give || ship is null) { _quote.Set(new[] { new HudRow("cargo", "Nothing in your hold they want.", Severity.Muted) }); return; }
        if (hostile)
        {
            var cost = Math.Ceiling(SectorState.PeacePrice(f) / sector.ValueTo(f, give)) / 1000;
            _quote.Set(new[] { new HudRow("mat_" + give, $"Peace costs {cost:0.##} t of {MaterialName(give)}; you have {ship.Inventory[give] / 1000.0:0.##} t", ship.Inventory[give] / 1000.0 >= cost ? Severity.Good : Severity.Warning, ship.Inventory[give] / 1000.0 / Math.Max(cost, 0.001)) });
            return;
        }
        if (station is null || Get() is not { } get) { _quote.Set(Array.Empty<HudRow>()); return; }
        var kg = Kg(ship, give);
        var gets = sector.BarterQuote(f, station, give, kg, get);
        _quote.Set(new[]
        {
            new HudRow("mat_" + give, $"give {kg / 1000.0:0.0##} t of {MaterialName(give)}   at {sector.BuyPrice(f, station, give):0.0#} per kg"),
            new HudRow("mat_" + get, $"get {gets / 1000.0:0.0##} t of {MaterialName(get)}   at {sector.SellPrice(f, get):0.0#} per kg", gets > 0 ? Severity.Good : Severity.Warning,
                gets / (double)Math.Max(1, station.Inventory[get]), Tip: $"They have {station.Inventory[get] / 1000.0:0.0} t; the bar is the share you'd take"),
        });
    }

    private void Show(ActionResult result)
    {
        if (result.Done) _changed();
        _message.Text = result.Message;
        _message.AddThemeColorOverride("font_color", result.Done ? OkColor : ErrorColor);
        RefreshGoods();
        Refresh();
    }
}
