using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using SpaceAutomationSandbox.Simulation;
using SpaceAutomationSandbox.UI;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Main scene: hosts the simulation, feeds it player input, and draws it. This node is a VIEW.
/// All state lives in <see cref="SectorState"/> (pure C#, no Godot), which only advances in whole
/// fixed ticks from <see cref="FixedStepClock"/>. Rendering interpolates between ticks and never
/// writes simulation state, except the player's <see cref="ShipControls"/>.
/// </summary>
public partial class Main : Node2D
{
    // Fixed seeds so the backdrop and sector are identical on every run.
    private const int StarfieldSeed = 20261120;
    private const int StarCount = 220;

    private const string MaterialsPath = "res://data/materials/materials.json";
    private const string PartsPath = "res://data/parts/parts.json";
    private const string RecipesPath = "res://data/recipes/recipes.json";
    private const string ResearchPath = "res://data/research/research.json";
    private const string StarterDesignPath = "res://data/blueprints/starter_ship.json";
    private const string StarterShipyardPath = "res://data/blueprints/starter_shipyard.json";

    // Zoom in screen pixels per sector metre. Mouse wheel changes it; this is view state only.
    private const float MinPixelsPerMetre = 0.00005f; // the whole star system fits (#53)
    private const float MaxPixelsPerMetre = 8f;
    private const float ZoomStep = 1.15f;
    private float _pixelsPerMetre = 1f;

    private static readonly Color SpaceColor = new(0.02f, 0.02f, 0.06f);
    private static readonly Color PlanetColor = new(0.30f, 0.55f, 0.85f);
    private static readonly Color AsteroidColor = new(0.65f, 0.55f, 0.45f);
    private static readonly Color StarColor = new(1f, 0.85f, 0.45f);

    /// <summary>World point at the centre of the screen. Drawing is relative to it, so it stays sharp far from the star (#53).</summary>
    private Vector2D _viewCentre;
    private bool _mapMode;

    /// <summary>In the map: the galaxy view (G, #21) instead of one system.</summary>
    private bool _galaxyView;

    /// <summary>In the map: the system picked in the map panel ("View"), instead of the one you're in.</summary>
    private BodyId _mapSystem = BodyId.None;
    private float _zoomBeforeMap = 1f;
    private MapPanel? _mapPanel;
    private static readonly Color MoonColor = new(0.62f, 0.62f, 0.66f);
    private static readonly Color IceColor = new(0.75f, 0.88f, 0.95f);
    private static readonly Color GasGiantColor = new(0.85f, 0.66f, 0.42f);
    private static readonly Color OrbitColor = new(1f, 1f, 1f, 0.12f);
    private static readonly Color NameColor = new(0.8f, 0.8f, 0.85f);
    private static readonly Color FlameColor = new(1f, 0.75f, 0.3f, 0.9f);
    private static readonly Color MarkerColor = new(0.4f, 1f, 0.6f);
    private static readonly Color TargetLineColor = new(0.4f, 1f, 0.6f, 0.25f);
    private static readonly Color RailColor = new(0.6f, 0.5f, 1f, 0.18f);
    private static readonly Color CraftLabelColor = new(0.75f, 0.75f, 0.85f, 0.8f);

    // Single quicksave slot in Godot's per-user data folder (%APPDATA%\Godot\app_userdata\<project>).
    private const string SavePath = "user://savegame.json";
    private const ulong StatusShowMs = 4000;

    private readonly TimeWarp _warp = new();
    private readonly TickCostMeter _tickCost = new();
    private readonly Dictionary<string, Color> _partColors = new();
    private PartCatalog? _catalog;
    private ShipDesign? _starterDesign;
    private ShipDesign? _starterShipyard;
    private SectorState _sector = null!;
    private CraftState? _player;
    private string _setupError = "";
    private string _status = "";
    private ulong _statusUntilMs;
    private Camera2D _camera = null!;
    private Hud _hud = null!;
    private readonly GameSettings _settings = GameSettings.Load();

    /// <summary>Holds the HUD and every panel; scaled by the interface scale and sized to the window (#52).</summary>
    private Control _uiRoot = null!;
    private SettingsPanel? _settingsPanel;
    private BlueprintEditorView? _editor;
    private ShipyardPanel? _shipyardPanel;
    private FleetPanel? _fleetPanel;
    private CargoPanel? _cargoPanel;
    private JobPanel? _jobPanel;
    private DebugPanel? _debugPanel;
    private DiagnosticsPanel? _diagnosticsPanel;
    private ResearchPanel? _researchPanel;

    // Debug game speed: how many seconds of game time pass per real second. View-only (results don't change).

    private bool EditorOpen => _editor is { Visible: true };

