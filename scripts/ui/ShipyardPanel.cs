using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.UI;

/// <summary>
/// Side panel for ordering ships and stations at a shipyard (key Y): stock, a design picker with
/// bill vs stock, Order, the queue with progress and Cancel, and the construction kits in storage
/// (stations are built as kits, #8: load one onto a ship in the cargo panel, or deploy an orbital
/// one beside the yard). Orders go through <see cref="SectorState.OrderBuild"/>, which takes the
/// materials or refuses with the exact shortfall. Ordering works from anywhere (docs/06).
/// "Work on craft" lists what the yard can reach (itself, docked ships, stations nearby) with Refit
/// (to the selected design, #56), Dismantle (with a preview and a confirming second click) and Pack
/// as kit (#57).
/// </summary>
public partial class ShipyardPanel : PanelContainer
{
    private static readonly Color ShortColor = new(1f, 0.45f, 0.4f);
    private static readonly Color OkColor = new(0.5f, 1f, 0.6f);
    private const double RefreshSeconds = 0.25;

    private readonly Func<SectorState> _sector;
    private readonly PartCatalog _catalog;
    private readonly Func<ShipDesign?> _currentShipDesign;

    private OptionButton _yardPicker = null!;
    private Label _stock = null!;
    private OptionButton _designPicker = null!;
    private Label _bill = null!;
    private VBoxContainer _kits = null!;
    private string _kitsSignature = "";
    private Label _status = null!;
    private VBoxContainer _queue = null!;
    private List<CraftId> _yards = new();
    private List<(string Label, Func<ShipDesign> Load)> _designs = new();
    private double _sinceRefresh;
    private string _queueSignature = "";
    private VBoxContainer _work = null!;
    private string _workSignature = "";
    private CraftId _confirmDismantle = CraftId.None;

    public ShipyardPanel(Func<SectorState> sector, PartCatalog catalog, Func<ShipDesign?> currentShipDesign)
    {
        _sector = sector;
        _catalog = catalog;
        _currentShipDesign = currentShipDesign;
        Build();
    }

    public ShipyardPanel() : this(() => new SectorState(0),
        new PartCatalog(Array.Empty<PartDefinition>(), new MaterialCatalog(Array.Empty<MaterialDefinition>())), () => null) { }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -380;
        OffsetRight = -12;
        OffsetTop = 12;
        OffsetBottom = -12;

        // Everything scrolls: the panel holds more than fits at 720p. Nothing may be wider than the panel
        // (#69: an unwrapped line widened it, pushing the Order button's centred label out of view), so
        // long text wraps and pickers clip long names.
        var outer = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        AddChild(outer);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        outer.AddChild(box);

