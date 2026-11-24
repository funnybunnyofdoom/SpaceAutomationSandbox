using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Cargo view (key C), usable anywhere. Shows what the craft you're flying holds, side by side with
/// the station it's docked at, or else with your target craft (Tab), read-only. Only across a dock
/// does each material row get buttons to load or unload 1 t or everything that fits (all moves go
/// through <see cref="SectorState.TryTransfer"/>: whole kg, all-or-nothing).
/// Below: construction kits on either side (Load/Unload across a dock, #8), and station requests
/// ("keep at least X t here", #1): editable for the station you're flying or docked at, read-only
/// for a targeted one.
/// </summary>
public partial class CargoPanel : PanelContainer
{
    private const long OneTonne = 1_000;
    private const double RefreshSeconds = 0.25;
    private static readonly Color Dim = new(1, 1, 1, 0.6f);

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _ship;
    private readonly MaterialCatalog _materials;
    private Label _hint = null!;
    private VBoxContainer _grid = null!;
    private CheckBox _showEmpty = null!;
    private readonly List<(string Id, StatBar Mine, StatBar? Theirs)> _rows = new();
    private HBoxContainer _sides = null!;
    private readonly DesignPicture _myPicture = new(), _theirPicture = new();
    private readonly StatBar _myRoom = new(), _theirRoom = new();
    private Label _status = null!;
    private VBoxContainer _crewRow = null!;
    private InfoList _crewBars = null!;
    private Button _crewIn = null!, _crewOut = null!, _recruit = null!;
    private VBoxContainer _kits = null!;
    private string _kitsKey = "";
    private Label _requestsTitle = null!;
    private InfoList _requests = null!;
    private HBoxContainer _requestRow = null!;
    private OptionButton _requestMaterial = null!;
    private SpinBox _requestTonnes = null!;
    private CraftId _requestStation = CraftId.None;
    private double _sinceRefresh;
    private string _layoutKey = "";

    public CargoPanel(Func<SectorState> sector, Func<CraftState?> ship, MaterialCatalog materials)
    {
        _sector = sector;
        _ship = ship;
        _materials = materials;
        Build();
    }

    public CargoPanel() : this(() => new SectorState(0), () => null, new MaterialCatalog(Array.Empty<MaterialDefinition>())) { }

