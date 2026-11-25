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
    private static readonly Color IonGlowColor = new(0.45f, 0.7f, 1f, 0.85f);
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

    /// <summary>Swarms (#30), opened from the Fleet panel.</summary>
    private SwarmPanel? _swarmPanel;
    private CargoPanel? _cargoPanel;
    private JobPanel? _jobPanel;
    private DebugPanel? _debugPanel;
    private DiagnosticsPanel? _diagnosticsPanel;
    private ResearchPanel? _researchPanel;

    /// <summary>Laws and civilizations (#36).</summary>
    private LawsPanel? _lawsPanel;

    /// <summary>Trade and diplomacy (#89).</summary>
    private TradePanel? _tradePanel;

    /// <summary>Power networks and sources (#75).</summary>
    private PowerPanel? _powerPanel;

    /// <summary>Native life (#94).</summary>
    private LifePanel? _lifePanel;

    /// <summary>The message log (#100).</summary>
    private LogPanel? _logPanel;
    private ProgramPanel? _programPanel;

    /// <summary>Milestones (#38).</summary>
    private MilestonesPanel? _milestonesPanel;

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
        SetUpBodyArt(); // #71-#74: the layers under this node
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
            _editor.LargeOpen = () => _sector is { LargeDesigns: true } || DevLarge; // #111
            _editor.CloseRequested += CloseEditor;
            _uiRoot.AddChild(_editor);

            _shipyardPanel = new ShipyardPanel(() => _sector, _catalog, () => _player?.Design) { Visible = false };
            _uiRoot.AddChild(_shipyardPanel);
            _fleetPanel = new FleetPanel(() => _sector, () => _player?.Id) { Visible = false };
            _fleetPanel.CraftChosen += Pilot;
            _fleetPanel.RenameChosen += AskRename; // #115
            _fleetPanel.Changed += () => _changedSinceSave = true;
            _fleetPanel.CraftTargeted += target =>
            {
                if (_player == null || target == _player.Id) return;
                _sector.SetTargetCraft(_player.Id, target);
                ShowStatus($"Target: {_sector.GetCraft(target).Name}.");
            };
            _uiRoot.AddChild(_fleetPanel);
            _swarmPanel = new SwarmPanel(() => _sector, () => _changedSinceSave = true) { Visible = false }; // #30
            _uiRoot.AddChild(_swarmPanel);
            _fleetPanel.SwarmsChosen += () => SwapPanel(_fleetPanel, _swarmPanel, () => _swarmPanel.Open()); // #100
            _swarmPanel.FleetChosen += () => SwapPanel(_swarmPanel, _fleetPanel, () => _fleetPanel.Open());
            _cargoPanel = new CargoPanel(() => _sector, () => _player, _catalog.Materials) { Visible = false };
            _uiRoot.AddChild(_cargoPanel);
            _jobPanel = new JobPanel(() => _sector, () => _player, _catalog.Materials) { Visible = false };
            _uiRoot.AddChild(_jobPanel);
            _mapPanel = new MapPanel(() => _sector, () => _player,
                body => { if (_player != null) { _sector.SetTarget(_player.Id, body); ShowStatus($"Target: {_sector.GetBody(body).Name}."); } },
                craft => { if (_player != null && craft != _player.Id) { _sector.SetTargetCraft(_player.Id, craft); ShowStatus($"Target: {_sector.GetCraft(craft).Name}."); } },
                ViewSystem, () => MapStar()?.Id, _catalog.Materials)
            { Visible = false };
            _uiRoot.AddChild(_mapPanel);
            _diagnosticsPanel = new DiagnosticsPanel(() => _sector, _warp, _tickCost) { Visible = false };
            _uiRoot.AddChild(_diagnosticsPanel);
            _researchPanel = new ResearchPanel(() => _sector, _catalog) { Visible = false };
            _uiRoot.AddChild(_researchPanel);
            _lawsPanel = new LawsPanel(() => _sector, () => _changedSinceSave = true) { Visible = false }; // #36
            _uiRoot.AddChild(_lawsPanel);
            _tradePanel = new TradePanel(() => _sector, () => _player, _catalog.Materials, () => _changedSinceSave = true) { Visible = false }; // #89
            _uiRoot.AddChild(_tradePanel);
            _lawsPanel.DiplomacyChosen += id => SwapPanel(_lawsPanel, _tradePanel, () => _tradePanel.Open(id < 0 ? null : id));
            _tradePanel.LawsChosen += () => SwapPanel(_tradePanel, _lawsPanel, () => _lawsPanel.Open());
            _powerPanel = new PowerPanel(() => _sector, () => _player) { Visible = false }; // #75
            _uiRoot.AddChild(_powerPanel);
            _lifePanel = new LifePanel(() => _sector, () => _player, () => _changedSinceSave = true) { Visible = false }; // #94
            _uiRoot.AddChild(_lifePanel);
            _logPanel = new LogPanel(() => _sector, () => Messages) { Visible = false }; // #100
            _uiRoot.AddChild(_logPanel);
            _programPanel = new ProgramPanel(() => _sector, () => _player, _catalog.Materials) { Visible = false }; // #120
            _programPanel.Changed += () => _changedSinceSave = true;
            _programPanel.JobsChosen += () => SwapPanel(_programPanel, _jobPanel, () => _jobPanel!.Open());
            _jobPanel!.ProgramsChosen += id => SwapPanel(_jobPanel, _programPanel, () => _programPanel.Open(id));
            _uiRoot.AddChild(_programPanel);
            _plannerPanel = new PlannerPanel(() => _sector, () => _player, _catalog.Materials, () => _shipyardPanel!.DesignChoices) { Visible = false }; // #121
            _plannerPanel.IssueChosen += GoToIssue;
            _shipyardPanel!.PlanChosen += design => SwapPanel(_shipyardPanel, _plannerPanel, () => _plannerPanel.Open(design));
            _uiRoot.AddChild(_plannerPanel);
            _contractsPanel = new ContractsPanel(() => _sector, () => _player) { Visible = false }; // #124
            _contractsPanel.Acted += r => { ShowStatus(r.Message); _changedSinceSave = true; };
            _contractsPanel.HaulChosen += (station, material) => SwapPanel(_contractsPanel, _jobPanel, () => _jobPanel!.OpenDelivery(station, material));
            _uiRoot.AddChild(_contractsPanel);
            _discoveriesPanel = new DiscoveriesPanel(() => _sector, () => _player, ChoosePoint) { Visible = false }; // #122
            _uiRoot.AddChild(_discoveriesPanel);
            _findPanel = new FindPanel(() => _sector, () => _player, () => Recent, () => CurrentTarget, t => _sector.Exists(t) ? TargetLabel(t) : "", id => _catalog.Materials.Contains(id) ? _catalog.Materials.Get(id).Name : id) { Visible = false }; // #109
            _findPanel.TargetChosen += ChooseTarget;
            _findPanel.FavouriteChosen += RecallFavourite;
            _findPanel.Changed += () => _changedSinceSave = true;
            _uiRoot.AddChild(_findPanel);
            UiLinks.Open = OpenLink;
            _milestonesPanel = new MilestonesPanel(() => _sector) { Visible = false }; // #38
            _uiRoot.AddChild(_milestonesPanel);
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
            if (startArgs.FirstOrDefault(q => q.StartsWith("--window=")) is { } asked && asked["--window=".Length..].Split('x') is [var across, var down]) GetWindow().Size = new Vector2I(int.Parse(across), int.Parse(down)); // dev
            if (System.Array.IndexOf(startArgs, "--title-pick=races") >= 0 && _catalog.Races.Races.FirstOrDefault(r => System.Array.IndexOf(startArgs, "--race=" + r.Id) >= 0) is { } raceArg) { _uiRoot.MoveChild(_raceCreator, _uiRoot.GetChildCount() - 1); _raceCreator.Open(raceArg); } // dev (#117)
            if (System.Array.IndexOf(startArgs, "--title-pick=setup") >= 0) { _uiRoot.MoveChild(_newGame, _uiRoot.GetChildCount() - 1); _newGame.Open(); if (System.Array.IndexOf(startArgs, "--advanced") >= 0) _newGame.ShowAdvanced(); } // #62 check
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
        if (System.Array.IndexOf(args, "--new-game") >= 0) StartNewGame(NewGame.DefaultSeed, "New game", race: _catalog?.Races.Races.FirstOrDefault(r => System.Array.IndexOf(args, "--race=" + r.Id) >= 0)); // ignores the save; nothing is written
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
        if (System.Array.IndexOf(args, "--demo-civ") >= 0) // #36 check: warp out, meet a wary civilization there, open the laws panel
        {
            DemoWarp();
            for (var i = 0; i < 60 * FixedStepClock.TicksPerSecond && _sector.Stars.Count < 2; i++) _sector.Step();
            if (_sector.Stars.Count > 1)
            {
                _sector.MeetCivilization(_sector.Stars[^1], cultureId: "territorial", baseline: -12);
                _sector.SetLaw(Simulation.Laws.ArmedGates, true);
            }
            _lawsPanel?.Open();
        }
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
        if (args.FirstOrDefault(a => a.StartsWith("--part-tip=")) is { } tipArg) _editor?.DevPoint(tipArg["--part-tip=".Length..]);
        if (System.Array.IndexOf(args, "--open-cargo") >= 0) _cargoPanel?.Open();
        if (System.Array.IndexOf(args, "--open-shipyard") >= 0) _shipyardPanel?.Open();
        if (args.FirstOrDefault(a => a.StartsWith("--shipyard-design=")) is { } designArg && int.TryParse(designArg["--shipyard-design=".Length..], out var designIndex))
            _shipyardPanel?.SelectDesign(designIndex);
        DemoSwarm(args); // #30
        DemoHuge(args); // #114
        if (args.FirstOrDefault(x => x.StartsWith("--window=")) is { } size && size["--window=".Length..].Split('x') is [var w, var h]) // dev: test small windows (#96)
            GetWindow().Size = new Vector2I(int.Parse(w), int.Parse(h));
        if (System.Array.IndexOf(args, "--open-programs") >= 0) DemoProgram(); // #120
        if (System.Array.IndexOf(args, "--open-contracts") >= 0) DemoContracts(); // #124
        if (args.FirstOrDefault(a => a.StartsWith("--open-planner")) is { } planner) DemoPlanner(planner.Contains("=rate")); // #121
        if (args.FirstOrDefault(a => a.StartsWith("--hazard=")) is { } happens) DemoHazard(happens.Split('=').Last()); // #123
        if (args.FirstOrDefault(a => a.StartsWith("--open-discoveries")) is { } discover) DemoPoints(discover.Split('=').Last()); // #122
        if (System.Array.IndexOf(args, "--open-find") >= 0) _findPanel?.Open(); // #109
        if (System.Array.IndexOf(args, "--open-log") >= 0) _logPanel?.Open(); // #100
        if (System.Array.IndexOf(args, "--open-milestones") >= 0) _milestonesPanel?.Open(); // #38
        if (System.Array.IndexOf(args, "--open-life") >= 0) _lifePanel?.Open(); // #94, with --add-life
        if (System.Array.IndexOf(args, "--open-trade") >= 0) { if (_lawsPanel != null) _lawsPanel.Visible = false; _tradePanel?.Open(); } // #89, with --demo-civ
        if (System.Array.IndexOf(args, "--open-swarms") >= 0) _swarmPanel?.Open();
        if (System.Array.IndexOf(args, "--open-fleet") >= 0) _fleetPanel?.Open();
        if (System.Array.IndexOf(args, "--open-diagnostics") >= 0) _diagnosticsPanel?.Open();
        if (System.Array.IndexOf(args, "--open-settings") >= 0) _settingsPanel?.Open();
        if (System.Array.IndexOf(args, "--open-controls") >= 0) { _uiRoot.MoveChild(_controls!, _uiRoot.GetChildCount() - 1); _controls!.Open(); }
        if (System.Array.IndexOf(args, "--open-map") >= 0) ToggleMap();
        if (System.Array.IndexOf(args, "--open-research") >= 0) _researchPanel?.Open();
        if (System.Array.IndexOf(args, "--open-power") >= 0) _powerPanel?.Open(); // #75
        if (System.Array.IndexOf(args, "--open-pause") >= 0) OpenPause();
        if (System.Array.IndexOf(args, "--open-pause") >= 0) { OpenPause(); _pause.ShowWorld(); } // dev (#119)
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
        DemoRaid(args); // #84 check
        SetUpBodyView(args); // #71-#74 check
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
        if (!SectorState.IsPlayers(_sector.GetCraft(id)) || _sector.GetCraft(id).IsWreck) { ShowStatus($"{_sector.GetCraft(id).Name} can't be flown."); return; } // #84
        if (_player != null) _player.Controls = ShipControls.Idle;
        _player = _sector.GetCraft(id);
        _sector.PilotCraftId = id; // the craft you fly is the one that surveys
        ShowStatus($"Now piloting {_player.Name} ({_player.Id}).");
    }

    private void OpenEditor()
    {
        if (_editor == null) { ShowStatus("Editor unavailable: part data failed to load."); return; }
        _editor.Open();
        _uiRoot.MoveChild(_editor, _uiRoot.GetChildCount() - 1);
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
    private RaceCreatorScreen _raceCreator = null!;

    /// <summary>Shown while a save loads (#67).</summary>
    private LoadingScreen _loadingScreen = null!;

    private void BuildMenus()
    {
        _confirm = new ConfirmDialog();
        _saveScreen = new SaveLoadScreen(() => SavesFolder, (question, choices) => _confirm.Ask(question, choices));
        _saveScreen.DefaultName = () => _gameName;
        _saveScreen.Portrait = PortraitOf; // #98
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
        _newGame = new NewGameScreen(() => _catalog?.Races ?? RaceCatalog.HumanOnly, race => _catalog is { } c ? SectorState.Describe(race, c) : System.Array.Empty<string>(),
            race => _catalog is { } c && race.IsCustom && RacePoints.Unbalanced(race, c));
        _raceCreator = new RaceCreatorScreen(() => _catalog, (question, choices) => { _uiRoot.MoveChild(_confirm, _uiRoot.GetChildCount() - 1); _confirm.Ask(question, choices); }); // #117
        _raceCreator.Changed += () => { if (_catalog is { } c) c.WithRaces(RaceLibrary.Load(c)); };
        _raceCreator.Closed += id => _newGame.Return(id);
        _newGame.CreatorChosen += race => { _uiRoot.MoveChild(_raceCreator, _uiRoot.GetChildCount() - 1); _raceCreator.Open(race); };
        _newGame.StartChosen += (name, seed, goals, supplies, race, options) => LeaveTitle(() => { StartNewGame(seed, name, supplies, race, options); _goalsOn = goals; });
        _newGame.BackChosen += ShowTitle;
        _pause = new PauseMenu();
        _pause.ResumeChosen += ResumeFromPause;
        _pause.SaveChosen += () => { _pause.Visible = false; _saveScreen.Open(SaveLoadScreen.ScreenMode.Save); };
        _pause.LoadChosen += () => { _pause.Visible = false; _saveScreen.Open(SaveLoadScreen.ScreenMode.Load); };
        _pause.WorldChanged += () => _changedSinceSave = true; // #119
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
        _uiRoot.AddChild(_raceCreator);
        if (_settingsPanel != null) _uiRoot.MoveChild(_settingsPanel, _uiRoot.GetChildCount() - 1);
        _uiRoot.AddChild(_confirm);
        _loadingScreen = new LoadingScreen(); // over everything (#67)
        _uiRoot.AddChild(_loadingScreen);
        BuildTopBar(); // #106
        // Any button pressed in the game's own panels counts as a change to save (not in the menus themselves).
        GetTree().NodeAdded += node =>
        {
            if (node is BaseButton button) button.Pressed += () => { if (!IsMenuControl(button)) _changedSinceSave = true; };
        };
    }

    private static bool IsMenuControl(Node node)
    {
        for (var n = node; n != null; n = n.GetParent())
            if (n is PauseMenu or SaveLoadScreen or ConfirmDialog or TitleScreen or SettingsPanel or NewGameScreen or RaceCreatorScreen or ControlsPanel or TopBar) return true;
        return false;
    }

    private void OpenPause()
    {
        if (!ModalOpen && !EditorOpen && _settingsPanel is not { Visible: true }) _lastPicture = TakePicture(); // before the menu covers it (#98)
        _pausedBeforeMenu = _warp.Paused;
        _warp.Paused = true;
        if (_sector is not null) _pause.Portrait = RaceLibrary.Portrait(_sector.Race);
        _pause.Open(_sector is null ? "" : $"{_gameName}   ·   seed {_sector.Seed}   ·   played {(int)(_playTimeS / 3600)} h {(int)(_playTimeS / 60) % 60} min", _goalsOn, _sector?.Options);
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
        ShowRace(RaceCatalog.HumanOnly.Default);
        ResumeFromPause();
        _warp.Paused = false;
        _player = null;
        _sector = null!; // released: the title has its own (#98)
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
            var header = new SaveHeader(name, System.DateTime.UtcNow.ToString("O"), _playTimeS, _sector.Tick, _sector.Seed, _gameName, _goalsOn, _goalsDone.ToArray(), _sector.Race.Id);
            SaveStore.Write(path, SaveGameSerializer.ToJson(_sector, _player.Id, header));
            SavePicture(path); // #98
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
        _loadingScreen.Begin(what, PortraitOf(SaveStore.Read(path).Race), Tip()); // #98
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
            ShowRace(_sector.Race);
            _lastEventShown = null; // events from before don't show as new (#36)
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
            catalog.WithPoints(PointLibrary.Load(catalog)); // #122
            RaceFiles.Catalog = catalog; // #125
            catalog.WithRaces(RaceLibrary.Load(catalog)); // #97, #116
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
            foreach (var material in materials.Materials)
                if (material.PlaceholderColor is { } hex) _materialColors[material.Id] = Color.FromHtml(hex);
            PartSprites.Load(catalog.Parts.Select(p => p.Id)); // pixel-art parts (#46)
            _catalog = catalog;
            _starterDesign = design;
            _starterShipyard = shipyard;
            RacePoints.SetUsual(design, shipyard, catalog); // #125
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
        OpenMenuScene(); // #98
        _hud.Visible = false;
        foreach (var panel in new Control?[] { _shipyardPanel, _fleetPanel, _swarmPanel, _tradePanel, _lifePanel, _milestonesPanel, _cargoPanel, _jobPanel, _debugPanel, _diagnosticsPanel, _researchPanel, _lawsPanel, _powerPanel, _mapPanel, _logPanel, _findPanel, _programPanel, _discoveriesPanel, _contractsPanel, _plannerPanel })
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
        CloseMenuScene(); // #98
        if (_title != null) _title.Visible = false;
        if (_settingsPanel != null) _settingsPanel.Visible = false;
        _hud.Visible = true;
        _mapMode = false;
        GD.Print($"[Sector] seed={_sector.Seed} tick={_sector.Tick} bodies={_sector.Bodies.Count} (from the main menu)");
    }

    /// <summary>A new game from a seed (#62); the default seed for command-line starts.</summary>
    private void StartNewGame() => StartNewGame(NewGame.DefaultSeed, "New game");

    private void StartNewGame(ulong seed, string name, StartingSupplies supplies = StartingSupplies.Standard, RaceDefinition? race = null, WorldOptions? options = null)
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

        var game = NewGame.Create(seed, _catalog, _starterDesign, _starterShipyard, supplies, race, options ?? WorldOptions.Usual()); // #119
        _sector = game.Sector;
        ShowRace(_sector.Race);
        _lastEventShown = null; // events from before don't show as new (#36)
        _player = _sector.GetCraft(game.PlayerCraftId);
        _savedTick = -1; // a new game is unsaved until it's saved
    }

    /// <summary>The newest sector event already shown on the status line (#36).</summary>
    private SectorEvent? _lastEventShown;

    /// <summary>A new event (contact, raid, a change of attitude) shows on the status line, with a sound.</summary>
    private void ShowNewEvents()
    {
        if (InMenu || _sector is null || _sector.Events.Count == 0) return;
        var last = _sector.Events[^1];
        if (ReferenceEquals(last, _lastEventShown)) return;
        var first = _lastEventShown is null;
        _lastEventShown = last;
        if (first && _sector.Tick > 1) return; // just loaded: old news
        ShowStatus(last.Text, remember: false); // it's in the log as an event
    }

    private void ShowStatus(string message, bool remember = true)
    {
        if (remember) Remember(message);
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
        if (DragMap(@event)) { GetViewport().SetInputAsHandled(); return; } // #104
        if (@event is InputEventMouseButton click && WorldClick(click)) { GetViewport().SetInputAsHandled(); return; } // #108
        // The side panels don't take keyboard focus, so flying continues while they're open.

        if (Pressed(InputActions.ZoomIn) || Pressed(InputActions.ZoomOut))
        {
            var before = _pixelsPerMetre;
            _pixelsPerMetre = Mathf.Clamp(Pressed(InputActions.ZoomIn) ? _pixelsPerMetre * ZoomStep : _pixelsPerMetre / ZoomStep,
                MinPixelsPerMetre, MaxPixelsPerMetre);
            if (_mapMode) ZoomMapTo(@event is InputEventMouseButton wheel ? wheel.Position - GetViewportRect().Size / 2 : Vector2.Zero, before); // #104
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
        else if (Pressed(InputActions.Cargo) && _player is { } trader && _sector.DockedTrader(trader) is not null) Toggle(_tradePanel, () => _tradePanel!.Open()); // #89
        else if (Pressed(InputActions.Cargo)) Toggle(_cargoPanel, () => _cargoPanel!.Open());
        else if (Pressed(InputActions.Diagnostics)) Toggle(_diagnosticsPanel, () => _diagnosticsPanel!.Open());
        else if (Pressed(InputActions.Settings)) Toggle(_settingsPanel, () => _settingsPanel!.Open());
        else if (Pressed(InputActions.Research)) Toggle(_researchPanel, () => _researchPanel!.Open());
        else if (Pressed(InputActions.Laws)) Toggle(_lawsPanel, () => _lawsPanel!.Open());
        else if (Pressed(InputActions.Log)) Toggle(_logPanel, () => _logPanel!.Open());
        else if (Pressed(InputActions.Milestones)) Toggle(_milestonesPanel, () => _milestonesPanel!.Open());
        else if (Pressed(InputActions.Planner)) Toggle(_plannerPanel, () => _plannerPanel!.Open());
        else if (Pressed(InputActions.Contracts)) Toggle(_contractsPanel, () => _contractsPanel!.Open());
        else if (Pressed(InputActions.Discoveries)) Toggle(_discoveriesPanel, () => _discoveriesPanel!.Open());
        else if (Pressed(InputActions.Life)) Toggle(_lifePanel, () => _lifePanel!.Open());
        else if (Pressed(InputActions.Power)) Toggle(_powerPanel, () => _powerPanel!.Open());
        else if (Pressed(InputActions.Map)) ToggleMap();
        else if (Pressed(InputActions.MenuBar)) ToggleTopBar(); // #106
        else if (Pressed(InputActions.Rename) && _player is { } mine) AskRename(mine.Id); // #115
        else if (Pressed(InputActions.Help)) _hud.ToggleHelp();
        else if (Pressed(InputActions.Fullscreen)) ToggleFullscreen();
        else if (Pressed(InputActions.TimeFaster)) { _warp.Paused = false; _warp.Faster(); ShowStatus($"Time {_warp}."); }
        else if (Pressed(InputActions.TimeSlower)) { _warp.Paused = false; _warp.Slower(); ShowStatus($"Time {_warp}."); }
        else if (Pressed(InputActions.TimePause)) { _warp.Paused = !_warp.Paused; ShowStatus(_warp.Paused ? $"Paused ({InputActions.Key1(InputActions.TimePause)} to resume)." : $"Time {_warp}."); }
        else if (Pressed(InputActions.ClosePanels))
        {
            // Esc closes what's open (map, help, panels); with nothing open it's the pause menu (#59).
            var panels = new Control?[] { _shipyardPanel, _fleetPanel, _swarmPanel, _tradePanel, _lifePanel, _milestonesPanel, _cargoPanel, _jobPanel, _debugPanel, _diagnosticsPanel, _settingsPanel, _researchPanel, _lawsPanel, _powerPanel, _logPanel, _findPanel, _programPanel, _discoveriesPanel, _contractsPanel, _plannerPanel };
            var anyOpen = _mapMode || _hud.HelpOpen || panels.Any(p => p is { Visible: true });
            if (!anyOpen) OpenPause();
            if (_mapMode) ToggleMap();
            _hud.CloseHelp();
            foreach (var panel in panels)
                if (panel != null) panel.Visible = false;
        }
        else if (_player == null) return;
        else if (Pressed(InputActions.HoldFire)) ToggleHoldFire(); // #85
        else if (Pressed(InputActions.Quantum) && (_changedSinceSave = true)) ShowStatus(_sector.RequestQuantumJump(_player.Id).Message); // #82
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
        else if (FindingKey(Pressed)) { } // #109, #110
        else if (Pressed(InputActions.TargetKindNext)) NextTargetKind(); // before the plain key: they share Tab (#107)
        else if (Pressed(InputActions.TargetPrevious)) CycleTarget(-1);
        else if (Pressed(InputActions.TargetCycle)) CycleTarget(1);
        else if (Pressed(InputActions.TargetAhead)) TargetAhead();
        else if (Pressed(InputActions.TargetClear)) ClearTarget();
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

    public override void _Process(double delta)
    {
        var audioStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _audio.Watch(InMenu ? null : _sector, InMenu ? null : _player, delta); // ship sounds; music plays throughout (#64)
        _audioCost.Add(System.Diagnostics.Stopwatch.GetElapsedTime(audioStart).TotalMilliseconds);
        ArrangePanels(); // #96
        WatchTarget(); // #109
        UpdateHover(); // #108
        if (!InMenu) _powerPanel?.Sample(); // #103
        UpdatePanelNav();
        ShowNewEvents(); // civilizations, laws (#36)
        StressFrame(delta);
        UpdateGoals(delta);
        if (InMenu)
        {
            QueueRedraw();
            _backdrop.QueueRedraw();
            MenuFrame(delta); // #98
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
        if (_mapMode && MapStar() is { } root) _viewCentre = _sector.GetPosition(root.Id) + _mapOffset;
        else if (_player != null) _viewCentre = InterpolatedPosition(_player);
        ApplyDevView();
        var hudStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _hud.Show(BuildHud());
        _hudCost.Add(System.Diagnostics.Stopwatch.GetElapsedTime(hudStart).TotalMilliseconds);
        QueueRedraw();
        _backdrop.QueueRedraw();
        DevScreenshot();
    }

    private int _framesShown;

    /// <summary>
    /// Dev/test hook: `-- --screenshot=path.png` saves the window at its real size after a few
    /// frames and quits (movie maker always records 1280×720, so it can't check other resolutions).
    /// </summary>
    private void DevScreenshot()
    {
        _framesShown++;
        // Dev hooks (#108): --click=x,y, --right-click=x,y or --point=x,y (window pixels), and --press=action, through the real input path.
        var clickAt = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--click-frame=")) is { } clickArg ? int.Parse(clickArg["--click-frame=".Length..]) : 11;
        foreach (var arg0 in OS.GetCmdlineUserArgs())
        {
            var parts = arg0.Split('=');
            if (parts.Length == 2 && parts[0] == "--press" && _framesShown == 10) Input.ParseInputEvent(new InputEventAction { Action = parts[1], Pressed = true });
            // --drag=x,y,x2,y2 presses at the first place and moves to the second; --drop lets go there (#112).
            if (parts.Length == 2 && parts[0] == "--drag" && parts[1].Split(',').Select(float.Parse).ToArray() is [var ax, var ay, var bx, var by])
            {
                var (from, to) = (new Vector2(ax, ay), new Vector2(bx, by));
                if (_framesShown == clickAt) Input.ParseInputEvent(new InputEventMouseButton { Position = from, GlobalPosition = from, Pressed = true, ButtonIndex = MouseButton.Left });
                if (_framesShown == clickAt + 1) Input.ParseInputEvent(new InputEventMouseMotion { Position = to, GlobalPosition = to, ButtonMask = MouseButtonMask.Left });
                if (_framesShown == clickAt + 2 && OS.GetCmdlineUserArgs().Contains("--drop")) Input.ParseInputEvent(new InputEventMouseButton { Position = to, GlobalPosition = to, Pressed = false, ButtonIndex = MouseButton.Left });
            }
            if (parts.Length != 2 || parts[0] is not ("--click" or "--right-click" or "--point" or "--wheel") || parts[1].Split(',') is not [var px, var py]) continue;
            var at = new Vector2(float.Parse(px), float.Parse(py));
            if (parts[0] == "--point") _devPointer = at;
            if (parts[0] == "--wheel" && _framesShown is >= 10 and < 40) Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, Pressed = true, ButtonIndex = MouseButton.WheelUp });
            if ((_framesShown == clickAt || _framesShown == clickAt + 1) && parts[0] is ("--click" or "--right-click"))
                Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, Pressed = _framesShown == clickAt, ButtonIndex = parts[0] == "--click" ? MouseButton.Left : MouseButton.Right });
        }
        var shotAt = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshot-frame=")) is { } frameArg ? int.Parse(frameArg["--screenshot-frame=".Length..]) : 20;
        if (_framesShown != shotAt) return;
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
        if (Time.GetTicksMsec() < _statusUntilMs) status += $"{_status}\n";
        if (_warp.Speed != 1 || _warp.Paused) status += $"TIME {_warp}   ({InputActions.Key1(InputActions.TimeSlower)} slower, {InputActions.Key1(InputActions.TimeFaster)} faster, {InputActions.Key1(InputActions.TimePause)} pause)\n";
        if (!_sector.Tuning.IsDefault) status += $"DEBUG: {_sector.Tuning}  (F3)\n";
        if (_player is not { } p) return new HudSections(StatusCard(status + _setupError), HudCardData.None, StationsCard(ShipyardSummary()), HudCardData.None, GoalsCard());

        var ship = (p.Kits.Count > 0
                       ? $"KIT on board: {p.Kits[0].Name} ({p.Kits[0].MassKg / 1000.0:0.0} t){(p.Kits.Count > 1 ? $" +{p.Kits.Count - 1} more" : "")} - "
                         + (_sector.DeployBlocker(p.Id) is { } blocked ? blocked : $"{InputActions.Key1(InputActions.Deploy)} to deploy here") + "\n"
                       : "")
                   + (SectorState.PowerStatus(p) is { Length: > 0 } power ? power + "\n" : "")
                   + FutureTechLine(p) // #81, #82
                   + WeaponsLine(p) // #85
                   + (p.AnimalStatus is { } animals ? animals + "\n" : "") // #93
                   + (p.SalvageStatus is { } salvage ? salvage + "\n" : p.Stats.SalvageRateKgPerS > 0 && p.Job is null && _sector.Crafts.Any(c => c.IsWreck) ? "salvage arm: target a wreck and hold beside it\n" : "") // #86
                   + (SectorState.PropellantStatus(p) is { Length: > 0 } drives ? drives + "\n" : "") // #80
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
                       : p.IsDocked && _sector.GetCraft(p.DockedToCraftId) is { } atGate && SectorState.IsGate(atGate)
                           ? (atGate.GateLostStarId.IsNone
                               ? $"WARP GATE {atGate.Name} - {InputActions.Key1(InputActions.Jump)} to jump\n"
                               : $"WARP GATE {atGate.Name}: its far gate in {_sector.GetBody(atGate.GateLostStarId).Name} was destroyed - {InputActions.Key1(InputActions.Jump)} sends a new one through (from its stock)\n")
                           : "")
                   + (_sector.Stars.Count > 1 && _sector.StarAt(p.Position) is { } here ? $"system: {here.Name}\n" : "")
                   + (!p.IsLanded && !p.IsDocked && !p.IsOnRails && !p.Cruising && _sector.GravitySource(p.Position) is { } pull
                       ? $"gravity {_sector.GravityAt(p.Position, _sector.TimeS).Length:0.00} m/s² from {pull.Name} - land under {SectorState.SafeImpactSpeedMps:0} m/s\n"
                       : "")
                   + (p.FlyToPhase > 0 ? $"FLYING TO {(CurrentTarget.IsNone ? "its target" : TargetName(CurrentTarget))}: {(p.FlyToPhase == 1 ? "turning" : "cruising")} - any flight control stops it\n" : "") // #108
                   + (p.Cruising
                       ? $"CRUISE {SectorState.CruiseSpeedMps:0} m/s - fuel {p.Inventory[SectorState.FuelMaterial] / 1000.0:0.00} t "
                         + $"({p.Inventory[SectorState.FuelMaterial] / System.Math.Max(SectorState.CruiseFuelKgPerS(p), 1e-9) / 60:0} min left), V or brake to drop out\n"
                       : p.CruiseStopReason is { } why && p.Stats.TotalThrustN > 0 ? $"cruise ended: {why} - {InputActions.Key1(InputActions.Cruise)} to cruise again\n" : "")
                   + (p.Job is { Active: true } job
                       ? $"AUTOPILOT ({job.Kind} job): {job.Status}   cycles {job.CompletedCycles}, delivered {job.DeliveredKg / 1000.0:0.0} t - {InputActions.Key1(InputActions.Job)} to view/stop\n"
                       : "")
                   + (_sector.MiningMaterial(p) is { } mining && p.FreeCargoKg > 0
                       ? $"MINING {mining}: {_sector.ExtractionRateKgPerS(p, mining):0} kg/s, {_sector.GetBody(p.LandedOnBodyId).Deposit(mining) / 1000.0:0.0} t left in {_sector.GetBody(p.LandedOnBodyId).Name}\n"
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

        return new HudSections(StatusCard(status), ShipCard(p, ship), StationsCard(ShipyardSummary()), TargetCard(p, target), GoalsCard());
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
        return $"site {site.Name}: {ore}" + (site.Surveyed && site.IsHot ? " - geothermal hot spot" : "")
               + (site.Flora > 0 ? $" - native plants {site.Flora * 100:0}%{(body.FloraHazard switch { FloraHazard.Clinging => ", clinging (machines are slower)", FloraHazard.Corrosive => ", corrosive (machines wear)", _ => "" })}" : "")
               + (site.Fauna > 0 ? $" - {body.AnimalName}s {site.Fauna * 100:0}%{(SectorState.AnimalsAngry(body, site) ? ", ANGRY" : "")}" : "") // #92, #93
               + (_sector.IsHotBody(body) ? $" ({body.Name} is hot all over: near its star)" : "");
    }

    private static string FormatDistance(double metres) =>
        System.Math.Abs(metres) >= 1e9 ? $"{metres / 1e9:0.00} million km" : System.Math.Abs(metres) >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";

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
        if (InMenu && !ShowsMenuScene || (_mapMode && _galaxyView)) HideBodies();
        _arrowHits.Clear();
        _starHits.Clear();
        _swarmHits.Clear();
        if (InMenu) // the title screen: its own little world (#98)
        {
            if (ShowsMenuScene) DrawMenuScene();
            return;
        }
        if (_mapMode && _galaxyView) { DrawGalaxy(); return; }

        var renderTimeS = RenderTimeS;
        DrawBodies(renderTimeS);
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
            if (view.Grow(radiusPx).HasPoint(at) && MapShows(craft)) _visibleCrafts.Add((craft, at, radiusPx));
        }
        foreach (var (craft, _, _) in _visibleCrafts) DrawCraft(craft);
        DrawSwarms(renderTimeS); // #30
        FlushBatch();
        DrawCombat(); // #84
        DrawSystemMap(renderTimeS); // #104
        DrawPoints(renderTimeS); // #122
        DrawHazards(renderTimeS); // #123
        DrawEdgeArrows(renderTimeS); // #101
        DrawHover(renderTimeS); // #108
        // Name tags, not when zoomed far out; in a crowd only stations and the target get one, so the text stays readable and cheap.
        var crowd = _visibleCrafts.Count > MaxNameTags;
        foreach (var (craft, at, radius) in _visibleCrafts)
        {
            if (craft == _player || _pixelsPerMetre <= 0.02f) continue;
            if (crowd && craft.Stats.TotalThrustN > 0 && _player?.TargetCraftId != craft.Id) continue;
            DrawString(ThemeDB.FallbackFont, at + new Vector2(Mathf.Max(8f, radius) + 4, 4), CraftTag(craft),
                HorizontalAlignment.Left, -1, 11, _sector.FactionById(craft.FactionId) is { } owner && !craft.IsWreck ? CivilizationRows.Accent(owner) : CraftLabelColor); // #118
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

    private void DrawCraft(CraftState craft)
    {
        var centre = ToPixels(InterpolatedPosition(craft));
        var heading = (float)Mathf.LerpAngle(craft.PreviousHeadingRad, craft.HeadingRad, _warp.Alpha);
        var cellPx = (float)ShipDesign.CellSizeM * _pixelsPerMetre;

        // Too small to see the grid: a heading marker instead, batched into one draw call for all craft (#39).
        if (DrawBig(craft, centre, heading, cellPx)) return; // #114
        if (cellPx < 1.5f)
        {
            var (sin, cos) = Mathf.SinCos(heading);
            Vector2 Rotate(float x, float y) => centre + new Vector2(x * cos - y * sin, x * sin + y * cos);
            BatchTriangle(Rotate(0, -8), Rotate(5, 6), Rotate(-5, 6), CraftTint(craft));
            return;
        }

        // Sprites (#46) from 3 px a cell; below that the part colours read better. Sprites have their own outline: no gap.
        var sprites = cellPx >= 3f && PartSprites.Atlas is not null;
        var gap = !sprites && cellPx >= 6f ? 1f : 0f;
        var stats = craft.Stats;
        // Cells go into the frame's triangle batch, already rotated and placed (#39).
        var (hs, hc) = Mathf.SinCos(heading);
        Vector2 World(Vector2 v) => centre + new Vector2(v.X * hc - v.Y * hs, v.X * hs + v.Y * hc);
        foreach (var cell in craft.Design.Cells)
        {
            var local = new Vector2((float)(cell.Pos.X - stats.CenterOfMassX), (float)(cell.Pos.Y - stats.CenterOfMassY)) * cellPx;
            var rect = new Rect2(local - new Vector2(cellPx, cellPx) / 2 + new Vector2(gap, gap) / 2,
                new Vector2(cellPx - gap, cellPx - gap));
            var damaged = craft.PartHealth.TryGetValue(cell.Pos, out var health) ? (float)(1 - health) * 0.8f : 0f;
            var corners = (World(rect.Position), World(rect.Position + new Vector2(rect.Size.X, 0)), World(rect.End), World(rect.Position + new Vector2(0, rect.Size.Y)));
            if (sprites && PartSprites.TryCorners(cell.PartId, cell.Rotation, out var uv0, out var uv1, out var uv2, out var uv3))
                // Damaged parts (#34) darken towards black-red as they lose health (the sprite is tinted).
                BatchSprite(corners, (uv0, uv1, uv2, uv3), Colors.White.Lerp(new Color(0.35f, 0.12f, 0.12f), damaged));
            else
                BatchQuad(corners.Item1, corners.Item2, corners.Item3, corners.Item4,
                    _partColors.GetValueOrDefault(cell.PartId, Colors.Magenta).Lerp(new Color(0.25f, 0.05f, 0.05f), damaged));
            if (craft.PartHealth.TryGetValue(cell.Pos, out var cracked) && cracked < 0.75 && cellPx >= 4f) // a crack marks damage beyond the darker colour (#68)
                _crackLines.Add((World(rect.Position + rect.Size * new Vector2(0.2f, 0.15f)), World(rect.Position + rect.Size * new Vector2(0.75f, 0.85f)), Mathf.Max(1f, cellPx / 8)));

            // Only rear-facing thrusters fire on throttle (docs/06); others stay dark for now.
            // Engines facing back fire on throttle: thrusters (Mk2/Mk3 too), rockets and ion drives (#80), not when dry.
            if (cell.Rotation == 0 && craft.Controls.Throttle > 0 && !craft.Controls.Brake
                && (cell.PartId.StartsWith("thruster") || (cell.PartId is "rocket" or "ion_drive" or "methalox_rocket" or "nuclear_rocket" or "antimatter_drive" && !craft.DryDrives.Contains(cell.PartId))))
            {
                var flameTop = local + new Vector2(0, cellPx / 2);
                var length = cell.PartId is "rocket" or "methalox_rocket" ? 1.6f : cell.PartId == "antimatter_drive" ? 2.4f : cell.PartId is "ion_drive" or "nuclear_rocket" ? 1.2f : 0.9f;
                BatchTriangle(World(flameTop + new Vector2(-cellPx * 0.3f, 0)), World(flameTop + new Vector2(cellPx * 0.3f, 0)),
                    World(flameTop + new Vector2(0, cellPx * length)), cell.PartId == "ion_drive" ? IonGlowColor : FlameColor);
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
        _mapOffset = Vector2D.Zero;
        _pixelsPerMetre = Mathf.Clamp((float)(Mathf.Min(screen.X - 420, screen.Y) / 2 / extent), MinPixelsPerMetre, MaxPixelsPerMetre);
    }

    // Screen-fixed backdrop: drawn around the camera so it never scrolls away. No parallax yet.
}