    public override void _Ready()
    {
        InputActions.EnsureDefaults(); // before any input is read
        RenderingServer.SetDefaultClearColor(SpaceColor);
        _camera = GetNode<Camera2D>("Camera2D");
        _settings.ApplyWindow();

        var hudLayer = new CanvasLayer();
        AddChild(hudLayer);
        _uiRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        hudLayer.AddChild(_uiRoot);
        GetViewport().SizeChanged += LayoutUi;
        _hud = new Hud(_settings, () => _settings.Save()); // areas on the left, key help on H (#51)
        _uiRoot.AddChild(_hud);
        _settingsPanel = new SettingsPanel(_settings, () => { _settings.ApplyWindow(); _settings.Save(); LayoutUi(); }) { Visible = false };
        _uiRoot.AddChild(_settingsPanel);

        LoadDefinitions();
        if (_catalog != null)
        {
            _editor = new BlueprintEditorView(_catalog, _partColors, () => _player?.Design) { Visible = false };
            _editor.LockedBy = partId => _sector.IsPartUnlocked(partId) ? null : _catalog.Research.TechFor(partId)?.Name; // research (#37)
            _editor.CloseRequested += CloseEditor;
            _uiRoot.AddChild(_editor);

            _shipyardPanel = new ShipyardPanel(() => _sector, _catalog, () => _player?.Design) { Visible = false };
            _uiRoot.AddChild(_shipyardPanel);
            _fleetPanel = new FleetPanel(() => _sector, () => _player?.Id) { Visible = false };
            _fleetPanel.CraftChosen += Pilot;
            _fleetPanel.CraftTargeted += target =>
            {
                if (_player == null || target == _player.Id) return;
                _sector.SetTargetCraft(_player.Id, target);
                ShowStatus($"Target: {_sector.GetCraft(target).Name}.");
            };
            _uiRoot.AddChild(_fleetPanel);
            _cargoPanel = new CargoPanel(() => _sector, () => _player, _catalog.Materials) { Visible = false };
            _uiRoot.AddChild(_cargoPanel);
            _jobPanel = new JobPanel(() => _sector, () => _player, _catalog.Materials) { Visible = false };
            _uiRoot.AddChild(_jobPanel);
            _mapPanel = new MapPanel(() => _sector, () => _player,
                body => { if (_player != null) { _sector.SetTarget(_player.Id, body); ShowStatus($"Target: {_sector.GetBody(body).Name}."); } },
                craft => { if (_player != null && craft != _player.Id) { _sector.SetTargetCraft(_player.Id, craft); ShowStatus($"Target: {_sector.GetCraft(craft).Name}."); } },
                ViewSystem)
            { Visible = false };
            _uiRoot.AddChild(_mapPanel);
            _diagnosticsPanel = new DiagnosticsPanel(() => _sector, _warp, _tickCost) { Visible = false };
            _uiRoot.AddChild(_diagnosticsPanel);
            _researchPanel = new ResearchPanel(() => _sector, _catalog) { Visible = false };
            _uiRoot.AddChild(_researchPanel);
            _debugPanel = new DebugPanel(() => _sector, () => _warp.Speed, SetGameSpeed, _catalog.Materials) { Visible = false };
            _uiRoot.AddChild(_debugPanel);
        }
        LayoutUi();
        // Continue where the player left off; a new game only if there's no usable save.
        if (_catalog != null && System.IO.File.Exists(SaveFilePath)) LoadGame(isStartup: true);
        else StartNewGame();

        GD.Print($"[Bootstrap] Main scene ready. C# assembly loaded. Godot {Engine.GetVersionInfo()["string"].AsString()}");
        GD.Print($"[Sector] seed={_sector.Seed} tick={_sector.Tick} bodies={_sector.Bodies.Count} tickRate={FixedStepClock.TicksPerSecond}/s");
        if (_player is { } p)
            GD.Print($"[Craft] {p.Id} '{p.Design.Name}' cells={p.Stats.CellCount} mass={p.Stats.TotalMassKg}kg thrust={p.Stats.TotalThrustN}N");
        GD.Print($"[Save] {SaveFilePath} {(_status.Length > 0 ? "- " + _status : "")}");

        // Dev/test hooks, e.g. `godot --path . -- --open-editor`: start with a panel open.
        var args = OS.GetCmdlineUserArgs();
        if (System.Array.IndexOf(args, "--new-game") >= 0) StartNewGame(); // ignores the save; nothing is written
        if (System.Array.IndexOf(args, "--demo-build") >= 0) DemoBuild();
        if (System.Array.IndexOf(args, "--demo-anchor") >= 0 && _player != null)
        {
            // Dev/test hook: drop onto the primary clamp-first (nose up), then anchor.
            _player.HeadingRad = 0;
            _player.Velocity = _sector.GetVelocityAt(SectorGenerator.HomePlanet(_sector).Id, _sector.TimeS) + new Vector2D(0, 4); // gently down: harmless (#34)
            for (var i = 0; i < 600 && !_player.IsLanded; i++) _sector.Step();
            _player.Controls = ShipControls.Idle;
            ShowStatus(_sector.TryAnchor(_player.Id).Message);
        }
        if (System.Array.IndexOf(args, "--demo-dock") >= 0 && _player != null
            && _sector.Crafts.FirstOrDefault(c => c.Stats.DockingPorts.Count > 0) is { } dockYard)
        {
            // Dev/test hook: put the player at the yard's first berth, dock, and load 5 t of metal.
            _player.Position = _sector.BerthPosition(dockYard, 0, _player.CollisionRadiusM, dockYard.Position);
            _player.Velocity = dockYard.Velocity;
            _sector.SetTargetCraft(_player.Id, dockYard.Id);
            ShowStatus(_sector.TryDock(_player.Id).Message);
            _sector.TryTransfer(dockYard.Id, _player.Id, "metal", 5_000);
        }
        if (System.Array.IndexOf(args, "--demo-mine") >= 0 && _player != null
            && _sector.Bodies.FirstOrDefault(b => b.IsMineable) is { } demoRock
            && _sector.Crafts.FirstOrDefault(c => c.Stats.DockingPorts.Count > 0) is { } demoYard)
        {
            // Dev/test hook: start a metal job on the player's ship and fast-forward ~5 minutes (mid-mining).
            _sector.MarkSurveyed(demoRock.Id);
            ShowStatus(_sector.StartJob(_player.Id, new MiningJob(demoRock.Id, demoRock.DepositsKg.Keys.First(), demoYard.Id, 0.25)).Message);
            for (var i = 0; i < 300 * FixedStepClock.TicksPerSecond; i++) _sector.Step();
        }
        if (System.Array.IndexOf(args, "--demo-warp") >= 0) DemoWarp();
        if (System.Array.IndexOf(args, "--demo-tug") >= 0 && _player != null && _sector.Crafts.FirstOrDefault(c => c.IsShipyard) is { } tugYard)
        {
            // Dev/test hook (#29): the player's ship docked at the yard as a tug, and the yard flown.
            _player.Position = _sector.BerthPosition(tugYard, 0, _player.CollisionRadiusM, tugYard.Position);
            _player.Velocity = tugYard.Velocity;
            _sector.TryDock(_player.Id);
            Pilot(tugYard.Id);
        }
        if (System.Array.IndexOf(args, "--demo-craters") >= 0 && _player is { IsLanded: true } miner)
        {
            // Dev/test hook (#28): big pits around where the player is mining, to see craters (no mass removed).
            var at = System.Math.Atan2(miner.LandedOffset.Y, miner.LandedOffset.X);
            _sector.DebugDigCrater(miner.LandedOnBodyId, at - 0.25, 20_000_000);
            _sector.DebugDigCrater(miner.LandedOnBodyId, at + 0.3, 200_000_000);
            _sector.DebugDigCrater(miner.LandedOnBodyId, at + 1.2, 2_000_000_000);
        }
        if (System.Array.IndexOf(args, "--open-job") >= 0) _jobPanel?.Open();
        if (System.Array.IndexOf(args, "--open-debug") >= 0) _debugPanel?.Open();
        if (System.Array.IndexOf(args, "--open-editor") >= 0) OpenEditor();
        if (System.Array.IndexOf(args, "--open-cargo") >= 0) _cargoPanel?.Open();
        if (System.Array.IndexOf(args, "--open-shipyard") >= 0) _shipyardPanel?.Open();
        if (args.FirstOrDefault(a => a.StartsWith("--shipyard-design=")) is { } designArg && int.TryParse(designArg["--shipyard-design=".Length..], out var designIndex))
            _shipyardPanel?.SelectDesign(designIndex);
        if (System.Array.IndexOf(args, "--open-fleet") >= 0) _fleetPanel?.Open();
        if (System.Array.IndexOf(args, "--open-diagnostics") >= 0) _diagnosticsPanel?.Open();
        if (System.Array.IndexOf(args, "--open-settings") >= 0) _settingsPanel?.Open();
        if (System.Array.IndexOf(args, "--open-map") >= 0) ToggleMap();
        if (System.Array.IndexOf(args, "--open-research") >= 0) _researchPanel?.Open();
        if (System.Array.IndexOf(args, "--open-galaxy") >= 0) { if (!_mapMode) ToggleMap(); _galaxyView = true; }
        if (System.Array.IndexOf(args, "--open-help") >= 0) _hud.ToggleHelp();
        if (System.Array.IndexOf(args, "--demo-crash") >= 0 && _player != null)
        {
            // Dev/test hook: drop the player onto Home tail first at 30 m/s, to see damage (#34).
            _player.Velocity = _sector.GetVelocityAt(SectorGenerator.HomePlanet(_sector).Id, _sector.TimeS) + new Vector2D(0, 30);
            for (var i = 0; i < 600 && !_player.IsLanded; i++) _sector.Step();
            _player.Controls = ShipControls.Idle;
        }
        if (args.FirstOrDefault(a => a.StartsWith("--speed=")) is { } speedArg && int.TryParse(speedArg["--speed=".Length..], out var speed))
            _warp.Speed = speed;
        foreach (var arg in args) // --zoom=0.2
            if (arg.StartsWith("--zoom=") && float.TryParse(arg[7..], System.Globalization.CultureInfo.InvariantCulture, out var zoom))
                _pixelsPerMetre = Mathf.Clamp(zoom, MinPixelsPerMetre, MaxPixelsPerMetre);
    }

