using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class FabricationTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static void RunSeconds(SectorState sector, double seconds)
    {
        var ticks = (int)Math.Ceiling(seconds * FixedStepClock.TicksPerSecond);
        for (var i = 0; i < ticks; i++) sector.Step();
    }

    /// <summary>
    /// Per material: yard stock + materials held by orders + materials inside crafts built since
    /// the start. Must equal the starting stock at every moment.
    /// </summary>
    private static SortedDictionary<string, long> Accounted(SectorState sector, CraftId yardId, ISet<CraftId> startingCrafts)
    {
        var total = new SortedDictionary<string, long>(StringComparer.Ordinal);
        void Add(string id, long kg) => total[id] = (total.TryGetValue(id, out var v) ? v : 0) + kg;

        var yard = sector.GetCraft(yardId);
        foreach (var (id, kg) in yard.Inventory.Kg) Add(id, kg);
        foreach (var order in yard.BuildQueue)
            foreach (var (id, kg) in order.MaterialsKg) Add(id, kg);
        foreach (var craft in sector.Crafts.Where(c => !startingCrafts.Contains(c.Id)))
            foreach (var (id, kg) in craft.Design.ComputeBillOfMaterials(Catalog).ToWholeKg()) Add(id, kg);
        return total;
    }

    [Fact]
    public void NewGame_HasPlayerShipAndAnOrbitingStockedShipyard()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var primary = game.Sector.Bodies.First(b => b.Orbit is null);

        Assert.True(yard.IsShipyard);
        Assert.True(yard.IsOnRails);
        Assert.Equal(primary.Id, yard.RailOrbit!.Parent);
        Assert.Equal(primary.RadiusM + NewGame.ShipyardAltitudeM, game.Sector.GetPosition(primary.Id).DistanceTo(yard.Position), 6);
        Assert.Equal(NewGame.StartingStockKg, yard.Inventory.Kg);
        Assert.False(game.Sector.GetCraft(game.PlayerCraftId).IsShipyard);
    }

    [Fact]
    public void Shipyard_StaysOnItsOrbit_WhateverItsControlsSay()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var orbit = yard.RailOrbit!;
        yard.Controls = new ShipControls(1, 1, false); // no thrusters: nothing happens

        RunSeconds(game.Sector, 300);

        Assert.True(yard.IsOnRails);
        Assert.True((game.Sector.RailPositionAt(orbit, game.Sector.TimeS) - yard.Position).Length < 1e-9);
    }

    [Fact]
    public void Order_TakesExactlyTheBillFromStock_AndBuildsAtTheFabricationRate()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var design = TestData.StarterShip();
        var bill = design.ComputeBillOfMaterials(Catalog).ToWholeKg();

        var result = sector.OrderBuild(yard.Id, design, Catalog);

        Assert.True(result.Accepted, string.Join("; ", result.Errors));
        foreach (var (id, kg) in NewGame.StartingStockKg) Assert.Equal(kg - (bill.TryGetValue(id, out var b) ? b : 0), yard.Inventory[id]);

        var buildSeconds = design.ComputeStats(Catalog).TotalMassKg / yard.Stats.FabricationRateKgPerS; // 17 t at 500 kg/s = 34 s
        var cratsBefore = sector.Crafts.Count;
        RunSeconds(sector, buildSeconds - 1);
        Assert.Equal(cratsBefore, sector.Crafts.Count);
        Assert.InRange(yard.BuildQueue[0].Fraction, 0.9, 0.999);

        RunSeconds(sector, 1.5);
        Assert.Empty(yard.BuildQueue);
        var built = sector.Crafts.Single(c => c.Id != game.PlayerCraftId && c.Id != game.ShipyardId);
        Assert.Equal(design.Cells, built.Design.Cells);
        Assert.Equal($"{design.Name} #{result.OrderId!.Value.Value}", built.Name);
    }

    /// <summary>Builds three ships at a yard with two docking ports: two dock, the third parks. Returns the third.</summary>
    private static CraftState BuildUntilOneParks(NewGameResult game)
    {
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        yard.Inventory.Add(NewGame.StartingStockKg); // enough for three or more
        var design = TestData.StarterShip();
        for (var i = 0; i < 3; i++) Assert.True(sector.OrderBuild(yard.Id, design, Catalog).Accepted);
        RunSeconds(sector, 3 * design.ComputeStats(Catalog).TotalMassKg / yard.Stats.FabricationRateKgPerS + 2);

        var built = sector.Crafts.Where(c => c.Id != game.PlayerCraftId && c.Id != game.ShipyardId).ToList();
        Assert.Equal(3, built.Count);
        Assert.Equal(2, built.Count(c => c.DockedToCraftId == yard.Id)); // the yard's two ports
        return built.Single(c => !c.IsDocked);
    }

    [Fact]
    public void BuiltShip_IsParkedAlongsideTheYard_OnItsOrbit_WhenEveryPortIsTaken()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = BuildUntilOneParks(game);

        Assert.True(ship.IsOnRails);
        Assert.Equal(yard.RailOrbit!.RadiusM, ship.RailOrbit!.RadiusM);
        var gap = ship.Position.DistanceTo(yard.Position) - ship.CollisionRadiusM - yard.CollisionRadiusM;
        // Parked a short arc behind on the same orbit: the straight-line gap is close to the target gap.
        Assert.InRange(gap, SectorState.ParkingGapM * 0.5, SectorState.ParkingGapM * 1.5);
        Assert.True((ship.Velocity - yard.Velocity).Length < 0.5, "Parked ships move with the yard.");

        RunSeconds(sector, 600); // ten minutes later still alongside
        Assert.Equal(gap, ship.Position.DistanceTo(yard.Position) - ship.CollisionRadiusM - yard.CollisionRadiusM, 6);
    }

    [Fact]
    public void ParkedShip_LeavesItsParkingOrbit_WhenItThrusts_ButNotWhenItBrakes()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var ship = BuildUntilOneParks(game);

        ship.Controls = new ShipControls(0, 0.5, true);
        RunSeconds(sector, 5);
        Assert.True(ship.IsOnRails);

        var railVelocity = ship.Velocity;
        ship.Controls = new ShipControls(1, 0, false);
        sector.Step();
        Assert.False(ship.IsOnRails);
        Assert.Equal(ship.Stats.MaxAccelerationMps2 * FixedStepClock.TickSeconds, (ship.Velocity - railVelocity).Length, 1);
    }

    [Fact]
    public void SecondShipParksInTheNextSlot_ThirdIsRefusedWithTheExactShortfall()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var design = TestData.StarterShip();

        Assert.True(sector.OrderBuild(yard.Id, design, Catalog).Accepted);
        Assert.True(sector.OrderBuild(yard.Id, design, Catalog).Accepted);
        var stockBefore = yard.Inventory.Kg.ToDictionary(kv => kv.Key, kv => kv.Value);
        var third = sector.OrderBuild(yard.Id, design, Catalog);

        Assert.False(third.Accepted);
        // Shortfall = what the third ship needs minus what the first two left behind.
        var bill = design.ComputeBillOfMaterials(Catalog).ToWholeKg();
        var expected = bill.Where(kv => kv.Value > stockBefore.GetValueOrDefault(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value - stockBefore.GetValueOrDefault(kv.Key));
        Assert.Equal(expected, third.ShortfallKg);
        Assert.Contains(third.Errors, e => e.Contains($"electronics short by {expected["electronics"]} kg"));
        Assert.Equal(stockBefore, yard.Inventory.Kg); // a refused order takes nothing

        // Both built, one after the other.
        RunSeconds(sector, 2 * design.ComputeStats(Catalog).TotalMassKg / yard.Stats.FabricationRateKgPerS + 2);
        var built = sector.Crafts.Where(c => c.Id != game.PlayerCraftId && c.Id != game.ShipyardId).ToList();
        Assert.Equal(2, built.Count);
        Assert.True(built[0].Position.DistanceTo(built[1].Position) >= built[0].CollisionRadiusM + built[1].CollisionRadiusM);
    }

    [Fact]
    public void Cancel_RefundsEverything_EvenHalfBuilt()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var order = sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog).OrderId!.Value;
        RunSeconds(sector, 15);

        Assert.True(sector.CancelBuild(yard.Id, order));
        Assert.False(sector.CancelBuild(yard.Id, order));
        Assert.Equal(NewGame.StartingStockKg, yard.Inventory.Kg);
    }

    [Fact]
    public void MaterialsAreConserved_AtEveryTick_ThroughOrdersCancelsAndBuilds()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var starting = new HashSet<CraftId>(sector.Crafts.Select(c => c.Id));
        var expected = new SortedDictionary<string, long>(NewGame.StartingStockKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        var design = TestData.StarterShip();

        for (var tick = 0; tick < 3000; tick++)
        {
            if (tick is 0 or 10 or 900) sector.OrderBuild(game.ShipyardId, design, Catalog);
            if (tick == 200 && sector.GetCraft(game.ShipyardId).BuildQueue.Count > 1)
                sector.CancelBuild(game.ShipyardId, sector.GetCraft(game.ShipyardId).BuildQueue[1].Id);
            sector.Step();
            Assert.Equal(expected, Accounted(sector, game.ShipyardId, starting));
        }
        Assert.Equal(2, sector.Crafts.Count - starting.Count);
    }

    [Fact]
    public void Order_IsRefused_ForAnInvalidDesign_OrAYardWithoutFabricator()
    {
        var game = TestData.NewGame();
        var noCockpit = new ShipDesign("x", "x", new[] { new DesignCell(new GridPos(0, 0), "thruster") });

        var bad = game.Sector.OrderBuild(game.ShipyardId, noCockpit, Catalog);
        var notYard = game.Sector.OrderBuild(game.PlayerCraftId, TestData.StarterShip(), Catalog);

        Assert.False(bad.Accepted);
        Assert.Contains(bad.Errors, e => e.Contains("control source"));
        Assert.False(notYard.Accepted);
        Assert.Contains(notYard.Errors, e => e.Contains("no fabricator"));
        Assert.Equal(NewGame.StartingStockKg, game.Sector.GetCraft(game.ShipyardId).Inventory.Kg);
    }

    [Fact]
    public void OrderedDesign_IsASnapshot()
    {
        // Ordering copies the design; editing the blueprint afterwards can't change what gets built.
        var game = TestData.NewGame();
        var editor = new BlueprintEditor(Catalog);
        editor.Load(TestData.StarterShip());
        game.Sector.OrderBuild(game.ShipyardId, editor.ToDesign(), Catalog);
        editor.Remove(new GridPos(0, -3));

        Assert.Equal(TestData.StarterShip().Cells.Count, game.Sector.GetCraft(game.ShipyardId).BuildQueue[0].Design.Cells.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)] // mid-build
    [InlineData(700)] // first built, second in progress
    public void SaveMidBuild_LoadAndContinue_MatchesNeverStopping(int saveAtTick)
    {
        static SectorState Play(SectorState sector, CraftId yard, int fromTick, int toTick)
        {
            for (var tick = fromTick; tick < toTick; tick++)
            {
                if (tick is 0 or 5) sector.OrderBuild(yard, TestData.StarterShip(), Catalog);
                sector.Step();
            }
            return sector;
        }

        var reference = TestData.NewGame();
        Play(reference.Sector, reference.ShipyardId, 0, 2000);

        var game = TestData.NewGame();
        Play(game.Sector, game.ShipyardId, 0, saveAtTick);
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId), Catalog);
        Play(loaded.Sector, game.ShipyardId, saveAtTick, 2000);

        Assert.Equal(SaveGameSerializer.ToJson(reference.Sector, reference.PlayerCraftId),
            SaveGameSerializer.ToJson(loaded.Sector, loaded.PlayerCraftId));
        Assert.Equal(4, loaded.Sector.Crafts.Count);
        Assert.Equal(3, loaded.Sector.NextOrderId);
    }

    [Fact]
    public void Version2Save_LoadsWithEmptyInventoriesAndNoQueues()
    {
        var game = TestData.NewGame();
        var v3 = SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId);
        var node = System.Text.Json.Nodes.JsonNode.Parse(v3)!;
        node["schemaVersion"] = 2;
        node["sector"]!.AsObject().Remove("nextOrderId");
        foreach (var craft in node["sector"]!["crafts"]!.AsArray())
        {
            craft!.AsObject().Remove("railOrbit");
            craft.AsObject().Remove("inventoryKg");
            craft.AsObject().Remove("buildQueue");
        }

        var loaded = SaveGameSerializer.FromJson(node.ToJsonString(), Catalog);

        var yard = loaded.Sector.GetCraft(game.ShipyardId);
        Assert.Empty(yard.Inventory.Kg);
        Assert.False(yard.IsOnRails); // an old file has no rails: the yard just floats where it was
        Assert.Equal(1, loaded.Sector.NextOrderId);
    }

    [Fact]
    public void Load_RejectsUnknownStoredMaterials()
    {
        var game = TestData.NewGame();
        var json = SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId).Replace("\"metal\":", "\"mithril\":");

        Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json, Catalog));
    }

    [Fact]
    public void StationDesign_ValidatesAsStation_NotAsShip()
    {
        var yard = TestData.StarterShipyard();

        Assert.Empty(yard.Validate(Catalog, DesignRole.Station));
        Assert.NotEmpty(yard.Validate(Catalog, DesignRole.Ship));
        Assert.Equal(DesignRole.Station, yard.SuggestedRole(Catalog));
        Assert.Equal(DesignRole.Ship, TestData.StarterShip().SuggestedRole(Catalog));
        var hullOnly = new ShipDesign("h", "h", new[] { new DesignCell(new GridPos(0, 0), "hull") });
        Assert.Contains(hullOnly.Validate(Catalog, DesignRole.Station), e => e.Contains("fabricator, docking port or cargo bay"));
    }

    [Fact]
    public void PartCosts_MustBeWholeKilograms()
    {
        var ex = Assert.Throws<FormatException>(() => PartCatalog.FromJson(
            """{ "schemaVersion": 2, "parts": [ { "id": "x", "massKg": 1.5, "materialsKg": { "metal": 1.5 } } ] }""",
            TestData.Materials()));
        Assert.Contains("whole number", ex.Message);
    }

    [Fact]
    public void Inventory_RemovesAllOrNothing()
    {
        var inv = new Inventory();
        inv.Add("metal", 100);
        inv.Add("electronics", 10);

        Assert.False(inv.TryRemove(new Dictionary<string, long> { ["metal"] = 50, ["electronics"] = 11 }));
        Assert.Equal(100, inv["metal"]);
        Assert.True(inv.TryRemove(new Dictionary<string, long> { ["metal"] = 100, ["electronics"] = 10 }));
        Assert.Empty(inv.Kg);
        Assert.Throws<ArgumentOutOfRangeException>(() => inv.Add("metal", -1));
    }
}
