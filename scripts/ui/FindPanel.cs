using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Find anything (#109): bodies, stations, ships, wrecks and others by name and filters, in every system, nearest first; favourites and
/// recent targets. Only the rows in view exist, so thousands of craft stay quick.
/// </summary>
public partial class FindPanel : PanelContainer
{
    private const double RefreshSeconds = 1;
    private const float RowHeight = 50;

    private static readonly (TargetKind? Kind, string Name, string Icon)[] Kinds =
    {
        (null, "Everything", ""), (TargetKind.Stations, "Your stations", "shipyard"), (TargetKind.Ships, "Your ships", "fleet"), (TargetKind.Bodies, "Bodies", "map"),
        (TargetKind.Enemies, "Enemies", "danger"), (TargetKind.Wrecks, "Wrecks", "salvage"), (TargetKind.Others, "Others' ships and stations", "trade"),
    };

    private static readonly string[] Jobs = { "mining", "freight", "requests", "survey", "salvage", "trade", "guard", "escort", "patrol" };

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _pilot;
    private readonly Func<IReadOnlyList<TargetRef>> _recent;
    private readonly Func<TargetRef> _current;
    private readonly Func<TargetRef, string> _describe;
    private readonly Func<string, string> _materialName;
    private readonly LineEdit _search;
    private readonly OptionButton _system, _owner, _job, _ore;
    private readonly Button _ports, _attention, _recentOnly;
    private readonly Button[] _slots = new Button[SectorState.FavouriteSlots];
    private readonly Label _count;
    private readonly ScrollContainer _scroll;
    private readonly Control _canvas;
    private readonly List<FindRow> _rows = new();
    private IReadOnlyList<FoundTarget> _found = Array.Empty<FoundTarget>();
    private TargetKind? _kind;
    private string _optionsKey = "";
    private SectorState? _of;
    private double _sinceRefresh;

    /// <summary>The player picked a target.</summary>
    public event Action<TargetRef>? TargetChosen;

    /// <summary>A favourite's slot was clicked, or its target is to be kept there.</summary>
    public event Action<int>? FavouriteChosen;

    /// <summary>Favourites were changed here.</summary>
    public event Action? Changed;

    public FindPanel(Func<SectorState> sector, Func<CraftState?> pilot, Func<IReadOnlyList<TargetRef>> recent, Func<TargetRef> current, Func<TargetRef, string> describe, Func<string, string> materialName)
    {
        (_sector, _pilot, _recent, _current, _describe, _materialName) = (sector, pilot, recent, current, describe, materialName);
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -492;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "FIND", InputActions.Find);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);

        var favourites = new HBoxContainer();
        favourites.AddThemeConstantOverride("separation", 2);
        box.AddChild(favourites);
        favourites.AddChild(UiTheme.Icon("favourite"));
        for (var i = 0; i < _slots.Length; i++)
        {
            var slot = i;
            var button = _slots[i] = new Button { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, Text = $"{i + 1}" };
            button.Pressed += () => FavouriteChosen?.Invoke(slot);
            button.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
                _sector().SetFavourite(slot, TargetRef.None);
                Changed?.Invoke();
                Refresh();
            };
            favourites.AddChild(button);
        }

        _search = new LineEdit { PlaceholderText = "Part of a name", ClearButtonEnabled = true };
        _search.TextChanged += _ => Refresh();
        box.AddChild(_search);
        var kinds = new HFlowContainer();
        box.AddChild(kinds);
        var group = new ButtonGroup();
        foreach (var (kind, name, icon) in Kinds)
        {
            var button = Filter(icon, name, kind is null ? "All" : "");
            button.ButtonGroup = group;
            button.ButtonPressed = kind is null;
            button.Pressed += () => { _kind = kind; Refresh(); };
            kinds.AddChild(button);
        }
        kinds.AddChild(new VSeparator());
        _ports = Filter("docked", "With a free docking port");
        _attention = Filter("warn", "Yours, waiting or with something wrong");
        _recentOnly = Filter("clock", "Recent targets");
        foreach (var button in new[] { _ports, _attention, _recentOnly })
        {
            button.Toggled += _ => Refresh();
            kinds.AddChild(button);
        }

        var options = new GridContainer { Columns = 2 };
        box.AddChild(options);
        (_system, _owner, _job, _ore) = (Options(options), Options(options), Options(options), Options(options));
        _ore.TooltipText = "Bodies with this ore at a surveyed site. The work key looks for it too.";
        _job.AddItem("Any job");
        foreach (var job in Jobs) _job.AddItem(char.ToUpperInvariant(job[0]) + job[1..]);

        _count = new Label { Modulate = new Color(1, 1, 1, 0.7f) };
        box.AddChild(_count);
        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(_scroll);
        _canvas = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        _scroll.AddChild(_canvas);
        _scroll.GetVScrollBar().ValueChanged += _ => ShowRows();
        _scroll.Resized += ShowRows;
    }

    public FindPanel() : this(() => new SectorState(0), () => null, Array.Empty<TargetRef>, () => TargetRef.None, _ => "", m => m) { }

    private static Button Filter(string icon, string tip, string text = "") => new()
    {
        ToggleMode = true, FocusMode = FocusModeEnum.None, TooltipText = tip, Text = text, Icon = icon.Length > 0 ? UiIcons.Get(icon) : null, TextureFilter = TextureFilterEnum.Nearest,
    };

    private OptionButton Options(Container parent)
    {
        var options = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        options.ItemSelected += _ => Refresh();
        parent.AddChild(options);
        return options;
    }

    /// <summary>The ore being looked for, or null for any.</summary>
    public string? Ore => Chosen(_ore);

    private static string? Chosen(OptionButton options) =>
        options.Selected > 0 && options.GetItemMetadata(options.Selected) is { VariantType: Variant.Type.String } chosen ? chosen.AsString() : null;

    public void Open()
    {
        Visible = true;
        Refresh();
        if (!Input.IsActionPressed(InputActions.Find)) return;
        Callable.From(() => { if (Visible) _search.GrabFocus(); }).CallDeferred(); // after the key, so that it isn't typed
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    /// <summary>Systems, civilizations and ores come along: lists them, keeping what's chosen.</summary>
    private void FillOptions(SectorState sector)
    {
        var systems = sector.Stars.Select(s => ($"{s.Id.Value}", $"{s.Name} system")).ToList();
        var owners = new[] { ("0", "Yours") }.Concat(sector.Factions.Select(f => ($"{f.Id}", f.Name))).ToList();
        var ores = sector.SurveyedOres().Select(o => (o, _materialName(o))).ToList();
        var key = string.Join("|", systems.Concat(owners).Concat(ores));
        if (key == _optionsKey && ReferenceEquals(_of, sector)) return;
        _optionsKey = key;
        _of = sector;
        Fill(_system, "Any system", systems);
        Fill(_owner, "Any owner", owners);
        Fill(_ore, "Any ore", ores);

        static void Fill(OptionButton options, string any, List<(string Id, string Name)> items)
        {
            var chosen = Chosen(options);
            options.Clear();
            options.AddItem(any);
            foreach (var (id, name) in items)
            {
                options.AddItem(name);
                options.SetItemMetadata(options.ItemCount - 1, id);
                if (id == chosen) options.Select(options.ItemCount - 1);
            }
        }
    }

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        FillOptions(sector);
        for (var i = 0; i < _slots.Length; i++)
        {
            var favourite = sector.Favourites[i];
            var gone = favourite is not null && !sector.Exists(favourite.Target);
            _slots[i].Text = favourite is null ? $"{i + 1}" : $"{i + 1} {(gone ? "×" : "")}{(gone ? favourite.Name : sector.NameOf(favourite.Target))}";
            _slots[i].Modulate = favourite is null ? new Color(1, 1, 1, 0.5f) : Colors.White;
            _slots[i].TooltipText = favourite is null ? $"Empty. {InputActions.Key1(InputActions.FavouriteStore(i))} keeps your target here."
                : $"{(gone ? $"{favourite.Name} is gone" : _describe(favourite.Target))}\n{InputActions.Key1(InputActions.FavouriteRecall(i))}: target it    {InputActions.Key1(InputActions.FavouriteStore(i))}: keep your target here    right click: empty";
        }
        if (_pilot() is not { } pilot) { _found = Array.Empty<FoundTarget>(); _count.Text = "Fly a craft first."; ShowRows(); return; }
        var found = sector.Find(pilot, new TargetQuery
        {
            Text = _search.Text, Kind = _kind, Star = Chosen(_system) is { } star ? new BodyId(uint.Parse(star)) : null, Owner = Chosen(_owner) is { } owner ? int.Parse(owner) : null,
            Job = _job.Selected > 0 ? Jobs[_job.Selected - 1] : null, Ore = Ore, FreePort = _ports.ButtonPressed, Attention = _attention.ButtonPressed,
        });
        if (_recentOnly.ButtonPressed)
        {
            var recent = _recent().ToList();
            found = found.Where(f => recent.Contains(f.Target)).OrderBy(f => recent.IndexOf(f.Target)).ToList();
        }
        _found = found;
        _count.Text = found.Count == 0 ? "Nothing matches." : $"{found.Count:N0} found, {(_recentOnly.ButtonPressed ? "latest" : "nearest")} first";
        var height = found.Count * Height;
        if (!Mathf.IsEqualApprox(_canvas.CustomMinimumSize.Y, height)) _canvas.CustomMinimumSize = new Vector2(0, height);
        ShowRows();
    }

    private float Height => Mathf.Round(RowHeight * GetThemeDefaultFontSize() / 16f);

    private void ShowRows()
    {
        if (!IsInsideTree() || !Visible) return;
        var sector = _sector();
        var first = Mathf.Max(0, Mathf.FloorToInt(_scroll.ScrollVertical / Height));
        var count = Mathf.Clamp(Mathf.CeilToInt(_scroll.Size.Y / Height) + 1, 0, Math.Max(0, _found.Count - first));
        while (_rows.Count < count)
        {
            var row = new FindRow();
            row.Chosen += t => { if (_sector().Exists(t)) TargetChosen?.Invoke(t); };
            row.Starred += Star;
            _rows.Add(row);
            _canvas.AddChild(row);
        }
        var current = _current();
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Visible = i < count && sector.Exists(_found[first + i].Target);
            if (!_rows[i].Visible) continue;
            var found = _found[first + i];
            _rows[i].Position = new Vector2(0, (first + i) * Height);
            _rows[i].Size = new Vector2(_canvas.Size.X, Height - 3);
            _rows[i].Set(sector, found, _describe(found.Target), found.Target == current);
        }
    }

    /// <summary>Keeps it in the first empty slot, or takes it out.</summary>
    private void Star(TargetRef target)
    {
        var sector = _sector();
        var slot = sector.Favourites.ToList().FindIndex(f => f?.Target == target);
        if (slot >= 0) sector.SetFavourite(slot, TargetRef.None);
        else if ((slot = sector.Favourites.ToList().FindIndex(f => f is null)) >= 0) sector.SetFavourite(slot, target);
        else { _count.Text = "All nine favourites are taken: right click one to empty it."; return; }
        Changed?.Invoke();
        Refresh();
    }

    private sealed partial class FindRow : PanelContainer
    {
        private readonly TextureRect _icon = UiTheme.Icon("");
        private readonly Label _name = Line(), _way = Line();
        private readonly Button _target, _star;
        private TargetRef _ref = TargetRef.None;

        public event Action<TargetRef>? Chosen, Starred;

        private static Label Line() => new()
        {
            ThemeTypeVariation = "HudText", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore,
        };

        public FindRow()
        {
            MouseFilter = MouseFilterEnum.Pass;
            MouseDefaultCursorShape = CursorShape.PointingHand;
            GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { AcceptEvent(); UiLinks.Open(_ref); } };
            var back = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.04f), ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2 };
            back.SetCornerRadiusAll(3);
            AddThemeStyleboxOverride("panel", back);
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            AddChild(row);
            row.AddChild(_icon);
            var middle = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
            middle.AddThemeConstantOverride("separation", 0);
            row.AddChild(middle);
            middle.AddChild(_name);
            middle.AddChild(_way);
            _star = new Button { FocusMode = FocusModeEnum.None, Flat = true, Icon = UiIcons.Get("favourite"), TextureFilter = TextureFilterEnum.Nearest, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            _star.Pressed += () => Starred?.Invoke(_ref);
            _target = new Button { FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            _target.Pressed += () => Chosen?.Invoke(_ref);
            row.AddChild(_star);
            row.AddChild(_target);
        }

        public void Set(SectorState sector, FoundTarget found, string description, bool current)
        {
            _ref = found.Target;
            var craft = found.Target.Craft.IsNone ? null : sector.GetCraft(found.Target.Craft);
            var body = craft is null ? sector.GetBody(found.Target.Body) : null;
            var icon = body is not null ? UiIcons.Get(UiIcons.BodyIcon(body)) : UiIcons.Get(sector.KindOf(craft!) switch
            {
                TargetKind.Stations => SectorState.IsGate(craft!) ? "gate" : "shipyard", TargetKind.Ships => "fleet", TargetKind.Enemies => "danger", TargetKind.Wrecks => "salvage", _ => "trade",
            });
            if (_icon.Texture != icon) _icon.Texture = icon;
            if (_name.Text != description) _name.Text = description;
            var star = found.Star is { } id ? sector.GetBody(id).Name : "?";
            var way = found.Jumps < 0 ? $"{star} system: no warp gate route"
                : $"{MapPanel.Distance(found.DistanceM)}, ~{MapPanel.TravelTime(found.DistanceM)}"
                  + (found.Jumps == 0 ? "" : $"  in the {star} system: {found.Jumps} jump{(found.Jumps == 1 ? "" : "s")}, first {(sector.Exists(new TargetRef(found.FirstGate, BodyId.None)) ? sector.GetCraft(found.FirstGate).Name : "gate")}");
            if (craft is not null && SectorState.AttentionReason(craft) is { } wrong) way += $";  {wrong}";
            if (_way.Text != way) _way.Text = way;
            _way.Modulate = UiTheme.Of(craft is not null && SectorState.AttentionReason(craft) is not null ? Severity.Warning : Severity.Muted);
            var tip = description + "\n" + way + (body?.OriginalName is { } was ? $"\nOriginally {was}" : "");
            if (TooltipText != tip) TooltipText = tip;
            var kept = sector.Favourites.Any(f => f?.Target == found.Target);
            _star.Modulate = new Color(1, 1, 1, kept ? 1 : 0.35f);
            _star.TooltipText = kept ? "A favourite: click to take it out" : "Keep as a favourite";
            _target.Text = current ? "Targeted" : "Target";
            _target.Disabled = current;
        }
    }
}