    // Dev/test hook: order two of the player's ship at the first yard and fast-forward 40 s,
    // so one finished ship is parked and the second is mid-build.
    private void DemoBuild()
    {
        if (_catalog == null || _player == null || _sector.Crafts.FirstOrDefault(c => c.IsShipyard) is not { } yard) return;
        for (var i = 0; i < 2; i++) _sector.OrderBuild(yard.Id, _player.Design, _catalog);
        for (var i = 0; i < 40 * FixedStepClock.TicksPerSecond; i++) _sector.Step();
    }

    // Dev/test hook (#54): a stocked gate station beside the yard, the player docked at it, one jump
    // (which opens a new star system), so the new system can be looked at.
    private void DemoWarp()
    {
        if (_catalog == null || _player == null || _sector.Crafts.FirstOrDefault(c => c.IsShipyard) is not { } yard) return;
        var design = new ShipDesign("demo_gate", "Demo gate", new[]
        {
            new DesignCell(new GridPos(0, 0), "warp_gate"), new DesignCell(new GridPos(0, -1), "docking_port"),
            new DesignCell(new GridPos(1, 0), "storage_silo"), new DesignCell(new GridPos(-1, 0), "solar_panel_mk2"),
            new DesignCell(new GridPos(-1, -1), "battery_mk2"), new DesignCell(new GridPos(-1, 1), "battery_mk2"),
        });
        var bill = design.ComputeBillOfMaterials(_catalog).ToWholeKg();
        var kit = new ConstructionKit(new OrderId(_sector.NextOrderId), "Gate", design, design.ComputeStats(_catalog), bill);
        _sector.RestoreKit(yard.Id, kit);
        _sector.RestoreNextOrderId(_sector.NextOrderId + 1);
        var before = _sector.Crafts.Select(c => c.Id).ToHashSet();
        _sector.TryDeployKit(yard.Id, kit.Id);
        var gate = _sector.Crafts.First(c => !before.Contains(c.Id));
        foreach (var (material, kg) in bill) _sector.DebugAddStock(gate.Id, material, kg);
        if (_player.IsDocked) _sector.TryUndock(_player.Id);
        _player.Position = _sector.BerthPosition(gate, 0, _player.CollisionRadiusM, gate.Position);
        _player.Velocity = gate.Velocity;
        _sector.TryDock(_player.Id);
        ShowStatus(_sector.RequestJump(_player.Id).Message);
        for (var i = 0; i < 40; i++) _sector.Step();
    }

    private void SetGameSpeed(int speed)
    {
        _warp.Speed = speed;
        ShowStatus($"Time {_warp}.");
    }

    /// <summary>Hands the flight controls to another craft. The old one keeps its last controls cleared.</summary>
    private void Pilot(CraftId id)
    {
        if (_player != null) _player.Controls = ShipControls.Idle;
        _player = _sector.GetCraft(id);
        _sector.PilotCraftId = id; // the craft you fly is the one that surveys
        ShowStatus($"Now piloting {_player.Name} ({_player.Id}).");
    }

    private void OpenEditor()
    {
        if (_editor == null) { ShowStatus("Editor unavailable: part data failed to load."); return; }
        _editor.Open();
        _hud.Visible = false;
    }

    private void CloseEditor()
    {
        if (_editor != null) _editor.Visible = false;
        _hud.Visible = true;
    }

    private static string SaveFilePath => ProjectSettings.GlobalizePath(SavePath);

    private void LoadDefinitions()
    {
        try
        {
            var materials = MaterialCatalog.FromJson(Godot.FileAccess.GetFileAsString(MaterialsPath));
            var recipes = RecipeCatalog.FromJson(Godot.FileAccess.GetFileAsString(RecipesPath), materials);
            var catalog = PartCatalog.FromJson(Godot.FileAccess.GetFileAsString(PartsPath), materials, recipes)
                .WithResearch(ResearchCatalog.FromJson(Godot.FileAccess.GetFileAsString(ResearchPath)));
            var design = ShipDesign.FromJson(Godot.FileAccess.GetFileAsString(StarterDesignPath));
            var shipyard = ShipDesign.FromJson(Godot.FileAccess.GetFileAsString(StarterShipyardPath));
            var errors = design.Validate(catalog, DesignRole.Ship)
                .Concat(shipyard.Validate(catalog, DesignRole.Station).Select(e => $"{shipyard.Id}: {e}")).ToList();
            if (errors.Count > 0)
            {
                _setupError = "Starter designs are invalid:\n  " + string.Join("\n  ", errors);
                GD.PushError(_setupError);
                return;
            }

            foreach (var part in catalog.Parts)
                _partColors[part.Id] = part.PlaceholderColor is { } hex ? Color.FromHtml(hex) : Colors.Magenta;
            _catalog = catalog;
            _starterDesign = design;
            _starterShipyard = shipyard;
        }
        catch (System.Exception e) // bad or missing data file: show it instead of crashing
        {
            _setupError = $"Could not load ship data: {e.Message}";
            GD.PushError(_setupError);
        }
    }

    private void StartNewGame()
    {
        _player = null;
        if (_catalog == null || _starterDesign == null || _starterShipyard == null)
        {
            _sector = SectorGenerator.Generate(NewGame.DefaultSeed); // something to look at while the error shows
            return;
        }

        var game = NewGame.Create(NewGame.DefaultSeed, _catalog, _starterDesign, _starterShipyard);
        _sector = game.Sector;
        _player = _sector.GetCraft(game.PlayerCraftId);
    }

