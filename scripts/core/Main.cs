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
    private Label _hud = null!;
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
        _hud = new Label { Position = new Vector2(20, 16) };
        _uiRoot.AddChild(_hud);
        _settingsPanel = new SettingsPanel(_settings, () => { _settings.ApplyWindow(); _settings.Save(); LayoutUi(); }) { Visible = false };
        _uiRoot.AddChild(_settingsPanel);

        LoadDefinitions();
        if (_catalog != null)
        {
            _editor = new BlueprintEditorView(_catalog, _partColors, () => _player?.Design) { Visible = false };
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
                craft => { if (_player != null && craft != _player.Id) { _sector.SetTargetCraft(_player.Id, craft); ShowStatus($"Target: {_sector.GetCraft(craft).Name}."); } })
            { Visible = false };
            _uiRoot.AddChild(_mapPanel);
            _diagnosticsPanel = new DiagnosticsPanel(() => _sector, _warp, _tickCost) { Visible = false };
            _uiRoot.AddChild(_diagnosticsPanel);
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
        if (System.Array.IndexOf(args, "--open-job") >= 0) _jobPanel?.Open();
        if (System.Array.IndexOf(args, "--open-debug") >= 0) _debugPanel?.Open();
        if (System.Array.IndexOf(args, "--open-editor") >= 0) OpenEditor();
        if (System.Array.IndexOf(args, "--open-cargo") >= 0) _cargoPanel?.Open();
        if (System.Array.IndexOf(args, "--open-shipyard") >= 0) _shipyardPanel?.Open();
        if (System.Array.IndexOf(args, "--open-fleet") >= 0) _fleetPanel?.Open();
        if (System.Array.IndexOf(args, "--open-diagnostics") >= 0) _diagnosticsPanel?.Open();
        if (System.Array.IndexOf(args, "--open-settings") >= 0) _settingsPanel?.Open();
        if (System.Array.IndexOf(args, "--open-map") >= 0) ToggleMap();
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
            var catalog = PartCatalog.FromJson(Godot.FileAccess.GetFileAsString(PartsPath), materials, recipes);
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
        else if (Pressed(InputActions.Map)) ToggleMap();
        else if (Pressed(InputActions.Fullscreen)) ToggleFullscreen();
        else if (Pressed(InputActions.TimeFaster)) { _warp.Paused = false; _warp.Faster(); ShowStatus($"Time {_warp}."); }
        else if (Pressed(InputActions.TimeSlower)) { _warp.Paused = false; _warp.Slower(); ShowStatus($"Time {_warp}."); }
        else if (Pressed(InputActions.TimePause)) { _warp.Paused = !_warp.Paused; ShowStatus(_warp.Paused ? "Paused (P to resume)." : $"Time {_warp}."); }
        else if (Pressed(InputActions.ClosePanels))
        {
            if (_mapMode) ToggleMap();
            foreach (var panel in new Control?[] { _shipyardPanel, _fleetPanel, _cargoPanel, _jobPanel, _debugPanel, _diagnosticsPanel, _settingsPanel })
                if (panel != null) panel.Visible = false;
        }
        else if (_player == null) return;
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
        if (_mapMode && _sector.Bodies.FirstOrDefault(b => b.Orbit is null) is { } root) _viewCentre = _sector.GetPosition(root.Id);
        else if (_player != null) _viewCentre = InterpolatedPosition(_player);
        _hud.Text = BuildHud();
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

    private string BuildHud()
    {
        var text = "SpaceAutomationSandbox (working title) - Phase 4\n"
                   + $"seed {_sector.Seed}   tick {_sector.Tick} ({FixedStepClock.TicksPerSecond}/s)   t={_sector.TimeS:0.0}s"

                   + $"   zoom {_pixelsPerMetre:0.#####} px/m\n";

        if (Time.GetTicksMsec() < _statusUntilMs) text += $">> {_status}\n";
        if (_warp.Speed != 1 || _warp.Paused) text += $"TIME {_warp}   ([ slower, ] faster, P pause)\n";
        if (!_sector.Tuning.IsDefault) text += $"DEBUG: {_sector.Tuning}  (F3)\n";
        if (_player is not { } p) return text + _setupError;

        var (nearest, nearestSurface) = _sector.NearestBody(p.Position);
        var headingDeg = Mathf.PosMod(Mathf.RadToDeg((float)p.HeadingRad), 360f);
        text += $"Piloting {p.Name} ({p.Id}) - {p.Design.Name}: {p.Stats.TotalMassKg / 1000:0.0} t, {p.Stats.TotalThrustN / 1000:0} kN, "
                + $"{p.Stats.TotalThrustN / p.MassKg:0.0} m/s²   "
                + (p.HasCargoLimit ? $"cargo {p.CargoKg / 1000.0:0.0} / {p.CargoCapacityKg / 1000.0:0} t\n" : $"stores {p.CargoKg / 1000.0:0.0} t\n")
                + (p.Kits.Count > 0
                    ? $"KIT on board: {p.Kits[0].Name} ({p.Kits[0].MassKg / 1000.0:0.0} t){(p.Kits.Count > 1 ? $" +{p.Kits.Count - 1} more" : "")} - "
                      + (_sector.DeployBlocker(p.Id) is { } blocked ? blocked : "U to deploy here") + "\n"
                    : "")
                + $"speed {p.SpeedMps:0.0} m/s (sector)   heading {headingDeg:000}°   "
                + $"pos ({p.Position.X / 1000:0.00}, {p.Position.Y / 1000:0.00}) km\n"
                + (SectorState.PowerStatus(p) is { Length: > 0 } power ? power + "\n" : "")
                + (SectorState.CrewStatus(p) is { Length: > 0 } crew ? crew + "\n" : "")
                + (p.PartHealth.Count > 0 || p.PartsLost > 0
                    ? $"DAMAGE: {p.PartHealth.Count} part(s) damaged"
                      + (p.PartHealth.Count > 0 ? $" (worst {p.PartHealth.Values.Min() * 100:0}%)" : "")
                      + (p.PartsLost > 0 ? $", {p.PartsLost} broken off" : "") + " - no repairs yet\n"
                    : "")
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
                        : $"On a fixed orbit around {_sector.GetBody(rail.Parent).Name} (no thrusters)\n")
                    : "")
                + ShipyardSummary();

        if (!p.TargetCraftId.IsNone)
        {
            var target = _sector.GetCraft(p.TargetCraftId);
            var rel = _sector.GetRelativeMotionToCraft(p.Id, target.Id);
            var ports = target.Stats.DockingPorts.Count;
            text += $"target: {target.Name} ({target.Id}{(ports > 0 ? $", {_sector.FreePorts(target).Count}/{ports} ports free" : "")})   "
                    + $"gap {FormatDistance(rel.SurfaceDistanceM)}   rel speed {rel.RelativeSpeedMps:0.0} m/s   "
                    + $"{(rel.ClosingSpeedMps >= 0 ? "closing" : "opening")} {System.Math.Abs(rel.ClosingSpeedMps):0.0} m/s"
                    + (rel.RelativeSpeedMps < SectorState.DockMaxRelativeSpeedMps && ports > 0 && !p.IsDocked ? "   [K to dock if within 100 m]" : "")
                    + "\n";
        }
        else if (p.TargetBodyId.IsNone)
        {
            text += "target: none - brake stops relative to the sector\n";
        }
        else
        {
            var target = _sector.GetBody(p.TargetBodyId);
            var rel = _sector.GetRelativeMotion(p.Id, target.Id);
            text += $"target: {Describe(target)}   surface {FormatDistance(rel.SurfaceDistanceM)}   "
                    + $"rel speed {rel.RelativeSpeedMps:0.0} m/s   "
                    + $"{(rel.ClosingSpeedMps >= 0 ? "closing" : "opening")} {System.Math.Abs(rel.ClosingSpeedMps):0.0} m/s"
                    + (rel.RelativeSpeedMps < 0.5 ? "   [MATCHED]" : "") + "\n";
        }

        return text
               + $"nearest: {Describe(nearest)}   surface {FormatDistance(nearestSurface)}\n"
               + "W/Up thrust   X reverse   Q/E strafe   V cruise   M map   A/D turn   S/Space brake (matches target)   G anchor   K dock   U deploy kit   Tab cycle target   T target nearest   wheel zoom\n"
               + "B blueprint editor   Y shipyard   F fleet   C cargo   J job   [ ] P time   F2 settings   F3 debug   F4 diagnostics   F6 save   F9 load   F7 new game   (closing the window autosaves)";
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
        return string.Concat(yards.Select(y => (y.BuildQueue.Count == 0
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
            : body.DepositsKg.Count == 0 ? ", mined out"
            : ", " + string.Join(", ", body.DepositsKg.Select(kv => $"{kv.Key} {kv.Value / 1000.0:0} t")))
        + ")";

    private static string FormatDistance(double metres) =>
        System.Math.Abs(metres) >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";

    public override void _Draw()
    {
        DrawStarfield();

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
        var orbitPx = body.Orbit is { } o ? (float)o.RadiusM * _pixelsPerMetre : 0f;
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
            BodyKind.Star => StarColor,
            BodyKind.Planet => PlanetColor,
            BodyKind.Moon => MoonColor,
            BodyKind.GasGiant => GasGiantColor,
            _ => body.DepositsKg.ContainsKey("ice") ? IceColor : AsteroidColor,
        };
        if (radiusPx < 200f) DrawCircle(centre, radiusPx, color);
        else DrawColoredPolygon(CirclePoints(centre, radiusPx, 512), color); // smoother edge up close

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
            var extent = _sector.Bodies.Where(b => b.Orbit is not null && _sector.GetBody(b.Orbit.Parent).Orbit is null)
                .Select(b => b.Orbit!.RadiusM + b.RadiusM).DefaultIfEmpty(100_000).Max() * 1.1;
            var screen = _uiRoot.GetViewportRect().Size;
            _pixelsPerMetre = Mathf.Clamp((float)(Mathf.Min(screen.X - 420, screen.Y) / 2 / extent), MinPixelsPerMetre, MaxPixelsPerMetre);
            _mapPanel?.Open();
        }
        else
        {
            _pixelsPerMetre = _zoomBeforeMap;
            if (_mapPanel != null) _mapPanel.Visible = false;
        }
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
