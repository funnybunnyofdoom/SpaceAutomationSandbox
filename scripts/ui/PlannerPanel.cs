using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// The production planner (#121): a design to build or a material rate to keep up, the chain behind it down to ores, what holds it back
/// and where to go about it. For players who want it: opened from the shipyard, with no key until one is set.
/// </summary>
public partial class PlannerPanel : PanelContainer
{
    private const double RefreshSeconds = 2;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _pilot;
    private readonly MaterialCatalog _materials;
    private readonly Func<IReadOnlyList<(string Label, Func<ShipDesign?> Load)>> _designs;
    private readonly OptionButton _goal, _design, _material, _scope;
    private readonly SpinBox _rate;
    private readonly HBoxContainer _designRow, _rateRow;
    private readonly InfoList _summary = new();
    private readonly VBoxContainer _issues = new(), _tree = new();
    private IReadOnlyList<(string Label, Func<ShipDesign?> Load)> _designList = Array.Empty<(string, Func<ShipDesign?>)>();
    private double _sinceRefresh;
    private string _shown = "";

    /// <summary>Go where an issue can be dealt with.</summary>
    public event Action<PlanIssue>? IssueChosen;

    public PlannerPanel(Func<SectorState> sector, Func<CraftState?> pilot, MaterialCatalog materials, Func<IReadOnlyList<(string Label, Func<ShipDesign?> Load)>> designs)
    {
        (_sector, _pilot, _materials, _designs) = (sector, pilot, materials, designs);
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -512;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var title = new Label { MouseFilter = MouseFilterEnum.Pass, TooltipText = "An estimate from the same rates the game uses. It changes nothing by itself." };
        InputActions.TitleWithKey(title, "PRODUCTION PLANNER", InputActions.Planner);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);

        var top = new HBoxContainer();
        box.AddChild(top);
        _goal = Options(top);
        _goal.AddItem("Build a design");
        _goal.AddItem("Keep up a rate");
        _goal.ItemSelected += _ => { Show(); Refresh(); };
        _scope = Options(top);
        _scope.AddItem("This system");
        _scope.AddItem("Every system");
        _scope.ItemSelected += _ => Refresh();

        _designRow = new HBoxContainer();
        box.AddChild(_designRow);
        _design = Options(_designRow);
        _design.ItemSelected += _ => Refresh();

        _rateRow = new HBoxContainer();
        box.AddChild(_rateRow);
        _material = Options(_rateRow);
        foreach (var m in materials.Materials) _material.AddIconItem(UiIcons.Material(m.Id), m.Name);
        _material.ItemSelected += _ => Refresh();
        _rate = new SpinBox { MinValue = 1, MaxValue = 1000, Step = 1, Value = 10, Suffix = "t/h", CustomMinimumSize = new Vector2(110, 0) };
        _rate.ValueChanged += _ => Refresh();
        _rateRow.AddChild(_rate);

