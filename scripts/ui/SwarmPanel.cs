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
    private readonly Dictionary<SwarmId, Label> _texts = new();

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
        head.AddChild(title);
        var close = new Button { Text = "Close", FocusMode = FocusModeEnum.None };
        close.Pressed += () => Visible = false;
        head.AddChild(close);
        box.AddChild(head);
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

    private static Label Text(string text, bool dim = false) => new()
    {
        Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(450, 0),
        Modulate = new Color(1, 1, 1, dim ? 0.65f : 1f),
    };

    private static Label Heading(string text) => new() { Text = text, Modulate = new Color(0.65f, 0.8f, 1f) };

    private static Button Action(string text, string tip, Action pressed)
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
                var label = Text($"{c.Ships.Count} × {c.DesignName}: {c.MaterialId} from {Body(sector, c.SourceBodyId)} to {Craft(sector, c.DestinationId)}");
                label.CustomMinimumSize = new Vector2(330, 0);
                label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(label);
                row.AddChild(Action("Form swarm", "Run these ships as one swarm (they join the swarm with the same blueprint and job, if there is one)", () => Show(_sector().FormSwarm(c.Ships))));
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

    private static void Describe(SectorState sector, SwarmState swarm, Label label)
    {
        var full = swarm.Rate.KgPerS(swarm);
        label.Text = $"{swarm.Name}: {swarm.Count:N0} × {swarm.Design.Name}\n"
                     + $"   {swarm.MaterialId} from {Body(sector, swarm.SourceBodyId)} to {Craft(sector, swarm.DestinationId)}, trip {swarm.Rate.CycleS / 60:0.0} min\n"
                     + $"   delivering {swarm.DeliveryKgPerS * 3.6:N0} t/h (could do {full * 3.6:N0} t/h), {swarm.DeliveredKg / 1000.0:N0} t so far\n"
                     + $"   in the holds {swarm.Inventory.TotalKg / 1000.0:N1} / {swarm.CargoCapacityKg / 1000.0:N0} t\n"
                     + $"   {swarm.Status}"
                     + (swarm.GrowRemaining > 0 ? $"\n   growing: {swarm.GrowRemaining:N0} more to order at {Craft(sector, swarm.GrowYardId)}" : "")
                     + (swarm.GrowWaitReason is { } why && swarm.GrowRemaining > 0 ? $"\n   growth waiting: {why}" : "");
        label.AddThemeColorOverride("font_color", swarm.LimitReason is null ? Colors.White : new Color(1f, 0.8f, 0.5f));
    }

    private VBoxContainer MakeSwarm(SectorState sector, SwarmState swarm, List<CraftState> stations)
    {
        var id = swarm.Id;
        var box = new VBoxContainer();
        var text = Text("");
        _texts[id] = text;
        box.AddChild(text);

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
                grow.AddChild(Action($"+{n}", $"Build {n} more ships into this swarm, one after another, as materials allow",
                    () => Show(_sector().GrowSwarm(id, yards[Math.Max(0, yard.Selected)].Id, n))));
            grow.AddChild(Action("Stop", "Order no more ships; one already being built still joins", () =>
            {
                _sector().StopGrowing(id);
                Show(new ActionResult(true, "Growth stopped."));
            }));
            box.AddChild(grow);
        }

        var split = new HBoxContainer();
        split.AddChild(new Label { Text = "Split off" });
        foreach (var n in new[] { 1, 10, SectorState.MaxSwarmSplit })
            split.AddChild(Action($"{n}", $"{n} ship{(n == 1 ? " leaves" : "s leave")} the swarm and appear beside its destination as ordinary ships, with their cargo and the same job",
                () => Show(_sector().SplitSwarm(id, n))));
        box.AddChild(split);

        // A new job: source, material, destination.
        var star = sector.Bodies.Any(b => b.Id == swarm.SourceBodyId) ? sector.StarOf(swarm.SourceBodyId) : null;
        var sources = sector.Bodies.Where(b => b.IsMineable && b.AnySiteSurveyed && (star is null || sector.StarOf(b.Id) == star)).ToList();
        if (sources.Count > 0 && stations.Count > 0)
        {
            var job = new HBoxContainer();
            var source = new OptionButton { FocusMode = FocusModeEnum.None };
            foreach (var b in sources) source.AddItem(b.Name);
            var material = new OptionButton { FocusMode = FocusModeEnum.None };
            var materials = new List<string>();
            void Materials()
            {
                var body = sources[Math.Max(0, source.Selected)];
                materials.Clear();
                materials.AddRange(body.MineableMaterials.Where(m => body.SurveyedDeposit(m) > 0));
                material.Clear();
                foreach (var m in materials) material.AddItem(m);
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
            job.AddChild(Action("Set job", "Mine this here and deliver it there; what's in the holds goes to the new destination", () =>
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
