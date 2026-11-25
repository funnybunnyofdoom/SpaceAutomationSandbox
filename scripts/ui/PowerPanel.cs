using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Power (O, #75, #103): the power network of the craft you fly (everything docked together shares one pool), and your target's if
/// it's a different one. What's made against what's used, over the last minutes too; the batteries; every source with its fuel;
/// and every machine that draws power, with why something can't run.
/// </summary>
public partial class PowerPanel : PanelContainer
{
    private const double RefreshSeconds = 0.25;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _player;
    private readonly NetworkView _mine = new(), _targets = new();
    private readonly PowerGraph _graph = new();
    private Label _none = null!, _targetTitle = null!;
    private double _sinceRefresh;

    public PowerPanel(Func<SectorState> sector, Func<CraftState?> player)
    {
        _sector = sector;
        _player = player;
        Build();
    }

    public PowerPanel() : this(() => new SectorState(0), () => null) { }

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
        var title = new Label
        {
            MouseFilter = MouseFilterEnum.Pass,
            TooltipText = "Craft docked together share one power pool. Solar comes first; generators burn fuel from\ntheir own craft's stock only for what's missing (and to top the batteries up).",
        };
        InputActions.TitleWithKey(title, "POWER", InputActions.Power);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(_none = new Label { Text = "Nothing to show: you aren't flying a craft." });
        box.AddChild(_graph);
        box.AddChild(_mine);
        box.AddChild(_targetTitle = UiTheme.Header(""));
        box.AddChild(_targets);
    }

    public void Open()
    {
        Visible = true;
        Refresh();
        _sinceRefresh = RefreshSeconds; // and again next frame, once a tick has built the networks
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshSeconds) return;
        _sinceRefresh = 0;
        Refresh();
    }

    /// <summary>Remembers the network's power once a second of game time, shown or not (called each frame; cheap).</summary>
    public void Sample()
    {
        if (_player() is not { } player) return;
        var sector = _sector();
        if (!_graph.Due(sector)) return;
        var root = sector.PowerRootOf(player);
        var members = sector.PowerMembersOf(root);
        if (members.Count == 0) return;
        _graph.Add(sector, root.Id, Made(members) / FixedStepClock.TickSeconds, root.NetworkUsedKJThisTick / FixedStepClock.TickSeconds,
            members.Sum(m => m.StoredEnergyKJ) / Math.Max(1, members.Sum(m => m.Stats.EnergyCapacityKJ)));
    }

    private static double Made(IReadOnlyCollection<CraftState> members) =>
        members.Sum(m => m.SolarKJThisTick + m.GeothermalKJThisTick + m.StellarHeatKJThisTick + m.GeneratedKJThisTick);

    private void Refresh()
    {
        var sector = _sector();
        var player = _player();
        _none.Visible = player is null;
        _graph.Visible = _mine.Visible = player is not null;
        CraftState? other = null;
        if (player is not null)
        {
            var root = sector.PowerRootOf(player);
            _mine.Set(sector, root);
            if (!player.TargetCraftId.IsNone && sector.Crafts.FirstOrDefault(c => c.Id == player.TargetCraftId) is { } target
                && sector.PowerRootOf(target) is var theirs && theirs != root)
            {
                other = theirs;
                _targetTitle.Text = $"Target: {target.Name}";
            }
        }
        _targetTitle.Visible = _targets.Visible = other is not null;
        if (other is not null) _targets.Set(sector, other);
    }

    /// <summary>One network: balance, batteries, sources, converters and users.</summary>
    private sealed partial class NetworkView : VBoxContainer
    {
        private readonly InfoList _rows = new();
        private readonly InfoList _sources = new(), _users = new();
        private readonly Label _sourcesTitle = UiTheme.Header("Sources"), _usersTitle = UiTheme.Header("Uses, at most");

        public NetworkView()
        {
            AddChild(_rows);
            AddChild(_sourcesTitle);
            AddChild(_sources);
            AddChild(_usersTitle);
            AddChild(_users);
        }

        private static double Kw(double kJPerTick) => kJPerTick / FixedStepClock.TickSeconds;

        private static string Hours(double seconds) =>
            double.IsInfinity(seconds) || seconds > 99 * 3600 ? "99+ h" : seconds >= 3600 ? $"{seconds / 3600:0.0} h" : $"{seconds / 60:0} min";

        public void Set(SectorState sector, CraftState root)
        {
            var members = sector.PowerMembersOf(root);
            var rows = new List<HudRow>();
            var several = members.Count > 1;
            rows.Add(new("docked", several ? $"{members.Count} craft share power: {string.Join(", ", members.Take(4).Select(m => m.Name))}{(members.Count > 4 ? "…" : "")}" : root.Name,
                Severity.Muted, Tip: string.Join(", ", members.Select(m => m.Name))));
            var unlimited = members.Any(m => m.LegacyFreePower);
            _sourcesTitle.Visible = _sources.Visible = _usersTitle.Visible = _users.Visible = !unlimited;
            if (unlimited)
            {
                rows.Add(new("power", "unlimited power (from an old save)"));
                _rows.Set(rows);
                return;
            }
            var made = Kw(Made(members));
            var used = Kw(root.NetworkUsedKJThisTick);
            var stored = members.Sum(m => m.StoredEnergyKJ);
            var capacity = members.Sum(m => m.Stats.EnergyCapacityKJ);
            rows.Add(new("power", $"making {made:N0} kW, using {used:N0} kW", used > made + 1e-6 ? Severity.Warning : Severity.Good, made > 0 ? used / made : used > 0 ? 1 : 0,
                Tip: "How much of what's made is used; full: all of it"));
            rows.Add(new("battery", $"{stored / 1000:N1} / {capacity / 1000:N0} MJ" + (used > made + 1e-6 ? $", empty in {Hours(stored / (used - made))}" : ""),
                capacity > 0 && stored < 0.2 * capacity ? Severity.Warning : Severity.Normal, capacity > 0 ? stored / capacity : 0));
            foreach (var m in members.Where(m => m.PowerShort)) rows.Add(new("warn", $"{m.Name} is short of power: something couldn't run", Severity.Bad, Link: TargetRef.Of(m)));
            _rows.Set(rows);

            string Of(CraftState m, string what) => several ? $"{m.Name}: {what}" : what;
            var sources = new List<HudRow>();
            void Source(CraftState m, string icon, string what, double now, double most, string note = "", string tip = "", Severity severity = Severity.Normal)
                => sources.Add(new(icon, $"{Of(m, what)}  {now:N0} / {most:N0} kW{note}", severity, most > 0 ? now / most : 0, Tip: tip, Link: several ? TargetRef.Of(m) : null));
            foreach (var m in members.Where(m => m.Stats.PowerGenerationKW > 0))
                Source(m, "star", "solar", Kw(m.SolarKJThisTick), m.Stats.PowerGenerationKW, m.InShadow ? "  in shadow" : "",
                    $"{m.SolarFactor * 100:0}% sunlight here{(m.InShadow ? $"; a body's shadow dims it to {SectorState.ShadowLight * 100:0}%" : "")}", m.InShadow ? Severity.Warning : Severity.Normal);
            foreach (var m in members.Where(m => m.Stats.StellarHeatKW > 0)) // #77
                Source(m, "star", "stellar heat", Kw(m.StellarHeatKJThisTick), m.Stats.StellarHeatKW, m.StellarHeatKJThisTick <= 0 ? "  too far" : "",
                    $"Needs at least {SectorState.HotSunlightFactor * 100:0}% sunlight: closer to the star");
            foreach (var m in members.Where(m => m.Stats.GeothermalKW > 0)) // #77
                Source(m, "planet", "geothermal", Kw(m.GeothermalKJThisTick), m.Stats.GeothermalKW, "", sector.GeothermalHeat(m) ?? "");
            foreach (var m in members)
                foreach (var g in m.Stats.GeneratorList)
                {
                    var batches = g.BurnsKg.Min(b => m.Inventory[b.Key] / Math.Max(1, b.Value));
                    var energyLeft = batches * g.KJPerBatch + m.GeneratorBankKJ.GetValueOrDefault(g.PartId);
                    var burns = $"Burns {string.Join(" + ", g.BurnsKg.Select(b => $"{b.Value} kg {b.Key}"))} a batch";
                    var fuel = string.Join(", ", g.BurnsKg.Keys.Select(k => $"{m.Inventory[k] / 1000.0:0.##} t {k}"));
                    Source(m, "mat_" + g.BurnsKg.Keys.First(), g.PartId.Replace('_', ' '), Kw(m.GeneratedKJThisTick), g.KW,
                        energyLeft <= 0 ? "  NO FUEL" : $"  {Hours(energyLeft / g.KW)}", energyLeft <= 0 ? burns : $"{burns}\n{fuel} left: {Hours(energyLeft / g.KW)} at full output",
                        energyLeft <= 0 ? Severity.Bad : Severity.Normal);
                }
            if (sources.Count == 0) sources.Add(new("idle", "none: only batteries", Severity.Muted));
            foreach (var m in members.Where(m => m.Stats.ConverterList.Count > 0)) // #78
                foreach (var c in m.Stats.ConverterList)
                    sources.Add(new("work", $"{Of(m, c.PartId.Replace('_', ' '))}  uses {Kw(m.ConvertedKJThisTick):N0} / {c.KW:N0} kW spare", Severity.Muted, c.KW > 0 ? Kw(m.ConvertedKJThisTick) / c.KW : 0,
                        Tip: $"{string.Join(" + ", c.InputsKg.Select(i => $"{i.Value} kg {i.Key}"))} into {string.Join(" + ", c.OutputsKg.Select(o => $"{o.Value} kg {o.Key}"))}\nOnly on spare power, above {SectorState.ConverterSpareAbove * 100:0}% battery"));
            _sources.Set(sources);

            var users = new List<(string Icon, string Name, double Kw)>();
            foreach (var m in members)
            {
                var s = m.Stats;
                void U(string icon, string name, double kw) { if (kw > 0) users.Add((icon, Of(m, name), kw)); }
                U("speed", "thrusters", s.ThrustPowerKW + s.ReversePowerKW + s.LeftPowerKW + s.RightPowerKW);
                var factor = SectorState.IsPlayers(m) ? sector.MachinePowerFactor : 1; // #116
                void M(string icon, string name, double kw) => U(icon, name, kw * factor);
                M("mining", "drills", s.DrillPowerKW); M("work", "refinery", s.RefineryPowerKW); M("shipyard", "fabricator", s.FabricatorPowerKW);
                M("mat_slag", "ejector", s.EjectorPowerKW); M("crew", "habitats", s.HabitatPowerKW); M("mat_food", "farms", s.FarmPowerKW);
                M("crew", "robots", s.RobotPowerKW); M("research", "labs", s.LabPowerKW); U("shield", "shields", s.ShieldPowerKW);
                M("salvage", "salvage arms", s.SalvagePowerKW); M("hull", "repair drones", s.RepairPowerKW); M("hull", "nanites", s.NanoPowerKW);
                M("life", "harvesters", s.HarvestPowerKW); M("life", "repellers", s.RepelPowerKW); M("mat_food", "ranches", s.RanchPowerKW);
                U("mat_antimatter", "containment", s.ContainmentPowerKW); U("speed", "quantum drive", s.QuantumDriveKW);
            }
            var biggest = users.Count == 0 ? 1 : users.Max(u => u.Kw);
            _users.Set(users.Count == 0 ? new[] { new HudRow("idle", "nothing", Severity.Muted) }
                : users.OrderByDescending(u => u.Kw).Select(u => new HudRow(u.Icon, $"{u.Name}  {u.Kw:N0} kW", Severity.Normal, u.Kw / biggest)).ToArray());
        }
    }

    /// <summary>The last five minutes of game time: made, used, and how full the batteries were.</summary>
    private sealed partial class PowerGraph : Control
    {
        private const int Samples = 300;

        private readonly List<(double Made, double Used, double Battery)> _points = new();
        private SectorState? _of;
        private CraftId _root = CraftId.None;
        private long _lastTick = -1;

        public PowerGraph()
        {
            CustomMinimumSize = new Vector2(0, 96);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            TooltipText = "The last 5 minutes of game time";
        }

        public bool Due(SectorState sector) => !ReferenceEquals(sector, _of) || sector.Tick < _lastTick || sector.Tick - _lastTick >= FixedStepClock.TicksPerSecond;

        public void Add(SectorState sector, CraftId root, double made, double used, double battery)
        {
            if (!ReferenceEquals(sector, _of) || sector.Tick < _lastTick || root != _root) _points.Clear(); // another game, or another network
            (_of, _root, _lastTick) = (sector, root, sector.Tick);
            _points.Add((made, used, battery));
            if (_points.Count > Samples) _points.RemoveAt(0);
            if (IsVisibleInTree()) QueueRedraw();
        }

        public override void _Draw()
        {
            var size = GetThemeFontSize("font_size", "HudText");
            var font = GetThemeDefaultFont();
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.45f));
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(UiTheme.Edge, 0.35f), filled: false);
            var top = size + 8f;
            var height = Size.Y - top - 4;
            var most = _points.Count == 0 ? 1 : Math.Max(1, _points.Max(p => Math.Max(p.Made, p.Used)));
            Vector2 At(int i, double share) => new(Size.X * (Samples - _points.Count + i) / (Samples - 1f), top + height * (1 - (float)Mathf.Clamp(share, 0, 1)));
            if (_points.Count > 1)
            {
                var battery = new List<Vector2> { new(At(0, 0).X, top + height) };
                for (var i = 0; i < _points.Count; i++) battery.Add(At(i, _points[i].Battery));
                battery.Add(new Vector2(At(_points.Count - 1, 0).X, top + height));
                for (var i = 1; i < battery.Count - 2; i++) // columns, so a flat line is drawn too
                    DrawColoredPolygon(new[] { battery[i], battery[i + 1], new Vector2(battery[i + 1].X, top + height + 0.5f), new Vector2(battery[i].X, top + height + 0.5f) }, new Color(UiTheme.Accent, 0.16f));
                DrawPolyline(_points.Select((p, i) => At(i, p.Made / most)).ToArray(), Palette.Good, 2);
                DrawPolyline(_points.Select((p, i) => At(i, p.Used / most)).ToArray(), Palette.Warning, 2);
            }
            // Named, not only coloured (#68).
            var x = 6f;
            foreach (var (text, color) in new[] { ("made", Palette.Good), ("used", Palette.Warning), ("battery", UiTheme.Accent) })
            {
                DrawRect(new Rect2(x, 6 + size * 0.35f, 12, 3), color);
                DrawString(font, new Vector2(x + 16, 4 + font.GetAscent(size)), text, HorizontalAlignment.Left, -1, size, Colors.White);
                x += 28 + font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
            }
            DrawString(font, new Vector2(0, 4 + font.GetAscent(size)), $"top: {most:N0} kW", HorizontalAlignment.Right, Size.X - 6, size, UiTheme.Of(Severity.Muted));
        }
    }
}
