using System;
using System.Linq;
using System.Text.Json.Nodes;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#2 storage limits and #26 save migrations.</summary>
public class StorageAndMigrationTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    [Fact]
    public void StarterShipyard_StoresUpTo400Tonnes()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);

        Assert.True(yard.HasCargoLimit);
        Assert.Equal(400_000, yard.CargoCapacityKg);
        Assert.Equal(400_000 - NewGame.StartingStockKg.Values.Sum(), yard.FreeCargoKg);
    }

    [Fact]
    public void StationWithoutStorage_CannotReceiveCargo()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var bare = new ShipDesign("bare", "Bare dock", new[] { new DesignCell(new GridPos(0, 0), "docking_port"), new DesignCell(new GridPos(0, 1), "hull") });
        var station = new CraftState(sector.AllocateCraftId(), "Bare dock", bare, bare.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(station);
        sector.PutOnRails(station.Id, yard.RailOrbit! with { PhaseAtZeroRad = yard.RailOrbit!.PhaseAtZeroRad + 0.3 });
        var ship = sector.GetCraft(game.PlayerCraftId);
        ship.Inventory.Add("metal", 1_000);
        ship.Position = sector.BerthPosition(station, 0, ship.CollisionRadiusM, station.Position);
        ship.Velocity = station.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);

        var result = sector.TryTransfer(ship.Id, station.Id, "metal", 1_000);

        Assert.False(result.Done);
        Assert.Contains("room for only 0 kg", result.Message);
    }

    [Fact]
    public void Refinery_WaitsWhenStorageIsFull_ThenFinishesOnceThereIsRoom()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.Inventory.Add("slag", yard.FreeCargoKg - 500); // 500 kg free
        yard.Inventory.Add("iron_ore", 1_000);              // setup only: the batch takes it out again, leaving 500 kg free

        for (var i = 0; i < 30 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        Assert.NotNull(yard.RefiningBatch);               // finished, but its 1 t of output doesn't fit in 500 kg
        Assert.Contains("waiting for storage room", sector.RefiningStatus(yard));
        var slag = yard.Inventory["slag"];

        yard.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { ["slag"] = 2_000 });
        sector.Step();
        Assert.Null(yard.RefiningBatch);
        Assert.Equal(slag - 2_000 + 300, yard.Inventory["slag"]);
        Assert.Equal(700, yard.Inventory["metal"] - NewGame.StartingStockKg["metal"]);
    }

    [Fact]
    public void Migrations_CoverEveryOlderVersion_InOrder()
    {
        Assert.Equal(Enumerable.Range(1, SaveGameSerializer.SchemaVersion - 1), SaveMigrations.UpgradableFrom);
    }

    [Fact]
    public void Version8Save_ShipyardWithoutStorage_KeepsUnlimitedStorage()
    {
        // A v8 game: the old starter shipyard (hull where the silos are now) holding 500 t.
        var game = TestData.NewGame();
        var json = SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId).Replace("\"storage_silo\"", "\"hull\"");
        var root = JsonNode.Parse(json)!.AsObject();
        root["schemaVersion"] = 8;
        var yardNode = root["sector"]!["crafts"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Shipyard")!.AsObject();
        yardNode["inventoryKg"]!["metal"] = 500_000;

        var steps = SaveMigrations.Migrate(root, Catalog);
        Assert.Single(steps);
        var loaded = SaveGameSerializer.FromJson(root.ToJsonString(), Catalog).Sector;
        var yard = loaded.GetCraft(game.ShipyardId);

        Assert.True(yard.LegacyUnlimitedStorage);
        Assert.False(yard.HasCargoLimit);
        Assert.Equal(500_000, yard.Inventory["metal"]);
        Assert.False(loaded.GetCraft(game.PlayerCraftId).LegacyUnlimitedStorage); // ships always had a limit

        // Saving again keeps the flag (it is part of v9).
        var again = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(loaded, game.PlayerCraftId), Catalog).Sector;
        Assert.True(again.GetCraft(game.ShipyardId).LegacyUnlimitedStorage);
    }

    [Fact]
    public void Version1Save_IsUpgradedThroughEveryStep()
    {
        var game = TestData.NewGame();
        var root = JsonNode.Parse(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId))!.AsObject();
        root["schemaVersion"] = 1;

        var steps = SaveMigrations.Migrate(root, Catalog);

        Assert.Equal(SaveGameSerializer.SchemaVersion - 1, steps.Count);
        Assert.Equal(SaveGameSerializer.SchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.All(root["sector"]!["bodies"]!.AsArray(), b => Assert.True(b!["surveyed"]!.GetValue<bool>()));
    }
}