    private void SaveGame()
    {
        if (_player == null) return;
        try
        {
            var json = SaveGameSerializer.ToJson(_sector, _player.Id);
            // Write-then-rename, so a crash mid-write can't destroy the previous save.
            var tmp = SaveFilePath + ".tmp";
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SaveFilePath)!);
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, SaveFilePath, overwrite: true);
            ShowStatus($"Saved at tick {_sector.Tick}.");
        }
        catch (System.Exception e)
        {
            ShowStatus($"SAVE FAILED: {e.Message}");
            GD.PushError(_status);
        }
    }

    private void LoadGame(bool isStartup)
    {
        if (_catalog == null) return;
        if (!System.IO.File.Exists(SaveFilePath)) { ShowStatus("No save to load."); return; }
        try
        {
            var loaded = SaveGameSerializer.FromJson(System.IO.File.ReadAllText(SaveFilePath), _catalog);
            _sector = loaded.Sector;
            _player = loaded.PlayerCraftId.IsNone ? null : _sector.GetCraft(loaded.PlayerCraftId);
            ShowStatus($"Loaded save at tick {_sector.Tick}.");
        }
        catch (System.Exception e)
        {
            if (!isStartup) { ShowStatus($"LOAD FAILED (game unchanged): {e.Message}"); GD.PushError(_status); return; }

            // Keep the unreadable file for inspection instead of overwriting it on the next save.
            var badPath = SaveFilePath + ".bad";
            System.IO.File.Move(SaveFilePath, badPath, overwrite: true);
            StartNewGame();
            ShowStatus($"Save could not be loaded ({e.Message}). Moved it to {badPath}; started a new game.");
            GD.PushError(_status);
        }
    }

    private void ShowStatus(string message)
    {
        _status = message;
        _statusUntilMs = Time.GetTicksMsec() + StatusShowMs;
    }

    // Autosave when the window is closed normally. (Stopping from the editor kills the
    // process without this notification; use F6 first if you need that state.)
    public override void _Notification(int what)
    {
        if (what != NotificationWMCloseRequest) return;
        SaveGame();
        _settings.CaptureWindow(); // reopen at the same size and place
        _settings.Save();
    }

    /// <summary>
    /// Sizes the UI root to the window in interface units and scales it (#52). Panels anchor to the
    /// root, so they follow the window edges at any size; the world just shows more of space.
    /// </summary>
    private void LayoutUi()
    {
        var size = GetViewport().GetVisibleRect().Size;
        var scale = _settings.EffectiveUiScale(size);
        _uiRoot.Position = Vector2.Zero;
        _uiRoot.Scale = new Vector2(scale, scale);
        _uiRoot.Size = size / scale;
        if (OS.GetCmdlineUserArgs().Contains("--print-layout")) GD.Print($"[Layout] window {DisplayServer.WindowGetSize()} viewport {size} ui scale {scale} ui size {_uiRoot.Size}");
    }

    private void ToggleFullscreen()
    {
        if (_settings.WindowMode == GameSettings.Mode.Windowed)
        {
            _settings.CaptureWindow();
            _settings.WindowMode = GameSettings.Mode.Fullscreen;
        }
        else _settings.WindowMode = GameSettings.Mode.Windowed;
        _settings.ApplyWindow();
        _settings.Save();
        LayoutUi();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Named actions (InputActions), so bindings can change; Echo is ignored for one-shot actions.
        bool Pressed(string action) => @event.IsActionPressed(action, allowEcho: false);

        if (EditorOpen)
        {
            // The editor owns input while open; only its close actions reach here.
            if (Pressed(InputActions.Editor) || Pressed(InputActions.ClosePanels))
            {
                CloseEditor();
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        // The side panels don't take keyboard focus, so flying continues while they're open.

        if (Pressed(InputActions.ZoomIn) || Pressed(InputActions.ZoomOut))
        {
            _pixelsPerMetre = Mathf.Clamp(Pressed(InputActions.ZoomIn) ? _pixelsPerMetre * ZoomStep : _pixelsPerMetre / ZoomStep,
                MinPixelsPerMetre, MaxPixelsPerMetre);
        }
        else if (Pressed(InputActions.QuickSave)) SaveGame();
        else if (Pressed(InputActions.QuickLoad)) LoadGame(isStartup: false);
        else if (Pressed(InputActions.NewGame)) { StartNewGame(); ShowStatus("New game. The save file is unchanged until you save or quit."); }
        else if (Pressed(InputActions.Editor)) OpenEditor();
        else if (Pressed(InputActions.Shipyard)) Toggle(_shipyardPanel, () => _shipyardPanel!.Open());
        else if (Pressed(InputActions.Fleet)) Toggle(_fleetPanel, () => _fleetPanel!.Open());
        else if (Pressed(InputActions.Debug)) Toggle(_debugPanel, () => _debugPanel!.Open());
        else if (Pressed(InputActions.Job)) Toggle(_jobPanel, () => _jobPanel!.Open());
        else if (Pressed(InputActions.Cargo)) Toggle(_cargoPanel, () => _cargoPanel!.Open());
        else if (Pressed(InputActions.Diagnostics)) Toggle(_diagnosticsPanel, () => _diagnosticsPanel!.Open());
        else if (Pressed(InputActions.Settings)) Toggle(_settingsPanel, () => _settingsPanel!.Open());
        else if (Pressed(InputActions.Research)) Toggle(_researchPanel, () => _researchPanel!.Open());
        else if (Pressed(InputActions.Map)) ToggleMap();
        else if (Pressed(InputActions.Help)) _hud.ToggleHelp();
        else if (Pressed(InputActions.Fullscreen)) ToggleFullscreen();
        else if (Pressed(InputActions.TimeFaster)) { _warp.Paused = false; _warp.Faster(); ShowStatus($"Time {_warp}."); }
        else if (Pressed(InputActions.TimeSlower)) { _warp.Paused = false; _warp.Slower(); ShowStatus($"Time {_warp}."); }
        else if (Pressed(InputActions.TimePause)) { _warp.Paused = !_warp.Paused; ShowStatus(_warp.Paused ? "Paused (P to resume)." : $"Time {_warp}."); }
        else if (Pressed(InputActions.ClosePanels))
        {
            if (_mapMode) ToggleMap();
            _hud.CloseHelp();
            foreach (var panel in new Control?[] { _shipyardPanel, _fleetPanel, _cargoPanel, _jobPanel, _debugPanel, _diagnosticsPanel, _settingsPanel, _researchPanel })
                if (panel != null) panel.Visible = false;
        }
        else if (_player == null) return;
        else if (Pressed(InputActions.Jump)) ShowStatus(_sector.RequestJump(_player.Id).Message);
        else if (_mapMode && Pressed(InputActions.Anchor)) _galaxyView = !_galaxyView; // in the map, G is the galaxy view (#21)
        else if (Pressed(InputActions.Dock))
            ShowStatus((_player.IsDocked ? _sector.TryUndock(_player.Id) : _sector.TryDock(_player.Id)).Message);
        else if (Pressed(InputActions.Cruise))
            ShowStatus((_player.Cruising ? _sector.TryStopCruise(_player.Id) : _sector.TryStartCruise(_player.Id)).Message);
        else if (Pressed(InputActions.Deploy))
            ShowStatus(_player.Kits.FirstOrDefault() is { } kit ? _sector.TryDeployKit(_player.Id, kit.Id).Message : "No construction kit on board.");
        else if (Pressed(InputActions.Anchor))
            ShowStatus((_player.IsAnchored ? _sector.TryRelease(_player.Id) : _sector.TryAnchor(_player.Id)).Message);
        else if (Pressed(InputActions.TargetCycle)) CycleTarget();
        else if (Pressed(InputActions.TargetNearest)) _sector.SetTarget(_player.Id, _sector.NearestBody(_player.Position).Body.Id);
        else return;
        GetViewport().SetInputAsHandled();
    }

    private static void Toggle(Control? panel, System.Action open)
    {
        if (panel == null) return;
        if (panel.Visible) panel.Visible = false;
        else open();
    }

    /// <summary>
    /// Tab order: each body by id, then every other craft by id, then "no target" (brake stops
    /// relative to the sector). Fine for a few craft; a fleet-scale picker comes with the fleet UI.
    /// </summary>
    private void CycleTarget()
    {
        var p = _player!;
        var options = _sector.Bodies.Select(b => (Body: b.Id, Craft: CraftId.None))
            .Concat(_sector.Crafts.Where(c => c.Id != p.Id).Select(c => (Body: BodyId.None, Craft: c.Id)))
            .Append((Body: BodyId.None, Craft: CraftId.None))
            .ToList();
        var current = options.FindIndex(o => o.Body == p.TargetBodyId && o.Craft == p.TargetCraftId);
        var (body, craft) = options[(current + 1) % options.Count];
        if (!craft.IsNone) _sector.SetTargetCraft(p.Id, craft);
        else
        {
            _sector.SetTargetCraft(p.Id, CraftId.None);
            _sector.SetTarget(p.Id, body);
        }
    }

    public override void _Process(double delta)
    {
        // Input is sampled once per frame and held for every tick run this frame.
        // No flying while the editor is open (typing a name must not fire thrusters).
        // A ship running a job is flown by it; the keyboard only flies ships without one.
        // Typing into a text/number box (e.g. the debug menu) mustn't fly the ship either.
        var typing = GetViewport().GuiGetFocusOwner() is LineEdit;
        if (_player is { IsAutomated: false }) _player.Controls = EditorOpen || typing ? ShipControls.Idle : ReadControls();

        // Run the ticks due, within a frame budget: at high warp a heavy sector runs slower rather than freezing.
        var due = _warp.TicksDue(delta);
        var frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var ran = 0;
        while (ran < due)
        {
            var tickStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _sector.Step();
            ran++;
            _tickCost.Add(System.Diagnostics.Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds > TimeWarp.FrameBudgetMs) break;
        }
        _warp.RecordFrame(delta, ran, due);

        _camera.Position = Vector2.Zero; // the world is drawn around _viewCentre instead (precision far from the star)
        if (_mapMode && MapStar() is { } root) _viewCentre = _sector.GetPosition(root.Id);
        else if (_player != null) _viewCentre = InterpolatedPosition(_player);
        _hud.Show(BuildHud());
        QueueRedraw();
        DevScreenshot();
    }

    private int _framesShown;

    /// <summary>
    /// Dev/test hook: `-- --screenshot=path.png` saves the window at its real size after a few
    /// frames and quits (movie maker always records 1280×720, so it can't check other resolutions).
    /// </summary>
    private void DevScreenshot()
    {
        if (++_framesShown != 20) return;
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot=")) is not { } arg) return;
        GetViewport().GetTexture().GetImage().SavePng(arg["--screenshot=".Length..]);
        GetTree().Quit();
    }

    // Input actions (keyboard or gamepad; trigger and stick are analog).
    private static ShipControls ReadControls()
    {
        var (throttle, turn, brake, strafe) = InputActions.ReadFlight();
        return new ShipControls(throttle, turn, brake, strafe);
    }

    /// <summary>
    /// This frame's HUD text, by area (#51). Seed, tick and zoom moved to the diagnostics panel (F4);
    /// the key list to the help overlay (H).
    /// </summary>
    private HudSections BuildHud()
    {
        var status = "";
        if (Time.GetTicksMsec() < _statusUntilMs) status += $">> {_status}\n";
        if (_warp.Speed != 1 || _warp.Paused) status += $"TIME {_warp}   ([ slower, ] faster, P pause)\n";
        if (!_sector.Tuning.IsDefault) status += $"DEBUG: {_sector.Tuning}  (F3)\n";
        if (_player is not { } p) return new HudSections(status + _setupError, "", ShipyardSummary(), "");

        var headingDeg = Mathf.PosMod(Mathf.RadToDeg((float)p.HeadingRad), 360f);
        var ship = $"{p.Name} - {p.Design.Name}: {p.SpeedMps:0.0} m/s, heading {headingDeg:000}°, "
                   + (p.HasCargoLimit ? $"cargo {p.CargoKg / 1000.0:0.0} / {p.CargoCapacityKg / 1000.0:0} t\n" : $"stores {p.CargoKg / 1000.0:0.0} t\n")
                   + $"{p.Stats.TotalMassKg / 1000:0.0} t, {p.Stats.TotalThrustN / 1000:0} kN, {p.Stats.TotalThrustN / p.MassKg:0.0} m/s²\n"
                   + (p.Kits.Count > 0
                       ? $"KIT on board: {p.Kits[0].Name} ({p.Kits[0].MassKg / 1000.0:0.0} t){(p.Kits.Count > 1 ? $" +{p.Kits.Count - 1} more" : "")} - "
                         + (_sector.DeployBlocker(p.Id) is { } blocked ? blocked : "U to deploy here") + "\n"
                       : "")
                   + (SectorState.PowerStatus(p) is { Length: > 0 } power ? power + "\n" : "")
                   + (SectorState.CrewStatus(p) is { Length: > 0 } crew ? crew + "\n" : "")
                   + (SectorState.NeedsRepair(p)
                       ? $"DAMAGE: {p.PartHealth.Count} part(s) damaged"
                         + (p.PartHealth.Count > 0 ? $" (worst {p.PartHealth.Values.Min() * 100:0}%)" : "")
                         + (SectorState.MissingParts(p) > 0 ? $", {SectorState.MissingParts(p)} broken off" : "")
                         + " - " + (p.RepairStatus ?? "dock at a shipyard to repair") + "\n"
                       : "")
                   + (p.YardStatus is { } yardWork ? $"SHIPYARD WORK: {yardWork}\n" : "")
                   + (_sector.TugsOf(p.Id) is { Count: > 0 } tugs
                       ? $"TUGS: {tugs.Count} docked{(p.TugThrustN > 0 ? $", pushing {p.TugThrustN / 1000:0} kN" : " (they push the way they face)")}, moving {_sector.MovingMassKg(p) / 1000:0} t\n"
                       : "")
                   + (p.GateStatus is { } gateNow ? $"WARP: {gateNow}{(p.JumpRequestTick > 0 ? " - N to cancel" : "")}\n"
                       : p.IsDocked && SectorState.IsGate(_sector.GetCraft(p.DockedToCraftId)) ? $"WARP GATE {_sector.GetCraft(p.DockedToCraftId).Name} - N to jump\n" : "")
                   + (_sector.Stars.Count > 1 && _sector.StarAt(p.Position) is { } here ? $"system: {here.Name}\n" : "")
                   + (!p.IsLanded && !p.IsDocked && !p.IsOnRails && !p.Cruising && _sector.GravitySource(p.Position) is { } pull
                       ? $"gravity {_sector.GravityAt(p.Position, _sector.TimeS).Length:0.00} m/s² from {pull.Name} - land under {SectorState.SafeImpactSpeedMps:0} m/s\n"
                       : "")
                   + (p.Cruising
                       ? $"CRUISE {SectorState.CruiseSpeedMps:0} m/s - fuel {p.Inventory[SectorState.FuelMaterial] / 1000.0:0.00} t "
                         + $"({p.Inventory[SectorState.FuelMaterial] / System.Math.Max(SectorState.CruiseFuelKgPerS(p), 1e-9) / 60:0} min left), V or brake to drop out\n"
                       : p.CruiseStopReason is { } why && p.Stats.TotalThrustN > 0 ? $"cruise ended: {why} - V to cruise again\n" : "")
                   + (p.Job is { Active: true } job
                       ? $"AUTOPILOT ({job.Kind} job): {job.Status}   cycles {job.CompletedCycles}, delivered {job.DeliveredKg / 1000.0:0.0} t - J to view/stop\n"
                       : "")
                   + (_sector.MiningMaterial(p) is { } mining && p.FreeCargoKg > 0
                       ? $"MINING {mining}: {p.Stats.MiningRateKgPerS:0} kg/s, {_sector.GetBody(p.LandedOnBodyId).Deposit(mining) / 1000.0:0.0} t left in {_sector.GetBody(p.LandedOnBodyId).Name}\n"
                       : "")
                   + (p.IsLanded ? LandedLine(p) : "")
                   + (p.IsDocked
                       ? $"DOCKED at {_sector.GetCraft(p.DockedToCraftId).Name}, port {p.DockPortIndex + 1} - C cargo, K or thrust to undock\n"
                       : "")
                   + (p.RailOrbit is { } rail
                       ? (p.Stats.TotalThrustN > 0
                           ? $"PARKED in orbit around {_sector.GetBody(rail.Parent).Name} - thrust to leave the parking orbit\n"
                           : _sector.TugsOf(p.Id).Count > 0
                               ? $"PARKED in orbit around {_sector.GetBody(rail.Parent).Name} - thrust or strafe the way a tug faces to move it\n"
                               : $"On a fixed orbit around {_sector.GetBody(rail.Parent).Name} (no thrusters: dock idle ships to it as tugs to move it)\n")
                       : "");

        string target;
        if (!p.TargetCraftId.IsNone)
        {
            var craft = _sector.GetCraft(p.TargetCraftId);
            var rel = _sector.GetRelativeMotionToCraft(p.Id, craft.Id);
            var ports = craft.Stats.DockingPorts.Count;
            target = $"{craft.Name} ({craft.Id}{(ports > 0 ? $", {_sector.FreePorts(craft).Count}/{ports} ports free" : "")})\n"
                     + $"gap {FormatDistance(rel.SurfaceDistanceM)}   rel speed {rel.RelativeSpeedMps:0.0} m/s   "
                     + $"{(rel.ClosingSpeedMps >= 0 ? "closing" : "opening")} {System.Math.Abs(rel.ClosingSpeedMps):0.0} m/s"
                     + (rel.RelativeSpeedMps < SectorState.DockMaxRelativeSpeedMps && ports > 0 && !p.IsDocked ? "   [K to dock if within 100 m]" : "")
                     + "\n";
        }
        else if (p.TargetBodyId.IsNone)
        {
            target = "none - brake stops relative to the sector\n";
        }
        else
        {
            var body = _sector.GetBody(p.TargetBodyId);
            var rel = _sector.GetRelativeMotion(p.Id, body.Id);
            target = $"{Describe(body)}\nsurface {FormatDistance(rel.SurfaceDistanceM)}   "
                     + $"rel speed {rel.RelativeSpeedMps:0.0} m/s   "
                     + $"{(rel.ClosingSpeedMps >= 0 ? "closing" : "opening")} {System.Math.Abs(rel.ClosingSpeedMps):0.0} m/s"
                     + (rel.RelativeSpeedMps < 0.5 ? "   [MATCHED]" : "") + "\n";
        }
        var (nearest, nearestSurface) = _sector.NearestBody(p.Position);
        target += $"nearest: {Describe(nearest)}, surface {FormatDistance(nearestSurface)}";

        return new HudSections(status, ship, ShipyardSummary(), target);
    }

    private string LandedLine(CraftState p)
    {
        var body = _sector.GetBody(p.LandedOnBodyId).Name;
        if (p.IsAnchored)
            return p.Stats.IsFixedStructure
                ? $"ANCHORED to {body} permanently (foundation)\n"
                : $"ANCHORED to {body} - G to release\n";
        return $"LANDED on {body} - thrust away from the surface to lift off"
               + (p.Stats.CanAnchor ? ", G to anchor\n" : " (no anchor clamp)\n");
    }

    private string ShipyardSummary()
    {
        var yards = _sector.Crafts.Where(c => c.IsShipyard).ToList();
        if (yards.Count == 0) return "No shipyard in this save - F7 starts a new game with one.\n";
        var gates = _sector.Crafts.Where(SectorState.IsGate).Select(g =>
            $"{g.Name}: warp gate to " + (_sector.Crafts.FirstOrDefault(c => c.Id == g.GateLinkId) is { } far
                ? $"{far.Name} ({_sector.StarAt(far.Position)?.Name} system)"
                : "nowhere yet (its first jump opens a new star system)") + "\n");
        var labs = _sector.Crafts.Where(c => c.Stats.ResearchRatePerS > 0).ToList();
        var research = labs.Count > 0 || _sector.ResearchPoints > 0
            ? $"Research: {_sector.ResearchPoints:0} points, +{_sector.ResearchRateNow * 60:0.#}/min from {labs.Count} lab(s)"
              + string.Concat(labs.Where(l => l.LabStatus is not null && !l.LabStatus.StartsWith("lab:")).Select(l => $"   {l.Name}: {l.LabStatus}")) + " - R to research\n"
            : "";
        return research + string.Concat(gates) + string.Concat(yards.Select(y => (y.BuildQueue.Count == 0
            ? $"{y.Name}: idle"
            : $"{y.Name}: building {y.BuildQueue[0].ShipName} {y.BuildQueue[0].Fraction * 100:0}%"
              + (y.BuildQueue.Count > 1 ? $" (+{y.BuildQueue.Count - 1} queued)" : ""))
            + (_sector.RefiningStatus(y) is { Length: > 0 } refining ? $"   {refining}" : "")
            + (y.Kits.Count > 0 ? $"   kits {y.Kits.Count}" : "")
            + (y.HasCargoLimit ? $"   storage {y.CargoKg / 1000.0:0.0}/{y.CargoCapacityKg / 1000.0:0} t" : "")
            + (y.PowerShort && !y.LegacyFreePower ? "   NO POWER" : "")
            + (SectorState.CrewStatus(y) is { Length: > 0 } crew ? $"   {crew}" : "")
            + (y.Stats.EjectionRateKgPerS > 0 && _sector.EjectedKg.TryGetValue("slag", out var ejected) ? $"   slag ejected {ejected / 1000.0:0.0} t" : "") + "\n"));
    }

    private static string Describe(CelestialBodyState body) =>
        $"{body.Name} ({body.Kind}"
        + (!body.IsMineable ? "" : !body.Surveyed ? $", unsurveyed - fly within {SectorState.SurveyRangeM / 1000:0} km"
            : body.DepositsKg.Count == 0 && body.BulkKg == 0 ? ", mined out"
            : ", " + string.Join(", ", body.DepositsKg.Select(kv => $"{kv.Key} {kv.Value / 1000.0:0} t")
                .Concat(body.BulkKg > 0 ? new[] { $"bulk {body.BulkMaterialId} {body.BulkKg / 1e9:0.###} Mt" } : System.Array.Empty<string>())))
        + ")";

    private static string FormatDistance(double metres) =>
        System.Math.Abs(metres) >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";

    public override void _Draw()
    {
        DrawStarfield();
        if (_mapMode && _galaxyView) { DrawGalaxy(); return; }

        // Interpolated time: smooth motion between 20 Hz ticks without touching the state.
        var renderTimeS = (_sector.Tick + _warp.Alpha) * FixedStepClock.TickSeconds;
        foreach (var body in _sector.Bodies) DrawBody(body, renderTimeS);
        if (_player is { TargetBodyId.IsNone: false } || _player is { TargetCraftId.IsNone: false }) DrawTargetMarker(_player, renderTimeS);
        foreach (var craft in _sector.Crafts.Where(c => c.IsShipyard && c.RailOrbit != null))
        {
            var orbit = craft.RailOrbit!;
            DrawArc(ToPixels(_sector.GetPositionAt(orbit.Parent, renderTimeS)), (float)orbit.RadiusM * _pixelsPerMetre,
                0, Mathf.Tau, 512, RailColor);
        }
        foreach (var craft in _sector.Crafts) DrawCraft(craft);
        foreach (var craft in _sector.Crafts.Where(c => c != _player && _pixelsPerMetre > 0.02f)) // name tags (not when zoomed far out)
        {
            var radiusPx = Mathf.Max(8f, (float)craft.CollisionRadiusM * _pixelsPerMetre);
            DrawString(ThemeDB.FallbackFont, ToPixels(InterpolatedPosition(craft)) + new Vector2(radiusPx + 4, 4), craft.Name,
                HorizontalAlignment.Left, -1, 11, CraftLabelColor);
        }
    }

    // Bracket ring around the target plus a faint line from the ship, so it can be found off-screen.
    private void DrawTargetMarker(CraftState craft, double renderTimeS)
    {
        Vector2 centre;
        float radiusPx;
        if (!craft.TargetCraftId.IsNone)
        {
            var other = _sector.GetCraft(craft.TargetCraftId);
            centre = ToPixels(InterpolatedPosition(other));
            radiusPx = Mathf.Max(8f, (float)other.CollisionRadiusM * _pixelsPerMetre) + 6f;
        }
        else
        {
            var target = _sector.GetBody(craft.TargetBodyId);
            centre = ToPixels(_sector.GetPositionAt(target.Id, renderTimeS));
            radiusPx = Mathf.Max(2f, (float)target.RadiusM * _pixelsPerMetre) + 6f;
        }

        DrawLine(ToPixels(InterpolatedPosition(craft)), centre, TargetLineColor, 1f);
        for (var i = 0; i < 4; i++) // four corner arcs
        {
            var start = Mathf.Tau * i / 4 + Mathf.Pi / 8;
            DrawArc(centre, radiusPx, start, start + Mathf.Pi / 4, 16, MarkerColor, 2f);
        }
    }

    private void DrawBody(CelestialBodyState body, double renderTimeS)
    {
        // Orbits: skip ones too small to see, and huge ones (from close up they're a straight line off-screen).
        var orbitPx = body.Orbit is { } o && body.Kind != BodyKind.Star ? (float)o.RadiusM * _pixelsPerMetre : 0f; // a star's place among the stars isn't an orbit to draw
        if (body.Orbit is { } orbit && orbitPx is > 3f and < 200_000f)
        {
            var parentPx = ToPixels(_sector.GetPositionAt(orbit.Parent, renderTimeS));
            DrawArc(parentPx, orbitPx, 0, Mathf.Tau, 512, OrbitColor);
        }

        var centre = ToPixels(_sector.GetPositionAt(body.Id, renderTimeS));
        var radiusPx = Mathf.Max(2f, (float)body.RadiusM * _pixelsPerMetre);
        if (centre.Length() - radiusPx > 4 * GetViewportRect().Size.Length()) return; // far off-screen
        var color = body.Kind switch
        {
            BodyKind.Star => body.StarClass switch { StarClass.Red => new Color(1f, 0.55f, 0.4f), StarClass.Blue => new Color(0.6f, 0.75f, 1f), _ => StarColor },
            BodyKind.Planet => PlanetColor,
            BodyKind.Moon => MoonColor,
            BodyKind.GasGiant => GasGiantColor,
            _ => body.DepositsKg.ContainsKey("ice") ? IceColor : AsteroidColor,
        };
        if (radiusPx < 200f) DrawCircle(centre, radiusPx, color);
        else DrawColoredPolygon(CirclePoints(centre, radiusPx, 512), color); // smoother edge up close
        DrawCraters(body, centre, radiusPx, color);

        // Zoomed out, a moon's or rock's name would sit on top of its planet's: only label bodies whose
        // orbit is big enough on screen to tell apart (and always the target).
        var isTarget = _player is { } p && p.TargetBodyId == body.Id;
        if (body.Orbit is not null && orbitPx < 28f && !isTarget) return;
        DrawString(ThemeDB.FallbackFont, centre + new Vector2(radiusPx + 4, 4), body.Surveyed || !body.IsMineable ? body.Name : $"{body.Name} (unsurveyed)",
            HorizontalAlignment.Left, -1, 12, NameColor);
    }

    private void DrawCraft(CraftState craft)
    {
        var centre = ToPixels(InterpolatedPosition(craft));
        var heading = (float)Mathf.LerpAngle(craft.PreviousHeadingRad, craft.HeadingRad, _warp.Alpha);
        var cellPx = (float)ShipDesign.CellSizeM * _pixelsPerMetre;

        // Too small to see the grid: draw a heading marker instead.
        if (cellPx < 1.5f)
        {
            DrawSetTransform(centre, heading, Vector2.One);
            DrawColoredPolygon(new[] { new Vector2(0, -8), new Vector2(5, 6), new Vector2(-5, 6) }, MarkerColor);
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            return;
        }

        var gap = cellPx >= 6f ? 1f : 0f;
        var stats = craft.Stats;
        DrawSetTransform(centre, heading, Vector2.One);
        foreach (var cell in craft.Design.Cells)
        {
            var local = new Vector2((float)(cell.Pos.X - stats.CenterOfMassX), (float)(cell.Pos.Y - stats.CenterOfMassY)) * cellPx;
            var rect = new Rect2(local - new Vector2(cellPx, cellPx) / 2 + new Vector2(gap, gap) / 2,
                new Vector2(cellPx - gap, cellPx - gap));
            var colour = _partColors.GetValueOrDefault(cell.PartId, Colors.Magenta);
            // Damaged parts (#34) darken towards black-red as they lose health.
            if (craft.PartHealth.TryGetValue(cell.Pos, out var health)) colour = colour.Lerp(new Color(0.25f, 0.05f, 0.05f), (float)(1 - health) * 0.8f);
            DrawRect(rect, colour);

            // Only rear-facing thrusters fire on throttle (docs/06); others stay dark for now.
            if (cell.PartId == "thruster" && cell.Rotation == 0 && craft.Controls.Throttle > 0 && !craft.Controls.Brake)
            {
                var flameTop = local + new Vector2(0, cellPx / 2);
                DrawColoredPolygon(new[]
                {
                    flameTop + new Vector2(-cellPx * 0.3f, 0), flameTop + new Vector2(cellPx * 0.3f, 0),
                    flameTop + new Vector2(0, cellPx * 0.9f),
                }, FlameColor);
            }
        }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    private Vector2D InterpolatedPosition(CraftState craft) =>
        craft.PreviousPosition + (craft.Position - craft.PreviousPosition) * _warp.Alpha;

    private Vector2 ToPixels(Vector2D metres) =>
        new((float)((metres.X - _viewCentre.X) * _pixelsPerMetre), (float)((metres.Y - _viewCentre.Y) * _pixelsPerMetre));

    /// <summary>
    /// System map (M, #53): zooms out to show the whole system around its star, with the target list;
    /// M again returns to the ship at the zoom it had. The wheel still zooms in between.
    /// </summary>
    private void ToggleMap()
    {
        _mapMode = !_mapMode;
        if (_mapMode)
        {
            _zoomBeforeMap = _pixelsPerMetre;
            _mapSystem = BodyId.None;
            _galaxyView = false;
            FitMapZoom();
            _mapPanel?.Open();
        }
        else
        {
            _pixelsPerMetre = _zoomBeforeMap;
            if (_mapPanel != null) _mapPanel.Visible = false;
        }
    }

    /// <summary>
    /// Craters (#28): each a darker pit at the surface where mining dug, with a darker floor, kept inside
    /// the body's disk (the body stays round for landing, so craft sit on the drawn surface).
    /// </summary>
    private void DrawCraters(CelestialBodyState body, Vector2 centre, float radiusPx, Color color)
    {
        if (body.Craters.Count == 0) return;
        var pit = color.Darkened(0.35f);
        var floor = color.Darkened(0.55f);
        foreach (var crater in body.Craters)
        {
            var r = (float)body.CraterRadiusM(crater.MassKg) * _pixelsPerMetre;
            if (r < 1.5f) continue; // too small to see at this zoom
            var dir = new Vector2(Mathf.Cos((float)crater.BearingRad), Mathf.Sin((float)crater.BearingRad));
            var rim = centre + dir * radiusPx;
            if (rim.Length() - r > 2 * GetViewportRect().Size.Length()) continue; // off-screen
            DrawColoredPolygon(ClippedToDisk(rim, r, centre, radiusPx), pit);
            DrawColoredPolygon(ClippedToDisk(rim - dir * r * 0.15f, r * 0.55f, centre, radiusPx), floor);
        }
    }

    /// <summary>A circle's outline with every point outside the disk pulled back onto its edge.</summary>
    private static Vector2[] ClippedToDisk(Vector2 c, float r, Vector2 disk, float diskR)
    {
        var points = new Vector2[32];
        for (var i = 0; i < points.Length; i++)
        {
            var a = Mathf.Tau * i / points.Length;
            var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            var d = p - disk;
            points[i] = d.Length() > diskR ? disk + d.Normalized() * diskR : p;
        }
        return points;
    }

    /// <summary>The star of the system shown on the map: the one you're in (#54).</summary>
    private CelestialBodyState? MapStar() =>
        _sector.Bodies.FirstOrDefault(b => b.Id == _mapSystem)
        ?? (_player is { } p ? _sector.StarAt(p.Position) : _sector.Bodies.FirstOrDefault(b => b.Orbit is null));

    /// <summary>Shows another system's map (from the map panel), at a zoom that fits it.</summary>
    private void ViewSystem(BodyId star)
    {
        _mapSystem = star;
        _galaxyView = false;
        FitMapZoom();
    }

    private void FitMapZoom()
    {
        var star = MapStar();
        var extent = _sector.Bodies.Where(b => b.Kind != BodyKind.Star && b.Orbit is { } o && star is not null && o.Parent == star.Id)
            .Select(b => b.Orbit!.RadiusM + b.RadiusM).DefaultIfEmpty(100_000).Max() * 1.1;
        var screen = _uiRoot.GetViewportRect().Size;
        _pixelsPerMetre = Mathf.Clamp((float)(Mathf.Min(screen.X - 420, screen.Y) / 2 / extent), MinPixelsPerMetre, MaxPixelsPerMetre);
    }

    /// <summary>
    /// The galaxy view (#21): every known star system around the screen centre at its real direction
    /// and (scaled) distance, coloured by star type, with its warp gate links, what it holds, and how
    /// many of your craft are there.
    /// </summary>
    private void DrawGalaxy()
    {
        DrawRect(new Rect2(-GetViewportRect().Size, GetViewportRect().Size * 2), new Color(0.01f, 0.01f, 0.03f, 0.92f));
        var stars = _sector.Stars;
        if (stars.Count == 0) return;
        var centre = new Vector2D(stars.Average(s => _sector.GetPosition(s.Id).X), stars.Average(s => _sector.GetPosition(s.Id).Y));
        var far = stars.Max(s => _sector.GetPosition(s.Id).DistanceTo(centre));
        var size = GetViewportRect().Size;
        var ui = _uiRoot.Scale.X; // text follows the interface scale
        var font = (int)Mathf.Round(15 * ui);
        var scale = far > 0 ? 0.35 * Mathf.Min(size.X - 420 * ui, size.Y) / far : 0;
        Vector2 At(CelestialBodyState s)
        {
            var d = _sector.GetPosition(s.Id) - centre;
            return new Vector2((float)(d.X * scale), (float)(d.Y * scale)) - new Vector2(210 * ui, 0);
        }
        foreach (var gate in _sector.Crafts.Where(SectorState.IsGate))
        {
            if (_sector.Crafts.FirstOrDefault(c => c.Id == gate.GateLinkId) is not { } other) continue;
            if (_sector.StarAt(gate.Position) is { } a && _sector.StarAt(other.Position) is { } b && a.Id.Value < b.Id.Value)
                DrawLine(At(a), At(b), new Color(0.75f, 0.5f, 1f, 0.8f), 2);
        }
        var here = _player is { } me ? _sector.StarAt(me.Position)?.Id : null;
        foreach (var star in stars)
        {
            var at = At(star);
            var colour = star.StarClass switch { StarClass.Red => new Color(1f, 0.55f, 0.4f), StarClass.Blue => new Color(0.6f, 0.75f, 1f), _ => StarColor };
            DrawCircle(at, (star.Id == here ? 12 : 9) * ui, colour);
            if (star.Id == here) DrawArc(at, 18 * ui, 0, Mathf.Tau, 48, Colors.White, 1.5f * ui);
            var bodies = _sector.Bodies.Where(b => b.Id != star.Id && _sector.StarOf(b.Id)?.Id == star.Id).ToList();
            var craft = _sector.Crafts.Count(c => _sector.StarAt(c.Position)?.Id == star.Id);
            var type = star.StarClass switch { StarClass.Red => "red dwarf", StarClass.Blue => "blue star", _ => "yellow star" };
            var exotic = bodies.Any(b => b.DepositsKg.ContainsKey("exotic_ore")) ? ", exotic ore" : "";
            var lines = $"{star.Name}{(star.Id == here ? "  (you are here)" : "")}\n{type}, {bodies.Count} bodies{exotic}\n{craft} of your craft";
            var y = at.Y + 26 * ui;
            foreach (var line in lines.Split('\n'))
            {
                DrawString(ThemeDB.FallbackFont, new Vector2(at.X - 70 * ui, y), line, HorizontalAlignment.Left, -1, font, NameColor);
                y += font + 4 * ui;
            }
        }
        // The legend along the top, between the HUD on the left and the panel on the right.
        DrawString(ThemeDB.FallbackFont, new Vector2(-size.X / 2 + 560 * ui, -size.Y / 2 + 30 * ui),
            "GALAXY  (G: back to the system)   Purple: warp gate links.  Pick a system in the panel to see its map.",
            HorizontalAlignment.Left, -1, font, NameColor);
    }

    private static Vector2[] CirclePoints(Vector2 centre, float radius, int count)
    {
        var points = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            var angle = Mathf.Tau * i / count;
            points[i] = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }
        return points;
    }

    // Screen-fixed backdrop: drawn around the camera so it never scrolls away. No parallax yet.
    private void DrawStarfield()
    {
        // Fills the window at any size, at the same star density as the 1280×720 design.
        var origin = _camera.Position;
        var half = GetViewportRect().Size / 2;
        var count = (int)(StarCount * (half.X * half.Y) / (640f * 360f));
        var rng = new RandomNumberGenerator { Seed = StarfieldSeed };
        for (var i = 0; i < count; i++)
        {
            var position = origin + new Vector2(rng.RandfRange(-half.X, half.X), rng.RandfRange(-half.Y, half.Y));
            var brightness = rng.RandfRange(0.35f, 1.0f);
            var size = rng.Randf() < 0.1f ? 2f : 1f;
            DrawRect(new Rect2(position.Floor(), new Vector2(size, size)),
                new Color(brightness, brightness, brightness));
        }
    }
}