        var title = new Label();
        InputActions.TitleWithKey(title, "SHIPYARD", InputActions.Shipyard);
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);

        _yardPicker = new OptionButton { FocusMode = FocusModeEnum.None, ClipText = true, CustomMinimumSize = new Vector2(340, 0) };
        _yardPicker.ItemSelected += _ => Refresh(force: true);
        box.AddChild(_yardPicker);

        _stock = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        box.AddChild(_stock);

        box.AddChild(new Label { Text = "Design (to build, or to refit a craft to):" });
        _designPicker = new OptionButton { FocusMode = FocusModeEnum.None, ClipText = true, CustomMinimumSize = new Vector2(340, 0) };
        _designPicker.ItemSelected += _ => Refresh(force: true);
        box.AddChild(_designPicker);
        _bill = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        box.AddChild(_bill);

        var order = new Button { Text = "Order", FocusMode = FocusModeEnum.None, TooltipText = "Build this design at the yard: ships launch when done, station designs become kits to carry and deploy" };
        order.Pressed += Order;
        box.AddChild(order);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        box.AddChild(_status);

        box.AddChild(new Label { Text = "Queue (first builds first):" });
        _queue = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(_queue);

        box.AddChild(new Label { Text = "Construction kits in storage:" });
        _kits = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(_kits);

        box.AddChild(new Label
        {
            Text = $"Work on craft (docked ships, stations within {SectorState.YardReachM:0} m, the yard itself). Removed parts return {SectorState.RecoveryFraction * 100:0}% of their materials (less if damaged); the rest is slag.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0),
        });
        _work = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(_work);
    }

    /// <summary>Shows the panel, refreshing the yard and design lists (designs may have been saved in the editor).</summary>
    public void Open()
    {
        RefreshDesignList();
        Visible = true;
        Refresh(force: true);
    }

    /// <summary>Dev/test: picks the design at this index in the list (if there is one).</summary>
    public void SelectDesign(int index)
    {
        if (index < 0 || index >= _designPicker.ItemCount) return;
        _designPicker.Select(index);
        Refresh(force: true);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= RefreshSeconds) Refresh();
    }

    private CraftState? SelectedYard()
    {
        var sector = _sector();
        var index = _yardPicker.Selected;
        return index >= 0 && index < _yards.Count && sector.Crafts.Any(c => c.Id == _yards[index]) ? sector.GetCraft(_yards[index]) : null;
    }

    private void RefreshYardList()
    {
        var yards = _sector().Crafts.Where(c => c.IsShipyard).Select(c => c.Id).ToList();
        if (yards.SequenceEqual(_yards)) return;
        var previous = SelectedYard()?.Id;
        _yards = yards;
        _yardPicker.Clear();
        foreach (var id in _yards) _yardPicker.AddItem($"{_sector().GetCraft(id).Name} ({id})");
        var keep = previous is { } p ? _yards.IndexOf(p) : -1;
        if (_yards.Count > 0) _yardPicker.Select(keep >= 0 ? keep : 0);
    }

    private void RefreshDesignList()
    {
        _designs = new List<(string, Func<ShipDesign>)>();
        if (_currentShipDesign() is { } current) _designs.Add(($"My current ship ({current.Name})", () => current));
        foreach (var entry in BlueprintLibrary.List())
        {
            try
            {
                var design = BlueprintLibrary.Load(entry);
                var role = design.SuggestedRole(_catalog);
                if (design.Validate(_catalog, role).Count > 0) continue;
                var kind = role == DesignRole.Ship ? "" : design.ComputeStats(_catalog).IsFixedStructure ? "  [kit: surface]" : "  [kit: station]";
                _designs.Add((entry.Label + kind, () => design));
            }
            catch (Exception) { /* unreadable files are listed as such in the editor; skip them here */ }
        }
        _designPicker.Clear();
        foreach (var (label, _) in _designs) _designPicker.AddItem(label);
        if (_designs.Count > 0) _designPicker.Select(0);
    }

    private ShipDesign? SelectedDesign()
    {
        var index = _designPicker.Selected;
        return index >= 0 && index < _designs.Count ? _designs[index].Load() : null;
    }

    private void Refresh(bool force = false)
    {
        _sinceRefresh = 0;
        RefreshYardList();
        var yard = SelectedYard();
        if (yard is null)
        {
            _stock.Text = "No shipyard in this game. (Saves from before shipyards existed have none: F7 starts a new game with one.)";
            _bill.Text = "";
            ClearQueue();
            return;
        }

        var materials = _catalog.Materials;
        string Name(string id) => materials.Contains(id) ? materials.Get(id).Name : id;
        var ids = materials.Materials.Select(m => m.Id).Concat(yard.Inventory.Kg.Keys).Distinct().ToList();
        _stock.Text = (yard.HasCargoLimit
                          ? $"Stock ({yard.CargoKg / 1000.0:0.0} / {yard.CargoCapacityKg / 1000.0:0} t storage, kits included):\n"
                          : $"Stock ({yard.Inventory.TotalKg / 1000.0:0.0} t, unlimited: old save):\n")
                      + string.Join("\n", ids.Select(id => $"  {Name(id)}  {yard.Inventory[id] / 1000.0:0.0##} t"));

        var selected = SelectedDesign();

        if (selected is { } design)
        {
            var bill = design.ComputeBillOfMaterials(_catalog).ToWholeKg();
            var short_ = yard.Inventory.Shortfall(bill);
            var stats = design.ComputeStats(_catalog);
            var kit = design.SuggestedRole(_catalog) == DesignRole.Station
                ? stats.IsFixedStructure ? "\nBuilt as a construction kit: carry it on a ship, land, and deploy (U)."
                  : "\nBuilt as a construction kit: deploy it beside the yard below, or carry it and deploy (U) in orbit or on a surface."
                : "";
            _bill.Text = $"{stats.TotalMassKg / 1000:0.0} t, about {stats.TotalMassKg / yard.Stats.FabricationRateKgPerS:0} s to build{kit}\n"
                         + string.Join("\n", bill.Select(kv => $"  {Name(kv.Key)}  {kv.Value / 1000.0:0.0##} / {yard.Inventory[kv.Key] / 1000.0:0.0##} t"
                                                               + (short_.ContainsKey(kv.Key) ? "  SHORT" : "")));
            _bill.AddThemeColorOverride("font_color", short_.Count > 0 ? ShortColor : OkColor);
        }
        else
        {
            _bill.Text = "No buildable designs. Make one in the blueprint editor (B)."; 
            _bill.AddThemeColorOverride("font_color", ShortColor);
        }

        RefreshQueue(yard, force);
        RefreshKits(yard, force);
        RefreshWork(yard, force);
    }

    private void RefreshWork(CraftState yard, bool force)
    {
        var sector = _sector();
        var craft = sector.CraftInYardReach(yard.Id);
        var signature = yard.Id + ":" + string.Join(",", craft.Select(c => $"{c.Id.Value}/{c.YardOrder?.Kind}"));
        if (force || signature != _workSignature)
        {
            _workSignature = signature;
            foreach (var child in _work.GetChildren()) { _work.RemoveChild(child); child.QueueFree(); }
            foreach (var c in craft)
            {
                var row = new VBoxContainer();
                row.AddChild(new Label { Name = "Text", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) });
                var buttons = new HBoxContainer();
                var (yardId, craftId) = (yard.Id, c.Id);
                if (c.YardOrder is not null)
                {
                    AddButton(buttons, "Cancel", () => sector.CancelYardWork(craftId));
                }
                else
                {
                    AddButton(buttons, "Refit to design", () =>
                    {
                        if (SelectedDesign() is not { } design) return new ActionResult(false, "No design selected.");
                        return sector.OrderRefit(yardId, craftId, design);
                    });
                    if (c.Id != yard.Id)
                    {
                        AddButton(buttons, "Dismantle", () =>
                        {
                            if (_confirmDismantle != craftId)
                            {
                                _confirmDismantle = craftId;
                                var preview = sector.PreviewDismantle(craftId);
                                return new ActionResult(false, $"Dismantle {sector.GetCraft(craftId).Name}? Returns "
                                    + string.Join(", ", preview.ReturnedKg.Select(kv => $"{kv.Value / 1000.0:0.0##} t {kv.Key}"))
                                    + $"; {preview.ScrapKg / 1000.0:0.0##} t becomes slag. Press Dismantle again to confirm.");
                            }
                            _confirmDismantle = CraftId.None;
                            return sector.OrderDismantle(yardId, craftId);
                        });
                        if (!c.IsShipyard && c.Design.SuggestedRole(_catalog) == DesignRole.Station)
                            AddButton(buttons, "Pack as kit", () => sector.OrderPackKit(yardId, craftId));
                    }
                }
                row.AddChild(buttons);
                _work.AddChild(row);
            }
            if (craft.Count == 0) _work.AddChild(new Label { Text = "  (nothing in reach)", Modulate = new Color(1, 1, 1, 0.6f) });
        }

        // Status text every refresh.
        for (var i = 0; i < craft.Count && i < _work.GetChildCount(); i++)
        {
            var c = craft[i];
            if (_work.GetChild(i).GetNodeOrNull<Label>("Text") is not { } label) continue;
            var doing = c.YardStatus ?? c.RepairStatus ?? (c.YardOrder is { } o ? $"{o.Kind.ToString().ToLowerInvariant()} queued" : "");
            var preview = c.YardOrder is null && SelectedDesign() is { } design && design.SuggestedRole(_catalog) == (c.IntactDesign ?? c.Design).SuggestedRole(_catalog)
                ? RefitSummary(sector, c, design) : "";
            label.Text = $"{c.Name} ({c.Id}) - {c.Design.Name}, {c.Design.Cells.Count} parts"
                         + (c.Id == yard.Id ? " (this yard)" : c.IsDocked ? " (docked)" : " (nearby)")
                         + (doing.Length > 0 ? "\n  " + doing : "")
                         + (preview.Length > 0 ? "\n  " + preview : "");
        }
    }

    private static string RefitSummary(SectorState sector, CraftState craft, ShipDesign design)
    {
        var p = sector.PreviewRefit(craft.Id, design);
        if (p.PartsAdded == 0 && p.PartsRemoved == 0) return $"matches {design.Name}";
        return $"refit to {design.Name}: {p.PartsRemoved} off, {p.PartsAdded} on; needs "
               + (p.CostKg.Count == 0 ? "nothing" : string.Join(", ", p.CostKg.Select(kv => $"{kv.Value / 1000.0:0.0##} t {kv.Key}")))
               + $", returns {p.ReturnedKg.Values.Sum() / 1000.0:0.0##} t (+{p.ScrapKg / 1000.0:0.0##} t slag)";
    }

    private void AddButton(HBoxContainer row, string text, Func<ActionResult> action)
    {
        var button = new Button { Text = text, FocusMode = FocusModeEnum.None };
        button.Pressed += () =>
        {
            var result = action();
            _status.Text = result.Message;
            _status.AddThemeColorOverride("font_color", result.Done ? OkColor : ShortColor);
            Refresh(force: true);
        };
        row.AddChild(button);
    }

    private void RefreshKits(CraftState yard, bool force)
    {
        var signature = yard.Id + ":" + string.Join(",", yard.Kits.Select(k => k.Id.Value));
        if (!force && signature == _kitsSignature) return;
        _kitsSignature = signature;
        foreach (var child in _kits.GetChildren()) { _kits.RemoveChild(child); child.QueueFree(); }
        foreach (var kit in yard.Kits)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label
            {
                Text = $"{kit.Name}  {kit.MassKg / 1000.0:0.0} t{(kit.NeedsSurface ? "  (surface)" : "")}", SizeFlagsHorizontal = SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(200, 0),
            });
            if (!kit.NeedsSurface)
            {
                var deploy = new Button { Text = "Deploy here", FocusMode = FocusModeEnum.None, TooltipText = "Unpack the kit at the yard's position" };
                var (yardId, kitId) = (yard.Id, kit.Id);
                deploy.Pressed += () =>
                {
                    var result = _sector().TryDeployKit(yardId, kitId);
                    _status.Text = result.Message;
                    _status.AddThemeColorOverride("font_color", result.Done ? OkColor : ShortColor);
                    Refresh(force: true);
                };
                row.AddChild(deploy);
            }
            _kits.AddChild(row);
        }
        if (yard.Kits.Count == 0)
            _kits.AddChild(new Label { Text = "  (none)", Modulate = new Color(1, 1, 1, 0.6f) });
    }

    private void RefreshQueue(CraftState yard, bool force)
    {
        // Rebuild rows only when the set of orders changes; otherwise just update progress text.
        var signature = string.Join(",", yard.BuildQueue.Select(o => o.Id.Value));
        if (force || signature != _queueSignature)
        {
            ClearQueue();
            _queueSignature = signature;
            foreach (var order in yard.BuildQueue)
            {
                var row = new HBoxContainer();
                row.AddChild(new Label
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill, Name = "Text",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(240, 0),
                });
                var cancel = new Button { Text = "Cancel", FocusMode = FocusModeEnum.None, TooltipText = "Cancel the order; materials already used come back to the yard" };
                var orderId = order.Id;
                var yardId = yard.Id;
                cancel.Pressed += () =>
                {
                    _status.Text = _sector().CancelBuild(yardId, orderId) ? $"Cancelled {orderId}; all materials returned." : "That order is already gone.";
                    Refresh(force: true);
                };
                row.AddChild(cancel);
                _queue.AddChild(row);
            }
        }

        for (var i = 0; i < yard.BuildQueue.Count && i < _queue.GetChildCount(); i++)
        {
            var order = yard.BuildQueue[i];
            var label = _queue.GetChild(i).GetNode<Label>("Text");
            var what = order.IsKit ? " (kit)" : "";
            label.Text = i > 0 ? $"{order.ShipName}{what}  waiting"
                : order.WaitingForRoom ? $"{order.ShipName}{what}  done, needs {order.TotalKg / 1000:0.0} t of storage room"
                : $"{order.ShipName}{what}  {order.Fraction * 100:0}%";
        }
        if (yard.BuildQueue.Count == 0 && _queue.GetChildCount() == 0)
            _queue.AddChild(new Label { Text = "  (empty)", Modulate = new Color(1, 1, 1, 0.6f) });
    }

    private void ClearQueue()
    {
        foreach (var child in _queue.GetChildren()) child.QueueFree();
        // QueueFree is deferred: detach now so child counts are right this frame.
        foreach (var child in _queue.GetChildren()) _queue.RemoveChild(child);
        _queueSignature = "";
    }

    private void Order()
    {
        if (SelectedYard() is not { } yard) { _status.Text = "No shipyard selected."; return; }
        if (SelectedDesign() is not { } design) { _status.Text = "No design selected."; return; }

        var result = _sector().OrderBuild(yard.Id, design, _catalog);
        _status.Text = result.Accepted ? $"Ordered {design.Name} ({result.OrderId})." : string.Join("\n", result.Errors);
        _status.AddThemeColorOverride("font_color", result.Accepted ? OkColor : ShortColor);
        Refresh(force: true);
    }
}
