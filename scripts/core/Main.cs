using System.Collections.Generic;
using System.Linq;
using Godot;
using SpaceAutomationSandbox.Core;
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
    /// <summary>The single save from before slots; moved into <see cref="SavesFolder"/> on first run (#60).</summary>
    private const string LegacySavePath = "user://savegame.json";
    private const string SavesPath = "user://saves";
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

    /// <summary>Rebinding (#63), opened from Settings.</summary>
    private ControlsPanel? _controls;
    private BlueprintEditorView? _editor;
    private ShipyardPanel? _shipyardPanel;
    private FleetPanel? _fleetPanel;
    private CargoPanel? _cargoPanel;
    private JobPanel? _jobPanel;
    private DebugPanel? _debugPanel;
    private DiagnosticsPanel? _diagnosticsPanel;
    private ResearchPanel? _researchPanel;

    /// <summary>The title screen and main menu (#58). While it shows, no game is running.</summary>
    private TitleScreen? _title;

    private bool InMenu => _title is { Visible: true };

    /// <summary>Sound and music (#64).</summary>
    private AudioDirector _audio = null!;

    // Debug game speed: how many seconds of game time pass per real second. View-only (results don't change).

    private bool EditorOpen => _editor is { Visible: true };

    public override void _Ready()
    {
        InputActions.EnsureDefaults(); // before any input is read
        InputActions.Apply(_settings.Bindings); // the player's own controls (#63)
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
        _audio = new AudioDirector(_settings);
        AddChild(_audio);
        ApplyTheme(); // text size, focus highlight, colours (#68)
        _settingsPanel = new SettingsPanel(_settings, () => { _settings.ApplyWindow(); _settings.Save(); LayoutUi(); _audio.ApplySettings(); ApplyTheme(); }) { Visible = false };
        _uiRoot.AddChild(_settingsPanel);
        _controls = new ControlsPanel(_settings, () => { _settings.Save(); _hud.RefreshHint(); });
        _uiRoot.AddChild(_controls);
        _settingsPanel.ControlsChosen += () =>
        {
            _settingsPanel.Visible = false;
            _uiRoot.MoveChild(_controls, _uiRoot.GetChildCount() - 1);
            _controls.Open();
        };
        _controls.Closed += () => // back to Settings
        {
            _settingsPanel.Open();
            if (InMenu || _pause.Visible) EnterPanelNav(_settingsPanel, fromMenu: true);
        };

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
        BuildMenus();
        if (SaveStore.MigrateLegacy(ProjectSettings.GlobalizePath(LegacySavePath), SavesFolder) is { } migrated)
            GD.Print($"[Save] moved the old save into the save folder: {migrated}");
        LayoutUi();
        // The title screen comes first (#58). Command-line runs (dev hooks, screenshots) skip it and start
        // as before, unless --title asks for it.
        var startArgs = OS.GetCmdlineUserArgs();
        if (_catalog != null && (startArgs.Length == 0 || System.Array.IndexOf(startArgs, "--title") >= 0))
        {
            ShowTitle();
            // Dev/test: choose from the menu without clicking.
            if (System.Array.IndexOf(startArgs, "--title-pick=new") >= 0) LeaveTitle(StartNewGame);
            if (System.Array.IndexOf(startArgs, "--title-pick=setup") >= 0) { _uiRoot.MoveChild(_newGame, _uiRoot.GetChildCount() - 1); _newGame.Open(); } // #62 check
            if (System.Array.IndexOf(startArgs, "--title-pick=continue") >= 0) ContinueFromTitle();
            return;
        }
        // Continue where the player left off; a new game only if there's no usable save.
        if (_catalog == null || !LoadNewest()) StartNewGame();

        GD.Print($"[Bootstrap] Main scene ready. C# assembly loaded. Godot {Engine.GetVersionInfo()["string"].AsString()}");
        GD.Print($"[Sector] seed={_sector.Seed} tick={_sector.Tick} bodies={_sector.Bodies.Count} tickRate={FixedStepClock.TicksPerSecond}/s");
        if (_player is { } p)
            GD.Print($"[Craft] {p.Id} '{p.Design.Name}' cells={p.Stats.CellCount} mass={p.Stats.TotalMassKg}kg thrust={p.Stats.TotalThrustN}N");
        GD.Print($"[Save] {SavesFolder} {(_status.Length > 0 ? "- " + _status : "")}");

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
        if (System.Array.IndexOf(args, "--open-controls") >= 0) { _uiRoot.MoveChild(_controls!, _uiRoot.GetChildCount() - 1); _controls!.Open(); }
        if (System.Array.IndexOf(args, "--open-map") >= 0) ToggleMap();
        if (System.Array.IndexOf(args, "--open-research") >= 0) _researchPanel?.Open();
        if (System.Array.IndexOf(args, "--open-pause") >= 0) OpenPause();
        if (System.Array.IndexOf(args, "--open-saves=save") >= 0) { OpenPause(); _pause.Visible = false; _saveScreen.Open(SaveLoadScreen.ScreenMode.Save); }
        if (System.Array.IndexOf(args, "--open-saves=load") >= 0) { OpenPause(); _pause.Visible = false; _saveScreen.Open(SaveLoadScreen.ScreenMode.Load); }
        if (System.Array.IndexOf(args, "--open-galaxy") >= 0) { if (!_mapMode) ToggleMap(); _galaxyView = true; }
        if (System.Array.IndexOf(args, "--open-help") >= 0) _hud.ToggleHelp();
        // #68 checks: use the open panel with keys/controller; text size and colours for this run only (not saved).
        if (System.Array.IndexOf(args, "--panel-nav") >= 0 && SidePanels.Where(p => p is { Visible: true }).OrderByDescending(p => p!.GetIndex()).FirstOrDefault() is { } navPanel)
            EnterPanelNav(navPanel, fromMenu: false);
        if (args.FirstOrDefault(a => a.StartsWith("--text-scale=")) is { } textArg && float.TryParse(textArg["--text-scale=".Length..], System.Globalization.CultureInfo.InvariantCulture, out var textScale))
        {
            _settings.TextScale = textScale;
            ApplyTheme();
        }
        if (System.Array.IndexOf(args, "--colorblind") >= 0) { _settings.ColorblindSafe = true; ApplyTheme(); }
        SetUpStress(args); // #39 performance check
        if (System.Array.IndexOf(args, "--goals") >= 0) { _goalsOn = true; _goalsDone.Add("survey"); } // #65 check: one goal done
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

    private static string SavesFolder => ProjectSettings.GlobalizePath(SavesPath);

    // --- Menus and saves (#59, #60, #61) ---
    private PauseMenu _pause = null!;
    private SaveLoadScreen _saveScreen = null!;
    private ConfirmDialog _confirm = null!;

    /// <summary>Real seconds of play in this game (saved in the header); runs only while the game does.</summary>
    private double _playTimeS;

    private double _sinceAutosaveS;

    /// <summary>The tick of the last save or load, and whether anything was done since that doesn't move time.</summary>
    private long _savedTick = -1;
    private bool _changedSinceSave;

    /// <summary>Unsaved progress: time has run, or the player did something, since the last save or load.</summary>
    private bool HasUnsavedChanges => _player != null && !InMenu && (_sector.Tick != _savedTick || _changedSinceSave);

    /// <summary>The pause menu, the save screen or a question is showing: the game waits.</summary>
    private bool ModalOpen => _loadingScreen is { Visible: true } || _pause is { Visible: true } || _saveScreen is { Visible: true } || _confirm is { Visible: true } || _controls is { Visible: true };

    private bool _pausedBeforeMenu;

    /// <summary>The game's name (#62): names its saves; kept in the save header.</summary>
    private string _gameName = "New game";

    private NewGameScreen _newGame = null!;

    /// <summary>Shown while a save loads (#67).</summary>
    private LoadingScreen _loadingScreen = null!;

    private void BuildMenus()
    {
        _confirm = new ConfirmDialog();
        _saveScreen = new SaveLoadScreen(() => SavesFolder, (question, choices) => _confirm.Ask(question, choices));
        _saveScreen.DefaultName = () => _gameName;
        _saveScreen.SaveChosen += (path, name) =>
        {
            if (SaveTo(path, name)) _saveScreen.ShowMessage($"Saved \"{name}\".");
            _saveScreen.Refresh();
        };
        _saveScreen.LoadChosen += path => AskIfUnsaved("Load this save? Progress since your last save will be lost.", () =>
        {
            LoadWithScreen(path, "save", ok =>
            {
                if (!ok) { _saveScreen.ShowMessage(_status); return; }
                _saveScreen.Visible = false;
                if (InMenu) LeaveTitle(() => { });
                else ResumeFromPause();
            });
        });
        _saveScreen.Closed += () => { if (InMenu) ShowTitle(); else OpenPauseAgain(); };
        _newGame = new NewGameScreen();
        _newGame.StartChosen += (name, seed, goals) => LeaveTitle(() => { StartNewGame(seed, name); _goalsOn = goals; });
        _newGame.BackChosen += ShowTitle;
        _pause = new PauseMenu();
        _pause.ResumeChosen += ResumeFromPause;
        _pause.SaveChosen += () => { _pause.Visible = false; _saveScreen.Open(SaveLoadScreen.ScreenMode.Save); };
        _pause.LoadChosen += () => { _pause.Visible = false; _saveScreen.Open(SaveLoadScreen.ScreenMode.Load); };
        _pause.SettingsChosen += () => { _settingsPanel!.Open(); EnterPanelNav(_settingsPanel, fromMenu: true); };
        _pause.HelpChosen += () => { ResumeFromPause(); _hud.ToggleHelp(); };
        _pause.GoalsToggled += () => { _goalsOn = !_goalsOn; _changedSinceSave = true; ResumeFromPause(); }; // #65
        _pause.GoalsRestarted += () => _confirm.Ask("Restart the goal list from the first goal?", ("Restart", () =>
        {
            _goalsDone.Clear();
            _goalsOn = true;
            _changedSinceSave = true;
            ResumeFromPause();
        }));
        _pause.QuitToMenuChosen += () => AskToQuit("Quit to the main menu?", ReturnToTitle);
        _pause.QuitToDesktopChosen += () => AskToQuit("Quit the game?", () => GetTree().Quit());
        // Above everything else: pause menu, then the save screen, then questions on top.
        _uiRoot.AddChild(_pause);
        _uiRoot.AddChild(_saveScreen);
        _uiRoot.AddChild(_newGame);
        if (_settingsPanel != null) _uiRoot.MoveChild(_settingsPanel, _uiRoot.GetChildCount() - 1);
        _uiRoot.AddChild(_confirm);
        _loadingScreen = new LoadingScreen(); // over everything (#67)
        _uiRoot.AddChild(_loadingScreen);
        // Any button pressed in the game's own panels counts as a change to save (not in the menus themselves).
        GetTree().NodeAdded += node =>
        {
            if (node is BaseButton button) button.Pressed += () => { if (!IsMenuControl(button)) _changedSinceSave = true; };
        };
    }

    private static bool IsMenuControl(Node node)
    {
        for (var n = node; n != null; n = n.GetParent())
            if (n is PauseMenu or SaveLoadScreen or ConfirmDialog or TitleScreen or SettingsPanel or NewGameScreen or ControlsPanel) return true;
        return false;
    }

    private void OpenPause()
    {
        _pausedBeforeMenu = _warp.Paused;
        _warp.Paused = true;
        _pause.Open(_sector is null ? "" : $"{_gameName}   ·   seed {_sector.Seed}   ·   played {(int)(_playTimeS / 3600)} h {(int)(_playTimeS / 60) % 60} min", _goalsOn);
    }

    /// <summary>Back to the pause menu from a screen opened from it (the game stays paused as it was).</summary>
    private void OpenPauseAgain()
    {
        var before = _pausedBeforeMenu;
        OpenPause();
        _pausedBeforeMenu = before;
    }

    private void ResumeFromPause()
    {
        _pause.Visible = false;
        _saveScreen.Visible = false;
        if (_settingsPanel != null) _settingsPanel.Visible = false;
        _warp.Paused = _pausedBeforeMenu;
    }

    /// <summary>Runs <paramref name="then"/>, asking first if there's unsaved progress (#61).</summary>
    private void AskIfUnsaved(string question, System.Action then)
    {
        if (!HasUnsavedChanges) { then(); return; }
        _confirm.Ask(question, ("Continue", then));
    }

    /// <summary>Quitting: asks (with "Save and quit") only when there's unsaved progress.</summary>
    private void AskToQuit(string question, System.Action quit)
    {
        if (!HasUnsavedChanges) { quit(); return; }
        _confirm.Ask(question + " You have unsaved progress.",
            ("Save and quit", () => { if (SaveTo(SaveStore.NewManualPath(SavesFolder, System.DateTime.UtcNow), $"Saved on quitting {System.DateTime.Now:yyyy-MM-dd HH:mm}")) quit(); }),
            ("Quit without saving", quit));
    }

    /// <summary>Back to the title screen: the game is let go (no sector, no craft, no panels).</summary>
    private void ReturnToTitle()
    {
        ResumeFromPause();
        _warp.Paused = false;
        _player = null;
        _sector = null!; // released: nothing reads it while the title screen shows
        _mapMode = false;
        _galaxyView = false;
        _hud.CloseHelp();
        ShowTitle();
    }

    /// <summary>Saves the game to a slot (#60). Returns false (with the reason in the status) if it failed.</summary>
    private bool SaveTo(string path, string name)
    {
        if (_player == null) return false;
        try
        {
            var header = new SaveHeader(name, System.DateTime.UtcNow.ToString("O"), _playTimeS, _sector.Tick, _sector.Seed, _gameName, _goalsOn, _goalsDone.ToArray());
            SaveStore.Write(path, SaveGameSerializer.ToJson(_sector, _player.Id, header));
            _savedTick = _sector.Tick;
            _changedSinceSave = false;
            ShowStatus($"Saved \"{name}\" at tick {_sector.Tick}.");
            return true;
        }
        catch (System.Exception e)
        {
            ShowStatus($"SAVE FAILED: {e.Message}");
            GD.PushError(_status);
            return false;
        }
    }

    /// <summary>Loads a slot (#60). On failure the current game (if any) is unchanged and the save is left where it is.</summary>
    private bool LoadFrom(string path)
    {
        if (_catalog == null) return false;
        try
        {
            return ApplyLoaded(SaveGameSerializer.FromJson(System.IO.File.ReadAllText(path), _catalog), path);
        }
        catch (System.Exception e)
        {
            ShowStatus($"LOAD FAILED ({System.IO.Path.GetFileName(path)}): {e.Message}");
            GD.PushError(_status);
            return false;
        }
    }

    /// <summary>
    /// Loads a slot with the loading screen (#67): the file is read and parsed on a worker thread (the
    /// simulation and save code don't touch Godot), then the game switches over here on the main thread.
    /// <paramref name="done"/> gets true on success; on failure the current game is unchanged.
    /// </summary>
    private async void LoadWithScreen(string path, string what, System.Action<bool> done)
    {
        if (_catalog == null || _loadingScreen.Visible) return;
        var catalog = _catalog;
        _loadingScreen.Begin(what);
        try
        {
            var loaded = await System.Threading.Tasks.Task.Run(() => SaveGameSerializer.FromJson(System.IO.File.ReadAllText(path), catalog));
            _loadingScreen.End();
            done(ApplyLoaded(loaded, path));
        }
        catch (System.Exception e)
        {
            _loadingScreen.End();
            ShowStatus($"LOAD FAILED ({System.IO.Path.GetFileName(path)}): {e.Message}");
            GD.PushError(_status);
            done(false);
        }
    }

    private bool ApplyLoaded(LoadedGame loaded, string path)
    {
        {
            _sector = loaded.Sector;
            _player = loaded.PlayerCraftId.IsNone ? null : _sector.GetCraft(loaded.PlayerCraftId);
            _playTimeS = loaded.Header?.PlayTimeS ?? 0;
            _gameName = loaded.Header is { GameName.Length: > 0 } h ? h.GameName : loaded.Header?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path);
            _goalsOn = loaded.Header?.GoalsOn ?? false; // #65
            _goalsDone.Clear();
            foreach (var id in loaded.Header?.GoalsDone ?? System.Array.Empty<string>()) _goalsDone.Add(id);
            _savedTick = _sector.Tick;
            _changedSinceSave = false;
            _sinceAutosaveS = 0;
            ShowStatus($"Loaded \"{loaded.Header?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path)}\" at tick {_sector.Tick}.");
            return true;
        }
    }

    /// <summary>Loads the newest good save (Continue, and command-line starts). False if there's none or it fails.</summary>
    private bool LoadNewest() => SaveStore.Newest(SavesFolder) is { } newest && LoadFrom(newest.Path);

    private void Autosave(string name)
    {
        if (_player == null || InMenu) return;
        SaveTo(SaveStore.NextAutosavePath(SavesFolder), name);
        _sinceAutosaveS = 0;
    }

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

    /// <summary>Shows the title screen: no game runs behind it (the HUD and panels are hidden).</summary>
    /// <summary>Continue: the newest save, with the loading screen (#67); on failure the menu stays and says why.</summary>
    private void ContinueFromTitle()
    {
        if (SaveStore.Newest(SavesFolder) is not { } newest) { _title!.Open(SaveSummary(), "No save to continue."); return; }
        LoadWithScreen(newest.Path, newest.Name, ok =>
        {
            if (ok) LeaveTitle(() => { });
            else _title!.Open(SaveSummary(), _status);
        });
    }

    private void ShowTitle()
    {
        if (_title == null)
        {
            _title = new TitleScreen();
            _title.ContinueChosen += ContinueFromTitle;
            _title.LoadChosen += () => { _title.Visible = false; _saveScreen.Open(SaveLoadScreen.ScreenMode.Load); _title.Visible = true; _uiRoot.MoveChild(_saveScreen, _uiRoot.GetChildCount() - 1); _uiRoot.MoveChild(_confirm, _uiRoot.GetChildCount() - 1); };
            _title.NewGameChosen += () => { _uiRoot.MoveChild(_newGame, _uiRoot.GetChildCount() - 1); _newGame.Open(); }; // name and seed first (#62)
            _title.SettingsChosen += () => { _settingsPanel!.Open(); EnterPanelNav(_settingsPanel, fromMenu: true); };
            if (_settingsPanel != null) _settingsPanel.Closed += () => { if (InMenu) ShowTitle(); }; // focus back on the menu
            _title.QuitChosen += () => GetTree().Quit();
            _uiRoot.AddChild(_title);
            _uiRoot.MoveChild(_title, _uiRoot.GetChildCount() - 1);
            if (_settingsPanel != null) _uiRoot.MoveChild(_settingsPanel, _uiRoot.GetChildCount() - 1); // settings open above the menu
        }
        _hud.Visible = false;
        foreach (var panel in new Control?[] { _shipyardPanel, _fleetPanel, _cargoPanel, _jobPanel, _debugPanel, _diagnosticsPanel, _researchPanel, _mapPanel })
            if (panel != null) panel.Visible = false;
        _title.Open(SaveSummary());
    }

    /// <summary>What Continue would load, for the title screen; null if there's no save.</summary>
    private static string? SaveSummary() =>
        SaveStore.Newest(SavesFolder) is { } newest ? $"{newest.Name}, saved {newest.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}" : null;

    /// <summary>Starts (or loads) a game from the menu and hides it.</summary>
    private void LeaveTitle(System.Action start)
    {
        start();
        if (_title != null) _title.Visible = false;
        if (_settingsPanel != null) _settingsPanel.Visible = false;
        _hud.Visible = true;
        _mapMode = false;
        GD.Print($"[Sector] seed={_sector.Seed} tick={_sector.Tick} bodies={_sector.Bodies.Count} (from the main menu)");
    }

    /// <summary>A new game from a seed (#62); the default seed for command-line starts.</summary>
    private void StartNewGame() => StartNewGame(NewGame.DefaultSeed, "New game");

    private void StartNewGame(ulong seed, string name)
    {
        _gameName = name;
        _goalsOn = false;
        _goalsDone.Clear();
        _player = null;
        _playTimeS = 0;
        _sinceAutosaveS = 0;
        _changedSinceSave = false;
        if (_catalog == null || _starterDesign == null || _starterShipyard == null)
        {
            _sector = SectorGenerator.Generate(NewGame.DefaultSeed); // something to look at while the error shows
            return;
        }

        var game = NewGame.Create(seed, _catalog, _starterDesign, _starterShipyard);
        _sector = game.Sector;
        _player = _sector.GetCraft(game.PlayerCraftId);
        _savedTick = -1; // a new game is unsaved until it's saved
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
        // Sound follows the window to the background and back (#64).
        if (what == NotificationApplicationFocusOut) { _audio?.SetFocused(false); return; }
        if (what == NotificationApplicationFocusIn) { _audio?.SetFocused(true); return; }
        if (what != NotificationWMCloseRequest) return;
        if (!InMenu) Autosave($"{_gameName} (autosave on closing)"); // nothing to save from the title screen
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
        if (PanelNavInput(@event)) // using a panel with keys or a controller (#68)
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (InMenu)
        {
            // The menu takes the keys (its buttons have focus); Esc closes Settings opened from it.
            if (Pressed(InputActions.ClosePanels) && _settingsPanel is { Visible: true })
            {
                _settingsPanel.Visible = false;
                ShowTitle();
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        // Named actions (InputActions), so bindings can change; Echo is ignored for one-shot actions.
        bool Pressed(string action) => @event.IsActionPressed(action, allowEcho: false);

        if (ModalOpen)
        {
            // The pause menu, save screen and questions have focus; Esc (or Start) on the pause menu resumes.
            if ((Pressed(InputActions.ClosePanels) || @event.IsActionPressed("ui_cancel")) && _pause.Visible && !_confirm.Visible && !_saveScreen.Visible)
            {
                if (_settingsPanel is { Visible: true }) _settingsPanel.Visible = false;
                else ResumeFromPause();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

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
        else if (Pressed(InputActions.QuickSave)) SaveTo(SaveStore.QuickPath(SavesFolder), $"{_gameName} (quicksave)");
        else if (Pressed(InputActions.QuickLoad))
        {
            var quick = SaveStore.QuickPath(SavesFolder);
            if (!System.IO.File.Exists(quick)) ShowStatus($"No quicksave yet ({InputActions.Key1(InputActions.QuickSave)} makes one).");
            else AskIfUnsaved("Load the quicksave? Progress since your last save will be lost.", () => LoadWithScreen(quick, "quicksave", _ => { }));
        }
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
        else if (Pressed(InputActions.TimePause)) { _warp.Paused = !_warp.Paused; ShowStatus(_warp.Paused ? $"Paused ({InputActions.Key1(InputActions.TimePause)} to resume)." : $"Time {_warp}."); }
        else if (Pressed(InputActions.ClosePanels))
        {
            // Esc closes what's open (map, help, panels); with nothing open it's the pause menu (#59).
            var panels = new Control?[] { _shipyardPanel, _fleetPanel, _cargoPanel, _jobPanel, _debugPanel, _diagnosticsPanel, _settingsPanel, _researchPanel };
            var anyOpen = _mapMode || _hud.HelpOpen || panels.Any(p => p is { Visible: true });
            if (!anyOpen) OpenPause();
            if (_mapMode) ToggleMap();
            _hud.CloseHelp();
            foreach (var panel in panels)
                if (panel != null) panel.Visible = false;
        }
        else if (_player == null) return;
        else if (Pressed(InputActions.Jump)) { ShowStatus(_sector.RequestJump(_player.Id).Message); _changedSinceSave = true; }
        else if (_mapMode && Pressed(InputActions.Anchor)) _galaxyView = !_galaxyView; // in the map, G is the galaxy view (#21)
        else if (Pressed(InputActions.Dock) && (_changedSinceSave = true))
            ShowStatus((_player.IsDocked ? _sector.TryUndock(_player.Id) : _sector.TryDock(_player.Id)).Message);
        else if (Pressed(InputActions.Cruise) && (_changedSinceSave = true))
            ShowStatus((_player.Cruising ? _sector.TryStopCruise(_player.Id) : _sector.TryStartCruise(_player.Id)).Message);
        else if (Pressed(InputActions.Deploy) && (_changedSinceSave = true))
            ShowStatus(_player.Kits.FirstOrDefault() is { } kit ? _sector.TryDeployKit(_player.Id, kit.Id).Message : "No construction kit on board.");
        else if (Pressed(InputActions.Anchor) && (_changedSinceSave = true))
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
        var audioStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _audio.Watch(InMenu ? null : _sector, InMenu ? null : _player, delta); // ship sounds; music plays throughout (#64)
        _audioCost.Add(System.Diagnostics.Stopwatch.GetElapsedTime(audioStart).TotalMilliseconds);
        UpdatePanelNav();
        StressFrame(delta);
        UpdateGoals(delta);
        if (InMenu)
        {
            QueueRedraw(); // just the starfield
            DevScreenshot();
            return;
        }
        // Input is sampled once per frame and held for every tick run this frame.
        // No flying while the editor is open (typing a name must not fire thrusters).
        // A ship running a job is flown by it; the keyboard only flies ships without one.
        // Typing into a text/number box (e.g. the debug menu) mustn't fly the ship either.
        var typing = GetViewport().GuiGetFocusOwner() is LineEdit;
        if (_player is { IsAutomated: false }) _player.Controls = EditorOpen || typing || PanelNavActive || _controls is { Visible: true } ? ShipControls.Idle : ReadControls();

        if (!ModalOpen && !_warp.Paused)
        {
            _playTimeS += delta;
            _sinceAutosaveS += delta;
            if (_settings.AutosaveMinutes > 0 && _sinceAutosaveS >= _settings.AutosaveMinutes * 60) Autosave($"{_gameName} (autosave)"); // #60
        }

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
        var hudStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _hud.Show(BuildHud());
        _hudCost.Add(System.Diagnostics.Stopwatch.GetElapsedTime(hudStart).TotalMilliseconds);
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
        if (_warp.Speed != 1 || _warp.Paused) status += $"TIME {_warp}   ({InputActions.Key1(InputActions.TimeSlower)} slower, {InputActions.Key1(InputActions.TimeFaster)} faster, {InputActions.Key1(InputActions.TimePause)} pause)\n";
        if (!_sector.Tuning.IsDefault) status += $"DEBUG: {_sector.Tuning}  (F3)\n";
        if (_player is not { } p) return new HudSections(status + _setupError, "", ShipyardSummary(), "", GoalsText());

        var headingDeg = Mathf.PosMod(Mathf.RadToDeg((float)p.HeadingRad), 360f);
        var ship = $"{p.Name} - {p.Design.Name}: {p.SpeedMps:0.0} m/s, heading {headingDeg:000}°, "
                   + (p.HasCargoLimit ? $"cargo {p.CargoKg / 1000.0:0.0} / {p.CargoCapacityKg / 1000.0:0} t\n" : $"stores {p.CargoKg / 1000.0:0.0} t\n")
                   + $"{p.Stats.TotalMassKg / 1000:0.0} t, {p.Stats.TotalThrustN / 1000:0} kN, {p.Stats.TotalThrustN / p.MassKg:0.0} m/s²\n"
                   + (p.Kits.Count > 0
                       ? $"KIT on board: {p.Kits[0].Name} ({p.Kits[0].MassKg / 1000.0:0.0} t){(p.Kits.Count > 1 ? $" +{p.Kits.Count - 1} more" : "")} - "
                         + (_sector.DeployBlocker(p.Id) is { } blocked ? blocked : $"{InputActions.Key1(InputActions.Deploy)} to deploy here") + "\n"
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
                   + (p.GateStatus is { } gateNow ? $"WARP: {gateNow}{(p.JumpRequestTick > 0 ? $" - {InputActions.Key1(InputActions.Jump)} to cancel" : "")}\n"
                       : p.IsDocked && SectorState.IsGate(_sector.GetCraft(p.DockedToCraftId)) ? $"WARP GATE {_sector.GetCraft(p.DockedToCraftId).Name} - {InputActions.Key1(InputActions.Jump)} to jump\n" : "")
                   + (_sector.Stars.Count > 1 && _sector.StarAt(p.Position) is { } here ? $"system: {here.Name}\n" : "")
                   + (!p.IsLanded && !p.IsDocked && !p.IsOnRails && !p.Cruising && _sector.GravitySource(p.Position) is { } pull
                       ? $"gravity {_sector.GravityAt(p.Position, _sector.TimeS).Length:0.00} m/s² from {pull.Name} - land under {SectorState.SafeImpactSpeedMps:0} m/s\n"
                       : "")
                   + (p.Cruising
                       ? $"CRUISE {SectorState.CruiseSpeedMps:0} m/s - fuel {p.Inventory[SectorState.FuelMaterial] / 1000.0:0.00} t "
                         + $"({p.Inventory[SectorState.FuelMaterial] / System.Math.Max(SectorState.CruiseFuelKgPerS(p), 1e-9) / 60:0} min left), V or brake to drop out\n"
                       : p.CruiseStopReason is { } why && p.Stats.TotalThrustN > 0 ? $"cruise ended: {why} - {InputActions.Key1(InputActions.Cruise)} to cruise again\n" : "")
                   + (p.Job is { Active: true } job
                       ? $"AUTOPILOT ({job.Kind} job): {job.Status}   cycles {job.CompletedCycles}, delivered {job.DeliveredKg / 1000.0:0.0} t - {InputActions.Key1(InputActions.Job)} to view/stop\n"
                       : "")
                   + (_sector.MiningMaterial(p) is { } mining && p.FreeCargoKg > 0
                       ? $"MINING {mining}: {p.Stats.MiningRateKgPerS:0} kg/s, {_sector.GetBody(p.LandedOnBodyId).Deposit(mining) / 1000.0:0.0} t left in {_sector.GetBody(p.LandedOnBodyId).Name}\n"
                       : "")
                   + (p.IsLanded ? LandedLine(p) : "")
                   + (p.IsDocked
                       ? $"DOCKED at {_sector.GetCraft(p.DockedToCraftId).Name}, port {p.DockPortIndex + 1} - {InputActions.Key1(InputActions.Cargo)} cargo, {InputActions.Key1(InputActions.Dock)} or thrust to undock\n"
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
                     + (rel.RelativeSpeedMps < SectorState.DockMaxRelativeSpeedMps && ports > 0 && !p.IsDocked ? $"   [{InputActions.Key1(InputActions.Dock)} to dock if within 100 m]" : "")
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
        // The site below you when you're close to a body's surface (#70).
        if (nearestSurface < SectorState.SurveyRangeM && SiteLine(p, nearest) is { Length: > 0 } siteLine) target += $"\n{siteLine}";

        return new HudSections(status, ship, ShipyardSummary(), target, GoalsText());
    }

    private string LandedLine(CraftState p)
    {
        var body = _sector.GetBody(p.LandedOnBodyId).Name;
        if (p.IsAnchored)
            return p.Stats.IsFixedStructure
                ? $"ANCHORED to {body} permanently (foundation)\n"
                : $"ANCHORED to {body} - {InputActions.Key1(InputActions.Anchor)} to release\n";
        return $"LANDED on {body} - thrust away from the surface to lift off"
               + (p.Stats.CanAnchor ? $", {InputActions.Key1(InputActions.Anchor)} to anchor\n" : " (no anchor clamp)\n");
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
              + string.Concat(labs.Where(l => l.LabStatus is not null && !l.LabStatus.StartsWith("lab:")).Select(l => $"   {l.Name}: {l.LabStatus}")) + $" - {InputActions.Key1(InputActions.Research)} to research\n"
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
        + (!body.IsMineable ? "" : !body.AnySiteSurveyed ? $", unsurveyed - fly within {SectorState.SurveyRangeM / 1000:0} km"
            : body.DepositsKg.Count == 0 && body.BulkKg == 0 ? ", mined out"
            : $", {body.Sites.Count(s => s.Surveyed)} of {body.Sites.Count} sites surveyed"
              + (body.BulkKg > 0 ? $", bulk {body.BulkMaterialId} {body.BulkKg / 1e9:0.###} Mt" : ""))
        + ")";

    /// <summary>
    /// The site below the player on a body (#70): the one landed on, or the one under its bearing when
    /// near; with its ore if surveyed. Empty when not near a mineable body.
    /// </summary>
    private string SiteLine(CraftState p, CelestialBodyState body)
    {
        if (body.Sites.Count == 0) return "";
        var centre = _sector.GetPosition(body.Id);
        var bearing = p.IsLanded && p.LandedOnBodyId == body.Id ? SectorState.LandedBearing(p) : System.Math.Atan2(p.Position.Y - centre.Y, p.Position.X - centre.X);
        if (body.SiteAt(bearing) is not { } site) return "";
        var ore = !site.Surveyed ? "unsurveyed"
            : site.DepositsKg.Count == 0 ? "no ore" + (body.BulkKg > 0 ? $" (bulk {body.BulkMaterialId})" : "")
            : string.Join(", ", site.DepositsKg.Select(kv => $"{kv.Key} {kv.Value / 1000.0:0.#} t"));
        return $"site {site.Name}: {ore}";
    }

    private static string FormatDistance(double metres) =>
        System.Math.Abs(metres) >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";

    /// <summary>More ships than this on screen: only stations and the target are named (#39).</summary>
    private const int MaxNameTags = 40;

    private readonly List<(CraftState Craft, Vector2 At, float RadiusPx)> _visibleCrafts = new();

    public override void _Draw()
    {
        var drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawWorld();
        _drawCost.Add(System.Diagnostics.Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds);
    }

    private void DrawWorld()
    {
        DrawStarfield();
        if (InMenu) return; // the title screen: no game to draw
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
        // Only what's on screen is drawn (#39: a thousand ships off-screen cost nothing to draw).
        var screen = GetViewportRect().Size;
        var view = new Rect2(-screen / 2, screen).Grow(64);
        _visibleCrafts.Clear();
        foreach (var craft in _sector.Crafts)
        {
            var at = ToPixels(InterpolatedPosition(craft));
            var radiusPx = (float)craft.CollisionRadiusM * _pixelsPerMetre;
            if (view.Grow(radiusPx).HasPoint(at)) _visibleCrafts.Add((craft, at, radiusPx));
        }
        foreach (var (craft, _, _) in _visibleCrafts) DrawCraft(craft);
        FlushBatch();
        // Name tags, not when zoomed far out; in a crowd only stations and the target get one, so the text stays readable and cheap.
        var crowd = _visibleCrafts.Count > MaxNameTags;
        foreach (var (craft, at, radius) in _visibleCrafts)
        {
            if (craft == _player || _pixelsPerMetre <= 0.02f) continue;
            if (crowd && craft.Stats.TotalThrustN > 0 && _player?.TargetCraftId != craft.Id) continue;
            DrawString(ThemeDB.FallbackFont, at + new Vector2(Mathf.Max(8f, radius) + 4, 4), craft.Name,
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

    private static readonly Color SiteTickColor = new(1f, 1f, 1f, 0.45f);
    private static readonly Color SiteLabelColor = new(0.85f, 0.9f, 1f, 0.85f);
    private static readonly Color SiteUnknownColor = new(0.7f, 0.7f, 0.75f, 0.55f);

    /// <summary>
    /// Surface sites (#70): a tick across the rim where each site starts, and the site's letter with its
    /// ore (or "?" until surveyed) just outside the rim, once a site is wide enough on screen to label.
    /// </summary>
    /// <summary>Sites get labels once each is wide enough on screen.</summary>
    private static bool SitesLabelled(CelestialBodyState body, float radiusPx) =>
        body.Sites.Count > 0 && radiusPx >= 40f && radiusPx * (float)body.Sites[0].WidthRad > 70f;

    private void DrawSites(CelestialBodyState body, Vector2 centre, float radiusPx)
    {
        if (body.Sites.Count == 0 || radiusPx < 40f) return;
        var view = new Rect2(-GetViewportRect().Size / 2, GetViewportRect().Size).Grow(40);
        var labelled = SitesLabelled(body, radiusPx);
        foreach (var site in body.Sites)
        {
            var (sin, cos) = Mathf.SinCos((float)site.StartRad);
            var edge = new Vector2(cos, sin);
            var tickAt = centre + edge * radiusPx;
            if (view.HasPoint(tickAt)) DrawLine(centre + edge * (radiusPx - 8), centre + edge * (radiusPx + 12), SiteTickColor, 2);
            if (!labelled) continue;
            var (cs, cc) = Mathf.SinCos((float)site.CentreRad);
            var at = centre + new Vector2(cc, cs) * (radiusPx + 22);
            if (!view.HasPoint(at)) continue;
            var ore = !site.Surveyed ? "?" : site.DepositsKg.Count == 0 ? "no ore" : string.Join(", ", site.DepositsKg.Keys.Select(k => k.Replace("_ore", "")));
            DrawString(ThemeDB.FallbackFont, at - new Vector2(20, 0), $"{SurfaceSite.Letter(site.Index)}: {ore}",
                HorizontalAlignment.Left, -1, 12, site.Surveyed ? SiteLabelColor : SiteUnknownColor);
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
        DrawSites(body, centre, radiusPx);

        // Zoomed out, a moon's or rock's name would sit on top of its planet's: only label bodies whose
        // orbit is big enough on screen to tell apart (and always the target).
        var isTarget = _player is { } p && p.TargetBodyId == body.Id;
        if (body.Orbit is not null && orbitPx < 28f && !isTarget) return;
        var name = body.AnySiteSurveyed ? body.Name : $"{body.Name} (unsurveyed)";
        if (SitesLabelled(body, radiusPx)) // the rim carries site labels (#70): the name goes in the middle instead
            DrawString(ThemeDB.FallbackFont, centre - new Vector2(ThemeDB.FallbackFont.GetStringSize(name, HorizontalAlignment.Left, -1, 14).X / 2, -5), name,
                HorizontalAlignment.Left, -1, 14, NameColor);
        else DrawString(ThemeDB.FallbackFont, centre + new Vector2(radiusPx + 4, 4), name, HorizontalAlignment.Left, -1, 12, NameColor);
    }

    private void DrawCraft(CraftState craft)
    {
        var centre = ToPixels(InterpolatedPosition(craft));
        var heading = (float)Mathf.LerpAngle(craft.PreviousHeadingRad, craft.HeadingRad, _warp.Alpha);
        var cellPx = (float)ShipDesign.CellSizeM * _pixelsPerMetre;

        // Too small to see the grid: a heading marker instead, batched into one draw call for all craft (#39).
        if (cellPx < 1.5f)
        {
            var (sin, cos) = Mathf.SinCos(heading);
            Vector2 Rotate(float x, float y) => centre + new Vector2(x * cos - y * sin, x * sin + y * cos);
            BatchTriangle(Rotate(0, -8), Rotate(5, 6), Rotate(-5, 6), MarkerColor);
            return;
        }

        var gap = cellPx >= 6f ? 1f : 0f;
        var stats = craft.Stats;
        // Cells go into the frame's triangle batch, already rotated and placed (#39).
        var (hs, hc) = Mathf.SinCos(heading);
        Vector2 World(Vector2 v) => centre + new Vector2(v.X * hc - v.Y * hs, v.X * hs + v.Y * hc);
        foreach (var cell in craft.Design.Cells)
        {
            var local = new Vector2((float)(cell.Pos.X - stats.CenterOfMassX), (float)(cell.Pos.Y - stats.CenterOfMassY)) * cellPx;
            var rect = new Rect2(local - new Vector2(cellPx, cellPx) / 2 + new Vector2(gap, gap) / 2,
                new Vector2(cellPx - gap, cellPx - gap));
            var colour = _partColors.GetValueOrDefault(cell.PartId, Colors.Magenta);
            // Damaged parts (#34) darken towards black-red as they lose health.
            if (craft.PartHealth.TryGetValue(cell.Pos, out var health)) colour = colour.Lerp(new Color(0.25f, 0.05f, 0.05f), (float)(1 - health) * 0.8f);
            BatchQuad(World(rect.Position), World(rect.Position + new Vector2(rect.Size.X, 0)), World(rect.End), World(rect.Position + new Vector2(0, rect.Size.Y)), colour);
            if (craft.PartHealth.TryGetValue(cell.Pos, out var cracked) && cracked < 0.75 && cellPx >= 4f) // a crack marks damage beyond the darker colour (#68)
                _crackLines.Add((World(rect.Position + rect.Size * new Vector2(0.2f, 0.15f)), World(rect.Position + rect.Size * new Vector2(0.75f, 0.85f)), Mathf.Max(1f, cellPx / 8)));

            // Only rear-facing thrusters fire on throttle (docs/06); others stay dark for now.
            if (cell.PartId == "thruster" && cell.Rotation == 0 && craft.Controls.Throttle > 0 && !craft.Controls.Brake)
            {
                var flameTop = local + new Vector2(0, cellPx / 2);
                BatchTriangle(World(flameTop + new Vector2(-cellPx * 0.3f, 0)), World(flameTop + new Vector2(cellPx * 0.3f, 0)),
                    World(flameTop + new Vector2(0, cellPx * 0.9f)), FlameColor);
            }
        }
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
            if (CircleInDisk(rim, r, centre, radiusPx) is { } pitShape) DrawColoredPolygon(pitShape, pit);
            if (CircleInDisk(rim - dir * r * 0.15f, r * 0.55f, centre, radiusPx) is { } floorShape) DrawColoredPolygon(floorShape, floor);
        }
    }

    /// <summary>
    /// The part of a circle (centre <paramref name="c"/>, radius <paramref name="r"/>) inside the body's
    /// disk, as a convex outline: the arc of the body's edge inside the circle, then the arc of the circle
    /// inside the body. Exact, so it always triangulates. Null when they don't overlap.
    /// </summary>
    private static Vector2[]? CircleInDisk(Vector2 c, float r, Vector2 disk, float diskR)
    {
        const int arcPoints = 16;
        var offset = c - disk;
        var d = offset.Length();
        if (d >= diskR + r) return null; // apart
        if (d + r <= diskR) return CirclePoints(c, r, 32); // wholly inside the body
        if (d + diskR <= r) return CirclePoints(disk, diskR, 32); // the body is wholly inside the circle
        var toward = Mathf.Atan2(offset.Y, offset.X);
        // Half-angles of the two arcs, from the triangle (body centre, circle centre, crossing point).
        var alpha = Mathf.Acos(Mathf.Clamp((d * d + diskR * diskR - r * r) / (2 * d * diskR), -1, 1)); // at the body centre
        var gamma = Mathf.Acos(Mathf.Clamp((d * d + r * r - diskR * diskR) / (2 * d * r), -1, 1));     // at the circle centre
        // Points spaced a few pixels apart (tiny arcs get few: points closer than float precision can
        // coincide, and a repeated vertex won't triangulate). The arcs meet at the crossings, listed once.
        var bodyPoints = Mathf.Clamp((int)(2 * alpha * diskR / 4) + 2, 2, arcPoints);
        var circlePoints = Mathf.Clamp((int)(2 * gamma * r / 4) + 2, 2, arcPoints);
        if (r < 1f || bodyPoints + circlePoints - 2 < 3) return null; // too small to draw
        var points = new Vector2[bodyPoints + circlePoints - 2];
        for (var i = 0; i < bodyPoints; i++)
        {
            // The body's edge, from one crossing to the other (through the direction of the circle)...
            var a = toward - alpha + 2 * alpha * i / (bodyPoints - 1);
            points[i] = disk + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * diskR;
        }
        for (var i = 1; i < circlePoints - 1; i++)
        {
            // ...then back along the circle between them, through the direction of the body's centre.
            var b = toward + Mathf.Pi - gamma + 2 * gamma * i / (circlePoints - 1);
            points[bodyPoints + i - 1] = c + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * r;
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
}
