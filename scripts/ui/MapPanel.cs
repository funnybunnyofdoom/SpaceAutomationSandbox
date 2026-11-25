using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The map's panel (M, #53, #104): what the map shows (filters), the systems, a card for the target body with its make-up, ores and
/// sites, and a list of bodies and stations of the system in view with distance and rough travel time, and a Target button.
/// </summary>
public partial class MapPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private const int MostRows = 150;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _pilot;
    private readonly Action<BodyId> _targetBody;
    private readonly Action<CraftId> _targetCraft;
    private readonly Action<BodyId> _viewSystem;
    private readonly Func<BodyId?> _viewed;
    private readonly MaterialCatalog _materials;
    private readonly HashSet<string> _hidden = new();
    private VBoxContainer _rows = null!;
    private OptionButton _systems = null!;
    private List<BodyId> _systemIds = new();
    private LineEdit _search = null!;
    private BodyCard _card = null!;
    private readonly List<(InfoRow Row, Func<HudRow> Text)> _labels = new();
    private string _layoutKey = "";
    private double _sinceRefresh;

    public MapPanel(Func<SectorState> sector, Func<CraftState?> pilot, Action<BodyId> targetBody, Action<CraftId> targetCraft, Action<BodyId>? viewSystem = null,
        Func<BodyId?>? viewed = null, MaterialCatalog? materials = null)
    {
        _viewSystem = viewSystem ?? (_ => { });
        _viewed = viewed ?? (() => null);
        _materials = materials ?? new MaterialCatalog(Array.Empty<MaterialDefinition>());
        _sector = sector;
        _pilot = pilot;
        _targetBody = targetBody;
        _targetCraft = targetCraft;
        Build();
    }

    public MapPanel() : this(() => new SectorState(0), () => null, _ => { }, _ => { }) { }

    // --- What the map shows ---

    public bool Shows(CelestialBodyState body) => body.Kind == BodyKind.Star || !_hidden.Contains(body.Kind switch { BodyKind.Moon => "moon", BodyKind.Asteroid => "asteroid", _ => "planet" });

    public bool Shows(CraftState craft) => !_hidden.Contains(SectorState.IsGate(craft) && !craft.IsWreck ? "gate" : craft.Stats.TotalThrustN > 0 || craft.IsWreck ? "fleet" : "orbit");

    public bool ShowsRoutes => !_hidden.Contains("freight");

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -412;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        AddChild(box);
        var title = new Label
        {
            MouseFilter = MouseFilterEnum.Pass,
            TooltipText = "Distances and rough travel times are from the craft you fly.\nClick things on the map to target them; right click to fly there.",
        };
        InputActions.TitleWithKey(title, "MAP", InputActions.Map);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);

        var filters = new HFlowContainer();
        box.AddChild(filters);
        foreach (var (icon, name) in new[] { ("planet", "Planets"), ("moon", "Moons"), ("asteroid", "Asteroids"), ("orbit", "Stations"), ("gate", "Warp gates"), ("fleet", "Ships and wrecks"), ("freight", "Routes") })
        {
            var button = new Button
            {
                ToggleMode = true, ButtonPressed = true, FocusMode = FocusModeEnum.None, TooltipText = $"Show {name.ToLowerInvariant()} on the map and in the list", Icon = UiIcons.Get(icon),
                IconAlignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(40, 32), TextureFilter = TextureFilterEnum.Nearest,
            };
            button.Toggled += on =>
            {
                if (on) _hidden.Remove(icon);
                else _hidden.Add(icon);
                _layoutKey = "";
                Refresh();
            };
            filters.AddChild(button);
        }
        _systems = new OptionButton { FocusMode = FocusModeEnum.None, ClipText = true };
        _systems.ItemSelected += i => { if (i < _systemIds.Count) { _viewSystem(_systemIds[(int)i]); _layoutKey = ""; Refresh(); } };
        box.AddChild(_systems);
        _search = new LineEdit { PlaceholderText = "Search this system", ClearButtonEnabled = true };
        _search.TextChanged += _ => { _layoutKey = ""; Refresh(); };
        box.AddChild(_search);
        box.AddChild(_card = new BodyCard(_materials));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_rows);
        box.AddChild(new Label
        {
            Text = $"{InputActions.Key1(InputActions.ZoomIn)} / {InputActions.Key1(InputActions.ZoomOut)}: zoom to the mouse    middle drag: move    {InputActions.Key1(InputActions.Anchor)}: galaxy view", AutowrapMode = TextServer.AutowrapMode.WordSmart,
            ThemeTypeVariation = "HudText", Modulate = new Color(1, 1, 1, 0.6f),
        });
    }

    public void Open()
    {
        Visible = true;
        _layoutKey = "";
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    /// <summary>Rough door-to-door time: cruise for long legs, the job autopilot's speed for short ones.</summary>
    public static string TravelTime(double distanceM)
    {
        var seconds = distanceM > SectorState.AutopilotCruiseMinDistanceM
            ? (distanceM - SectorState.AutopilotCruiseArriveM) / SectorState.CruiseSpeedMps + SectorState.AutopilotCruiseArriveM / SectorState.JobCruiseSpeedMps + 60
            : distanceM / SectorState.JobCruiseSpeedMps;
        return seconds < 90 ? $"{seconds:0} s" : $"{seconds / 60:0} min";
    }

    public static string Distance(double m) => m >= 1_000_000 ? $"{m / 1e6:0.00} Mm" : m >= 1000 ? $"{m / 1000:0} km" : $"{m:0} m";

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var pilot = _pilot();
        var here = pilot is null ? null : sector.StarAt(pilot.Position);
        var viewed = sector.Stars.FirstOrDefault(s => s.Id == _viewed()) ?? here ?? sector.Stars.FirstOrDefault();

        var stars = sector.Stars.OrderBy(s => s.Id == here?.Id ? 0 : 1).ThenBy(s => s.Id.Value).ToList();
        if (!stars.Select(s => s.Id).SequenceEqual(_systemIds))
        {
            _systemIds = stars.Select(s => s.Id).ToList();
            _systems.Clear();
            foreach (var star in stars)
                _systems.AddIconItem(UiIcons.Get("star"), $"{star.Name} system, {star.StarClass switch { StarClass.Red => "red dwarf", StarClass.Blue => "blue star", _ => "yellow star" }}");
        }
        var at = viewed is null ? -1 : _systemIds.IndexOf(viewed.Id);
        if (at >= 0 && _systems.Selected != at) _systems.Select(at);

        var bodies = sector.Bodies.Where(b => viewed is not null && (b.Id == viewed.Id || sector.StarOf(b.Id)?.Id == viewed.Id)).ToList();
        var stations = sector.Crafts.Where(c => c.Stats.DockingPorts.Count > 0 && Shows(c) && sector.StarAt(c.Position)?.Id == viewed?.Id).Select(c => c.Id).ToList();
        var key = $"{viewed?.Id}|{bodies.Count}|{string.Join(",", stations)}";
        if (key != _layoutKey) Rebuild(sector, viewed, bodies, stations, key);
        foreach (var (row, text) in _labels) row.Set(text());

        var body = pilot is { TargetCraftId.IsNone: true, TargetBodyId.IsNone: false } ? sector.Bodies.FirstOrDefault(b => b.Id == pilot.TargetBodyId) : null;
        _card.Visible = body is not null;
        if (body is not null) _card.Set(body);
    }

    private void Rebuild(SectorState sector, CelestialBodyState? star, List<CelestialBodyState> bodies, List<CraftId> stations, string key)
    {
        _layoutKey = key;
        _labels.Clear();
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        if (star is null) return;
        var search = _search.Text.Trim();
        bool Found(string name) => search.Length == 0 || name.Contains(search, StringComparison.OrdinalIgnoreCase);

        string Far(SectorState s, Vector2D position, double radius)
        {
            if (_pilot() is not { } pilot) return "";
            if (s.StarAt(pilot.Position)?.Id != star.Id) return "  by warp";
            var d = Math.Max(0, position.DistanceTo(pilot.Position) - radius);
            return $"  {Distance(d)}, ~{TravelTime(d)}";
        }

        void AddBody(CelestialBodyState body, int depth)
        {
            var id = body.Id;
            if (Shows(body) && Found(body.Name))
                AddRow(depth, () =>
                {
                    var s = _sector();
                    if (s.Bodies.FirstOrDefault(b => b.Id == id) is not { } b) return new HudRow("idle", "(gone)", Severity.Muted);
                    var unsurveyed = b.IsMineable && !b.AnySiteSurveyed;
                    return new HudRow(UiIcons.BodyIcon(b), $"{b.Name}{Far(s, s.GetPosition(id), b.RadiusM)}{(unsurveyed ? "  ?" : "")}", unsurveyed ? Severity.Muted : Severity.Normal,
                        Tip: unsurveyed ? "Not surveyed yet" : "", Link: TargetRef.Of(b));
                }, () => _targetBody(id));
            foreach (var child in bodies.Where(b => b.Orbit?.Parent == body.Id && b.Kind != BodyKind.Star)) AddBody(child, depth + 1);
        }
        AddBody(star, 0);

        var named = stations.Select(id => sector.GetCraft(id)).Where(c => Found(c.Name)).ToList();
        if (named.Count > 0) _rows.AddChild(UiTheme.Header("Stations"));
        foreach (var station in named.Take(MostRows))
        {
            var id = station.Id;
            AddRow(0, () =>
            {
                var s = _sector();
                if (s.Crafts.FirstOrDefault(x => x.Id == id) is not { } c) return new HudRow("idle", "(gone)", Severity.Muted);
                var closed = c.GateClosed ? "  closed" : "";
                return new HudRow(SectorState.IsGate(c) ? "gate" : c.IsShipyard ? "shipyard" : c.IsTradeStation ? "trade" : "orbit", $"{c.Name}{Far(s, c.Position, c.CollisionRadiusM)}{closed}",
                    c.GateClosed || FleetPanel.Trouble(c) is not null ? Severity.Warning : Severity.Normal, Tip: FleetPanel.Trouble(c) ?? "", Link: TargetRef.Of(c));
            }, () => _targetCraft(id));
        }
        if (named.Count > MostRows) _rows.AddChild(new Label { Text = $"{named.Count - MostRows} more: search for a name", ThemeTypeVariation = "HudText", Modulate = new Color(1, 1, 1, 0.6f) });
    }

    private void AddRow(int depth, Func<HudRow> text, Action target)
    {
        var row = new HBoxContainer();
        row.AddChild(new Control { CustomMinimumSize = new Vector2(depth * 14, 0) });
        var info = new InfoRow { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(info);
        var button = new Button { Icon = UiIcons.Get("target"), FocusMode = FocusModeEnum.None, TooltipText = "Target it: the HUD shows distance and speed, and brake matches its motion" };
        button.Pressed += target;
        row.AddChild(button);
        _rows.AddChild(row);
        _labels.Add((info, text));
    }

    /// <summary>A body's make-up (#104): a ring of its sites by their main ore, what's left of it, and its ores as bars.</summary>
    private sealed partial class BodyCard : PanelContainer
    {
        private readonly MaterialCatalog _materials;
        private readonly SiteRing _ring;
        private readonly InfoList _rows = new();

        public BodyCard(MaterialCatalog materials)
        {
            _materials = materials;
            _ring = new SiteRing(materials);
            var row = new HBoxContainer();
            AddChild(row);
            row.AddChild(_ring);
            row.AddChild(_rows);
        }

        public BodyCard() : this(new MaterialCatalog(Array.Empty<MaterialDefinition>())) { }

        public void Set(CelestialBodyState body)
        {
            _ring.Set(body);
            var surveyed = body.Sites.Count(s => s.Surveyed);
            var rows = new List<HudRow>
            {
                new(UiIcons.BodyIcon(body), $"{body.Name}, {(body.Kind == BodyKind.GasGiant ? "gas giant" : body.Kind.ToString().ToLowerInvariant())}, radius {body.RadiusM / 1000:0.##} km"),
            };
            if (body.Sites.Count > 0)
                rows.Add(new("survey", $"{surveyed} of {body.Sites.Count} sites surveyed", surveyed == body.Sites.Count ? Severity.Good : Severity.Warning, surveyed / (double)body.Sites.Count,
                    Tip: $"Fly within {SectorState.SurveyRangeM / 1000:0} km to survey the sites you can see"));
            if (body.IsDismantlable && body.OriginalMassKg > 0)
                rows.Add(new("mining", $"{body.MassKg / body.OriginalMassKg * 100:0.##}% of it left", Severity.Normal, body.MassKg / body.OriginalMassKg));
            var ores = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var site in body.Sites.Where(s => s.Surveyed))
                foreach (var (id, kg) in site.DepositsKg) ores[id] = ores.GetValueOrDefault(id) + kg;
            var most = ores.Count == 0 ? 1 : ores.Values.Max();
            foreach (var (id, kg) in ores.OrderByDescending(o => o.Value))
                rows.Add(new("mat_" + id, $"{(_materials.Contains(id) ? _materials.Get(id).Name : id)}  {kg / 1000.0:N0} t", Severity.Normal, kg / (double)most));
            if (surveyed > 0 && body.BulkKg > 0) rows.Add(new("mat_" + body.BulkMaterialId, $"and {body.BulkKg / 1e9:N2} Mt of {body.BulkMaterialId}", Severity.Muted));
            if (body.LifeFound) rows.Add(new("life", SectorState.HealthOf(body).ToString(), Severity.Good));
            _rows.Set(rows);
        }
    }

    /// <summary>The sites round a body, each in the colour of its main ore; unsurveyed ones are empty with a "?".</summary>
    private sealed partial class SiteRing : Control
    {
        private readonly MaterialCatalog _materials;
        private CelestialBodyState? _body;
        private string _key = "";

        public SiteRing(MaterialCatalog materials)
        {
            _materials = materials;
            CustomMinimumSize = new Vector2(104, 104);
            SizeFlagsVertical = SizeFlags.ShrinkCenter;
            TextureFilter = TextureFilterEnum.Nearest;
            TooltipText = "Its surface sites, by their main ore";
        }

        public SiteRing() : this(new MaterialCatalog(Array.Empty<MaterialDefinition>())) { }

        public void Set(CelestialBodyState body)
        {
            var key = body.Id + string.Join("|", body.Sites.Select(s => s.Surveyed ? Main(s) : "?"));
            if (key == _key && ReferenceEquals(body, _body)) return;
            (_key, _body) = (key, body);
            var found = body.Sites.Where(s => s.Surveyed).Select(s => $"{SurfaceSite.Letter(s.Index)}: {(Main(s) is { Length: > 0 } ore ? $"{ore} {s.DepositsKg[ore] / 1000.0:N0} t" : "no ore")}{(s.IsHot ? ", hot" : "")}");
            TooltipText = body.Sites.Count == 0 ? "" : "Its surface sites, by their main ore\n" + string.Join("\n", found);
            QueueRedraw();
        }

        private static string Main(SurfaceSite site) => site.DepositsKg.Count == 0 ? "" : site.DepositsKg.OrderByDescending(d => d.Value).ThenBy(d => d.Key, StringComparer.Ordinal).First().Key;

        public override void _Draw()
        {
            if (_body is null) return;
            var centre = Size / 2;
            if (UiIcons.Get(UiIcons.BodyIcon(_body)) is { } icon) DrawTextureRect(icon, new Rect2(centre - new Vector2(24, 24), new Vector2(48, 48)), false);
            var font = GetThemeDefaultFont();
            for (var i = 0; i < _body.Sites.Count; i++)
            {
                var site = _body.Sites[i];
                var from = Mathf.Tau * i / _body.Sites.Count;
                var to = Mathf.Tau * (i + 1) / _body.Sites.Count - 0.04f;
                var ore = site.Surveyed ? Main(site) : "";
                var color = !site.Surveyed ? new Color(1, 1, 1, 0.12f)
                    : ore.Length > 0 && _materials.Contains(ore) && _materials.Get(ore).PlaceholderColor is { } hex ? Color.FromHtml(hex) : new Color(0.35f, 0.37f, 0.42f);
                DrawArc(centre, 40, from, to, 24, color, 14);
                // Letters as well as colours (#68).
                var middle = centre + Vector2.FromAngle((from + to) / 2) * 40;
                if (_body.Sites.Count > 14) continue;
                var text = site.Surveyed ? SurfaceSite.Letter(site.Index) : "?";
                var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, 11);
                DrawString(font, middle + new Vector2(-size.X / 2 + 1, 5), text, HorizontalAlignment.Left, -1, 11, new Color(0, 0, 0, 0.9f));
                DrawString(font, middle + new Vector2(-size.X / 2, 4), text, HorizontalAlignment.Left, -1, 11, Colors.White);
            }
        }
    }
}
