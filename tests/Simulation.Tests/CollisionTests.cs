using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class CollisionTests
{
    private static (SectorState Sector, CraftState Craft) Setup(ulong seed = 20261120)
    {
        var sector = SectorGenerator.Generate(seed);
        var design = TestData.StarterShip();
        var craft = new CraftState(sector.AllocateCraftId(), "Test", design, design.ComputeStats(TestData.Catalog()),
            SectorGenerator.PlayerSpawnPoint(sector), 0);
        sector.AddCraft(craft);
        return (sector, craft);
    }

    private static CelestialBodyState Primary(SectorState s) => s.Bodies.First(b => b.Orbit is null);
    private static CelestialBodyState Rock(SectorState s) => s.Bodies.First(b => b.IsMineable);

    private static void Run(SectorState sector, CraftState craft, ShipControls controls, int ticks)
    {
        craft.Controls = controls;
        for (var i = 0; i < ticks; i++) sector.Step();
    }

    private static double CentreDistance(SectorState s, CraftState c, CelestialBodyState b) =>
        s.GetPosition(b.Id).DistanceTo(c.Position);

    [Fact]
    public void BoundingRadius_CoversEveryCellCorner()
    {
        var single = new ShipDesign("one", "one", new[] { new DesignCell(new GridPos(0, 0), "cockpit") });
        Assert.Equal(Math.Sqrt(0.5) * ShipDesign.CellSizeM, single.ComputeStats(TestData.Catalog()).BoundingRadiusM, 9);

        var stats = TestData.StarterShip().ComputeStats(TestData.Catalog());
        foreach (var cell in TestData.StarterShip().Cells)
        foreach (var (dx, dy) in new[] { (-0.5, -0.5), (0.5, -0.5), (-0.5, 0.5), (0.5, 0.5) })
        {
            var x = (cell.Pos.X + dx - stats.CenterOfMassX) * ShipDesign.CellSizeM;
            var y = (cell.Pos.Y + dy - stats.CenterOfMassY) * ShipDesign.CellSizeM;
            Assert.True(Math.Sqrt(x * x + y * y) <= stats.BoundingRadiusM + 1e-9);
        }
    }

    [Fact]
    public void FlyingIntoThePlanet_LandsOnItsSurface_AndStops()
    {
        var (sector, craft) = Setup();
        var planet = Primary(sector);
        craft.HeadingRad = Math.PI; // nose towards +Y, i.e. down at the planet below the spawn point

        Run(sector, craft, new ShipControls(1, 0, false), 200);

        Assert.True(craft.IsLanded);
        Assert.Equal(planet.Id, craft.LandedOnBodyId);
        Assert.InRange(sector.SurfaceGap(craft, planet), -1e-6, 1e-6); // resting on its lowest cell
        Assert.Equal(Vector2D.Zero, craft.Velocity); // the primary doesn't move
    }

    [Fact]
    public void ThrustingIntoTheSurface_StaysLanded_ThrustingAway_LiftsOff()
    {
        var (sector, craft) = Setup();
        var planet = Primary(sector);
        craft.HeadingRad = Math.PI;
        Run(sector, craft, new ShipControls(1, 0, false), 200);
        var landedAt = craft.Position;

        Run(sector, craft, new ShipControls(1, 0, false), 100); // still pushing down
        Assert.True(craft.IsLanded);
        Assert.Equal(landedAt, craft.Position);

        Run(sector, craft, new ShipControls(0, 1, false), 42); // turning on the ground is allowed
        Assert.True(craft.IsLanded);

        craft.HeadingRad = 0; // nose up, away from the planet
        Run(sector, craft, new ShipControls(1, 0, false), 40);
        Assert.False(craft.IsLanded);
        Assert.True(CentreDistance(sector, craft, planet) > planet.RadiusM + craft.CollisionRadiusM + 10);
    }

    [Fact]
    public void LandedOnTheMovingAsteroid_RidesAlong_WithAFixedOffset()
    {
        var (sector, craft) = Setup();
        var rock = Rock(sector);
        // Drop the ship onto the rock: start just outside, falling in at 5 m/s relative.
        var rockPos = sector.GetPosition(rock.Id);
        craft.Position = rockPos + new Vector2D(0, -(rock.RadiusM + craft.CollisionRadiusM + 2));
        craft.Velocity = sector.GetVelocityAt(rock.Id, sector.TimeS) + new Vector2D(0, 5);
        Run(sector, craft, ShipControls.Idle, 20);
        Assert.Equal(rock.Id, craft.LandedOnBodyId);
        var offset = craft.Position - sector.GetPosition(rock.Id);

        // Ten minutes of orbit, holding brake part of the time: no drift and no spurious lift-off.
        Run(sector, craft, ShipControls.Idle, 6000);
        Run(sector, craft, new ShipControls(0, 0, true), 6000);

        Assert.True(craft.IsLanded);
        Assert.True((craft.Position - sector.GetPosition(rock.Id) - offset).Length < 1e-6);
        Assert.True((craft.Velocity - sector.GetVelocityAt(rock.Id, sector.TimeS)).Length < 1e-9);
    }

    [Fact]
    public void FastImpact_StillEndsOnTheSurface_NotInside()
    {
        var (sector, craft) = Setup();
        var planet = Primary(sector);
        craft.Velocity = new Vector2D(0, 2000); // 2 km/s straight down: 100 m per tick
        Run(sector, craft, ShipControls.Idle, 5);

        Assert.True(craft.IsLanded);
        Assert.InRange(sector.SurfaceGap(craft, planet), -1e-6, 1e-6);
    }

    [Fact]
    public void RandomFlying_NeverEndsATickInsideABody()
    {
        var (sector, craft) = Setup(7);
        var rng = new DeterministicRandom(123);
        var landings = 0;
        for (var tick = 0; tick < 40_000; tick++)
        {
            if (tick % 40 == 0)
                craft.Controls = new ShipControls(rng.NextDouble() < 0.7 ? 1 : 0, rng.Range(-1, 1), rng.NextDouble() < 0.1);
            var wasLanded = craft.IsLanded;
            sector.Step();
            if (!wasLanded && craft.IsLanded) landings++;

            foreach (var body in sector.Bodies)
                Assert.True(sector.SurfaceGap(craft, body) >= -1e-6, $"tick {tick}: a cell is inside {body.Name} by {-sector.SurfaceGap(craft, body)} m (landed={craft.IsLanded}, anchored={craft.IsAnchored}, controls={craft.Controls})");
        }
        Assert.True(landings > 0, "The random pilot should hit the planet at least once (it spawns 300 m up).");
    }

    [Fact]
    public void SaveWhileLanded_LoadsLanded_AndContinuesIdentically()
    {
        static SectorState LandOnRock()
        {
            var (sector, craft) = Setup();
            var rock = Rock(sector);
            craft.Position = sector.GetPosition(rock.Id) + new Vector2D(rock.RadiusM + craft.CollisionRadiusM + 1, 0);
            craft.Velocity = sector.GetVelocityAt(rock.Id, sector.TimeS) + new Vector2D(-3, 0);
            for (var i = 0; i < 400 && !craft.IsLanded; i++) sector.Step();
            Assert.True(craft.IsLanded);
            return sector;
        }

        var reference = LandOnRock();
        var refCraft = reference.Crafts[0];
        Run(reference, refCraft, ShipControls.Idle, 500);
        refCraft.HeadingRad = Math.PI / 2; // nose +X: away from the rock, so the next thrust lifts off
        Run(reference, refCraft, new ShipControls(1, 0, false), 100);

        var saved = LandOnRock();
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(saved, saved.Crafts[0].Id), TestData.Catalog()).Sector;
        var craft = loaded.Crafts[0];
        Assert.True(craft.IsLanded);
        Run(loaded, craft, ShipControls.Idle, 500);
        craft.HeadingRad = Math.PI / 2;
        Run(loaded, craft, new ShipControls(1, 0, false), 100);

        Assert.False(craft.IsLanded);
        Assert.Equal(refCraft.Position, craft.Position);
        Assert.Equal(refCraft.Velocity, craft.Velocity);
    }

    [Fact]
    public void Version1Save_StillLoads_AsFlying()
    {
        var (sector, craft) = Setup();
        var v2 = SaveGameSerializer.ToJson(sector, craft.Id);
        var v1 = v2.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1")
                   .Replace("\"landedOnBodyId\": 0,", "")
                   .Replace("\"landedOffset\": null", "\"unused\": null");
        Assert.DoesNotContain("landed", v1);

        var loaded = SaveGameSerializer.FromJson(v1, TestData.Catalog());

        Assert.False(loaded.Sector.Crafts[0].IsLanded);
        Assert.Equal(craft.Position, loaded.Sector.Crafts[0].Position);
    }

    [Fact]
    public void Load_RejectsLandingOnAMissingBody()
    {
        var (sector, craft) = Setup();
        craft.HeadingRad = Math.PI;
        Run(sector, craft, new ShipControls(1, 0, false), 200);
        var json = SaveGameSerializer.ToJson(sector, craft.Id);
        Assert.Contains("\"landedOnBodyId\": 1", json);

        Assert.Throws<FormatException>(() =>
            SaveGameSerializer.FromJson(json.Replace("\"landedOnBodyId\": 1", "\"landedOnBodyId\": 9"), TestData.Catalog()));
    }
}
