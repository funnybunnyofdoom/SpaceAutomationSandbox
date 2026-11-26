using System;
using System.Linq;
using System.Text.Json.Nodes;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Stations are built as construction kits, carried and deployed (#8).</summary>
public class StationBuildTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static ShipDesign Depot() => TestData.SurfaceDepot();

    private static void RunSeconds(SectorState sector, double seconds)
    {
        for (var i = 0; i < (int)Math.Ceiling(seconds * FixedStepClock.TicksPerSecond); i++) sector.Step();
    }

    /// <summary>Orders a depot kit and runs until it's in the yard's storage.</summary>
    private static ConstructionKit BuildDepotKit(NewGameResult game)
    {
        var yard = game.Sector.GetCraft(game.ShipyardId);
        Assert.True(game.Sector.OrderBuild(yard.Id, Depot(), Catalog).Accepted);
        RunSeconds(game.Sector, 20);
        return Assert.Single(yard.Kits);
    }

    /// <summary>Docks the player's ship at the yard and loads the yard's first kit.</summary>
    private static CraftState LoadKitOnPlayer(NewGameResult game)
    {
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);
        if (!ship.IsDocked) sector.RestoreDock(ship.Id, yard.Id, sector.FreePorts(yard)[0]);
        Assert.True(sector.TryTransferKit(yard.Id, ship.Id, yard.Kits[0].Id).Done);
        return ship;
    }

    private static long Ledger(SectorState sector) =>
        sector.Crafts.Sum(c => c.CargoKg + c.BuildQueue.Sum(o => o.MaterialsKg.Values.Sum()) + (c.RefiningBatch?.InputsKg.Values.Sum() ?? 0)
                               + (long)c.Design.ComputeBillOfMaterials(Catalog).TotalKg)
        + sector.EjectedKg.Values.Sum();

    [Fact]
    public void SurfaceDepot_IsAValidFixedStation()
    {
        var depot = Depot();
        Assert.Equal(DesignRole.Station, depot.SuggestedRole(Catalog));
        Assert.Empty(depot.Validate(Catalog, DesignRole.Station));
        Assert.True(depot.ComputeStats(Catalog).IsFixedStructure);
    }

    [Fact]
    public void StationOrder_FinishesAsAKit_InTheYardsStorage_NotAsACraft()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var crafts = game.Sector.Crafts.Count;
        var ledger = Ledger(game.Sector);

        var kit = BuildDepotKit(game);

        Assert.Equal(crafts, game.Sector.Crafts.Count);
        Assert.Equal(Depot().ComputeBillOfMaterials(Catalog).ToWholeKg(), kit.MaterialsKg);
        Assert.Equal(kit.MassKg, yard.KitsKg);
        Assert.True(kit.NeedsSurface);
        Assert.Equal(ledger, Ledger(game.Sector));
    }

    [Fact]
    public void Kit_IsCarried_AndDeployedOnASurface_AnchoredAndUpright()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var kit = BuildDepotKit(game);
        var ship = LoadKitOnPlayer(game);
        var emptyMass = ship.Stats.TotalMassKg + ship.Inventory.TotalKg;
        Assert.Equal(emptyMass + kit.MassKg, ship.MassKg); // carrying it is heavier
        var ledger = Ledger(sector);

        Assert.Equal("Undock first.", sector.DeployBlocker(ship.Id));
        Assert.False(sector.TryDeployKit(ship.Id, kit.Id).Done);
        sector.TryUndock(ship.Id);
        Assert.Contains("land on a body", sector.DeployBlocker(ship.Id)); // foundations: not in flight

        var rock = sector.Bodies.First(b => b.IsMineable);
        TestData.LandAt(sector, ship, rock);
        sector.Step();
        var result = sector.TryDeployKit(ship.Id, kit.Id);
        Assert.True(result.Done, result.Message);
        var depot = sector.Crafts.Single(c => c.Design.Id == "surface_depot");

        Assert.Empty(ship.Kits);
        Assert.Equal(rock.Id, depot.LandedOnBodyId);
        Assert.True(depot.IsAnchored);
        Assert.InRange(sector.SurfaceGap(depot, rock), -1e-6, 1e-6);
        var outward = depot.Position - sector.GetPosition(rock.Id);
        Assert.True((outward.X * depot.Forward.X + outward.Y * depot.Forward.Y) / outward.Length > 0.999);
        Assert.True(depot.Position.DistanceTo(ship.Position) >= depot.CollisionRadiusM + ship.CollisionRadiusM);
        Assert.Equal(ledger, Ledger(sector)); // the kit became the depot

        RunSeconds(sector, 60); // rides along with the rock
        Assert.InRange(sector.SurfaceGap(depot, rock), -1e-6, 1e-6);
    }

    [Fact]
    public void OrbitalStationKit_DeploysBesideTheYard_OnRails()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.Inventory.Add(NewGame.StartingStockKg);
        Assert.True(sector.OrderBuild(yard.Id, TestData.StarterShipyard(), Catalog).Accepted);
        RunSeconds(sector, 120);
        var kit = Assert.Single(yard.Kits);
        Assert.False(kit.NeedsSurface);

        Assert.True(sector.TryDeployKit(yard.Id, kit.Id).Done);
        var station = sector.Crafts.Single(c => c.Id != yard.Id && c.IsShipyard);
        Assert.True(station.IsOnRails);
        Assert.False(station.IsDocked);
        Assert.InRange(station.Position.DistanceTo(yard.Position) - station.CollisionRadiusM - yard.CollisionRadiusM, 0, 2 * SectorState.ParkingGapM);
    }

    [Fact]
    public void OrbitalStationKit_DeploysInFlight_IntoOrbitAroundTheNearestBody_OnlyWhenMatched()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.Inventory.Add(NewGame.StartingStockKg);
        Assert.True(sector.OrderBuild(yard.Id, TestData.StarterShipyard(), Catalog).Accepted);
        RunSeconds(sector, 120);
        // The starter ship holds 40 t and a yard kit is heavier: use a hauler with a storage silo.
        var hauler = new ShipDesign("hauler", "Hauler", new[]
        {
            new DesignCell(new GridPos(0, 0), "cockpit"), new DesignCell(new GridPos(0, 1), "thruster"),
            new DesignCell(new GridPos(1, 0), "storage_silo"),
        });
        var carrier = new CraftState(sector.AllocateCraftId(), "Hauler", hauler, hauler.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(carrier);
        sector.RestoreDock(carrier.Id, yard.Id, sector.FreePorts(yard)[0]);
        Assert.True(sector.TryTransferKit(yard.Id, carrier.Id, yard.Kits[0].Id).Done);
        sector.TryUndock(carrier.Id);

        var rock = sector.Bodies.First(b => b.IsMineable);
        var rockVelocity = sector.GetVelocityAt(rock.Id, sector.TimeS);
        carrier.Position = sector.GetPosition(rock.Id) + new Vector2D(rock.RadiusM + 2_000, 0);
        carrier.Velocity = rockVelocity + new Vector2D(0, 50);
        Assert.Contains("Match speed", sector.DeployBlocker(carrier.Id));
        carrier.Velocity = rockVelocity;
        Assert.Null(sector.DeployBlocker(carrier.Id));

        Assert.True(sector.TryDeployKit(carrier.Id, carrier.Kits[0].Id).Done);
        var station = sector.Crafts.Single(c => c.Design.Id == TestData.StarterShipyard().Id && c.Id != yard.Id);
        Assert.Equal(rock.Id, station.RailOrbit!.Parent);
        var lane = station.RailOrbit.RadiusM;
        Assert.InRange(lane, rock.RadiusM + 2_000 + carrier.CollisionRadiusM + station.CollisionRadiusM, rock.RadiusM + 2_200); // just outside the carrier
        // The carrier hangs still over the rock while the station circles it: a whole lap passes without them touching (the creator's
        // carrier was rammed by a station deployed on its own circle).
        var period = station.RailOrbit.PeriodS;
        for (var t = 0.0; t < period * 1.05; t += 1)
        {
            carrier.Position = sector.GetPosition(rock.Id) + new Vector2D(rock.RadiusM + 2_000, 0);
            carrier.Velocity = sector.GetVelocityAt(rock.Id, sector.TimeS);
            RunSeconds(sector, 1);
            Assert.True(station.Position.DistanceTo(carrier.Position) >= station.CollisionRadiusM + carrier.CollisionRadiusM, $"touched at {t} s");
        }
        Assert.Contains(sector.Crafts, c => c.Id == carrier.Id);
        Assert.InRange(station.Position.DistanceTo(sector.GetPosition(rock.Id)), lane - 1, lane + 1); // stays with the rock
    }

    [Fact]
    public void FinishedKit_WaitsForStorageRoom()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        Assert.True(sector.OrderBuild(yard.Id, Depot(), Catalog).Accepted);
        yard.Inventory.Add("silicate", yard.FreeCargoKg); // storage full
        RunSeconds(sector, 20);

        var order = Assert.Single(yard.BuildQueue);
        Assert.True(order.WaitingForRoom);
        Assert.Empty(yard.Kits);

        yard.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { ["silicate"] = 10_000 });
        sector.Step();
        Assert.Empty(yard.BuildQueue);
        Assert.Single(yard.Kits);
    }

    [Fact]
    public void Kits_AndQueuedStationOrders_SurviveSaveLoad()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        BuildDepotKit(game);
        LoadKitOnPlayer(game);
        sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), Catalog); // goes first
        sector.OrderBuild(game.ShipyardId, Depot(), Catalog);                // then becomes a kit

        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        Assert.Single(loaded.GetCraft(game.PlayerCraftId).Kits);

        RunSeconds(loaded, 60);
        Assert.Single(loaded.GetCraft(game.ShipyardId).Kits);
        Assert.True(loaded.NextOrderId > loaded.GetCraft(game.ShipyardId).Kits[0].Id.Value);
    }

    [Fact]
    public void Version10Save_StationOrderWithALandingSite_BecomesAKit()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        sector.OrderBuild(game.ShipyardId, Depot(), Catalog);
        var root = JsonNode.Parse(SaveGameSerializer.ToJson(sector, game.PlayerCraftId))!.AsObject();
        root["schemaVersion"] = 10;
        var yard = root["sector"]!["crafts"]!.AsArray().OfType<JsonObject>().Single(c => c["id"]!.GetValue<uint>() == game.ShipyardId.Value);
        yard["buildQueue"]![0]!.AsObject()["landingSiteBodyId"] = sector.Bodies.First(b => b.IsMineable).Id.Value;

        var loaded = SaveGameSerializer.FromJson(root.ToJsonString(), Catalog).Sector;
        Assert.True(loaded.GetCraft(game.ShipyardId).BuildQueue[0].IsKit);
        RunSeconds(loaded, 20);
        Assert.Single(loaded.GetCraft(game.ShipyardId).Kits);
    }
}