        box.AddChild(_summary);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        var inside = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        inside.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(inside);
        inside.AddChild(UiTheme.Header("What holds it back"));
        inside.AddChild(_issues);
        inside.AddChild(UiTheme.Header("The chain"));
        inside.AddChild(_tree);
        Show();
    }

    public PlannerPanel() : this(() => new SectorState(0), () => null, new MaterialCatalog(Array.Empty<MaterialDefinition>()), Array.Empty<(string, Func<ShipDesign?>)>) { }

    private static OptionButton Options(Container parent)
    {
        var options = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        parent.AddChild(options);
        return options;
    }

    private void Show()
    {
        _designRow.Visible = _goal.Selected == 0;
        _rateRow.Visible = _goal.Selected == 1;
    }

    /// <summary>Opens on a design (from the shipyard), or as it was.</summary>
    public void Open(ShipDesign? design = null)
    {
        _designList = _designs();
        _design.Clear();
        foreach (var (label, _) in _designList) _design.AddItem(label);
        if (design is not null)
        {
            _goal.Select(0);
            var at = _designList.ToList().FindIndex(d => d.Load()?.Id == design.Id);
            if (at < 0) { _designList = _designList.Prepend((design.Name, () => design)).ToList(); _design.AddItem(design.Name); _design.Select(_design.ItemCount - 1); ReorderFirst(); }
            else _design.Select(at);
        }
        else if (_design.ItemCount > 0 && _design.Selected < 0) _design.Select(0);
        Show();
        Visible = true;
        Refresh();
    }

    /// <summary>Opens on keeping up a material (tests, the dev hook).</summary>
    public void OpenRate(string material, double tonnesPerHour)
    {
        Open();
        _goal.Select(1);
        _material.Select(_materials.Materials.ToList().FindIndex(m => m.Id == material));
        _rate.SetValueNoSignal(tonnesPerHour);
        Show();
        Refresh();
    }

    private void ReorderFirst()
    {
        _design.Clear();
        foreach (var (label, _) in _designList) _design.AddItem(label);
        _design.Select(0);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private string Name(string material) => _materials.Contains(material) ? _materials.Get(material).Name : material;

    private static string Hours(double? hours) => hours is not { } h ? "never, as things are" : h < 1 ? $"{h * 60:0} min" : $"{h:0.#} h";

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        if (sector.Parts is null) return;
        var star = _scope.Selected == 1 || _pilot() is not { } pilot ? (BodyId?)null : sector.StarAt(pilot.Position)?.Id;
        ProductionPlan plan;
        var rows = new List<HudRow>();
        if (_goal.Selected == 0)
        {
            if (_design.Selected < 0 || _design.Selected >= _designList.Count || _designList[_design.Selected].Load() is not { } design) { _summary.Set(new[] { new HudRow("idle", "Choose a design.", Severity.Muted) }); return; }
            plan = sector.PlanDesign(design, star);
            rows.Add(new HudRow("clock", $"The first {design.Name}: {Hours(plan.Hours)}", plan.Hours is null ? Severity.Bad : Severity.Normal,
                Tip: $"Making what the stock lacks, then {Hours(plan.FabricationHours)} at the fabricators"));
            if (plan.Fix.Length > 0) rows.Add(new HudRow("ok", $"With {plan.Fix}: {Hours(plan.HoursWithFix)}", Severity.Good));
        }
        else
        {
            var material = _materials.Materials[Math.Max(0, _material.Selected)].Id;
            plan = sector.PlanRate(material, _rate.Value * 1000, star);
            var share = plan.Tree.Count == 0 ? 0 : plan.Tree.Min(n => n.Share);
            rows.Add(new HudRow("clock", $"{_rate.Value:0} t/h of {Name(material)}: {share * 100:0}% of it now", share >= 0.999 ? Severity.Good : Severity.Warning, share));
            if (plan.Fix.Length > 0 && plan.HoursWithFix is { } after) rows.Add(new HudRow("ok", $"With {plan.Fix}: {after:0}%", Severity.Good));
        }
        _summary.Set(rows);

        var key = string.Join("|", plan.Issues.Select(i => i.Text)) + "#" + string.Join("|", Flatten(plan.Tree).Select(n => $"{n.Material}{n.Need:0}{n.MadePerHour:0}{n.Share:0.00}"));
        if (key == _shown) return;
        _shown = key;
        foreach (var child in _issues.GetChildren().Concat(_tree.GetChildren())) { child.GetParent().RemoveChild(child); child.QueueFree(); }
        if (plan.Issues.Count == 0) _issues.AddChild(Text("Nothing: it can all be done as things are."));
        foreach (var issue in plan.Issues)
        {
            var row = new HBoxContainer();
            var info = new InfoRow { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            info.Set(new HudRow(Icon(issue.Limit), $"{issue.Text} {issue.Suggestion}", issue.Limit is PlanLimit.Unobtainable ? Severity.Bad : Severity.Warning));
            row.AddChild(info);
            if (!issue.Target.IsNone || issue.Limit is PlanLimit.Miners or PlanLimit.Fabrication or PlanLimit.Refineries or PlanLimit.Power)
            {
                var go = new Button { Text = "Go", FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Opens where it can be done" };
                go.Pressed += () => IssueChosen?.Invoke(issue);
                row.AddChild(go);
            }
            _issues.AddChild(row);
        }
        foreach (var node in plan.Tree) AddNode(node, 0, _goal.Selected == 1);
    }

    private static IEnumerable<PlanNode> Flatten(IEnumerable<PlanNode> nodes) => nodes.SelectMany(n => Flatten(n.Inputs).Prepend(n));

    private void AddNode(PlanNode node, int depth, bool perHour)
    {
        var row = new HBoxContainer();
        row.AddChild(new Control { CustomMinimumSize = new Vector2(depth * 16, 0) });
        var info = new InfoRow { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var need = perHour ? $"{node.Need / 1000:0.##} t/h" : $"{node.Need / 1000:0.##} t";
        var stock = node.StockKg > 0 ? $", {node.StockKg / 1000.0:0.#} t in stock" : "";
        var made = node.MadePerHour > 0 ? $", {node.MadePerHour / 1000:0.##} t/h made" : "";
        info.Set(new HudRow("mat_" + node.Material, $"{Name(node.Material)}: {need}{stock}{made}  ({node.How})",
            node.Share >= 0.999 ? Severity.Good : node.Share <= 0 ? Severity.Bad : Severity.Warning, Tip: $"{node.Share * 100:0}% covered"));
        row.AddChild(info);
        _tree.AddChild(row);
        foreach (var input in node.Inputs) AddNode(input, depth + 1, perHour);
    }

    private static string Icon(PlanLimit limit) => limit switch
    {
        PlanLimit.Survey => "survey", PlanLimit.Miners => "mining", PlanLimit.Refineries => "shipyard", PlanLimit.Freight => "freight", PlanLimit.Storage => "cargo",
        PlanLimit.Power => "power", PlanLimit.Fabrication => "shipyard", _ => "danger",
    };

    private static Label Text(string text) => new() { Text = text, ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0), Modulate = new Color(1, 1, 1, 0.75f) };
}
