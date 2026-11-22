using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Local gravity (#32), impact damage and craft collisions (#34).</summary>
public class GravityAndDamageTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerSecond = FixedStepClock.TicksPerSecond;

    private static long Mass(SectorState sector) => sector.ComputeMassLedger().TotalKg;

    /// <summary>Unit vector from Home's centre, <paramref name="angle"/> radians round from straight up.</summary>
    private static Vector2D Up(double angle = 0) => new(Math.Sin(angle), -Math.Cos(angle));

    /// <summary>A plain starter ship (no job) <paramref name="altitude"/> above Home's surface, nose up, moving with Home.</summary>
    private static CraftState AddShip(SectorState sector, double altitude, double angle = 0, string name = "Test")
    {
        var design = TestData.StarterShip();
        var stats = design.ComputeStats(Catalog);
        var home = SectorGenerator.HomePlanet(sector);
        var ship = new CraftState(sector.AllocateCraftId(), name, design, stats,
            sector.GetPosition(home.Id) + Up(angle) * (home.RadiusM + stats.BoundingRadiusM + altitude), angle)
        {
            Velocity = sector.GetVelocityAt(home.Id, sector.TimeS),
        };
        sector.AddCraft(ship);
        return ship;
    }

    [Fact]
    public void Gravity_PullsTowardsTheInnermostBody_AndFallsOffWithDistance()
    {
        var sector = TestData.NewGame().Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var centre = sector.GetPosition(home.Id);

        var atSurface = sector.GravityAt(centre + new Vector2D(0, -home.RadiusM), 0);
        Assert.Equal(SectorState.SurfaceGravityMps2(BodyKind.Planet), atSurface.Length, 9);
        Assert.True(atSurface.Y > 0); // points down at Home (+Y from above)
        Assert.Equal(atSurface.Length / 4, sector.GravityAt(centre + new Vector2D(0, -2 * home.RadiusM), 0).Length, 9);
        Assert.Equal(Vector2D.Zero, sector.GravityAt(centre + new Vector2D(0, -SectorState.GravityReachRadii * home.RadiusM - 10), 0));

        // Next to the Moon its own pull applies, not Home's (innermost reach wins).
        var moon = sector.Bodies.Single(b => b.Name == "Moon");
        Assert.Equal(moon, sector.GravitySource(sector.GetPosition(moon.Id) + new Vector2D(moon.RadiusM + 100, 0)));
    }

    [Fact]
    public void ShipLeftAlone_FallsAndLands_ButTheBrakeHovers()
    {
        var sector = TestData.NewGame().Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var hovering = AddShip(sector, 200, -0.4, "Hover");
        var falling = AddShip(sector, 200, 0.4, "Fall");
        sector.SetTarget(hovering.Id, home.Id);
        hovering.Controls = new ShipControls(0, 0, true);
        var height = sector.SurfaceGap(hovering, home);

        for (var i = 0; i < 40 * TicksPerSecond; i++) sector.Step();

        Assert.True(falling.IsLanded); // ~200 m at 0.6 m/s² takes ~26 s
        Assert.False(hovering.IsLanded);
        Assert.InRange(sector.SurfaceGap(hovering, home), height - 5, height + 5);
    }

    [Fact]
    public void NewGame_PlayerStartsInOrbit_AndTheYardKeepsPace()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var player = sector.GetCraft(game.PlayerCraftId);
        player.Controls = ShipControls.Idle;
        var altitude = sector.GetPosition(home.Id).DistanceTo(player.Position) - home.RadiusM;

        for (var i = 0; i < 5 * 60 * TicksPerSecond; i++) sector.Step();

        Assert.False(player.IsLanded);
        Assert.InRange(sector.GetPosition(home.Id).DistanceTo(player.Position) - home.RadiusM, altitude - 20, altitude + 20); // still in orbit
        var yard = sector.GetCraft(game.ShipyardId);
        Assert.Equal(SectorState.CircularOrbitSpeedMps(home, yard.RailOrbit!.RadiusM), yard.RailOrbit.SpeedMps, 6);
    }

    [Fact]
    public void SoftLanding_IsHarmless_HardLanding_BreaksTheLowestParts_AndConservesMass()
    {
        var sector = TestData.NewGame().Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var soft = AddShip(sector, 3, -0.5, "Soft"); // low, so gravity adds little to the 4 m/s
        var hard = AddShip(sector, 60, 0.5, "Hard");
        var mass = Mass(sector);
        soft.Velocity += Up(-0.5) * -4; // 4 m/s down
        hard.Velocity += Up(0.5) * -40; // 40 m/s down, tail first (the anchor clamp and thrusters)

        for (var i = 0; i < 15 * TicksPerSecond && !(soft.IsLanded && hard.IsLanded); i++) sector.Step();

        Assert.True(soft.IsLanded && hard.IsLanded);
        Assert.Empty(soft.PartHealth);
        Assert.True(hard.PartsLost > 0, "a 40 m/s landing breaks parts off");
        Assert.DoesNotContain(hard.Design.Cells, c => c.PartId == "anchor_clamp"); // the lowest part took the hit
        Assert.True(hard.Design.Cells.Count < soft.Design.Cells.Count);
        Assert.Equal(mass, Mass(sector)); // broken parts are debris in the ledger
        Assert.Contains(sector.RecentImpacts, e => e.Craft == hard.Id && e.With == home.Name);
    }

    [Fact]
    public void DamagedThruster_PushesLess()
    {
        var design = TestData.StarterShip();
        var thruster = design.Cells.First(c => c.PartId == "thruster");
        var full = design.ComputeStats(Catalog);
        var damaged = design.ComputeStats(Catalog, new System.Collections.Generic.Dictionary<GridPos, double> { [thruster.Pos] = 0.5 });
        Assert.Equal(full.TotalThrustN - Catalog.Get("thruster").ThrustN * 0.5, damaged.TotalThrustN, 6);
        Assert.Equal(full.TotalMassKg, damaged.TotalMassKg);
    }

    [Fact]
    public void Ships_PushEachOtherApart_GentleBumpsAreHarmless_FastOnesHurt()
    {
        var sector = TestData.NewGame().Sector;
        // Out of every body's reach, so only the bump moves them.
        var home = SectorGenerator.HomePlanet(sector);
        var far = Up() * (SectorState.GravityReachRadii * home.RadiusM + 5_000);
        var a = AddShip(sector, 0, 0, "A");
        var b = AddShip(sector, 0, 0, "B");
        var c = AddShip(sector, 0, 0, "C");
        var d = AddShip(sector, 0, 0, "D");
        var homePos = sector.GetPosition(home.Id);
        a.Position = a.PreviousPosition = homePos + far + new Vector2D(-30, 0);
        b.Position = b.PreviousPosition = homePos + far + new Vector2D(30, 0);
        c.Position = c.PreviousPosition = homePos + far + new Vector2D(-30, 400);
        d.Position = d.PreviousPosition = homePos + far + new Vector2D(30, 400);
        a.Velocity += new Vector2D(2, 0);
        b.Velocity += new Vector2D(-2, 0); // 4 m/s closing: a harmless bump
        c.Velocity += new Vector2D(15, 0);
        d.Velocity += new Vector2D(-15, 0); // 30 m/s closing
        var mass = Mass(sector);

        for (var i = 0; i < 10 * TicksPerSecond; i++) sector.Step();

        Assert.True(b.Position.X - a.Position.X > 0, "they don't pass through each other");
        Assert.Empty(a.PartHealth);
        Assert.Empty(b.PartHealth);
        Assert.True(c.PartHealth.Count + c.PartsLost > 0 && d.PartHealth.Count + d.PartsLost > 0);
        Assert.Equal(mass, Mass(sector));
    }

    [Fact]
    public void Damage_SurvivesSaveLoad_AndBrokenShipsStillLoad()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var hard = AddShip(sector, 60, 0, "Hard");
        hard.Velocity += Up() * -60; // straight down
        for (var i = 0; i < 5 * TicksPerSecond && !hard.IsLanded; i++) sector.Step();
        Assert.True(hard.PartsLost > 0);

        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        var copy = loaded.GetCraft(hard.Id);
        Assert.Equal(hard.PartHealth, copy.PartHealth);
        Assert.Equal(hard.PartsLost, copy.PartsLost);
        Assert.Equal(hard.Stats.TotalThrustN, copy.Stats.TotalThrustN);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
    }

    [Fact]
    public void AutomatedShips_DockWithoutHittingTheYard()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = sector.Bodies.First(b => b.IsMineable);
        Assert.True(sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, "iron_ore", game.ShipyardId, 0.25)).Done);
        var job = sector.GetCraft(game.PlayerCraftId).Job!;
        for (var i = 0; i < 30 * 60 * TicksPerSecond && job.CompletedCycles < 2; i++) sector.Step();

        Assert.True(job.CompletedCycles >= 2);
        Assert.Empty(sector.RecentImpacts);
    }
}
