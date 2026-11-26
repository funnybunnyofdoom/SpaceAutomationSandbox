using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Fleet manager (key F, #103): counts to click as filters, search by name or id, sorting, and a row per craft with its picture,
/// what it's doing or waiting for, and its battery and cargo. Only the rows in view exist, so thousands of craft stay quick.
/// </summary>
public partial class FleetPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;
    private const float RowHeight = 74;
    private static readonly (string Name, string Icon)[] Filters =
        { ("All", ""), ("Ships", "fleet"), ("Stations", "shipyard"), ("Automated", "work"), ("Waiting", "wait"), ("Idle ships", "idle") };
    private static readonly string[] Sorts = { "Id", "Name", "Distance", "State" };

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftId?> _piloted;
    private Label _title = null!, _empty = null!;
    private LineEdit _search = null!;
    private OptionButton _sort = null!;
    private ScrollContainer _scroll = null!;
    private Control _canvas = null!;
    private readonly List<Button> _filterButtons = new();
    private readonly List<FleetRow> _rows = new();
    private int _filter;
    private double _sinceRefresh;
    private List<CraftState> _shown = new();

    /// <summary>Raised when the player picks a craft to fly.</summary>
    public event Action<CraftId>? CraftChosen;

    /// <summary>Raised when the player picks a craft as the piloted ship's target.</summary>
    public event Action<CraftId>? CraftTargeted;

    /// <summary>Raised when something in the game was changed from here.</summary>
    public event Action? Changed;

    /// <summary>Raised when the player wants to rename a craft (#115).</summary>
    public event Action<CraftId>? RenameChosen;

    /// <summary>Mark it as a wreck to salvage (#135).</summary>
    public event Action<CraftId>? ScrapChosen;

    /// <summary>Send it for repairs, or call that off (#148).</summary>
    public event Action<CraftId>? RepairChosen;

    /// <summary>Mark every defunct craft as a wreck (#144).</summary>
    public event Action? CleanUpChosen;

    private Button _cleanUp = null!;

    /// <summary>Raised by the Swarms button (#30).</summary>
    public event Action? SwarmsChosen;

    public FleetPanel(Func<SectorState> sector, Func<CraftId?> piloted)
    {
        _sector = sector;
        _piloted = piloted;
        Build();
    }

    public FleetPanel() : this(() => new SectorState(0), () => null) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -492;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        AddChild(box);
        _title = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 18);
        _title.ThemeTypeVariation = "SectionHeader";
        var head = new HBoxContainer();
        head.AddChild(_title);
        box.AddChild(head);
        box.AddChild(UiTabs.Row(("fleet", "Fleet", null), ("swarm", "Swarms", () => SwarmsChosen?.Invoke())));

        var filters = new HFlowContainer();
        box.AddChild(filters);
        var group = new ButtonGroup();
        for (var i = 0; i < Filters.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                ToggleMode = true, ButtonGroup = group, ButtonPressed = i == 0, FocusMode = FocusModeEnum.None, TooltipText = Filters[i].Name,
                Icon = Filters[i].Icon.Length > 0 ? UiIcons.Get(Filters[i].Icon) : null, TextureFilter = TextureFilterEnum.Nearest,
            };
            button.Pressed += () => { _filter = index; _scroll.ScrollVertical = 0; Refresh(); };
            filters.AddChild(button);
            _filterButtons.Add(button);
        }

        var controls = new HBoxContainer();
        box.AddChild(controls);
        _search = new LineEdit { PlaceholderText = "Search name or id", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => Refresh();
        controls.AddChild(_search);
        _sort = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var s in Sorts) _sort.AddItem($"by {s}");
        _sort.ItemSelected += _ => Refresh();
        controls.AddChild(_sort);
        _cleanUp = new Button { FocusMode = FocusModeEnum.None, Icon = UiIcons.Get("salvage"), Visible = false, TooltipText = "Mark every defunct craft (lost half its parts, or all its engines) as a wreck for salvagers" };
        _cleanUp.Pressed += () => CleanUpChosen?.Invoke();
        controls.AddChild(_cleanUp);

        _empty = new Label { Text = "  No craft match.", Modulate = new Color(1, 1, 1, 0.6f), Visible = false };
        box.AddChild(_empty);
        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(_scroll);
        _canvas = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Pass };
        _scroll.AddChild(_canvas);
        _scroll.GetVScrollBar().ValueChanged += _ => ShowRows();
        _scroll.Resized += ShowRows;
    }

    public void Open()
    {
        Visible = true;
        Refresh();
    }

    /// <summary>Opens on the craft with a name (#100).</summary>
    public void OpenAt(string name)
    {
        _filter = 0;
        _filterButtons[0].ButtonPressed = true;
        _search.Text = name;
        Open();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private static bool IsShip(CraftState c) => c.Stats.TotalThrustN > 0;
    public static bool IsWaiting(CraftState c) => c.Job is { Active: true } job && job.Status.StartsWith("waiting");

    private bool Matches(CraftState c, int filter) => filter switch
    {
        1 => IsShip(c),
        2 => !IsShip(c),
        3 => c.IsAutomated,
        4 => IsWaiting(c),
        5 => IsShip(c) && !c.IsAutomated && c.Id != _piloted(),
        _ => true,
    };

    private CraftState? Pilot(SectorState sector) => _piloted() is { } id ? sector.Crafts.FirstOrDefault(c => c.Id == id) : null;

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var crafts = sector.Crafts.Where(SectorState.IsPlayers).ToList(); // not raiders (#84)
        var pilot = Pilot(sector);
        _title.Text = $"FLEET ({crafts.Count:N0})  ({InputActions.Key1(InputActions.Fleet)} to close)"
                      + (sector.Swarms.Count > 0 ? $"   + {sector.Swarms.Sum(s => s.Count):N0} in swarms" : "");
        var defunct = sector.DefunctCrafts().Count; // #144
        _cleanUp.Visible = defunct > 0;
        _cleanUp.Text = $"Clean up {defunct}";
        for (var i = 0; i < Filters.Length; i++)
        {
            var count = crafts.Count(c => Matches(c, i));
            _filterButtons[i].Text = Filters[i].Icon.Length == 0 ? $"All {count:N0}" : $"{count:N0}";
        }

        double Distance(CraftState c) => pilot is null ? 0 : pilot.Position.DistanceTo(c.Position);
        var search = _search.Text.Trim();
        var visible = crafts.Where(c => Matches(c, _filter) && (search.Length == 0 || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Id.ToString().Contains(search)));
        _shown = (_sort.Selected switch
        {
            1 => visible.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id.Value),
            2 => visible.OrderBy(Distance).ThenBy(c => c.Id.Value),
            3 => visible.OrderBy(State).ThenBy(c => c.Id.Value),
            _ => visible.OrderBy(c => c.Id.Value),
        }).ToList();
        _empty.Visible = _shown.Count == 0;
        var height = _shown.Count * Height;
        if (!Mathf.IsEqualApprox(_canvas.CustomMinimumSize.Y, height)) _canvas.CustomMinimumSize = new Vector2(0, height);
        ShowRows();
    }

    private float Height => Mathf.Round(RowHeight * GetThemeDefaultFontSize() / 16f);

    /// <summary>Places the rows that are in view; they're reused while scrolling.</summary>
    private void ShowRows()
    {
        if (!IsInsideTree() || !Visible) return;
        var sector = _sector();
        var pilot = Pilot(sector);
        var first = Mathf.Max(0, Mathf.FloorToInt(_scroll.ScrollVertical / Height));
        var count = Mathf.Min(Mathf.CeilToInt(_scroll.Size.Y / Height) + 1, _shown.Count - first);
        while (_rows.Count < count)
        {
            var row = new FleetRow();
            row.Fly += id => { CraftChosen?.Invoke(id); Refresh(); };
            row.Target += id => CraftTargeted?.Invoke(id);
            row.Rename += id => RenameChosen?.Invoke(id);
            row.Scrap += id => ScrapChosen?.Invoke(id);
            row.Repair += id => { RepairChosen?.Invoke(id); Refresh(); };
            row.Shelter += id => // #123
            {
                if (_sector().Crafts.FirstOrDefault(c => c.Id == id) is not { } ship) return;
                ship.KeepWorking = !ship.KeepWorking;
                Changed?.Invoke();
            };
            row.Gate += id =>
            {
                if (_sector().Crafts.FirstOrDefault(c => c.Id == id) is not { } gate) return;
                _sector().SetGateClosed(id, !gate.GateClosed); // #95
                Changed?.Invoke();
                Refresh();
            };
            _rows.Add(row);
            _canvas.AddChild(row);
        }
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Visible = i < count;
            if (i >= count) continue;
            _rows[i].Position = new Vector2(0, (first + i) * Height);
            _rows[i].Size = new Vector2(_canvas.Size.X, Height - 4);
            _rows[i].Set(sector, _shown[first + i], pilot);
        }
    }

    public static string Where(SectorState sector, CraftState craft) =>
        craft.IsDocked ? $"docked at {sector.GetCraft(craft.DockedToCraftId).Name}, port {craft.DockPortIndex + 1}"
        : craft.IsLanded ? $"{(craft.IsAnchored ? "anchored to" : "landed on")} {sector.GetBody(craft.LandedOnBodyId).Name}"
        : craft.RailOrbit is { } rail ? $"{(craft.IsShipyard ? "orbiting" : "parked in orbit around")} {sector.GetBody(rail.Parent).Name}"
        : $"flying, {craft.SpeedMps:0} m/s";

    /// <summary>Sort key: in trouble or waiting first, then working, building, idle.</summary>
    private static int State(CraftState c) => Trouble(c) is not null ? 0 : IsWaiting(c) ? 1 : c.IsAutomated ? 2 : c.BuildQueue.Count > 0 ? 3 : 4;

    /// <summary>What's wrong with a craft, or null.</summary>
    public static string? Trouble(CraftState craft) =>
        craft.IsWreck ? "wreck, adrift"
        : craft.CrewShortReason is { } why ? $"stopped: {why}"
        : craft.PowerShort && !craft.LegacyFreePower ? "no power"
        : craft.PartHealth.Count > 0 || craft.PartsLost > 0 ? $"damaged ({craft.PartHealth.Count + craft.PartsLost} parts)" : null;

    /// <summary>A job's icon, by its kind.</summary>
    public static string JobIcon(string kind) => kind switch
    {
        "mining" => "mining", "freight" or "requests" => "freight", "survey" => "survey", "salvage" => "salvage", "trade" => "trade",
        "program" => "edit_program", "guard" or "escort" or "patrol" => "weapon", _ => "job",
    };

    private static string FormatDistance(double m) => m >= 1000 ? $"{m / 1000:N1} km" : $"{m:0} m";

    /// <summary>One craft: picture, name, what it's doing, battery and cargo, and its buttons.</summary>
    private sealed partial class FleetRow : PanelContainer
    {
        private readonly DesignPicture _picture = new() { CustomMinimumSize = new Vector2(44, 44) };
        private readonly Label _name = Line(), _doing = Line();
        private readonly TextureRect _light = UiTheme.Icon(""), _job = UiTheme.Icon("");
        private readonly StatBar _battery = new(), _cargo = new();
        private readonly Button _fly, _target, _gate, _rename, _shelter, _scrap, _repair;
        private CraftId _id = CraftId.None;

        public event Action<CraftId>? Fly, Target, Gate, Rename, Shelter, Scrap, Repair;

        private static Label Line() => new()
        {
            ThemeTypeVariation = "HudText", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore,
        };

        public FleetRow()
        {
            MouseFilter = MouseFilterEnum.Pass;
            MouseDefaultCursorShape = CursorShape.PointingHand;
            GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { AcceptEvent(); UiLinks.Open(new TargetRef(_id, BodyId.None)); } }; // #100
            var back = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.04f), ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2 };
            back.SetCornerRadiusAll(3);
            AddThemeStyleboxOverride("panel", back);
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            AddChild(row);
            row.AddChild(_picture);
            var middle = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
            middle.AddThemeConstantOverride("separation", 1);
            row.AddChild(middle);
            var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            top.AddChild(_light);
            top.AddChild(_name);
            _rename = Button("", "Rename", () => Rename?.Invoke(_id));
            (_rename.Icon, _rename.Flat, _rename.TextureFilter) = (UiIcons.Get("tool_draw"), true, TextureFilterEnum.Nearest);
            row.AddChild(_rename);
            _shelter = Button("", "", () => Shelter?.Invoke(_id));
            (_shelter.Icon, _shelter.Flat, _shelter.TextureFilter) = (UiIcons.Get("shield"), true, TextureFilterEnum.Nearest);
            row.AddChild(_shelter);
            _scrap = Button("", "Mark as a wreck: crew go home, salvagers take it apart", () => Scrap?.Invoke(_id)); // #135
            (_scrap.Icon, _scrap.Flat, _scrap.TextureFilter) = (UiIcons.Get("salvage"), true, TextureFilterEnum.Nearest);
            row.AddChild(_scrap);
            _repair = Button("", "", () => Repair?.Invoke(_id)); // #148
            (_repair.Icon, _repair.Flat, _repair.TextureFilter) = (UiIcons.Get("shipyard"), true, TextureFilterEnum.Nearest);
            row.AddChild(_repair);
            middle.AddChild(top);
            var second = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            second.AddChild(_job);
            second.AddChild(_doing);
            middle.AddChild(second);
            var bars = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            bars.AddChild(_battery);
            bars.AddChild(_cargo);
            middle.AddChild(bars);
            _fly = Button("Fly", "Take control of this craft (stations too: docked ships push them as tugs)", () => Fly?.Invoke(_id));
            _target = Button("Target", "Target it: the HUD shows distance and speed, and brake matches its motion", () => Target?.Invoke(_id));
            _gate = Button("", "A closed gate lets nothing through, either way", () => Gate?.Invoke(_id));
            foreach (var button in new[] { _fly, _target, _gate }) row.AddChild(button);
        }

        private static Button Button(string text, string tip, Action pressed)
        {
            var button = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            button.Pressed += pressed;
            return button;
        }

        public void Set(SectorState sector, CraftState craft, CraftState? pilot)
        {
            _id = craft.Id;
            var you = pilot?.Id == craft.Id;
            _picture.Design = craft.Design;
            var distance = pilot is null || you ? "" : $"  {FormatDistance(pilot.Position.DistanceTo(craft.Position))}";
            Put(_name, $"{craft.Name}{(you ? "  (you)" : "")}  {craft.Id}{distance}");
            _name.Modulate = you ? Palette.Good : Colors.White;

            var trouble = Trouble(craft);
            var waiting = IsWaiting(craft);
            Put(_light, trouble is not null ? craft.IsWreck ? "danger" : "warn" : waiting ? "wait" : craft.IsAutomated || craft.BuildQueue.Count > 0 ? "work" : you ? "ok" : "idle");
            var where = Where(sector, craft);
            var doing = (trouble is not null ? trouble + ";  " : "")
                        + (craft.SeekRepair ? $"repairs: {craft.SeekRepairStatus ?? "setting off"};  " : "") // #148
                        + (craft.Job is { Active: true } job ? $"{job.Status} ({job.CompletedCycles})"
                            : craft.BuildQueue.Count > 0 ? $"building {craft.BuildQueue[0].ShipName} {craft.BuildQueue[0].Fraction * 100:0}%"
                            : craft.GateClosed ? "GATE CLOSED" : where + (craft.Crew > 0 ? $", crew {craft.Crew}" : ""));
            Put(_doing, doing);
            _doing.Modulate = UiTheme.Of(craft.IsWreck ? Severity.Bad : trouble is not null || waiting || craft.GateClosed ? Severity.Warning : Severity.Muted);
            Put(_job, craft.Job is { Active: true } active ? JobIcon(active.Kind) : SectorState.IsGate(craft) ? "gate" : craft.IsShipyard ? "shipyard" : IsShip(craft) ? "fleet" : "orbit");
            var tip = $"{craft.Name} ({craft.Id})\n{doing}\n{where}";
            if (TooltipText != tip) TooltipText = tip;

            _battery.Visible = craft.Stats.EnergyCapacityKJ > 0;
            if (_battery.Visible)
            {
                var share = craft.StoredEnergyKJ / craft.Stats.EnergyCapacityKJ;
                _battery.Set(share, $"power {share * 100:0}%", craft.PowerShort ? Severity.Bad : share < 0.2 ? Severity.Warning : Severity.Normal);
            }
            _cargo.Visible = craft.HasCargoLimit ? craft.CargoCapacityKg > 0 : craft.CargoKg > 0;
            if (_cargo.Visible)
                _cargo.Set(craft.HasCargoLimit ? craft.CargoKg / (double)craft.CargoCapacityKg : 0,
                    craft.HasCargoLimit ? $"{craft.CargoKg / 1000.0:0.#} / {craft.CargoCapacityKg / 1000.0:0.#} t" : $"{craft.CargoKg / 1000.0:0.#} t");

            _rename.Visible = !craft.IsWreck;
            _repair.Visible = craft.SeekRepair || sector.SeekRepairBlocker(craft) is null;
            _repair.Modulate = new Color(1, 1, 1, craft.SeekRepair ? 1 : 0.75f);
            _repair.TooltipText = craft.SeekRepair ? "Going for repairs. Click: call it off" : "Seek repair: fly to the nearest shipyard and be repaired, then carry on";
            _scrap.Visible = !craft.IsWreck && !you && (SectorState.NeedsRepair(craft) || craft.Stats.TotalThrustN <= 0 && !craft.IsShipyard || craft.CrewShortReason is not null); // shown for the defunct; the menu has it for any
            _shelter.Visible = craft.IsAutomated && (sector.Options[WorldOptions.SolarFlares] != WorldAmount.Off || sector.Options[WorldOptions.MeteorShowers] != WorldAmount.Off); // #123
            _shelter.Modulate = new Color(1, 1, 1, craft.KeepWorking ? 0.3f : 1);
            _shelter.TooltipText = craft.KeepWorking ? "Keeps working through flares and meteors. Click: shelter from them" : "Shelters from flares and meteors. Click: keep working";
            _fly.Text = you ? "Flying" : "Fly";
            _fly.Disabled = _target.Disabled = you;
            _gate.Visible = SectorState.IsGate(craft) && craft.Stats.GateLockCount > 0; // #95
            if (_gate.Visible) _gate.Text = craft.GateClosed ? "Open gate" : "Close gate";
        }

        private static void Put(Label label, string text) { if (label.Text != text) label.Text = text; }

        private static void Put(TextureRect icon, string name)
        {
            var texture = UiIcons.Get(name);
            if (icon.Texture != texture) icon.Texture = texture;
        }
    }
}
