using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
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
    private Label _header = null!;
    private GridContainer _grid = null!;
    private Label _status = null!;
    private VBoxContainer _crewRow = null!;
    private Label _crewLabel = null!;
    private Button _crewIn = null!, _crewOut = null!, _recruit = null!;
    private VBoxContainer _kits = null!;
    private string _kitsKey = "";
    private Label _requestsTitle = null!;
    private Label _requests = null!;
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
        OffsetLeft = -450;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(box);
        var title = new Label { Text = "CARGO  (C to close)" };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        _hint = new Label { Modulate = Dim, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        box.AddChild(_hint);
        _header = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        box.AddChild(_header);
        _grid = new GridContainer { Columns = 3 };
        box.AddChild(_grid);
        // Crew: the counts on one line, the buttons under them (so the panel never grows past its frame).
        _crewRow = new VBoxContainer();
        box.AddChild(_crewRow);
        _crewLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        _crewRow.AddChild(_crewLabel);
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
        box.AddChild(new Label { Text = "Construction kits:", Modulate = Dim });
        _kits = new VBoxContainer();
        box.AddChild(_kits);

        _requestsTitle = new Label { Modulate = Dim, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        box.AddChild(_requestsTitle);
        _requests = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        box.AddChild(_requests);
        _requestRow = new HBoxContainer();
        box.AddChild(_requestRow);
        _requestMaterial = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var m in _materials.Materials) _requestMaterial.AddItem(m.Name);
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

    private string MaterialName(string id) => _materials.Contains(id) ? _materials.Get(id).Name : id;

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var self = _ship();
        if (self is null)
        {
            _crewRow.Visible = false;
            _hint.Text = "";
            _header.Text = "No craft.";
            Rebuild("", null, null, false, new List<string>());
            RebuildKits(null, null, false);
            ShowRequests(sector, null, null, false);
            return;
        }

        var other = Other(sector, self);
        var canMove = other is not null && DockedTogether(self, other);
        _hint.Text = canMove ? $"+ loads into {self.Name}, − unloads to {other!.Name}"
            : other is not null ? $"Viewing only: dock at {other.Name} (K) to move cargo."
            : "Target a station or ship (Tab) to see its stock too; dock (K) to move cargo.";
        _header.Text = $"{self.Name}: {Holds(self)}" + (other is null ? "" : $"      {other.Name}: {Holds(other)}");

        var ids = _materials.Materials.Select(m => m.Id).Concat(self.Inventory.Kg.Keys)
            .Concat(other?.Inventory.Kg.Keys ?? Enumerable.Empty<string>()).Distinct().ToList();
        Rebuild($"{self.Id}|{other?.Id}|{canMove}|{string.Join(",", ids)}", self, other, canMove, ids);

        // Update the amount labels in place (columns 1 and 2 of each row).
        var columns = _grid.Columns;
        for (var row = 0; row < ids.Count; row++)
        {
            ((Label)_grid.GetChild(1 + (row + 1) * columns)).Text = $"{self.Inventory[ids[row]] / 1000.0:0.0##} t";
            if (other is not null) ((Label)_grid.GetChild(2 + (row + 1) * columns)).Text = $"{other.Inventory[ids[row]] / 1000.0:0.0##} t";
        }

        RebuildKits(self, other, canMove);
        ShowRequests(sector, self, other, canMove);

        var anyCrew = self.Stats.CrewCapacity > 0 || self.Crew > 0 || (other is not null && (other.Stats.CrewCapacity > 0 || other.Crew > 0));
        _crewRow.Visible = anyCrew;
        _crewLabel.Text = $"Crew: {self.Name} {self.Crew}/{self.Stats.CrewCapacity}"
                          + (other is null ? "" : $"   {other.Name} {other.Crew}/{other.Stats.CrewCapacity}");
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
        if (self is null) return;

        _grid.Columns = 2 + (other is null ? 0 : 1) + (canMove ? 4 : 0);
        _grid.AddChild(new Label { Text = "Material", Modulate = Dim });
        _grid.AddChild(new Label { Text = self.Name, Modulate = Dim, ClipText = true, CustomMinimumSize = new Vector2(80, 0) });
        if (other is not null) _grid.AddChild(new Label { Text = other.Name, Modulate = Dim, ClipText = true, CustomMinimumSize = new Vector2(80, 0) });
        if (canMove) for (var i = 0; i < 4; i++) _grid.AddChild(new Label());

        foreach (var id in ids)
        {
            _grid.AddChild(new Label { Text = MaterialName(id), CustomMinimumSize = new Vector2(96, 0) });
            _grid.AddChild(new Label { CustomMinimumSize = new Vector2(62, 0) });
            if (other is not null) _grid.AddChild(new Label { CustomMinimumSize = new Vector2(62, 0) });
            if (!canMove) continue;
            var (selfId, otherId) = (self.Id, other!.Id);
            _grid.AddChild(MoveButton("+1 t", $"Load 1 t into {self.Name}", () => Move(otherId, selfId, id, OneTonne)));
            _grid.AddChild(MoveButton("+all", $"Load as much as fits into {self.Name}", () => Move(otherId, selfId, id, null)));
            _grid.AddChild(MoveButton("−1 t", $"Unload 1 t to {other.Name}", () => Move(selfId, otherId, id, OneTonne)));
            _grid.AddChild(MoveButton("−all", $"Unload everything to {other.Name}", () => Move(selfId, otherId, id, null)));
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
            _requests.Text = "";
            return;
        }
        _requestsTitle.Text = $"{station.Name} requests (serve-requests freighters keep at least this much here)"
                              + (editable ? ":" : " - dock there to change them:");
        // Its own requests and the automatic food and water for its crew (#14), whichever is more.
        var wanted = station.Requests.Keys
            .Concat(SectorState.AutoSupplyKg(station, SectorState.FoodMaterial) > 0 ? new[] { SectorState.FoodMaterial, SectorState.WaterMaterial } : Array.Empty<string>())
            .Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
        _requests.Text = wanted.Count == 0 ? "  (none)"
            : string.Join("\n", wanted.Select(m =>
                $"  {MaterialName(m)}: keep {SectorState.WantedKg(station, m) / 1000.0:0.0##} t"
                + (SectorState.AutoSupplyKg(station, m) >= station.Requests.GetValueOrDefault(m) ? $" (auto: {SectorState.AutoSupplyHours:0} h for the crew)" : "")
                + $", has {station.Inventory[m] / 1000.0:0.0} t, on the way {sector.IncomingKg(station.Id, m) / 1000.0:0.0} t"));
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
