using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#56 refits; #57 dismantling and packing stations into kits.</summary>
public class RefitTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    /// <summary>The starter ship with its drill swapped for a hull plate.</summary>
    private static ShipDesign NoDrill()
    {
        var starter = TestData.StarterShip();
        return new ShipDesign("no_drill", "No-drill ship",
            starter.Cells.Select(c => c.Pos == new GridPos(0, -3) ? new DesignCell(c.Pos, "hull") : c));
    }

    private static (SectorState Sector, CraftState Ship, CraftState Yard) DockedSecondShip()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), "Second", design, design.ComputeStats(Catalog),
            sector.BerthPosition(yard, 1, 0, yard.Position), 0) { Velocity = yard.Velocity };
        sector.AddCraft(ship);
        ship.Position = sector.BerthPosition(yard, 1, ship.CollisionRadiusM, yard.Position);
        Assert.True(sector.TryDock(ship.Id).Done);
        return (sector, ship, yard);
    }

    private static bool RunUntil(SectorState sector, Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (done()) return true;
            sector.Step();
        }
        return done();
    }

    private static Dictionary<string, long> Stock(CraftState c) => c.Inventory.Kg.ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void Refit_ChangesTheParts_KeepsTheCraft_AndEveryKgIsAccountedFor()
    {
        var (sector, ship, yard) = DockedSecondShip();
        var target = NoDrill();
        var massBefore = sector.ComputeMassLedger().TotalKg;
        var preview = sector.PreviewRefit(ship.Id, target);
        Assert.Equal(1, preview.PartsRemoved);
        Assert.Equal(1, preview.PartsAdded);
        var stockBefore = Stock(yard);

        Assert.True(sector.OrderRefit(yard.Id, ship.Id, target).Done);
        Assert.True(RunUntil(sector, () => ship.YardOrder is null, 10 * TicksPerMinute), ship.YardStatus);

        Assert.Equal("Second", ship.Name);
        Assert.Equal(target.Id, ship.Design.Id);
        Assert.Equal(0, ship.Stats.MiningRateKgPerS);
        Assert.True(ship.IsDocked);
        foreach (var material in preview.CostKg.Keys.Union(preview.ReturnedKg.Keys).Append(SectorState.ScrapMaterial).Distinct())
            Assert.Equal(stockBefore.GetValueOrDefault(material) - preview.CostKg.GetValueOrDefault(material) + preview.ReturnedKg.GetValueOrDefault(material)
                         + (material == SectorState.ScrapMaterial ? preview.ScrapKg : 0), yard.Inventory[material]);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Refit_PausesTheJob_WithAReason_ThenItCarriesOn()
    {
        var (sector, ship, yard) = DockedSecondShip();
        var rock = TestData.HomeRocks(sector)[0];
        var job = new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25);
        Assert.True(sector.StartJob(ship.Id, job).Done);
        // A refit that keeps the drill: swap a hull plate for a cargo bay.
        var starter = TestData.StarterShip();
        var target = new ShipDesign("more_cargo", "More cargo", starter.Cells.Select(c => c.Pos == new GridPos(0, 0) ? new DesignCell(c.Pos, "cargo") : c));
        Assert.True(sector.OrderRefit(yard.Id, ship.Id, target).Done);

        sector.Step();
        Assert.Contains("refit", job.Status);
        Assert.True(ship.IsDocked);
        Assert.True(RunUntil(sector, () => ship.YardOrder is null, 10 * TicksPerMinute), ship.YardStatus);
        Assert.True(RunUntil(sector, () => job.CompletedCycles >= 1, 60 * TicksPerMinute), job.Status);
        Assert.Equal("more_cargo", ship.Design.Id);
    }

    [Fact]
    public void Refit_IsRefused_WithAReason()
    {
        var (sector, ship, yard) = DockedSecondShip();
        Assert.Contains("already matches", sector.OrderRefit(yard.Id, ship.Id, TestData.StarterShip()).Message);
        Assert.Contains("station", sector.OrderRefit(yard.Id, ship.Id, TestData.StarterShipyard()).Message);
        ship.Inventory.Add("iron_ore", 30_000);
        var lessCargo = new ShipDesign("less", "Less cargo", TestData.StarterShip().Cells.Select(c => c.Pos == new GridPos(1, 0) ? new DesignCell(c.Pos, "hull") : c));
        Assert.Contains("won't fit", sector.OrderRefit(yard.Id, ship.Id, lessCargo).Message);
        Assert.True(sector.TryUndock(ship.Id).Done);
        Assert.Contains("must be docked", sector.OrderRefit(yard.Id, ship.Id, NoDrill()).Message);
    }

    [Fact]
    public void Dismantle_ReturnsMaterials_RemovesTheCraft_AndItsIdIsNeverReused()
    {
        var (sector, ship, yard) = DockedSecondShip();
        var id = ship.Id;
        var massBefore = sector.ComputeMassLedger().TotalKg;
        var preview = sector.PreviewDismantle(ship.Id);
        var stockBefore = Stock(yard);
        Assert.Equal(ship.Stats.TotalMassKg, preview.ReturnedKg.Values.Sum() + preview.ScrapKg, 0);
        Assert.True(preview.ScrapKg > 0);

        Assert.True(sector.OrderDismantle(yard.Id, ship.Id).Done);
        Assert.True(RunUntil(sector, () => sector.Crafts.All(c => c.Id != id), 20 * TicksPerMinute), ship.YardStatus);

        foreach (var (material, kg) in preview.ReturnedKg)
            Assert.Equal(stockBefore.GetValueOrDefault(material) + kg + (material == SectorState.ScrapMaterial ? preview.ScrapKg : 0), yard.Inventory[material]);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);

        // Saved and loaded, the id stays retired.
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), Catalog).Sector;
        Assert.True(loaded.AllocateCraftId().Value > id.Value);
        Assert.True(sector.AllocateCraftId().Value > id.Value);
    }

    [Fact]
    public void Dismantle_IsRefused_WithAReason()
    {
        var (sector, ship, yard) = DockedSecondShip();
        Assert.Contains("can't take itself apart", sector.OrderDismantle(yard.Id, yard.Id).Message);
        ship.Inventory.Add("iron_ore", 1_000);
        Assert.Contains("cargo", sector.OrderDismantle(yard.Id, ship.Id).Message);
        ship.Inventory.TryRemove(new Dictionary<string, long> { ["iron_ore"] = 1_000 });
        sector.PilotCraftId = ship.Id;
        Assert.Contains("You're flying", sector.OrderDismantle(yard.Id, ship.Id).Message);
    }

    [Fact]
    public void JobsUsingADismantledStation_WaitWithTheReason_AndTheGameStillSaves()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var station = DeployStationBesideYard(sector, yard);
        var rock = TestData.HomeRocks(sector)[0];
        var design = TestData.StarterShip();
        var miner = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(400, -400), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(miner);
        var job = new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), station.Id, 0.25);
        Assert.True(sector.StartJob(miner.Id, job).Done);

        Assert.True(sector.OrderDismantle(yard.Id, station.Id).Done, sector.OrderDismantle(yard.Id, station.Id).Message);
        Assert.True(RunUntil(sector, () => sector.Crafts.All(c => c.Id != station.Id), 20 * TicksPerMinute));
        sector.Step();
        Assert.Contains("destination is missing", job.Status);

        var json = SaveGameSerializer.ToJson(sector, sector.PilotCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        loaded.Step();
        Assert.Contains("destination is missing", loaded.GetCraft(miner.Id).Job!.Status);
    }

    /// <summary>A small orbital station (storage, port, panel), deployed beside the yard from a kit.</summary>
    private static CraftState DeployStationBesideYard(SectorState sector, CraftState yard)
    {
        var design = new ShipDesign("small_station", "Small station", new[]
        {
            new DesignCell(new GridPos(0, 0), "storage_silo"),
            new DesignCell(new GridPos(1, 0), "hull"),
            new DesignCell(new GridPos(0, -1), "docking_port"),
            new DesignCell(new GridPos(-1, 0), "solar_panel"),
        });
        Assert.Equal(DesignRole.Station, design.SuggestedRole(Catalog));
        var kit = new ConstructionKit(new OrderId(sector.NextOrderId), "Depot", design, design.ComputeStats(Catalog),
            design.ComputeBillOfMaterials(Catalog).ToWholeKg());
        sector.RestoreKit(yard.Id, kit);
        sector.RestoreNextOrderId(sector.NextOrderId + 1);
        var before = sector.Crafts.Select(c => c.Id).ToHashSet();
        Assert.True(sector.TryDeployKit(yard.Id, kit.Id).Done);
        return sector.Crafts.Single(c => !before.Contains(c.Id));
    }

    [Fact]
    public void Station_PacksIntoAKit_WithNothingLost()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var station = DeployStationBesideYard(sector, yard);
        var massBefore = sector.ComputeMassLedger().TotalKg;
        Assert.Contains(station, sector.CraftInYardReach(yard.Id));

        Assert.True(sector.OrderPackKit(yard.Id, station.Id).Done);
        Assert.True(RunUntil(sector, () => sector.Crafts.All(c => c.Id != station.Id), 20 * TicksPerMinute), station.YardStatus);

        var kit = Assert.Single(yard.Kits);
        Assert.Equal("small_station", kit.Design.Id);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
        // And it deploys again.
        Assert.True(sector.TryDeployKit(yard.Id, kit.Id).Done);
    }

    [Fact]
    public void Pack_IsRefused_ForShipsAndDamagedStations()
    {
        var (sector, ship, yard) = DockedSecondShip();
        Assert.Contains("only stations", sector.OrderPackKit(yard.Id, ship.Id).Message);
        var station = DeployStationBesideYard(sector, yard);
        sector.DebugDamagePart(station.Id, new GridPos(1, 0), 0.5);
        Assert.Contains("repair it first", sector.OrderPackKit(yard.Id, station.Id).Message);
    }

    [Fact]
    public void SaveLoad_MidRefit_ContinuesExactly()
    {
        SectorState Make()
        {
            var (s, ship, yard) = DockedSecondShip();
            Assert.True(s.OrderRefit(yard.Id, ship.Id, NoDrill()).Done);
            return s;
        }
        var reference = Make();
        var sector = Make();
        for (var i = 0; i < 20; i++) { reference.Step(); sector.Step(); }
        var json = SaveGameSerializer.ToJson(sector, sector.PilotCraftId);
        Assert.Contains("\"yardOrder\"", json);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        for (var i = 0; i < 5 * TicksPerMinute; i++) { reference.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(reference, reference.PilotCraftId), SaveGameSerializer.ToJson(loaded, loaded.PilotCraftId));
    }
}
