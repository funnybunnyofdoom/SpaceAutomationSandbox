using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;
using Xunit.Abstractions;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>The star system's cruise drive and solar power by distance (#53).</summary>
public class CruiseTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private readonly ITestOutputHelper _log;

    public CruiseTests(ITestOutputHelper log) => _log = log;

    private static CelestialBodyState Body(SectorState s, string name) => s.Bodies.Single(b => b.Name == name);

    private static long Mass(SectorState sector) => sector.ComputeMassLedger().TotalKg;

    /// <summary>The player's ship out between Home and Cinder, 100 km from Home, pointing at Cinder, with fuel.</summary>
    private static (SectorState Sector, CraftState Ship) OutInSpace(long fuelKg = 500)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var ship = sector.GetCraft(game.PlayerCraftId);
        var home = sector.GetPosition(SectorGenerator.HomePlanet(sector).Id);
        var toCinder = sector.GetPosition(Body(sector, "Cinder").Id) - home;
        var direction = toCinder * (1 / toCinder.Length);
        ship.Position = ship.PreviousPosition = home + direction * 100_000;
        ship.Velocity = SectorGenerator.PlayerSpawnVelocity(sector);
        ship.HeadingRad = ship.PreviousHeadingRad = Math.Atan2(direction.X, -direction.Y);
        ship.Inventory.Add(SectorState.FuelMaterial, fuelKg);
        return (sector, ship);
    }

    [Fact]
    public void Cruise_IsRefused_NearBodies_AndWithoutFuel()
    {
        var game = TestData.NewGame();
        var ship = game.Sector.GetCraft(game.PlayerCraftId);
        Assert.Contains("No fuel", game.Sector.TryStartCruise(ship.Id).Message);
        ship.Inventory.Add(SectorState.FuelMaterial, 100);
        Assert.Contains("Too close to Home", game.Sector.TryStartCruise(ship.Id).Message);
        Assert.False(ship.Cruising);
    }

    [Fact]
    public void Cruise_CrossesToTheNextPlanet_DropsOutMatched_AndBurnsFuelExactly()
    {
        var (sector, ship) = OutInSpace();
        var cinder = Body(sector, "Cinder");
        var mass = Mass(sector);
        var fuel = ship.Inventory["fuel"];
        Assert.True(sector.TryStartCruise(ship.Id).Done);

        var ticks = 0;
        for (; ticks < 15 * TicksPerMinute && ship.Cruising; ticks++)
        {
            ship.Controls = new ShipControls(0, Autopilot.TurnToward(ship, (sector.GetPosition(cinder.Id) - ship.Position) * (1 / (sector.GetPosition(cinder.Id) - ship.Position).Length)), false);
            sector.Step();
        }
        _log.WriteLine($"cruised {ticks / (double)TicksPerMinute:0.0} min, burned {fuel - ship.Inventory["fuel"]} kg; {ship.CruiseStopReason}");

        Assert.False(ship.Cruising);
        Assert.Equal("arrived near Cinder", ship.CruiseStopReason);
        Assert.InRange(sector.GetPosition(cinder.Id).DistanceTo(ship.Position) - cinder.RadiusM, SectorState.CruiseDropoutM - 100, SectorState.CruiseDropoutM + 100);
        Assert.Equal(sector.GetVelocityAt(cinder.Id, sector.TimeS), ship.Velocity);
        Assert.InRange(ticks / (double)TicksPerMinute, 1.5, 3); // ~150 km at 1 km/s
        Assert.Equal(fuel - ship.Inventory["fuel"], sector.EjectedKg["fuel"]);
        Assert.Equal(mass, Mass(sector));
    }

    [Fact]
    public void RunningOutOfFuel_DropsOutOfCruise()
    {
        var (sector, ship) = OutInSpace(fuelKg: 2);
        Assert.True(sector.TryStartCruise(ship.Id).Done);
        for (var i = 0; i < TicksPerMinute && ship.Cruising; i++) sector.Step();
        Assert.False(ship.Cruising);
        Assert.Equal("out of fuel", ship.CruiseStopReason);
        Assert.True(ship.Velocity.Length < 300); // matched to the nearest body again, not still at cruise speed
    }

    [Fact]
    public void Braking_DropsOutOfCruise()
    {
        var (sector, ship) = OutInSpace();
        Assert.True(sector.TryStartCruise(ship.Id).Done);
        sector.Step();
        ship.Controls = new ShipControls(0, 0, true);
        sector.Step();
        Assert.False(ship.Cruising);
    }

    [Fact]
    public void MiningJob_OnAnotherPlanet_CruisesThere()
    {
        var game = TestData.NewGame(keepPlayerFuel: true);
        var sector = game.Sector;
        var ship = sector.GetCraft(game.PlayerCraftId);
        ship.Inventory.Add(SectorState.FuelMaterial, 300);
        var cinder = Body(sector, "Cinder");
        Assert.True(sector.StartJob(ship.Id, new MiningJob(cinder.Id, "iron_ore", game.ShipyardId, 0.25)).Done);
        var job = (MiningJob)ship.Job!;
        var mass = Mass(sector);

        var cruised = false;
        var ticks = 0;
        for (; ticks < 20 * TicksPerMinute && job.State is not (MiningJobState.Landing or MiningJobState.Mining); ticks++)
        {
            sector.Step();
            cruised |= ship.Cruising;
        }
        _log.WriteLine($"reached Cinder in {ticks / (double)TicksPerMinute:0.0} min: {job.Status}; fuel burned {(sector.EjectedKg.TryGetValue("fuel", out var burned) ? burned : 0)} kg");

        Assert.True(cruised, "the autopilot should cruise a 250 km leg");
        Assert.InRange(ticks / (double)TicksPerMinute, 3, 12); // 5–10 minutes between neighbouring planets
        Assert.Equal(mass, Mass(sector));
    }

    [Fact]
    public void CruiseState_SurvivesSaveLoad_AndContinuesIdentically()
    {
        var (sector, ship) = OutInSpace();
        Assert.True(sector.TryStartCruise(ship.Id).Done);
        for (var i = 0; i < 77; i++) sector.Step();

        var json = SaveGameSerializer.ToJson(sector, ship.Id);
        Assert.Contains("\"cruising\": true", json);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        for (var i = 0; i < 300; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, ship.Id), SaveGameSerializer.ToJson(loaded, ship.Id));
    }

    [Fact]
    public void Sunlight_DependsOnDistanceFromTheStar()
    {
        var sector = TestData.NewGame().Sector;
        Assert.Equal(1, sector.SolarFactorAt(SectorGenerator.PlayerSpawnPoint(sector)));
        Assert.Equal(4, sector.SolarFactorAt(sector.GetPosition(Body(sector, "Cinder").Id)));
        Assert.Equal(0.1, sector.SolarFactorAt(sector.GetPosition(Body(sector, "Rime").Id)));
        Assert.Equal(0.2, sector.SolarFactorAt(sector.GetPosition(Body(sector, "Giant").Id)));
    }

    [Fact]
    public void NewGame_GivesThePlayerSomeFuel_AndTheYardAStock()
    {
        var game = TestData.NewGame(keepPlayerFuel: true);
        Assert.Equal(NewGame.PlayerStartingFuelKg, game.Sector.GetCraft(game.PlayerCraftId).Inventory["fuel"]);
        Assert.Equal(NewGame.StartingStockKg["fuel"], game.Sector.GetCraft(game.ShipyardId).Inventory["fuel"]);
    }
}
