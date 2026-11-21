using System;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Scrolling list of every craft in the sector (key F): name, id, where it is, speed.
/// Clicking a row hands piloting to that craft. A first, simple fleet view; filtering, grouping
/// and search come later, when there are many ships (docs/06).
/// </summary>
public partial class FleetPanel : PanelContainer
{
    private const double RefreshSeconds = 0.5;

    private readonly Func<SectorState> _sector;
    private readonly Func<CraftId?> _piloted;
    private VBoxContainer _rows = null!;
    private Label _title = null!;
    private double _sinceRefresh;
    private string _signature = "";

    /// <summary>Raised when the player picks a craft to fly.</summary>
    public event Action<CraftId>? CraftChosen;

    public FleetPanel(Func<SectorState> sector, Func<CraftId?> piloted)
    {
        _sector = sector;
        _piloted = piloted;
        Build();
    }

    public FleetPanel() : this(() => new SectorState(0), () => null) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.BottomLeft);
        OffsetLeft = 12;
        OffsetRight = 452;
        OffsetTop = -330;
        OffsetBottom = -12;

        var box = new VBoxContainer();
        AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(_title);
        box.AddChild(new Label { Text = "Click a craft to fly it.", Modulate = new Color(1, 1, 1, 0.6f) });

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_rows);
    }

    public void Open()
    {
        Visible = true;
        Refresh(force: true);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private void Refresh(bool force = false)
    {
        _sinceRefresh = 0;
        var sector = _sector();
        var crafts = sector.Crafts;
        _title.Text = $"FLEET ({crafts.Count})  (F to close)";

        var signature = string.Join(",", crafts.Select(c => c.Id.Value)) + "|" + _piloted();
        if (force || signature != _signature)
        {
            _signature = signature;
            foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
            foreach (var craft in crafts)
            {
                var id = craft.Id;
                var button = new Button { Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, ToggleMode = true };
                button.ButtonPressed = _piloted() == id;
                button.Pressed += () => { CraftChosen?.Invoke(id); Refresh(force: true); };
                _rows.AddChild(button);
            }
        }

        // Update texts in place (positions and speeds change every frame).
        for (var i = 0; i < crafts.Count && i < _rows.GetChildCount(); i++)
            ((Button)_rows.GetChild(i)).Text = Describe(sector, crafts[i]);
    }

    public static string Where(SectorState sector, CraftState craft) =>
        craft.IsLanded ? $"{(craft.IsAnchored ? "anchored to" : "landed on")} {sector.GetBody(craft.LandedOnBodyId).Name}"
        : craft.RailOrbit is { } rail ? $"{(craft.IsShipyard ? "orbiting" : "parked in orbit around")} {sector.GetBody(rail.Parent).Name}"
        : $"flying, {craft.SpeedMps:0} m/s";

    private string Describe(SectorState sector, CraftState craft)
    {
        var role = craft.IsShipyard ? "shipyard" : craft.Stats.TotalThrustN > 0 ? "ship" : "craft";
        var you = _piloted() == craft.Id ? "  [piloting]" : "";
        var building = craft.BuildQueue.Count > 0 ? $"  building {craft.BuildQueue[0].Fraction * 100:0}%" : "";
        return $"{craft.Name}  ({craft.Id}, {role})\n   {Where(sector, craft)}{building}{you}";
    }
}
