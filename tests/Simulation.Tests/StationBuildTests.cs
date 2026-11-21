using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class StationBuildTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static ShipDesign Depot() => ShipDesign.FromJson(TestData.Read("data/blueprints/surface_depot.json"));

    private static void RunSeconds(SectorState sector, double seconds)
    {
        for (var i = 0; i < (int)Math.Ceiling(seconds * FixedStepClock.TicksPerSecond); i++) sector.Step();
    }

    private static CraftState Built(NewGameResult game) =>
        game.Sector.Crafts.Single(c => c.Id != game.PlayerCraftId && c.Id != game.ShipyardId);

    [Fact]
    public void SurfaceDepot_IsAValidFixedStation()
    {
        var depot = Depot();
        Assert.Equal(DesignRole.Station, depot.SuggestedRole(Catalog));
        Assert.Empty(depot.Validate(Catalog, DesignRole.Station));
        Assert.True(depot.ComputeStats(Catalog).IsFixedStructure);
    }

    [Fact]
    public void FixedStructure_NeedsALandingSite()
    {
        var game = TestData.NewGame();

        var result = game.Sector.OrderBuild(game.ShipyardId, Depot(), Catalog);

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.Contains("choose a body"));
        Assert.Equal(NewGame.StartingStockKg, game.Sector.GetCraft(game.ShipyardId).Inventory.Kg);
    }

    [Fact]
    public void FixedStructure_IsPlacedOnTheChosenBody_AnchoredAndUpright()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = sector.Bodies.Single(b => b.IsMineable);

        Assert.True(sector.OrderBuild(game.ShipyardId, Depot(), Catalog, landingSite: rock.Id).Accepted);
        RunSeconds(sector, 20);
        var depot = Built(game);

        Assert.Equal(rock.Id, depot.LandedOnBodyId);
        Assert.True(depot.IsAnchored);
        Assert.Equal(rock.RadiusM + depot.CollisionRadiusM, sector.GetPosition(rock.Id).DistanceTo(depot.Position), 6);
        // Nose points away from the rock, so the foundation row (rear) faces the ground.
        var outward = depot.Position - sector.GetPosition(rock.Id);
        var forward = depot.Forward;
        Assert.True((outward.X * forward.X + outward.Y * forward.Y) / outward.Length > 0.999);

        RunSeconds(sector, 120); // rides along with the orbiting rock
        Assert.Equal(rock.RadiusM + depot.CollisionRadiusM, sector.GetPosition(rock.Id).DistanceTo(depot.Position), 6);
    }

    [Fact]
    public void OrbitalStation_IsPlacedOnAFixedOrbitNearTheYard()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.Inventory.Add(NewGame.StartingStockKg);

        Assert.True(sector.OrderBuild(yard.Id, TestData.StarterShipyard(), Catalog).Accepted);
        RunSeconds(sector, 60);
        var station = Built(game);

        Assert.True(station.IsOnRails);
        Assert.False(station.IsDocked); // stations don't take a docking port
        Assert.True(station.IsShipyard);
        Assert.InRange(station.Position.DistanceTo(yard.Position) - station.CollisionRadiusM - yard.CollisionRadiusM, 0, 2 * SectorState.ParkingGapM);
    }

    [Fact]
    public void BuildingAStation_ConservesMaterials()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var bill = Depot().ComputeBillOfMaterials(Catalog).ToWholeKg();
        sector.OrderBuild(yard.Id, Depot(), Catalog, landingSite: sector.Bodies[0].Id);
        RunSeconds(sector, 20);

        foreach (var (material, kg) in NewGame.StartingStockKg)
            Assert.Equal(kg, yard.Inventory[material] + (bill.TryGetValue(material, out var used) ? used : 0));
        Assert.Equal(Depot().ComputeStats(Catalog).TotalMassKg, bill.Values.Sum());
    }

    [Fact]
    public void LandingSite_IsSaved_ForAQueuedStructure()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = sector.Bodies.Single(b => b.IsMineable);
        sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), Catalog);       // goes first
        sector.OrderBuild(game.ShipyardId, Depot(), Catalog, landingSite: rock.Id); // waits

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), Catalog);
        RunSeconds(loaded.Sector, 60);

        var depot = loaded.Sector.Crafts.Single(c => c.Design.Id == "surface_depot");
        Assert.Equal(rock.Id, depot.LandedOnBodyId);
    }
}
