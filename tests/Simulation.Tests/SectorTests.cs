using System;
using System.Linq;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class SectorTests
{
    private const ulong Seed = 20261120;

    [Fact]
    public void SameSeed_GeneratesIdenticalSector()
    {
        var a = SectorGenerator.Generate(Seed);
        var b = SectorGenerator.Generate(Seed);

        Assert.Equal(Describe(a), Describe(b));
    }

    [Fact]
    public void DifferentSeed_GeneratesDifferentSector()
    {
        Assert.NotEqual(Describe(SectorGenerator.Generate(1)), Describe(SectorGenerator.Generate(2)));
    }

    [Fact]
    public void StarterSector_HasAHomePlanet_WithMineableBodiesOrbitingIt()
    {
        var sector = SectorGenerator.Generate(Seed);

        var planet = SectorGenerator.HomePlanet(sector);
        Assert.Equal(SectorGenerator.HomeName, planet.Name);
        Assert.Equal(new[] { "Rock-1", "Rock-2", "Rock-3", "Moon", "Ice-1" }, TestData.HomeRocks(sector).Select(b => b.Name));
        Assert.True(sector.Bodies.First(b => b.Name == "Rock-2").Deposit("silicate") > 0);
        var rock = sector.Bodies.First(b => b.IsMineable);
        Assert.Equal(BodyKind.Star, sector.GetBody(planet.Orbit!.Parent).Kind);
        Assert.Equal(planet.Id, rock.Orbit!.Parent);
        Assert.True(rock.Orbit.RadiusM > planet.RadiusM + rock.RadiusM, "Orbit must clear the planet surface.");
    }

    [Fact]
    public void BodyIds_AreUniqueNonZeroAndStableAcrossGenerations()
    {
        var ids = SectorGenerator.Generate(Seed).Bodies.Select(b => b.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(BodyId.None, ids);
        Assert.Equal(ids, SectorGenerator.Generate(Seed).Bodies.Select(b => b.Id));
    }

    [Fact]
    public void StateAfterNTicks_IsIndependentOfFramePacing()
    {
        // Drive two sectors with different frame rates for the same real time.
        var slow = Drive(SectorGenerator.Generate(Seed), 1.0 / 30, 30 * 60);
        var fast = Drive(SectorGenerator.Generate(Seed), 1.0 / 144, 144 * 60);

        Assert.Equal(slow.Tick, fast.Tick);
        Assert.Equal(Describe(slow), Describe(fast));
    }

    [Fact]
    public void Orbit_ReturnsToStartAfterOnePeriod_AndKeepsItsRadius()
    {
        var sector = SectorGenerator.Generate(Seed);
        var rock = sector.Bodies.First(b => b.IsMineable);
        // Relative to its parent (the home planet itself orbits the star).
        Vector2D Offset(double t) => sector.GetPositionAt(rock.Id, t) - sector.GetPositionAt(rock.Orbit!.Parent, t);
        var start = Offset(0);

        var afterPeriod = Offset(rock.Orbit!.PeriodS);
        var halfway = Offset(rock.Orbit.PeriodS / 2);

        Assert.True(start.DistanceTo(afterPeriod) < 1e-6);
        Assert.Equal(rock.Orbit.RadiusM * 2, start.DistanceTo(halfway), 6);
        Assert.Equal(rock.Orbit.RadiusM, halfway.Length, 6);
    }

    [Fact]
    public void Radius_IsDerivedFromMass_AtFixedDensity()
    {
        var mass = CelestialBodyState.MassFromRadius(1000, 2500);
        Assert.Equal(1000, CelestialBodyState.RadiusFromMass(mass, 2500), 9);

        // docs/02: removing 75% of the mass leaves ~62.996% of the radius.
        var ratio = CelestialBodyState.RadiusFromMass(mass * 0.25, 2500) / 1000;
        Assert.Equal(0.62996, ratio, 5);
    }

    [Fact]
    public void AddBody_RejectsDuplicateIdsAndUnknownParents()
    {
        var sector = new SectorState(1);
        var planet = new CelestialBodyState(new BodyId(1), "P", BodyKind.Planet, 1e9, 5500, false, null);
        sector.AddBody(planet);

        Assert.Throws<InvalidOperationException>(() => sector.AddBody(planet));
        var orphan = new CelestialBodyState(new BodyId(2), "R", BodyKind.Asteroid, 1e6, 2500, true,
            new CircularOrbit(new BodyId(99), 1000, 60, 0));
        Assert.Throws<InvalidOperationException>(() => sector.AddBody(orphan));
    }

    private static SectorState Drive(SectorState sector, double frameDelta, int frames)
    {
        var clock = new FixedStepClock(maxTicksPerAdvance: 1000);
        for (var f = 0; f < frames; f++)
        {
            var ticks = clock.Advance(frameDelta);
            for (var t = 0; t < ticks; t++) sector.Step();
        }
        return sector;
    }

    /// <summary>Exact (round-trip) text of everything that defines the sector at its current tick.</summary>
    private static string Describe(SectorState sector) => string.Join("\n",
        sector.Bodies.Select(b =>
        {
            var p = sector.GetPosition(b.Id);
            return $"{b.Id}|{b.Name}|{b.Kind}|{b.MassKg:R}|{b.DensityKgPerM3:R}|{b.IsMineable}|{b.Orbit}|{p.X:R},{p.Y:R}";
        }).Prepend($"seed={sector.Seed} tick={sector.Tick}"));
}
