using System;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class FlightTests
{
    private const double Dt = FixedStepClock.TickSeconds;

    private static CraftState NewCraft(Vector2D? position = null)
    {
        var catalog = TestData.Catalog();
        var design = TestData.StarterShip();
        return new CraftState(new CraftId(1), "Test", design, design.ComputeStats(catalog),
            position ?? Vector2D.Zero, headingRad: 0);
    }

    private static void Run(CraftState craft, ShipControls controls, int ticks)
    {
        craft.Controls = controls;
        for (var i = 0; i < ticks; i++) FlightModel.Step(craft, Dt);
    }

    [Fact]
    public void FullThrottle_AcceleratesAtThrustOverMass_AlongTheNose()
    {
        var craft = NewCraft();
        Run(craft, new ShipControls(1, 0, false), FixedStepClock.TicksPerSecond); // 1 s

        Assert.Equal(craft.Stats.MaxAccelerationMps2, craft.SpeedMps, 9);
        Assert.Equal(0, craft.Velocity.X, 9);
        Assert.True(craft.Velocity.Y < 0, "Heading 0 faces −Y (up on screen).");
    }

    [Fact]
    public void WithoutInput_VelocityIsConserved()
    {
        var craft = NewCraft();
        Run(craft, new ShipControls(1, 0, false), 10);
        var velocity = craft.Velocity;
        var position = craft.Position;

        Run(craft, ShipControls.Idle, 100);

        Assert.Equal(velocity, craft.Velocity);
        Assert.Equal(position.Y + velocity.Y * Dt * 100, craft.Position.Y, 6);
    }

    [Fact]
    public void Brake_StopsExactly_WithoutReversing_InTheExpectedTime()
    {
        var craft = NewCraft();
        Run(craft, new ShipControls(1, 0, false), 40); // 2 s of thrust
        var speed = craft.SpeedMps;

        var ticks = 0;
        craft.Controls = new ShipControls(0, 0, true);
        while (craft.SpeedMps > 0 && ticks < 1000)
        {
            var before = craft.Velocity;
            FlightModel.Step(craft, Dt);
            Assert.True(craft.Velocity.Y <= 0 && craft.Velocity.Y >= before.Y, "Brake must not push into reverse.");
            ticks++;
        }

        Assert.Equal(Vector2D.Zero, craft.Velocity);
        // Same thrust both ways, so braking takes as long as speeding up (±1 tick of rounding).
        Assert.InRange(ticks, 40, 41);
        Assert.True(speed > 0);
    }

    [Fact]
    public void Turning_RotatesAtTheFixedRate_AndThrustFollowsTheNose()
    {
        var craft = NewCraft();
        // Turn clockwise for whatever time gives a 90° turn, then thrust: should move +X (right).
        var ticks = (int)Math.Round(Math.PI / 2 / FlightModel.TurnRateRadPerS / Dt);
        Run(craft, new ShipControls(0, 1, false), ticks);
        Assert.Equal(Math.PI / 2, craft.HeadingRad, 1);

        Run(craft, new ShipControls(1, 0, false), 20);
        Assert.True(craft.Velocity.X > 0);
        Assert.True(Math.Abs(craft.Velocity.Y) < craft.Velocity.X * 0.05);
    }

    [Fact]
    public void ControlsAreClamped()
    {
        var a = NewCraft();
        var b = NewCraft();
        Run(a, new ShipControls(5, 7, false), 20);
        Run(b, new ShipControls(1, 1, false), 20);

        Assert.Equal(b.Velocity, a.Velocity);
        Assert.Equal(b.HeadingRad, a.HeadingRad);

        var c = NewCraft();
        Run(c, new ShipControls(double.NaN, double.NaN, false), 20);
        Assert.Equal(Vector2D.Zero, c.Velocity);
    }

    [Fact]
    public void SameInputScript_ReplaysToIdenticalState_InASector()
    {
        static string Fly()
        {
            var sector = SectorGenerator.Generate(42);
            var craft = NewCraft(SectorGenerator.PlayerSpawnPoint(sector));
            sector.AddCraft(craft);
            var rng = new DeterministicRandom(99); // scripted "pilot"
            for (var tick = 0; tick < 2000; tick++)
            {
                if (tick % 15 == 0)
                    craft.Controls = new ShipControls(rng.NextDouble(), rng.Range(-1, 1), rng.NextDouble() < 0.2);
                sector.Step();
            }
            return $"{sector.Tick}|{craft.Position.X:R},{craft.Position.Y:R}|{craft.Velocity.X:R},{craft.Velocity.Y:R}|{craft.HeadingRad:R}";
        }

        Assert.Equal(Fly(), Fly());
    }

    [Fact]
    public void Sector_SpawnsAboveThePrimary_AndReportsNearestBody()
    {
        var sector = SectorGenerator.Generate(20261120);
        var spawn = SectorGenerator.PlayerSpawnPoint(sector);

        var (body, surfaceDistance) = sector.NearestBody(spawn);

        Assert.Equal(BodyKind.Planet, body.Kind);
        Assert.Equal(SectorGenerator.PlayerSpawnAltitudeM, surfaceDistance, 6);
    }

    [Fact]
    public void CraftIds_AreAllocatedUniquely()
    {
        var sector = SectorGenerator.Generate(1);
        var a = sector.AllocateCraftId();
        var b = sector.AllocateCraftId();

        Assert.NotEqual(a, b);
        Assert.False(a.IsNone);
        Assert.Throws<ArgumentException>(() => new CraftState(CraftId.None, "x", TestData.StarterShip(),
            TestData.StarterShip().ComputeStats(TestData.Catalog()), Vector2D.Zero, 0));
    }
}
