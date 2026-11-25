using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Swarms (#30), opened from the Fleet panel: groups of identical miners that can swarm, and every swarm with
/// its rate, what limits it, and its orders (grow, split off ships, change job).
/// </summary>
public partial class SwarmPanel : PanelContainer
{
    private const double RefreshSeconds = 1;
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);

    private readonly Func<SectorState> _sector;
    private readonly Action _changed;
    private Label _message = null!;
    private VBoxContainer _candidates = null!;
    private VBoxContainer _swarms = null!;
    private double _sinceRefresh;
    private string _candidateKey = "", _swarmKey = "";
    private readonly Dictionary<SwarmId, InfoList> _texts = new();

    public SwarmPanel(Func<SectorState> sector, Action changed)
    {
        _sector = sector;
        _changed = changed;
        Build();
    }

    public SwarmPanel() : this(() => new SectorState(0), () => { }) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -490;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(box);

        var head = new HBoxContainer();
        var title = new Label { Text = "SWARMS", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.ThemeTypeVariation = "SectionHeader";
        head.AddChild(title);
        box.AddChild(head);
        box.AddChild(UiTabs.Row(("fleet", "Fleet", () => FleetChosen?.Invoke()), ("swarm", "Swarms", null)));
        box.AddChild(Text($"A swarm runs {SectorState.MinSwarmShips} or more identical miners with the same job as one, so fleets can be huge. "
                          + "It fills and empties at the pace its design, distance, the destination's free docking ports and storage allow.", dim: true));
        _message = Text("");
        box.AddChild(_message);
        box.AddChild(Heading("Ready to swarm"));
        _candidates = new VBoxContainer();
        box.AddChild(_candidates);
        box.AddChild(Heading("Swarms"));
        _swarms = new VBoxContainer();
        box.AddChild(_swarms);
    }

    /// <summary>Raised by the Fleet tab (#100).</summary>
    public event Action? FleetChosen;

    private static Label Text(string text, bool dim = false) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(450, 0),
        Modulate = new Color(1, 1, 1, dim ? 0.65f : 1f),
    };

    private static Label Heading(string text) => UiTheme.Header(text);

    private static Button Act(string text, string tip, Action pressed)
    {
        var button = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None };
        button.Pressed += pressed;
        return button;
    }

    public void Open()
    {
        Visible = true;
        _message.Text = "";
        Refresh(force: true);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private void Show(ActionResult result)
    {
        if (result.Done) _changed();
        _message.Text = result.Message;
        _message.AddThemeColorOverride("font_color", result.Done ? OkColor : ErrorColor);
        Refresh(force: true);
    }

    private static string Body(SectorState sector, BodyId id) => sector.Bodies.FirstOrDefault(b => b.Id == id)?.Name ?? "(gone)";
    private static string Craft(SectorState sector, CraftId id) => sector.Crafts.FirstOrDefault(c => c.Id == id)?.Name ?? "(gone)";

    private static void Empty(Node box)
    {
        foreach (var child in box.GetChildren()) { box.RemoveChild(child); child.QueueFree(); }
    }

    private void Refresh(bool force = false)
    {
        _sinceRefresh = 0;
        var sector = _sector();

        var candidates = sector.SwarmCandidates();
        var key = string.Join("|", candidates.Select(c => $"{c.DesignName},{c.SourceBodyId},{c.MaterialId},{c.DestinationId},{c.Ships.Count}"));
        if (force || key != _candidateKey)
        {
            _candidateKey = key;
            Empty(_candidates);
            if (candidates.Count == 0)
                _candidates.AddChild(Text($"None: it takes {SectorState.MinSwarmShips} undamaged solar-powered miners of one blueprint on the same mining job (the one you fly doesn't count).", dim: true));
            foreach (var c in candidates)
            {
                var row = new HBoxContainer();
                if (sector.Crafts.FirstOrDefault(x => x.Id == c.Ships[0]) is { } first) row.AddChild(new DesignPicture { Design = first.Design });
                row.AddChild(UiTheme.Icon("mat_" + c.MaterialId));
                var label = Text($"{c.Ships.Count} × {c.DesignName}: {c.MaterialId} from {Body(sector, c.SourceBodyId)} to {Craft(sector, c.DestinationId)}");
                label.CustomMinimumSize = new Vector2(260, 0);
                label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(label);
                row.AddChild(Act("Form swarm", "Run these ships as one swarm (they join the swarm with the same blueprint and job, if there is one)", () => Show(_sector().FormSwarm(c.Ships))));
                _candidates.AddChild(row);
            }
        }

        // Rows hold choices the player may be making: rebuilt only when swarms, yards or stations come and go.
        var stations = sector.Crafts.Where(c => c.Stats.DockingPorts.Count > 0).ToList();
        key = string.Join("|", sector.Swarms.Select(s => s.Id)) + "/" + string.Join(",", stations.Select(s => s.Id));
        if (force || key != _swarmKey)
        {
            _swarmKey = key;
            _texts.Clear();
            Empty(_swarms);
            if (sector.Swarms.Count == 0) _swarms.AddChild(Text("None yet.", dim: true));
            foreach (var swarm in sector.Swarms) _swarms.AddChild(MakeSwarm(sector, swarm, stations));
        }
        foreach (var swarm in sector.Swarms)
            if (_texts.TryGetValue(swarm.Id, out var label)) Describe(sector, swarm, label);
    }

    private static void Describe(SectorState sector, SwarmState swarm, InfoList list)
    {
        var full = swarm.Rate.KgPerS(swarm);
        var rows = new List<HudRow>
        {
            new("swarm", $"{swarm.Name}: {swarm.Count:N0} × {swarm.Design.Name}"),
            new("mat_" + swarm.MaterialId, $"{swarm.MaterialId} from {Body(sector, swarm.SourceBodyId)} to {Craft(sector, swarm.DestinationId)}, trip {swarm.Rate.CycleS / 60:0.0} min", Severity.Muted, Link: new TargetRef(swarm.DestinationId, BodyId.None)),
            new("freight", $"{swarm.DeliveryKgPerS * 3.6:N0} of {full * 3.6:N0} t/h", swarm.DeliveryKgPerS < 0.5 * full ? Severity.Warning : Severity.Good, full > 0 ? swarm.DeliveryKgPerS / full : 0,
                Tip: $"Delivering now, of what the ships could do. {swarm.DeliveredKg / 1000.0:N0} t so far"),
            new("cargo", $"{swarm.Inventory.TotalKg / 1000.0:N1} / {swarm.CargoCapacityKg / 1000.0:N0} t in the holds", Severity.Normal, swarm.Inventory.TotalKg / (double)Math.Max(1, swarm.CargoCapacityKg)),
            new(swarm.LimitReason is null ? "work" : "wait", swarm.Status, swarm.LimitReason is null ? Severity.Normal : Severity.Warning),
        };
        if (swarm.GrowRemaining > 0)
        {
            rows.Add(new("shipyard", $"growing: {swarm.GrowRemaining:N0} more to order at {Craft(sector, swarm.GrowYardId)}"));
            if (swarm.GrowWaitReason is { } why) rows.Add(new("wait", $"growth waiting: {why}", Severity.Warning));
        }
        list.Set(rows);
    }

    private VBoxContainer MakeSwarm(SectorState sector, SwarmState swarm, List<CraftState> stations)
    {
        var id = swarm.Id;
        var box = new VBoxContainer();
        var head = new HBoxContainer();
        head.AddChild(new DesignPicture { Design = swarm.Design, CustomMinimumSize = new Vector2(64, 64), SizeFlagsVertical = SizeFlags.ShrinkBegin });
        var text = new InfoList();
        _texts[id] = text;
        head.AddChild(text);
        box.AddChild(head);

        var yards = stations.Where(s => s.IsShipyard).ToList();
        if (yards.Count > 0)
        {
            var grow = new HBoxContainer();
            grow.AddChild(new Label { Text = "Grow at" });
            var yard = new OptionButton { FocusMode = FocusModeEnum.None };
            foreach (var y in yards) yard.AddItem(y.Name);
            var growing = yards.FindIndex(y => y.Id == swarm.GrowYardId);
            if (growing >= 0) yard.Select(growing);
            grow.AddChild(yard);
            foreach (var n in new[] { 10, 100, 500 })
                grow.AddChild(Act($"+{n}", $"Build {n} more ships into this swarm, one after another, as materials allow",
                    () => Show(_sector().GrowSwarm(id, yards[Math.Max(0, yard.Selected)].Id, n))));
            grow.AddChild(Act("Stop", "Order no more ships; one already being built still joins", () =>
            {
                _sector().StopGrowing(id);
                Show(new ActionResult(true, "Growth stopped."));
            }));
            box.AddChild(grow);
        }

        var split = new HBoxContainer();
        split.AddChild(new Label { Text = "Split off" });
        foreach (var n in new[] { 1, 10, SectorState.MaxSwarmSplit })
            split.AddChild(Act($"{n}", $"{n} ship{(n == 1 ? " leaves" : "s leave")} the swarm and appear beside its destination as ordinary ships, with their cargo and the same job",
                () => Show(_sector().SplitSwarm(id, n))));
        box.AddChild(split);

        // A new job: source, material, destination.
        var star = sector.Bodies.Any(b => b.Id == swarm.SourceBodyId) ? sector.StarOf(swarm.SourceBodyId) : null;
        var sources = sector.Bodies.Where(b => b.IsMineable && b.AnySiteSurveyed && (star is null || sector.StarOf(b.Id) == star)).ToList();
        if (sources.Count > 0 && stations.Count > 0)
        {
            var job = new HBoxContainer();
            var source = new OptionButton { FocusMode = FocusModeEnum.None };
            foreach (var b in sources) source.AddIconItem(UiIcons.Get(UiIcons.BodyIcon(b)), b.Name);
            var material = new OptionButton { FocusMode = FocusModeEnum.None };
            var materials = new List<string>();
            void Materials()
            {
                var body = sources[Math.Max(0, source.Selected)];
                materials.Clear();
                materials.AddRange(body.MineableMaterials.Where(m => body.SurveyedDeposit(m) > 0));
                material.Clear();
                foreach (var m in materials) material.AddIconItem(UiIcons.Material(m), m);
                var now = materials.IndexOf(_sector().Swarms.FirstOrDefault(s => s.Id == id)?.MaterialId ?? "");
                if (materials.Count > 0) material.Select(Math.Max(0, now));
            }
            source.Select(Math.Max(0, sources.FindIndex(b => b.Id == swarm.SourceBodyId)));
            source.ItemSelected += _ => Materials();
            Materials();
            var destination = new OptionButton { FocusMode = FocusModeEnum.None };
            foreach (var s in stations) destination.AddItem(s.Name);
            destination.Select(Math.Max(0, stations.FindIndex(s => s.Id == swarm.DestinationId)));
            foreach (var option in new[] { source, material, destination })
            {
                option.ClipText = true;
                option.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                job.AddChild(option);
            }
            job.AddChild(Act("Set job", "Mine this here and deliver it there; what's in the holds goes to the new destination", () =>
            {
                if (materials.Count == 0) { Show(new ActionResult(false, "Nothing to mine there.")); return; }
                Show(_sector().RetargetSwarm(id, sources[Math.Max(0, source.Selected)].Id, materials[Math.Max(0, material.Selected)],
                    stations[Math.Max(0, destination.Selected)].Id));
            }));
            box.AddChild(job);
        }
        box.AddChild(new HSeparator());
        return box;
    }
}
