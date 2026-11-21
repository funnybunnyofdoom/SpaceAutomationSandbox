using Godot;
using SpaceAutomationSandbox.Simulation;

namespace SpaceAutomationSandbox.Core;

/// <summary>
/// Main scene: hosts the simulation and draws it. This node is a VIEW. The sector state lives
/// in <see cref="SectorState"/> (pure C#, no Godot), which advances only in whole fixed ticks
/// from <see cref="FixedStepClock"/>. Rendering interpolates between ticks and never writes state.
/// </summary>
public partial class Main : Node2D
{
    // Fixed seeds so the backdrop and sector are identical on every run.
    private const int StarfieldSeed = 20261120;
    private const ulong SectorSeed = 20261120;
    private const int StarCount = 220;

    // View scale: 1 px = 100 m at zoom 1. Placeholder until the camera gets zoom controls.
    private const float PixelsPerMetre = 0.01f;

    private static readonly Color SpaceColor = new(0.02f, 0.02f, 0.06f);
    private static readonly Color PlanetColor = new(0.30f, 0.55f, 0.85f);
    private static readonly Color AsteroidColor = new(0.65f, 0.55f, 0.45f);
    private static readonly Color OrbitColor = new(1f, 1f, 1f, 0.12f);
    private static readonly Color NameColor = new(0.8f, 0.8f, 0.85f);

    private readonly FixedStepClock _clock = new();
    private SectorState _sector = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(SpaceColor);
        _sector = SectorGenerator.Generate(SectorSeed);

        _status = new Label { Position = new Vector2(-620, -340) };
        AddChild(_status);

        GD.Print($"[Bootstrap] Main scene ready. C# assembly loaded. Godot {Engine.GetVersionInfo()["string"].AsString()}");
        GD.Print($"[Sector] seed={_sector.Seed} bodies={_sector.Bodies.Count} tickRate={FixedStepClock.TicksPerSecond}/s");
    }

    public override void _Process(double delta)
    {
        var ticks = _clock.Advance(delta);
        for (var i = 0; i < ticks; i++) _sector.Step();

        _status.Text = "SpaceAutomationSandbox (working title) - Phase 1 sim core\n"
                       + $"seed {_sector.Seed}   tick {_sector.Tick} ({FixedStepClock.TicksPerSecond}/s)   t={_sector.TimeS:0.0}s"
                       + (_clock.DroppedTicks > 0 ? $"   dropped {_clock.DroppedTicks}" : "");
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawStarfield();

        // Interpolated time: smooth motion between 20 Hz ticks without touching the state.
        var renderTimeS = (_sector.Tick + _clock.Alpha) * FixedStepClock.TickSeconds;
        var font = ThemeDB.FallbackFont;

        foreach (var body in _sector.Bodies)
        {
            if (body.Orbit is { } orbit)
            {
                var parentPx = ToPixels(_sector.GetPositionAt(orbit.Parent, renderTimeS));
                DrawArc(parentPx, (float)orbit.RadiusM * PixelsPerMetre, 0, Mathf.Tau, 128, OrbitColor);
            }

            var centre = ToPixels(_sector.GetPositionAt(body.Id, renderTimeS));
            var radiusPx = Mathf.Max(2f, (float)body.RadiusM * PixelsPerMetre);
            DrawCircle(centre, radiusPx, body.Kind == BodyKind.Planet ? PlanetColor : AsteroidColor);
            DrawString(font, centre + new Vector2(radiusPx + 4, 4), body.Name,
                HorizontalAlignment.Left, -1, 12, NameColor);
        }
    }

    private static Vector2 ToPixels(Vector2D metres) =>
        new((float)(metres.X * PixelsPerMetre), (float)(metres.Y * PixelsPerMetre));

    private void DrawStarfield()
    {
        var rng = new RandomNumberGenerator { Seed = StarfieldSeed };
        for (var i = 0; i < StarCount; i++)
        {
            var position = new Vector2(rng.RandfRange(-640, 640), rng.RandfRange(-360, 360));
            var brightness = rng.RandfRange(0.35f, 1.0f);
            var size = rng.Randf() < 0.1f ? 2f : 1f;
            DrawRect(new Rect2(position.Floor(), new Vector2(size, size)),
                new Color(brightness, brightness, brightness));
        }
    }
}
