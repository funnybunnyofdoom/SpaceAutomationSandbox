using System.Collections.Generic;
using Godot;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

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
    private const ulong SectorSeed = 20261120;
    private const int StarCount = 220;

    private const string PartsPath = "res://data/parts/parts.json";
    private const string StarterDesignPath = "res://data/blueprints/starter_ship.json";

    // Zoom in screen pixels per sector metre. Mouse wheel changes it; this is view state only.
    private const float MinPixelsPerMetre = 0.005f;
    private const float MaxPixelsPerMetre = 8f;
    private const float ZoomStep = 1.15f;
    private float _pixelsPerMetre = 1f;

    private static readonly Color SpaceColor = new(0.02f, 0.02f, 0.06f);
    private static readonly Color PlanetColor = new(0.30f, 0.55f, 0.85f);
    private static readonly Color AsteroidColor = new(0.65f, 0.55f, 0.45f);
    private static readonly Color OrbitColor = new(1f, 1f, 1f, 0.12f);
    private static readonly Color NameColor = new(0.8f, 0.8f, 0.85f);
    private static readonly Color FlameColor = new(1f, 0.75f, 0.3f, 0.9f);
    private static readonly Color MarkerColor = new(0.4f, 1f, 0.6f);
    private static readonly Color TargetLineColor = new(0.4f, 1f, 0.6f, 0.25f);

    private readonly FixedStepClock _clock = new();
    private readonly Dictionary<string, Color> _partColors = new();
    private SectorState _sector = null!;
    private CraftState? _player;
    private string _setupError = "";
    private Camera2D _camera = null!;
    private Label _hud = null!;

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(SpaceColor);
        _camera = GetNode<Camera2D>("Camera2D");

        var hudLayer = new CanvasLayer();
        AddChild(hudLayer);
        _hud = new Label { Position = new Vector2(20, 16) };
        hudLayer.AddChild(_hud);

        _sector = SectorGenerator.Generate(SectorSeed);
        SpawnPlayer();

        GD.Print($"[Bootstrap] Main scene ready. C# assembly loaded. Godot {Engine.GetVersionInfo()["string"].AsString()}");
        GD.Print($"[Sector] seed={_sector.Seed} bodies={_sector.Bodies.Count} tickRate={FixedStepClock.TicksPerSecond}/s");
        if (_player is { } p)
            GD.Print($"[Craft] {p.Id} '{p.Design.Name}' cells={p.Stats.CellCount} mass={p.Stats.TotalMassKg}kg thrust={p.Stats.TotalThrustN}N");
    }

    private void SpawnPlayer()
    {
        try
        {
            var catalog = PartCatalog.FromJson(Godot.FileAccess.GetFileAsString(PartsPath));
            var design = ShipDesign.FromJson(Godot.FileAccess.GetFileAsString(StarterDesignPath));
            var errors = design.Validate(catalog);
            if (errors.Count > 0)
            {
                _setupError = $"Starter design '{design.Id}' is invalid:\n  " + string.Join("\n  ", errors);
                GD.PushError(_setupError);
                return;
            }

            foreach (var part in catalog.Parts)
                _partColors[part.Id] = part.PlaceholderColor is { } hex ? Color.FromHtml(hex) : Colors.Magenta;

            _player = new CraftState(_sector.AllocateCraftId(), "Player", design, design.ComputeStats(catalog),
                SectorGenerator.PlayerSpawnPoint(_sector), headingRad: 0);
            _sector.AddCraft(_player);
            _sector.SetTarget(_player.Id, _sector.NearestBody(_player.Position).Body.Id);
        }
        catch (System.Exception e) // bad or missing data file: show it instead of crashing
        {
            _setupError = $"Could not load ship data: {e.Message}";
            GD.PushError(_setupError);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } mouse)
        {
            if (mouse.ButtonIndex == MouseButton.WheelUp) _pixelsPerMetre *= ZoomStep;
            else if (mouse.ButtonIndex == MouseButton.WheelDown) _pixelsPerMetre /= ZoomStep;
            _pixelsPerMetre = Mathf.Clamp(_pixelsPerMetre, MinPixelsPerMetre, MaxPixelsPerMetre);
        }
        else if (@event is InputEventKey { Pressed: true, Echo: false } key && _player != null)
        {
            if (key.PhysicalKeycode == Key.Tab) CycleTarget();
            else if (key.PhysicalKeycode == Key.T)
                _sector.SetTarget(_player.Id, _sector.NearestBody(_player.Position).Body.Id);
        }
    }

    /// <summary>Tab order: each body by id, then "no target" (brake stops relative to the sector).</summary>
    private void CycleTarget()
    {
        var bodies = _sector.Bodies;
        var index = -1;
        for (var i = 0; i < bodies.Count; i++)
            if (bodies[i].Id == _player!.TargetBodyId) index = i;

        var next = index + 1;
        _sector.SetTarget(_player!.Id, next < bodies.Count ? bodies[next].Id : BodyId.None);
    }

    public override void _Process(double delta)
    {
        // Input is sampled once per frame and held for every tick run this frame.
        if (_player != null) _player.Controls = ReadControls();

        var ticks = _clock.Advance(delta);
        for (var i = 0; i < ticks; i++) _sector.Step();

        if (_player != null) _camera.Position = ToPixels(InterpolatedPosition(_player));
        _hud.Text = BuildHud();
        QueueRedraw();
    }

    // Raw keys for now; move to InputMap actions when rebinding or gamepads are needed.
    private static ShipControls ReadControls()
    {
        static bool Down(Key a, Key b) => Input.IsPhysicalKeyPressed(a) || Input.IsPhysicalKeyPressed(b);
        var throttle = Down(Key.W, Key.Up) ? 1.0 : 0.0;
        var turn = (Down(Key.D, Key.Right) ? 1.0 : 0.0) - (Down(Key.A, Key.Left) ? 1.0 : 0.0);
        return new ShipControls(throttle, turn, Down(Key.S, Key.Down) || Input.IsPhysicalKeyPressed(Key.Space));
    }

    private string BuildHud()
    {
        var text = "SpaceAutomationSandbox (working title) - Phase 1\n"
                   + $"seed {_sector.Seed}   tick {_sector.Tick} ({FixedStepClock.TicksPerSecond}/s)   t={_sector.TimeS:0.0}s"
                   + (_clock.DroppedTicks > 0 ? $"   dropped {_clock.DroppedTicks}" : "")
                   + $"   zoom {_pixelsPerMetre:0.###} px/m\n";

        if (_player is not { } p) return text + _setupError;

        var (nearest, nearestSurface) = _sector.NearestBody(p.Position);
        var headingDeg = Mathf.PosMod(Mathf.RadToDeg((float)p.HeadingRad), 360f);
        text += $"{p.Design.Name}: {p.Stats.TotalMassKg / 1000:0.0} t, {p.Stats.TotalThrustN / 1000:0} kN, "
                + $"{p.Stats.MaxAccelerationMps2:0.0} m/s²   cargo cap {p.Stats.CargoCapacityKg / 1000:0} t\n"
                + $"speed {p.SpeedMps:0.0} m/s (sector)   heading {headingDeg:000}°   "
                + $"pos ({p.Position.X / 1000:0.00}, {p.Position.Y / 1000:0.00}) km\n";

        if (p.TargetBodyId.IsNone)
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
               + "W/Up thrust   A/D turn   S/Space brake (matches target)   Tab cycle target   T target nearest   wheel zoom";
    }

    private static string Describe(CelestialBodyState body) =>
        $"{body.Name} ({body.Kind}{(body.IsMineable ? ", mineable" : "")})";

    private static string FormatDistance(double metres) =>
        System.Math.Abs(metres) >= 1000 ? $"{metres / 1000:0.00} km" : $"{metres:0} m";

    public override void _Draw()
    {
        DrawStarfield();

        // Interpolated time: smooth motion between 20 Hz ticks without touching the state.
        var renderTimeS = (_sector.Tick + _clock.Alpha) * FixedStepClock.TickSeconds;
        foreach (var body in _sector.Bodies) DrawBody(body, renderTimeS);
        if (_player is { TargetBodyId.IsNone: false }) DrawTargetMarker(_player, renderTimeS);
        foreach (var craft in _sector.Crafts) DrawCraft(craft);
    }

    // Bracket ring around the target plus a faint line from the ship, so it can be found off-screen.
    private void DrawTargetMarker(CraftState craft, double renderTimeS)
    {
        var target = _sector.GetBody(craft.TargetBodyId);
        var centre = ToPixels(_sector.GetPositionAt(target.Id, renderTimeS));
        var radiusPx = Mathf.Max(2f, (float)target.RadiusM * _pixelsPerMetre) + 6f;

        DrawLine(ToPixels(InterpolatedPosition(craft)), centre, TargetLineColor, 1f);
        for (var i = 0; i < 4; i++) // four corner arcs
        {
            var start = Mathf.Tau * i / 4 + Mathf.Pi / 8;
            DrawArc(centre, radiusPx, start, start + Mathf.Pi / 4, 16, MarkerColor, 2f);
        }
    }

    private void DrawBody(CelestialBodyState body, double renderTimeS)
    {
        if (body.Orbit is { } orbit)
        {
            var parentPx = ToPixels(_sector.GetPositionAt(orbit.Parent, renderTimeS));
            DrawArc(parentPx, (float)orbit.RadiusM * _pixelsPerMetre, 0, Mathf.Tau, 512, OrbitColor);
        }

        var centre = ToPixels(_sector.GetPositionAt(body.Id, renderTimeS));
        var radiusPx = Mathf.Max(2f, (float)body.RadiusM * _pixelsPerMetre);
        var color = body.Kind == BodyKind.Planet ? PlanetColor : AsteroidColor;
        if (radiusPx < 200f) DrawCircle(centre, radiusPx, color);
        else DrawColoredPolygon(CirclePoints(centre, radiusPx, 512), color); // smoother edge up close

        DrawString(ThemeDB.FallbackFont, centre + new Vector2(radiusPx + 4, 4), body.Name,
            HorizontalAlignment.Left, -1, 12, NameColor);
    }

    private void DrawCraft(CraftState craft)
    {
        var centre = ToPixels(InterpolatedPosition(craft));
        var heading = (float)Mathf.LerpAngle(craft.PreviousHeadingRad, craft.HeadingRad, _clock.Alpha);
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
            DrawRect(rect, _partColors.GetValueOrDefault(cell.PartId, Colors.Magenta));

            if (cell.PartId == "thruster" && craft.Controls.Throttle > 0 && !craft.Controls.Brake)
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
        craft.PreviousPosition + (craft.Position - craft.PreviousPosition) * _clock.Alpha;

    private Vector2 ToPixels(Vector2D metres) =>
        new((float)(metres.X * _pixelsPerMetre), (float)(metres.Y * _pixelsPerMetre));

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
        var origin = _camera.Position;
        var rng = new RandomNumberGenerator { Seed = StarfieldSeed };
        for (var i = 0; i < StarCount; i++)
        {
            var position = origin + new Vector2(rng.RandfRange(-640, 640), rng.RandfRange(-360, 360));
            var brightness = rng.RandfRange(0.35f, 1.0f);
            var size = rng.Randf() < 0.1f ? 2f : 1f;
            DrawRect(new Rect2(position.Floor(), new Vector2(size, size)),
                new Color(brightness, brightness, brightness));
        }
    }
}
