using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#13 robots, #15 Mk2 parts, #24 recovery when a station goes away and comes back.</summary>
public class WorkforceAndRecoveryTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    /// <summary>The starter ship (14 parts) plus four more below it: 18 parts, so it needs 2 crew. <paramref name="extra"/> is the first added part.</summary>
    private static ShipDesign Big(string extra, bool solar = true)
    {
        var cells = TestData.StarterShip().Cells
            .Select(c => !solar && c.PartId == "solar_panel" ? new DesignCell(c.Pos, "hull") : c)
            .Concat(new[]
            {
                new DesignCell(new GridPos(-1, 2), extra),
                new DesignCell(new GridPos(1, 2), "hull"),
                new DesignCell(new GridPos(-1, 3), "hull"),
                new DesignCell(new GridPos(1, 3), "hull"),
            });
        return new ShipDesign("big", "Big", cells);
    }

    private static (SectorState Sector, CraftState Craft) Spawn(ShipDesign design)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var craft = new CraftState(sector.AllocateCraftId(), "Big", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(400, -400), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(craft);
        return (sector, craft);
    }

    [Fact]
    public void RobotBay_RunsABigCraft_WithNoCrew()
    {
        var (sector, craft) = Spawn(Big("robot_bay"));
        Assert.Equal(2, craft.Stats.CrewRequired);
        Assert.Equal(4, craft.Stats.RobotCount);
        sector.Step();
        Assert.Null(craft.CrewShortReason);
        Assert.Equal(4, craft.RobotsWorking);
        Assert.Contains("robots 4/4", SectorState.CrewStatus(craft));

        // The same craft without the robot bay can't run.
        var (other, plain) = Spawn(Big("hull"));
        other.Step();
        Assert.Contains("needs 2 crew", plain.CrewShortReason);
    }

    [Fact]
    public void Robots_StopWithoutPower_AndSaySo()
    {
        var (sector, craft) = Spawn(Big("robot_bay", solar: false));
        craft.StoredEnergyKJ = 0;
        sector.Step();
        Assert.Equal(0, craft.RobotsWorking);
        Assert.Contains("no power for the robots", craft.CrewShortReason);
    }

    [Fact]
    public void DamagedRobotBay_HasFewerWorkingRobots()
    {
        var (sector, craft) = Spawn(Big("robot_bay"));
        sector.DebugDamagePart(craft.Id, new GridPos(-1, 2), 0.5);
        Assert.Equal(2, craft.Stats.RobotCount);
        sector.Step();
        Assert.Null(craft.CrewShortReason); // 2 robots still cover 2 crew
    }

    [Theory]
    [InlineData("thruster", "thruster_mk2")]
    [InlineData("drill", "drill_mk2")]
    [InlineData("solar_panel", "solar_panel_mk2")]
    [InlineData("battery", "battery_mk2")]
    [InlineData("refinery", "refinery_mk2")]
    [InlineData("fabricator", "fabricator_mk2")]
    public void Mk2Parts_DoubleTheOutput_ForMoreElectronicsAndComposites(string basic, string mk2)
    {
        var a = Catalog.Get(basic);
        var b = Catalog.Get(mk2);
        double Output(PartDefinition p) => p.ThrustN + p.MiningRateKgPerS + p.PowerGenerationKW + p.EnergyCapacityKJ + p.RefiningRateKgPerS + p.FabricationRateKgPerS;
        Assert.Equal(2 * Output(a), Output(b), 6);
        var billA = a.Bill.ToWholeKg();
        var billB = b.Bill.ToWholeKg();
        Assert.True(billB.GetValueOrDefault("electronics") > billA.GetValueOrDefault("electronics"));
        Assert.True(billB.GetValueOrDefault("composites") >= billA.GetValueOrDefault("composites"));
    }

    [Fact]
    public void RefitToAMk2Drill_DoublesTheMiningRate()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);
        ship.Position = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
        Assert.True(sector.TryDock(ship.Id).Done);
        var before = ship.Stats.MiningRateKgPerS;
        var target = new ShipDesign("mk2_miner", "Mk2 miner", ship.Design.Cells.Select(c => c.PartId == "drill" ? c with { PartId = "drill_mk2" } : c));
        Assert.True(sector.OrderRefit(yard.Id, ship.Id, target).Done);
        for (var i = 0; i < 5 * TicksPerMinute && ship.YardOrder is not null; i++) sector.Step();
        Assert.Null(ship.YardOrder);
        Assert.Equal(2 * before, ship.Stats.MiningRateKgPerS);
    }

    // --- #24: a station goes away (packed into a kit) and comes back ---

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
    public void PackedStation_ComesBackAsItself_AndJobsUsingItCarryOn()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var station = DeployStationBesideYard(sector, yard);
        var stationId = station.Id;
        sector.SetRequest(stationId, "metal", 5_000);
        var rock = TestData.HomeRocks(sector)[0];
        var design = TestData.StarterShip();
        var miner = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(400, -400), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(miner);
        var job = new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), stationId, 0.25);
        Assert.True(sector.StartJob(miner.Id, job).Done);

        // The station is packed away while the miner works...
        Assert.True(sector.OrderPackKit(yard.Id, stationId).Done);
        Assert.True(Run(sector, () => sector.Crafts.All(c => c.Id != stationId), 5 * TicksPerMinute));
        Assert.True(Run(sector, () => job.Status.Contains("come back"), 60 * TicksPerMinute), job.Status);
        for (var i = 0; i < TicksPerMinute; i++) sector.Step(); // keeps waiting

        // ...and comes back: same id, same requests, and the job delivers to it without being restarted.
        var kit = Assert.Single(yard.Kits);
        Assert.True(sector.TryDeployKit(yard.Id, kit.Id).Done);
        var back = sector.GetCraft(stationId);
        Assert.Equal(5_000, back.Requests["metal"]);
        Assert.True(Run(sector, () => job.CompletedCycles >= 1, 60 * TicksPerMinute), job.Status);
        Assert.True(back.Inventory.TotalKg > 0);
    }

    [Fact]
    public void PackedStation_KitSurvivesSaveLoad_AndStillComesBackAsItself()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var station = DeployStationBesideYard(sector, yard);
        var stationId = station.Id;
        Assert.True(sector.OrderPackKit(yard.Id, stationId).Done);
        Assert.True(Run(sector, () => sector.Crafts.All(c => c.Id != stationId), 5 * TicksPerMinute));

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), Catalog).Sector;
        var loadedYard = loaded.GetCraft(yard.Id);
        Assert.True(loaded.TryDeployKit(loadedYard.Id, Assert.Single(loadedYard.Kits).Id).Done);
        Assert.Equal("small_station", loaded.GetCraft(stationId).Design.Id);
    }

    private static bool Run(SectorState sector, Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (done()) return true;
            sector.Step();
        }
        return done();
    }
}
