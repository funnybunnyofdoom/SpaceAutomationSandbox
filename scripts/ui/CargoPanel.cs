using System;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Moves materials between the piloted ship and the station it's docked at (key C).
/// One row per material: amounts on both sides and buttons to load or unload 1 t or everything
/// that fits. All moves go through <see cref="SectorState.TryTransfer"/> (whole kg, all-or-nothing).
/// </summary>
public partial class CargoPanel : PanelContainer
{
    private const long OneTonne = 1_000;
    private const double RefreshSeconds = 0.25;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftState?> _ship;
    private readonly MaterialCatalog _materials;
    private Label _header = null!;
    private GridContainer _grid = null!;
    private Label _status = null!;
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
        SetAnchorsPreset(LayoutPreset.CenterBottom);
        OffsetLeft = -215;
        OffsetRight = 215;
        OffsetTop = -250;
        OffsetBottom = -12;

        var box = new VBoxContainer();
        AddChild(box);
        var title = new Label { Text = "CARGO  (C to close)" };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(new Label { Text = "+ loads into the ship, − unloads to the station", Modulate = new Color(1, 1, 1, 0.6f) });
        _header = new Label();
        box.AddChild(_header);
        _grid = new GridContainer { Columns = 7 };
        box.AddChild(_grid);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) };
        box.AddChild(_status);
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

    private void Refresh()
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var ship = _ship();
        if (ship is not { IsDocked: true })
        {
            _header.Text = ship is null ? "No ship." : $"{ship.Name} isn't docked. Dock at a station (K) to move cargo.";
            Rebuild("");
            return;
        }

        var station = sector.GetCraft(ship.DockedToCraftId);
        _header.Text = $"{ship.Name}: {ship.Inventory.TotalKg / 1000.0:0.0} / {ship.CargoCapacityKg / 1000.0:0} t      at {station.Name}";

        var ids = _materials.Materials.Select(m => m.Id)
            .Concat(ship.Inventory.Kg.Keys).Concat(station.Inventory.Kg.Keys).Distinct().ToList();
        Rebuild($"{ship.Id}|{station.Id}|{string.Join(",", ids)}", ship, station, ids);

        // Update the amount labels in place (columns 1 and 2 of each row).
        for (var row = 0; row < ids.Count; row++)
        {
            ((Label)_grid.GetChild(1 + (row + 1) * 7)).Text = $"{ship.Inventory[ids[row]] / 1000.0:0.0##} t";
            ((Label)_grid.GetChild(2 + (row + 1) * 7)).Text = $"{station.Inventory[ids[row]] / 1000.0:0.0##} t";
        }
    }

    private void Rebuild(string key, CraftState? ship = null, CraftState? station = null, System.Collections.Generic.List<string>? ids = null)
    {
        if (key == _layoutKey) return;
        _layoutKey = key;
        foreach (var child in _grid.GetChildren()) { _grid.RemoveChild(child); child.QueueFree(); }
        if (ship is null || station is null || ids is null) return;

        foreach (var heading in new[] { "Material", "Ship", "Station", "", "", "", "" })
            _grid.AddChild(new Label { Text = heading, Modulate = new Color(1, 1, 1, 0.6f) });

        foreach (var id in ids)
        {
            var name = _materials.Contains(id) ? _materials.Get(id).Name : id;
            _grid.AddChild(new Label { Text = name, CustomMinimumSize = new Vector2(96, 0) });
            _grid.AddChild(new Label { CustomMinimumSize = new Vector2(62, 0) });
            _grid.AddChild(new Label { CustomMinimumSize = new Vector2(62, 0) });
            var shipId = ship.Id;
            var stationId = station.Id;
            _grid.AddChild(MoveButton("+1 t", "Load 1 t into the ship", () => Move(stationId, shipId, id, OneTonne)));
            _grid.AddChild(MoveButton("+all", "Load as much as fits into the ship", () => Move(stationId, shipId, id, null)));
            _grid.AddChild(MoveButton("−1 t", "Unload 1 t to the station", () => Move(shipId, stationId, id, OneTonne)));
            _grid.AddChild(MoveButton("−all", "Unload everything to the station", () => Move(shipId, stationId, id, null)));
        }
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
