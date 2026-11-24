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
    private HFlowContainer _stock = null!;
    private Label _note = null!;
    private InfoList _room = null!, _bill = null!;
    private GridContainer _tiles = null!;
    private readonly List<Button> _tileButtons = new();
    private int _design = -1;
    private string _stockSignature = "";
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
        OffsetLeft = -412;
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

        // What it holds: room as a bar, then an icon and amount per material in stock (#102).
        box.AddChild(_room = new InfoList());
        _stock = new HFlowContainer { CustomMinimumSize = new Vector2(340, 0) };
        _stock.AddThemeConstantOverride("h_separation", 10);
        box.AddChild(_stock);

        box.AddChild(UiTheme.Header("Design to build, or refit a craft to"));
        var tileScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(340, 216) };
        box.AddChild(tileScroll);
        _tiles = new GridContainer { Columns = 4 };
        _tiles.AddThemeConstantOverride("h_separation", 4);
        _tiles.AddThemeConstantOverride("v_separation", 4);
        tileScroll.AddChild(_tiles);
        _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0), ThemeTypeVariation = "HudText" };
        box.AddChild(_note);
        box.AddChild(_bill = new InfoList());

        var order = new Button { Text = "Order", Icon = UiIcons.Get("shipyard"), FocusMode = FocusModeEnum.None, TooltipText = "Build this design at the yard: ships launch when done, station designs become kits to carry and deploy" };
        order.Pressed += Order;
        box.AddChild(order);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(340, 0) };
        box.AddChild(_status);

        box.AddChild(UiTheme.Header("Queue (first builds first)"));
        _queue = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(_queue);

        box.AddChild(UiTheme.Header("Construction kits in storage"));
        _kits = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(_kits);

        box.AddChild(UiTheme.Header("Work on craft"));
        box.AddChild(new Label
        {
            ThemeTypeVariation = "HudText", Modulate = new Color(1, 1, 1, 0.65f),
            Text = $"Docked ships, stations within {SectorState.YardReachM:0} m, the yard itself. Removed parts return {SectorState.RecoveryFraction * 100:0}% of their materials (less if damaged); the rest is slag.",
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

    /// <summary>Opens on a shipyard (#100).</summary>
    public void Open(CraftId yard)
    {
        Open();
        if (_yards.IndexOf(yard) is var at and >= 0) { _yardPicker.Select(at); Refresh(force: true); }
    }

    /// <summary>Dev/test: picks the design at this index in the list (if there is one).</summary>
    public void SelectDesign(int index)
    {
        if (index < 0 || index >= _designs.Count) return;
        _design = index;
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
        foreach (var child in _tiles.GetChildren()) { _tiles.RemoveChild(child); child.QueueFree(); }
        _tileButtons.Clear();
        var group = new ButtonGroup();
        for (var i = 0; i < _designs.Count; i++)
        {
            var index = i;
            var (label, load) = _designs[i];
            var tile = new Button { ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(88, 102), TooltipText = label };
            var picture = new DesignPicture { Design = load() };
            picture.SetAnchorsPreset(LayoutPreset.TopWide);
            picture.OffsetLeft = 12;
            picture.OffsetRight = -12;
            picture.OffsetTop = 6;
            picture.OffsetBottom = 58;
            tile.AddChild(picture);
            var name = new Label
            {
                Text = label, ThemeTypeVariation = "HudText", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MaxLinesVisible = 2, ClipText = true, MouseFilter = MouseFilterEnum.Ignore,
            };
            name.AddThemeFontSizeOverride("font_size", 12);
            name.SetAnchorsPreset(LayoutPreset.BottomWide);
            name.OffsetTop = -41;
            name.AddThemeConstantOverride("line_spacing", 0);
            name.OffsetLeft = 3;
            name.OffsetRight = -3;
            tile.AddChild(name);
            tile.Pressed += () => { _design = index; Refresh(force: true); };
            _tiles.AddChild(tile);
            _tileButtons.Add(tile);
        }
        _design = _designs.Count > 0 ? Mathf.Clamp(_design, 0, _designs.Count - 1) : -1;
    }

    private ShipDesign? SelectedDesign()
    {
        return _design >= 0 && _design < _designs.Count ? _designs[_design].Load() : null;
    }

    private void Refresh(bool force = false)
    {
        _sinceRefresh = 0;
        RefreshYardList();
        var yard = SelectedYard();
        if (yard is null)
        {
            _note.Text = "No shipyard in this game. (Saves from before shipyards existed have none: F7 starts a new game with one.)";
            _bill.Set(Array.Empty<HudRow>());
            _room.Set(Array.Empty<HudRow>());
            ClearQueue();
            return;
        }

        var materials = _catalog.Materials;
        string Name(string id) => materials.Contains(id) ? materials.Get(id).Name : id;
        for (var i = 0; i < _tileButtons.Count; i++) _tileButtons[i].SetPressedNoSignal(i == _design);
        _room.Set(new[]
        {
            yard.HasCargoLimit
                ? new HudRow("cargo", $"{yard.CargoKg / 1000.0:0.0} / {yard.CargoCapacityKg / 1000.0:0} t stored, kits included", yard.CargoKg >= 0.97 * yard.CargoCapacityKg ? Severity.Warning : Severity.Normal,
                    (double)yard.CargoKg / Math.Max(1, yard.CargoCapacityKg))
                : new HudRow("cargo", $"{yard.Inventory.TotalKg / 1000.0:0.0} t stored, unlimited (old save)"),
        });
        var held = yard.Inventory.Kg.Where(kv => kv.Value > 0).OrderBy(kv => materials.OrderOf(kv.Key)).ToList();
        var stockSignature = yard.Id + string.Join(",", held.Select(kv => kv.Key));
        if (force || stockSignature != _stockSignature)
        {
            _stockSignature = stockSignature;
            foreach (var child in _stock.GetChildren()) { _stock.RemoveChild(child); child.QueueFree(); }
            foreach (var (id, _) in held)
            {
                var chip = new HBoxContainer { TooltipText = Name(id), MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(84, 0) };
                chip.AddChild(UiTheme.Icon("mat_" + id));
                chip.AddChild(new Label { ThemeTypeVariation = "HudText", MouseFilter = MouseFilterEnum.Ignore });
                _stock.AddChild(chip);
            }
        }
        for (var i = 0; i < held.Count && i < _stock.GetChildCount(); i++) ((Label)_stock.GetChild(i).GetChild(1)).Text = $"{held[i].Value / 1000.0:0.0##} t";

        if (SelectedDesign() is { } design)
        {
            var bill = design.ComputeBillOfMaterials(_catalog).ToWholeKg();
            var stats = design.ComputeStats(_catalog);
            _note.Text = $"{design.Name}: {stats.CellCount} parts, {stats.TotalMassKg / 1000:0.0} t, about {stats.TotalMassKg / yard.Stats.FabricationRateKgPerS:0} s to build."
                + (design.SuggestedRole(_catalog) != DesignRole.Station ? ""
                    : stats.IsFixedStructure ? "\nBuilt as a construction kit: carry it on a ship, land, and deploy (U)."
                    : "\nBuilt as a construction kit: deploy it beside the yard below, or carry it and deploy (U) in orbit or on a surface.");
            _bill.Set(bill.OrderBy(kv => materials.OrderOf(kv.Key)).Select(kv =>
            {
                var have = yard.Inventory[kv.Key];
                var missing = kv.Value - have;
                return new HudRow("mat_" + kv.Key, $"{Name(kv.Key)}  {kv.Value / 1000.0:0.0##} t" + (missing > 0 ? $"   SHORT {missing / 1000.0:0.0##} t" : ""),
                    missing > 0 ? Severity.Bad : Severity.Good, (double)have / Math.Max(1, kv.Value), Tip: $"Needs {kv.Value / 1000.0:0.0##} t, has {have / 1000.0:0.0##} t");
            }).ToList());
        }
        else
        {
            _note.Text = "No buildable designs. Make one in the blueprint editor (B).";
            _bill.Set(Array.Empty<HudRow>());
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
                var top = new HBoxContainer { Name = "Top" };
                row.AddChild(top);
                var linked = c.Id;
                top.MouseDefaultCursorShape = CursorShape.PointingHand;
                top.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) UiLinks.Open(new TargetRef(linked, BodyId.None)); }; // #100
                top.AddChild(new DesignPicture { Design = c.Design, CustomMinimumSize = new Vector2(36, 36) });
                top.AddChild(new Label { Name = "Text", SizeFlagsHorizontal = SizeFlags.ExpandFill, ThemeTypeVariation = "HudText", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(290, 0) });
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
            if (_work.GetChild(i).GetNodeOrNull<Label>("Top/Text") is not { } label) continue;
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
            row.AddChild(new DesignPicture { Design = kit.Design, CustomMinimumSize = new Vector2(30, 30) });
            row.AddChild(new Label
            {
                Text = $"{kit.Name}  {kit.MassKg / 1000.0:0.0} t{(kit.NeedsSurface ? "  (surface)" : "")}", SizeFlagsHorizontal = SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(180, 0),
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
                row.AddChild(new DesignPicture { Design = order.Design, CustomMinimumSize = new Vector2(30, 30) });
                row.AddChild(new StatBar { Name = "Bar", SizeFlagsVertical = SizeFlags.ShrinkCenter });
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
            var bar = _queue.GetChild(i).GetNode<StatBar>("Bar");
            var what = order.IsKit ? " (kit)" : "";
            bar.Set(order.Fraction, i > 0 ? $"{order.ShipName}{what}  waiting"
                : order.WaitingForRoom ? $"{order.ShipName}{what}  done, needs {order.TotalKg / 1000:0.0} t of storage room"
                : $"{order.ShipName}{what}  {order.Fraction * 100:0}%", order.WaitingForRoom ? Severity.Warning : Severity.Normal);
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
