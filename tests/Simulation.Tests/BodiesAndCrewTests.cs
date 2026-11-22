using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#27 dismantling bodies completely; #12 recruiting crew; #14 supply routing.</summary>
public class BodiesAndCrewTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    private static bool Run(SectorState sector, Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (done()) return true;
            sector.Step();
        }
        return done();
    }

    /// <summary>A tiny rock (radius 20 m) orbiting Home, with a little iron ore, added to a new game.</summary>
    private static CelestialBodyState TinyRock(SectorState sector, BodyId parent, string name = "Pebble", double orbitRadiusM = 9_000)
    {
        var mass = Math.Round(CelestialBodyState.MassFromRadius(20, SectorGenerator.AsteroidDensityKgPerM3));
        var body = new CelestialBodyState(sector.AllocateBodyId(), name, BodyKind.Asteroid, mass, SectorGenerator.AsteroidDensityKgPerM3,
            isMineable: true, new CircularOrbit(parent, orbitRadiusM, 3_000, 0), new Dictionary<string, long> { ["iron_ore"] = 2_000 });
        sector.AddBody(body);
        sector.MarkSurveyed(body.Id);
        return body;
    }

    [Fact]
    public void PastItsDeposits_ABodyIsMinedAsRock_AndShrinks()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = TestData.HomeRocks(sector)[0];
        Assert.True(rock.IsDismantlable);
        Assert.Equal("rock", rock.BulkMaterialId);
        Assert.Contains("rock", rock.MineableMaterials);
        foreach (var (material, kg) in rock.DepositsKg.ToList()) rock.Extract(material, kg); // ore gone (test shortcut)
        var ship = sector.GetCraft(game.PlayerCraftId);
        TestData.LandAt(sector, ship, rock);
        Assert.True(sector.TryAnchor(ship.Id).Done);
        var massBefore = sector.ComputeMassLedger().TotalKg;
        var radius = rock.RadiusM;

        for (var i = 0; i < TicksPerMinute; i++) sector.Step();

        Assert.True(ship.Inventory["rock"] > 0);
        Assert.True(rock.RadiusM < radius);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
        // Rock refines to slag and a little metal.
        Assert.Contains(Catalog.Recipes.Recipes, r => r.InputsKg.ContainsKey("rock"));
    }

    [Fact]
    public void IcyBodies_AreMinedAsIce()
    {
        var sector = TestData.NewGame().Sector;
        var ice = sector.Bodies.First(b => b.Name == "Ice-1");
        Assert.Equal("ice", ice.BulkMaterialId);
        Assert.True(ice.Deposit("ice") > ice.DepositsKg.GetValueOrDefault("ice"));
    }

    [Fact]
    public void MinedDownToItsRemnant_ABodyIsGone_AndEverythingOnItDriftsFree()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var pebble = TinyRock(sector, home.Id);
        var design = TestData.StarterShip();
        var miner = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), sector.GetPosition(pebble.Id), 0);
        sector.AddCraft(miner);
        TestData.LandAt(sector, miner, pebble);
        var job = new MiningJob(pebble.Id, "rock", game.ShipyardId, 1.0);
        Assert.True(sector.StartJob(miner.Id, job).Done, sector.StartJob(miner.Id, job).Message);
        pebble.Extract("rock", pebble.BulkKg - 3_000); // leave a few tonnes (test shortcut)
        pebble.Extract("iron_ore", 2_000);
        var massBefore = sector.ComputeMassLedger().TotalKg;
        sector.SetTarget(game.PlayerCraftId, pebble.Id);

        Assert.True(Run(sector, () => sector.Bodies.All(b => b.Id != pebble.Id), 10 * TicksPerMinute), job.Status);

        Assert.False(miner.IsLanded);
        Assert.Equal(BodyId.None, sector.GetCraft(game.PlayerCraftId).TargetBodyId);
        Assert.Contains("Pebble", sector.BodiesGone);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg); // the remnant is counted as scattered debris
        sector.Step();
        Assert.Contains("source body is missing", job.Status);

        // Saved and loaded: the job still names the body that's gone, and its id isn't handed out again.
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), Catalog).Sector;
        Assert.True(loaded.AllocateBodyId().Value > pebble.Id.Value);
        loaded.Step();
        Assert.Contains("source body is missing", loaded.GetCraft(miner.Id).Job!.Status);
    }

    [Fact]
    public void ABodyWithSomethingInOrbit_KeepsItsRemnant()
    {
        var sector = TestData.NewGame().Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var parent = TinyRock(sector, home.Id, "Parent", 9_000);
        TinyRock(sector, parent.Id, "Child", 200);
        parent.Extract("rock", parent.BulkKg);
        parent.Extract("iron_ore", 2_000);
        Assert.True(parent.MinedOut);
        sector.Step();
        Assert.Contains(parent, sector.Bodies);
        Assert.Contains("Child", sector.RemnantReason(parent));
        Assert.Equal(0, parent.Deposit("rock"));
    }

    [Fact]
    public void OriginalMass_SurvivesSaveLoad()
    {
        var sector = TestData.NewGame().Sector;
        var rock = TestData.HomeRocks(sector)[0];
        rock.Extract("rock", 1_000_000);
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), Catalog).Sector;
        var again = loaded.GetBody(rock.Id);
        Assert.Equal(rock.OriginalMassKg, again.OriginalMassKg);
        Assert.Equal(rock.BulkKg, again.BulkKg);
    }

    // --- #12 recruiting ---

    [Fact]
    public void Recruiting_AddsCrewAtOnce_ForFoodAndWater()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var (crew, food, water) = (yard.Crew, yard.Inventory["food"], yard.Inventory["water"]);
        var massBefore = sector.ComputeMassLedger().TotalKg;

        Assert.True(sector.TryRecruit(yard.Id, 1).Done);

        Assert.Equal(crew + 1, yard.Crew);
        Assert.Equal(food - SectorState.RecruitFoodKg, yard.Inventory["food"]);
        Assert.Equal(water - SectorState.RecruitWaterKg, yard.Inventory["water"]);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
        Assert.Contains("room for", sector.TryRecruit(yard.Id, 99).Message);
        Assert.Contains("no habitat", sector.TryRecruit(game.PlayerCraftId, 1).Message);
        yard.Inventory.TryRemove(new Dictionary<string, long> { ["food"] = yard.Inventory["food"] });
        Assert.Contains("food short", sector.TryRecruit(yard.Id, 1).Message);
    }

    // --- #14 supply routing ---

    private static CraftState DeployStationBesideYard(SectorState sector, CraftState yard)
    {
        var design = new ShipDesign("small_station", "Small station", new[]
        {
            new DesignCell(new GridPos(0, 0), "storage_silo"),
            new DesignCell(new GridPos(1, 0), "hull"),
            new DesignCell(new GridPos(0, -1), "docking_port"),
            new DesignCell(new GridPos(-1, 0), "solar_panel"),
        });
        var kit = new ConstructionKit(new OrderId(sector.NextOrderId), "Depot", design, design.ComputeStats(Catalog),
            design.ComputeBillOfMaterials(Catalog).ToWholeKg());
        sector.RestoreKit(yard.Id, kit);
        sector.RestoreNextOrderId(sector.NextOrderId + 1);
        var before = sector.Crafts.Select(c => c.Id).ToHashSet();
        Assert.True(sector.TryDeployKit(yard.Id, kit.Id).Done);
        return sector.Crafts.Single(c => !before.Contains(c.Id));
    }

    [Fact]
    public void CrewedStation_RequestsADaysFoodAndWater_AndFreightersBringIt()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        Assert.Equal((long)Math.Ceiling(yard.Crew * 3.0 * 24), SectorState.AutoSupplyKg(yard, "food"));
        Assert.Equal(0, sector.OpenRequestKg(yard.Id, "food")); // it has plenty now
        yard.Inventory.TryRemove(new Dictionary<string, long> { ["food"] = yard.Inventory["food"] });
        Assert.True(sector.OpenRequestKg(yard.Id, "food") > 0);

        var depot = DeployStationBesideYard(sector, yard);
        sector.DebugAddStock(depot.Id, "food", 2_000);
        var design = TestData.StarterShip();
        var freighter = new CraftState(sector.AllocateCraftId(), "F", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(400, -400), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(freighter);
        var job = new ServeRequestsJob(100);
        Assert.True(sector.StartJob(freighter.Id, job).Done);

        Assert.True(Run(sector, () => yard.Inventory["food"] >= SectorState.AutoSupplyKg(yard, "food") - 20, 30 * TicksPerMinute), job.Status);
    }

    [Fact]
    public void CrewedShip_TopsUpFoodAndWater_WhenDocked()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = DeployStationBesideYard(sector, yard);
        var ship = sector.GetCraft(game.PlayerCraftId);
        ship.Position = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        ship.Crew = 2;
        Assert.True(sector.StartJob(ship.Id, new HaulJob(yard.Id, "metal", depot.Id, 1_000, 0)).Done);
        var yardFood = yard.Inventory["food"];

        sector.Step();

        Assert.Equal(2 * 3 * 24, ship.Inventory["food"]);
        Assert.Equal(2 * 6 * 24, ship.Inventory["water"]);
        Assert.Equal(yardFood - 2 * 3 * 24, yard.Inventory["food"]);
    }
}
