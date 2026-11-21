using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class RendezvousTests
{
    private const double Dt = FixedStepClock.TickSeconds;

    private static (SectorState Sector, CraftState Craft, CelestialBodyState Rock) Setup(ulong seed, Vector2D? at = null)
    {
        var sector = SectorGenerator.Generate(seed);
        var rock = sector.Bodies.First(b => b.IsMineable);
        var design = TestData.StarterShip();
        var craft = new CraftState(sector.AllocateCraftId(), "Test", design, design.ComputeStats(TestData.Catalog()),
            at ?? SectorGenerator.PlayerSpawnPoint(sector), 0);
        sector.AddCraft(craft);
        return (sector, craft, rock);
    }

    [Fact]
    public void OrbitVelocity_MatchesTheChangeInPosition()
    {
        var (sector, _, rock) = Setup(20261120);
        const double t = 123.4, h = 1e-3;

        var numeric = (sector.GetPositionAt(rock.Id, t + h) - sector.GetPositionAt(rock.Id, t - h)) * (1 / (2 * h));
        var analytic = sector.GetVelocityAt(rock.Id, t);

        Assert.True((numeric - analytic).Length < 1e-4);
        Assert.Equal(rock.Orbit!.SpeedMps, analytic.Length, 6);
        Assert.Equal(Vector2D.Zero, sector.GetVelocityAt(sector.Bodies.First(b => b.Orbit is null).Id, t));
    }

    public static IEnumerable<object[]> Seeds() => new[] { 1UL, 2UL, 42UL, 20261120UL }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void GeneratedAsteroid_IsSlowEnoughForTheStarterShipToHoldPosition(ulong seed)
    {
        var (_, craft, rock) = Setup(seed);
        var orbit = rock.Orbit!;
        var centripetal = orbit.SpeedMps * orbit.SpeedMps / orbit.RadiusM;

        Assert.InRange(orbit.SpeedMps, 30, 200);
        Assert.True(centripetal < craft.Stats.MaxAccelerationMps2 * 0.1,
            $"Holding station needs {centripetal:0.00} m/s², ship has {craft.Stats.MaxAccelerationMps2:0.0}.");
    }

    [Fact]
    public void BrakeWithTarget_MatchesTargetVelocity_ThenHoldsStation()
    {
        // Start 200 m off the rock's surface, at rest in the sector frame (so the rock is racing past).
        var (sector, craft, rock) = Setup(20261120);
        var rockPos = sector.GetPosition(rock.Id);
        craft.Position = rockPos + new Vector2D(0, -(rock.RadiusM + 200));
        sector.SetTarget(craft.Id, rock.Id);
        craft.Controls = new ShipControls(0, 0, true);

        // Matching ~100 m/s at ~17.6 m/s² takes well under 10 s.
        for (var i = 0; i < 10 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        Assert.True(sector.GetRelativeMotion(craft.Id, rock.Id).RelativeSpeedMps < 0.1);

        // Hold brake for a further 2 minutes: the ship follows the curving orbit and the gap barely changes.
        var gapAfterMatch = sector.GetRelativeMotion(craft.Id, rock.Id).SurfaceDistanceM;
        for (var i = 0; i < 120 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        var after = sector.GetRelativeMotion(craft.Id, rock.Id);

        Assert.True(after.RelativeSpeedMps < 0.1, $"relative speed {after.RelativeSpeedMps}");
        Assert.InRange(after.SurfaceDistanceM, gapAfterMatch - 5, gapAfterMatch + 5);
    }

    [Fact]
    public void BrakeWithoutTarget_StillStopsInTheSectorFrame()
    {
        var (sector, craft, _) = Setup(20261120);
        craft.Controls = new ShipControls(1, 0, false);
        for (var i = 0; i < 20; i++) sector.Step();

        craft.Controls = new ShipControls(0, 0, true);
        for (var i = 0; i < 40; i++) sector.Step();

        Assert.Equal(Vector2D.Zero, craft.Velocity);
    }

    [Fact]
    public void BrakeTowardsReference_NeverOvershoots()
    {
        var (_, craft, _) = Setup(1);
        var reference = new Vector2D(30, -40); // 50 m/s
        craft.Controls = new ShipControls(0, 0, true);

        var previousGap = double.PositiveInfinity;
        for (var i = 0; i < 100; i++)
        {
            FlightModel.Step(craft, Dt, reference);
            var gap = (craft.Velocity - reference).Length;
            Assert.True(gap <= previousGap);
            previousGap = gap;
        }
        Assert.Equal(reference, craft.Velocity);
    }

    [Fact]
    public void RelativeMotion_ClosingSpeedIsPositiveWhenApproaching()
    {
        var (sector, craft, rock) = Setup(20261120);
        sector.SetTarget(craft.Id, rock.Id);
        var rockPos = sector.GetPosition(rock.Id);
        var rockVel = sector.GetVelocityAt(rock.Id, sector.TimeS);
        craft.Position = rockPos + new Vector2D(0, -5000);

        craft.Velocity = rockVel + new Vector2D(0, 20); // +Y is towards the rock from above
        Assert.Equal(20, sector.GetRelativeMotion(craft.Id, rock.Id).ClosingSpeedMps, 6);

        craft.Velocity = rockVel + new Vector2D(0, -20);
        var away = sector.GetRelativeMotion(craft.Id, rock.Id);
        Assert.Equal(-20, away.ClosingSpeedMps, 6);
        Assert.Equal(20, away.RelativeSpeedMps, 6);
        Assert.Equal(5000 - rock.RadiusM, away.SurfaceDistanceM, 6);
    }

    [Fact]
    public void SetTarget_RejectsUnknownBodies_AndAcceptsNone()
    {
        var (sector, craft, _) = Setup(1);

        Assert.Throws<KeyNotFoundException>(() => sector.SetTarget(craft.Id, new BodyId(99)));
        sector.SetTarget(craft.Id, BodyId.None);
        Assert.True(craft.TargetBodyId.IsNone);
    }
}