    private void Build()
    {
        // Right side, full height, scrolling: materials, kits and requests don't fit under the HUD.
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -472;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(box);
        var title = new Label();
        InputActions.TitleWithKey(title, "CARGO", InputActions.Cargo);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        _hint = new Label { Modulate = Dim, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        box.AddChild(_hint);
        // Both craft with how full they are, then a row per material: bars to compare, arrows or dragging to move (#102).
        _sides = new HBoxContainer();
        box.AddChild(_sides);
        _sides.AddChild(_myPicture);
        _sides.AddChild(_myRoom);
        _sides.AddChild(_theirRoom);
        _sides.AddChild(_theirPicture);
        _showEmpty = new CheckBox { Text = "Show materials neither holds", FocusMode = FocusModeEnum.None };
        _showEmpty.Toggled += _ => Refresh();
        _grid = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("separation", 3);
        box.AddChild(_grid);
        box.AddChild(_showEmpty);
        // Crew: the counts on one line, the buttons under them (so the panel never grows past its frame).
        _crewRow = new VBoxContainer();
        box.AddChild(_crewRow);
        _crewRow.AddChild(UiTheme.Header("Crew"));
        _crewRow.AddChild(_crewBars = new InfoList());
        var crewButtons = new HBoxContainer();
        _crewRow.AddChild(crewButtons);
        _crewIn = MoveButton("+1 crew", "Move one crew member into the craft you fly", () => MoveCrew(true));
        _crewOut = MoveButton("−1 crew", "Move one crew member to the other craft", () => MoveCrew(false));
        crewButtons.AddChild(_crewIn);
        crewButtons.AddChild(_crewOut);
        _recruit = MoveButton("Recruit 1",
            $"Take on one crew member now, for {SectorState.RecruitFoodKg} kg food and {SectorState.RecruitWaterKg} kg water from that craft's stock",
            () =>
            {
                if (RecruitAt() is not { } at) return;
                _status.Text = _sector().TryRecruit(at.Id, 1).Message;
                Refresh();
            });
        crewButtons.AddChild(_recruit);
        box.AddChild(UiTheme.Header("Construction kits"));
        _kits = new VBoxContainer();
        box.AddChild(_kits);

        _requestsTitle = new Label { Modulate = Dim, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0), ThemeTypeVariation = "HudText" };
        box.AddChild(UiTheme.Header("Requests"));
        box.AddChild(_requestsTitle);
        box.AddChild(_requests = new InfoList());
        _requestRow = new HBoxContainer();
        box.AddChild(_requestRow);
        _requestMaterial = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var m in _materials.Materials) _requestMaterial.AddIconItem(UiIcons.Material(m.Id), m.Name);
        if (_materials.Materials.Count > 0) _requestMaterial.Select(0);
        _requestRow.AddChild(_requestMaterial);
        _requestTonnes = new SpinBox { MinValue = 0, MaxValue = 10_000, Step = 1, Value = 10, Suffix = "t" };
        _requestRow.AddChild(_requestTonnes);
        var set = new Button { Text = "Set", TooltipText = "Set the request (0 t clears it)", FocusMode = FocusModeEnum.None };
        set.Pressed += SetRequest;
        _requestRow.AddChild(set);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        box.AddChild(_status);
    }

    public void Open()
    {
        Visible = true;
        _layoutKey = "";
        _kitsKey = "";
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    /// <summary>The craft shown beside yours: the station you're docked at, else your target craft.</summary>
    private static CraftState? Other(SectorState sector, CraftState self)
    {
        if (self.IsDocked) return sector.GetCraft(self.DockedToCraftId);
        return !self.TargetCraftId.IsNone && self.TargetCraftId != self.Id && sector.Crafts.Any(c => c.Id == self.TargetCraftId)
            ? sector.GetCraft(self.TargetCraftId)
            : null;
    }

    private static bool DockedTogether(CraftState a, CraftState b) => a.DockedToCraftId == b.Id || b.DockedToCraftId == a.Id;

    private static string Holds(CraftState c) =>
        c.HasCargoLimit ? $"{c.CargoKg / 1000.0:0.0} / {c.CargoCapacityKg / 1000.0:0} t" : $"{c.CargoKg / 1000.0:0.0} t (unlimited)";

    private static double Fullness(CraftState c) => c.HasCargoLimit ? c.CargoKg / (double)Math.Max(1, c.CargoCapacityKg) : 0;

    /// <summary>A material's share of the room (of what's held, where there's no limit).</summary>
    private static double Share(CraftState c, string id) =>
        c.Inventory[id] / (double)Math.Max(1, c.HasCargoLimit ? c.CargoCapacityKg : c.Inventory.TotalKg);

    private string MaterialName(string id) => _materials.Contains(id) ? _materials.Get(id).Name : id;

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var self = _ship();
        if (self is null)
        {
            _crewRow.Visible = false;
            _hint.Text = "No craft.";
            _sides.Visible = false;
            Rebuild("", null, null, false, new List<string>());
            RebuildKits(null, null, false);
            ShowRequests(sector, null, null, false);
            return;
        }

        var other = Other(sector, self);
        var canMove = other is not null && DockedTogether(self, other);
        _hint.Text = canMove ? $"Left: {self.Name}. Right: {other!.Name}. Arrows move 1 t or all that fits; or drag a bar across (Ctrl: 1 t)."
            : other is not null ? $"Viewing only: dock at {other.Name} (K) to move cargo."
            : "Target a station or ship (Tab) to see its stock too; dock (K) to move cargo.";
        _sides.Visible = true;
        _myPicture.Design = self.Design;
        _myRoom.Set(Fullness(self), $"{self.Name}  {Holds(self)}");
        _theirPicture.Visible = _theirRoom.Visible = other is not null;
        if (other is not null)
        {
            _theirPicture.Design = other.Design;
            _theirRoom.Set(Fullness(other), $"{other.Name}  {Holds(other)}");
        }

        var ids = _materials.Materials.Select(m => m.Id).Concat(self.Inventory.Kg.Keys)
            .Concat(other?.Inventory.Kg.Keys ?? Enumerable.Empty<string>()).Distinct()
            .Where(id => _showEmpty.ButtonPressed || self.Inventory[id] > 0 || other?.Inventory[id] > 0).ToList();
        Rebuild($"{self.Id}|{other?.Id}|{canMove}|{string.Join(",", ids)}", self, other, canMove, ids);

        // Update the bars in place.
        foreach (var (id, mine, theirs) in _rows)
        {
            mine.Set(Share(self, id), $"{self.Inventory[id] / 1000.0:0.0##} t");
            if (other is not null) theirs?.Set(Share(other, id), $"{other.Inventory[id] / 1000.0:0.0##} t");
        }

        RebuildKits(self, other, canMove);
        ShowRequests(sector, self, other, canMove);

        var anyCrew = self.Stats.CrewCapacity > 0 || self.Crew > 0 || (other is not null && (other.Stats.CrewCapacity > 0 || other.Crew > 0));
        _crewRow.Visible = anyCrew;
        _crewBars.Set(new[] { self, other }.Where(c => c is not null && (c.Crew > 0 || c.Stats.CrewCapacity > 0)).Select(c =>
        {
            var hours = c!.Crew > 0 ? SectorState.SuppliesLastHours(c) : double.PositiveInfinity;
            return new HudRow("crew", $"{c.Name}: {c.Crew} / {c.Stats.CrewCapacity} crew" + (double.IsFinite(hours) ? $", food and water for {hours:0} h" : ""),
                c.Crew > 0 && hours < 24 ? Severity.Warning : Severity.Normal, c.Crew / (double)Math.Max(1, c.Stats.CrewCapacity));
        }).ToList());
        _crewIn.Visible = _crewOut.Visible = canMove;
        _recruit.Visible = RecruitAt() is { } at;
        if (RecruitAt() is { } where) _recruit.Text = $"Recruit 1 at {where.Name}";
    }

    /// <summary>Where "Recruit" hires (#12): the craft you fly if it has habitats, else the station beside you.</summary>
    private CraftState? RecruitAt()
    {
        if (_ship() is not { } self) return null;
        if (self.Stats.CrewCapacity > 0 && !self.LegacyNoCrew) return self;
        return Other(_sector(), self) is { Stats.CrewCapacity: > 0, LegacyNoCrew: false } other ? other : null;
    }

    private void MoveCrew(bool intoSelf)
    {
        var sector = _sector();
        if (_ship() is not { } self || Other(sector, self) is not { } other) return;
        _status.Text = (intoSelf ? sector.TryTransferCrew(other.Id, self.Id, 1) : sector.TryTransferCrew(self.Id, other.Id, 1)).Message;
        Refresh();
    }

    private void Rebuild(string key, CraftState? self, CraftState? other, bool canMove, List<string> ids)
    {
        if (key == _layoutKey) return;
        _layoutKey = key;
        foreach (var child in _grid.GetChildren()) { _grid.RemoveChild(child); child.QueueFree(); }
        _rows.Clear();
        if (self is null) return;
        if (ids.Count == 0) _grid.AddChild(new Label { Text = "  (nothing held)", Modulate = Dim });
        foreach (var id in ids)
        {
            var row = new HBoxContainer { TooltipText = MaterialName(id), MouseFilter = MouseFilterEnum.Pass };
            row.AddThemeConstantOverride("separation", 4);
            row.AddChild(UiTheme.Icon("mat_" + id));
            row.AddChild(new Label { Text = MaterialName(id), ThemeTypeVariation = "HudText", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, CustomMinimumSize = new Vector2(92, 0), MouseFilter = MouseFilterEnum.Ignore });
            var selfId = self.Id;
            var mine = canMove ? new CargoBar(selfId, id, Drop) : new StatBar();
            row.AddChild(mine);
            StatBar? theirs = null;
            if (other is not null)
            {
                var otherId = other.Id;
                if (canMove)
                {
                    row.AddChild(MoveButton("«", $"Load all that fits into {self.Name}", () => Move(otherId, selfId, id, null)));
                    row.AddChild(MoveButton("‹", $"Load 1 t into {self.Name}", () => Move(otherId, selfId, id, OneTonne)));
                    row.AddChild(MoveButton("›", $"Unload 1 t to {other.Name}", () => Move(selfId, otherId, id, OneTonne)));
                    row.AddChild(MoveButton("»", $"Unload all that fits to {other.Name}", () => Move(selfId, otherId, id, null)));
                }
                theirs = canMove ? new CargoBar(otherId, id, Drop) : new StatBar();
                row.AddChild(theirs);
            }
            _grid.AddChild(row);
            _rows.Add((id, mine, theirs));
        }
    }

    private void Drop(CraftId from, CraftId to, string material, bool oneTonne) => Move(from, to, material, oneTonne ? OneTonne : null);

    /// <summary>A material bar that can be dragged onto the other craft's side (#102): all that fits, or 1 t with Ctrl.</summary>
    private sealed partial class CargoBar : StatBar
    {
        private readonly CraftId _craft;
        private readonly string _material;
        private readonly Action<CraftId, CraftId, string, bool> _drop;

        public CargoBar(CraftId craft, string material, Action<CraftId, CraftId, string, bool> drop)
        {
            _craft = craft;
            _material = material;
            _drop = drop;
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = CursorShape.Drag;
        }

        public CargoBar() : this(CraftId.None, "", (_, _, _, _) => { }) { }

        public override Variant _GetDragData(Vector2 atPosition)
        {
            SetDragPreview(UiTheme.Icon("mat_" + _material, 32));
            return $"cargo|{_craft.Value}|{_material}";
        }

        private static (CraftId From, string Material)? Read(Variant data)
        {
            if (data.VariantType != Variant.Type.String || data.AsString().Split('|') is not ["cargo", var craft, var material] || !uint.TryParse(craft, out var id)) return null;
            return (new CraftId(id), material);
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data) => Read(data) is { } from && from.From != _craft;

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (Read(data) is { } from) _drop(from.From, _craft, from.Material, Input.IsKeyPressed(Key.Ctrl));
        }
    }

    private void RebuildKits(CraftState? self, CraftState? other, bool canMove)
    {
        var key = self is null ? ""
            : $"{self.Id}|{other?.Id}|{canMove}|{string.Join(",", self.Kits.Select(k => k.Id.Value))}|{string.Join(",", other?.Kits.Select(k => k.Id.Value) ?? Enumerable.Empty<long>())}";
        if (key == _kitsKey) return;
        _kitsKey = key;
        foreach (var child in _kits.GetChildren()) { _kits.RemoveChild(child); child.QueueFree(); }
        if (self is null) return;
        if (self.Kits.Count + (other?.Kits.Count ?? 0) == 0)
        {
            _kits.AddChild(new Label { Text = "  (none here; a shipyard builds station designs as kits)", Modulate = Dim });
            return;
        }
        var sides = new List<(CraftState Holder, CraftState? To, string Verb)> { (self, other, "Unload") };
        if (other is not null) sides.Insert(0, (other, self, "Load"));
        foreach (var (holder, to, verb) in sides)
            foreach (var kit in holder.Kits)
            {
                var row = new HBoxContainer();
                row.AddChild(new DesignPicture { Design = kit.Design, CustomMinimumSize = new Vector2(30, 30) });
                row.AddChild(new Label
                {
                    Text = $"{kit.Name}  {kit.MassKg / 1000.0:0.0} t  (in {holder.Name}){(kit.NeedsSurface ? "  surface" : "")}",
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                });
                if (canMove && to is not null)
                {
                    var (from, dest, id) = (holder.Id, to.Id, kit.Id);
                    row.AddChild(MoveButton(verb, $"{verb} the kit ({(verb == "Load" ? "into" : "to")} {to.Name})", () =>
                    {
                        _status.Text = _sector().TryTransferKit(from, dest, id).Message;
                        Refresh();
                    }));
                }
                _kits.AddChild(row);
            }
    }

    /// <summary>
    /// Requests of the station in view: the one you fly (if it has a docking port), else the one
    /// beside you. Editable when you fly it or are docked to it; read-only for a target further away.
    /// </summary>
    private void ShowRequests(SectorState sector, CraftState? self, CraftState? other, bool canMove)
    {
        CraftState? station = self is { Stats.DockingPorts.Count: > 0 } ? self
            : other is { Stats.DockingPorts.Count: > 0 } ? other : null;
        _requestStation = station?.Id ?? CraftId.None;
        var editable = station is not null && (ReferenceEquals(station, self) || canMove);
        _requestRow.Visible = editable;
        if (station is null)
        {
            _requestsTitle.Text = "Station requests: fly or target a station with a docking port to see them.";
            _requests.Set(Array.Empty<HudRow>());
            return;
        }
        _requestsTitle.Text = $"{station.Name} requests (serve-requests freighters keep at least this much here)"
                              + (editable ? ":" : " - dock there to change them:");
        // Its own requests and the automatic food and water for its crew (#14), whichever is more.
        var wanted = station.Requests.Keys
            .Concat(SectorState.AutoSupplyKg(station, SectorState.FoodMaterial) > 0 ? new[] { SectorState.FoodMaterial, SectorState.WaterMaterial } : Array.Empty<string>())
            .Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
        _requests.Set(wanted.Count == 0 ? new[] { new HudRow("idle", "none", Severity.Muted) }
            : wanted.Select(m =>
            {
                var want = SectorState.WantedKg(station, m);
                var has = station.Inventory[m];
                var coming = sector.IncomingKg(station.Id, m);
                var auto = SectorState.AutoSupplyKg(station, m) >= station.Requests.GetValueOrDefault(m);
                return new HudRow("mat_" + m, $"{MaterialName(m)}  {has / 1000.0:0.0} of {want / 1000.0:0.0##} t" + (coming > 0 ? $"  + {coming / 1000.0:0.0} t coming" : "") + (auto ? "  (auto)" : ""),
                    has >= want ? Severity.Good : has + coming >= want ? Severity.Normal : Severity.Warning, has / (double)Math.Max(1, want),
                    Tip: $"Keep {want / 1000.0:0.0##} t here" + (auto ? $" (automatic: {SectorState.AutoSupplyHours:0} h for the crew)" : "") + $". Has {has / 1000.0:0.0} t, on the way {coming / 1000.0:0.0} t.");
            }).ToArray());
    }

    private void SetRequest()
    {
        if (_requestStation.IsNone) return;
        var index = _requestMaterial.Selected;
        if (index < 0 || index >= _materials.Materials.Count) return;
        var result = _sector().SetRequest(_requestStation, _materials.Materials[index].Id, (long)Math.Round(_requestTonnes.Value * 1000));
        _status.Text = result.Message;
        Refresh();
    }

    private static Button MoveButton(string text, string tooltip, Action onPressed)
    {
        var button = new Button { Text = text, TooltipText = tooltip, FocusMode = FocusModeEnum.None };
        button.Pressed += onPressed;
        return button;
    }

    /// <summary>Moves a fixed amount, or (null) as much as fits.</summary>
    private void Move(CraftId from, CraftId to, string material, long? kg)
    {
        var sector = _sector();
        var amount = kg ?? sector.MaxTransferKg(from, to, material);
        if (kg is { } wanted) amount = Math.Min(wanted, sector.MaxTransferKg(from, to, material));
        var result = sector.TryTransfer(from, to, material, amount);
        _status.Text = result.Message;
        Refresh();
    }
}
