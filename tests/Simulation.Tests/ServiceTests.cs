using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#3 repairs and servicing; #4 survey jobs (automated scouts).</summary>
public class ServiceTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private static readonly GridPos Drill = new(0, -3);
    private static readonly GridPos LeftThruster = new(-1, 1);

    private static (SectorState Sector, CraftState Ship, CraftState Yard) Docked()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var ship = sector.GetCraft(game.PlayerCraftId);
        ship.Position = sector.BerthPosition(yard, 0, ship.CollisionRadiusM, yard.Position);
        ship.Velocity = yard.Velocity;
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

    [Fact]
    public void DockedAtAShipyard_DamageIsRepaired_ForItsShareOfTheMaterials()
    {
        var (sector, ship, yard) = Docked();
        var massBefore = sector.ComputeMassLedger().TotalKg;
        sector.DebugDamagePart(ship.Id, LeftThruster, 0.4);
        var thrustDamaged = ship.Stats.TotalThrustN;
        var bill = Catalog.Get(ship.Design.Cells.First(c => c.Pos == LeftThruster).PartId).Bill.ToWholeKg();
        var stockBefore = bill.Keys.ToDictionary(m => m, m => yard.Inventory[m]);

        Assert.True(RunUntil(sector, () => !SectorState.NeedsRepair(ship), 10 * TicksPerMinute), ship.RepairStatus);

        Assert.True(ship.Stats.TotalThrustN > thrustDamaged);
        foreach (var (material, kg) in bill)
            Assert.Equal((long)Math.Ceiling(kg * 0.6 - 1e-9), stockBefore[material] - yard.Inventory[material]);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg); // the worn material is counted as consumed
    }

    [Fact]
    public void BrokenOffPart_IsRebuilt_AtFullCost_AndTheCraftIsWholeAgain()
    {
        var (sector, ship, yard) = Docked();
        var cells = ship.Design.Cells.Count;
        var massBefore = sector.ComputeMassLedger().TotalKg;
        sector.DebugDamagePart(ship.Id, Drill, 0);
        Assert.Equal(cells - 1, ship.Design.Cells.Count);
        Assert.Equal(1, SectorState.MissingParts(ship));
        Assert.Equal(0, ship.Stats.MiningRateKgPerS);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg); // the drill is debris now

        Assert.True(RunUntil(sector, () => !SectorState.NeedsRepair(ship), 20 * TicksPerMinute), ship.RepairStatus);

        Assert.Equal(cells, ship.Design.Cells.Count);
        Assert.Null(ship.IntactDesign);
        Assert.True(ship.Stats.MiningRateKgPerS > 0);
        Assert.True(ship.IsDocked && ship.DockedToCraftId == yard.Id);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void WithoutMaterials_TheRepairWaits_AndSaysWhatIsMissing()
    {
        var (sector, ship, yard) = Docked();
        sector.DebugDamagePart(ship.Id, Drill, 0);
        var bill = Catalog.Get("drill").Bill.ToWholeKg();
        foreach (var material in bill.Keys) yard.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { [material] = yard.Inventory[material] });

        for (var i = 0; i < 10 * TicksPerMinute; i++) sector.Step();

        Assert.Equal(1, SectorState.MissingParts(ship));
        Assert.StartsWith("waiting:", ship.RepairStatus);
        Assert.Contains(bill.Keys.First(), ship.RepairStatus);
    }

    [Fact]
    public void AutomatedMiner_GoesForRepairs_ThenCarriesOn()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rock = TestData.HomeRocks(sector)[0];
        var design = TestData.StarterShip();
        var miner = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(300, -300), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(miner);
        var job = new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25);
        Assert.True(sector.StartJob(miner.Id, job).Done);
        sector.DebugDamagePart(miner.Id, LeftThruster, 0.3);
        var massBefore = sector.ComputeMassLedger().TotalKg;

        var sawStatus = false;
        Assert.True(RunUntil(sector, () =>
        {
            sawStatus |= job.Status.Contains("repairs at " + yard.Name);
            return !SectorState.NeedsRepair(miner);
        }, 20 * TicksPerMinute), job.Status);
        Assert.True(sawStatus);
        Assert.True(miner.ServiceAt.IsNone || SectorState.NeedsRepair(miner) == false);

        // Back to work: it completes a mining cycle.
        Assert.True(RunUntil(sector, () => job.CompletedCycles >= 1, 60 * TicksPerMinute), job.Status);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void LightDamage_DoesntInterruptTheJob()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rock = TestData.HomeRocks(sector)[0];
        var design = TestData.StarterShip();
        var miner = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(300, -300), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(miner);
        Assert.True(sector.StartJob(miner.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25)).Done);
        sector.DebugDamagePart(miner.Id, LeftThruster, 0.8);
        for (var i = 0; i < TicksPerMinute; i++)
        {
            sector.Step();
            Assert.True(miner.ServiceAt.IsNone);
        }
    }

    [Fact]
    public void Scout_SurveysBodies_SoJobsCanUseThem()
    {
        var game = TestData.NewGame(surveyAll: false);
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var design = TestData.StarterShip();
        var scout = new CraftState(sector.AllocateCraftId(), "Scout", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(300, -300), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(scout);
        sector.PilotCraftId = CraftId.None; // nobody visits anything: only the scout surveys
        var rocks = TestData.HomeRocks(sector);
        Assert.All(rocks, r => Assert.False(r.Surveyed));
        var job = new SurveyJob();
        Assert.True(sector.StartJob(scout.Id, job).Done);

        Assert.True(RunUntil(sector, () => rocks.All(r => r.Surveyed), 60 * TicksPerMinute), job.Status);
        Assert.True(job.CompletedCycles >= rocks.Count);

        // A miner can now work a rock nobody piloted to.
        var miner = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(600, -300), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(miner);
        Assert.True(sector.StartJob(miner.Id, new MiningJob(rocks[0].Id, rocks[0].DepositsKg.Keys.First(), yard.Id, 0.25)).Done);
    }

    [Fact]
    public void Scout_WithNothingLeft_WaitsWithAReason()
    {
        var game = TestData.NewGame(); // everything surveyed
        var sector = game.Sector;
        var ship = sector.GetCraft(game.PlayerCraftId);
        var job = new SurveyJob();
        Assert.True(sector.StartJob(ship.Id, job).Done);
        sector.Step();
        Assert.Equal(SurveyJobState.Waiting, job.State);
        Assert.Contains("every body", job.Status);
    }

    [Fact]
    public void SaveLoad_MidRepair_AndScouting_ContinuesExactly()
    {
        SectorState Make()
        {
            var (sector, ship, _) = Docked();
            sector.DebugDamagePart(ship.Id, Drill, 0);
            sector.DebugDamagePart(ship.Id, LeftThruster, 0.4);
            var design = TestData.StarterShip();
            var yard = sector.Crafts.First(c => c.IsShipyard);
            var scout = new CraftState(sector.AllocateCraftId(), "Scout", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(300, -300), 0)
            {
                Velocity = yard.Velocity,
            };
            sector.AddCraft(scout);
            foreach (var body in sector.Bodies.Where(b => b.IsMineable)) body.Surveyed = false;
            sector.StartJob(scout.Id, new SurveyJob());
            return sector;
        }
        var reference = Make();
        var sector = Make();
        var ship = sector.GetCraft(sector.PilotCraftId);
        // Save while the broken-off drill is being rebuilt.
        for (var i = 0; i < 2 * TicksPerMinute && !(SectorState.MissingParts(ship) == 1 && ship.PartHealth.Count == 0 && ship.RepairProgressKg > 0); i++)
        {
            reference.Step();
            sector.Step();
        }
        Assert.True(SectorState.NeedsRepair(ship) && ship.RepairProgressKg > 0, $"needs {SectorState.NeedsRepair(ship)} progress {ship.RepairProgressKg} docked {ship.DockedToCraftId} status {ship.RepairStatus} damaged {ship.PartHealth.Count} missing {SectorState.MissingParts(ship)}");

        var json = SaveGameSerializer.ToJson(sector, sector.PilotCraftId);
        Assert.Contains("\"intactDesign\"", json);
        Assert.Contains("\"survey\"", json);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        for (var i = 0; i < 5 * TicksPerMinute; i++) { reference.Step(); loaded.Step(); }

        Assert.Equal(SaveGameSerializer.ToJson(reference, reference.PilotCraftId), SaveGameSerializer.ToJson(loaded, loaded.PilotCraftId));
    }
}
