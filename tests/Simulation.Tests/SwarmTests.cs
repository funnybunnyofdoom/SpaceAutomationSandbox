using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Swarms (#30): many ships as one flow, with exact mass, real limits and throughput close to real ships.</summary>
public class SwarmTests
{
    private const int Minute = 60 * FixedStepClock.TicksPerSecond;

    private static (SectorState Sector, CraftState Yard, CelestialBodyState Rock, List<CraftId> Ships) Game(int ships, string rockName = "Moon")
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rock = sector.Bodies.First(b => b.Name == rockName);
        rock.AddDeposit("iron_ore", 500_000_000);
        sector.MarkSurveyed(rock.Id);
        var design = TestData.StarterShip();
        var stats = design.ComputeStats(TestData.Catalog());
        var ids = new List<CraftId>();
        for (var i = 0; i < ships; i++)
        {
            var craft = new CraftState(sector.AllocateCraftId(), $"M{i}", design, stats,
                yard.Position + new Vector2D(200 + 30 * (i % 20), -300 - 30 * (i / 20)), 0) { Velocity = yard.Velocity };
            sector.AddCraft(craft);
            var started = sector.StartJob(craft.Id, new MiningJob(rock.Id, "iron_ore", yard.Id, 0.25));
            Assert.True(started.Done, started.Message);
            ids.Add(craft.Id);
        }
        yard.LegacyUnlimitedStorage = true; // room for anything (storage limits have their own test)
        return (sector, yard, rock, ids);
    }

    private static void Run(SectorState s, int ticks) { for (var i = 0; i < ticks; i++) s.Step(); }

    private static SwarmState Form(SectorState sector, List<CraftId> ids)
    {
        var formed = sector.FormSwarm(ids);
        Assert.True(formed.Done, formed.Message);
        return sector.Swarms[^1];
    }

    /// <summary>
    /// Every kg anywhere: bodies, craft and their holds and queues, swarms and their holds, and what left the sector.
    /// </summary>
    private static double TotalMass(SectorState s) =>
        s.Bodies.Where(b => b.IsMineable).Sum(b => b.MassKg)
        + s.Crafts.Sum(c => c.Stats.TotalMassKg + c.Inventory.TotalKg + c.KitsKg + c.BuildQueue.Sum(o => o.MaterialsKg.Values.Sum())
                            + (c.RefiningBatch?.InputsKg.Values.Sum() ?? 0))
        + s.Swarms.Sum(w => w.DryMassKg + w.Inventory.TotalKg)
        + s.EjectedKg.Values.Sum();

    [Theory]
    [InlineData("Moon")]
    [InlineData("Rock-1")]
    [InlineData("Rock-3")]
    public void Throughput_IsCloseToRealShips(string rock)
    {
        var (real, _, _, ships) = Game(5, rock);
        Run(real, 120 * Minute);
        var realKg = ships.Sum(i => real.GetCraft(i).Job!.DeliveredKg);
        var (sector, _, _, ids) = Game(5, rock);
        var swarm = Form(sector, ids);
        Run(sector, 120 * Minute);
        Assert.InRange(swarm.DeliveredKg / (double)realKg, 0.75, 1.25);
    }

    [Fact]
    public void Forming_TakesTheShipsCargoAndMassExactly()
    {
        var (sector, _, rock, ids) = Game(6);
        Run(sector, 8 * Minute); // some are loaded, some in flight, some docked
        var mass = TotalMass(sector);
        var cargo = ids.Sum(i => sector.GetCraft(i).Inventory.TotalKg);
        var crafts = sector.Crafts.Count;
        var swarm = Form(sector, ids);
        Assert.Equal(6, swarm.Count);
        Assert.Equal(cargo, swarm.Inventory.TotalKg);
        Assert.Equal(crafts - 6, sector.Crafts.Count);
        Assert.Equal(mass, TotalMass(sector));
        Assert.Equal(rock.Id, swarm.SourceBodyId);
        Run(sector, 10); // nothing refers to the removed craft any more
    }

    [Fact]
    public void Mass_IsExact_EveryTick()
    {
        var (sector, _, _, ids) = Game(40);
        var ledger = sector.ComputeMassLedger().TotalKg;
        var swarm = Form(sector, ids);
        Assert.Equal(ledger, sector.ComputeMassLedger().TotalKg);
        var mass = TotalMass(sector);
        for (var i = 0; i < 20 * Minute; i++)
        {
            sector.Step();
            Assert.Equal(mass, TotalMass(sector));
            Assert.True(swarm.Inventory.TotalKg <= swarm.CargoCapacityKg);
        }
        Assert.True(swarm.DeliveredKg > 0);
        Assert.Equal(ledger, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Forming_IsRefused_WithReasons()
    {
        var (sector, yard, _, ids) = Game(6);
        Assert.Contains("at least", sector.FormSwarm(ids.Take(2).ToList()).Message);
        sector.StopJob(ids[0]);
        Assert.Contains("no mining job", sector.FormSwarm(ids).Message);
        sector.StartJob(ids[0], new MiningJob(sector.Bodies.First(b => b.Name == "Rock-3").Id, "iron_ore", yard.Id, 0.25));
        Assert.Contains("same job", sector.FormSwarm(ids).Message);
        sector.PilotCraftId = ids[1];
        Assert.Contains("flying it", sector.FormSwarm(ids.Skip(1).ToList()).Message);
        Assert.Empty(sector.Swarms);
        Assert.True(ids.All(i => sector.Crafts.Any(c => c.Id == i))); // a refusal changes nothing
    }

    [Fact]
    public void Candidates_GroupByDesignAndJob_AndJoinAnExistingSwarm()
    {
        var (sector, _, _, ids) = Game(7);
        var candidate = Assert.Single(sector.SwarmCandidates());
        Assert.Equal(ids, candidate.Ships);
        var swarm = Form(sector, ids.Take(6).ToList());
        var rest = Assert.Single(sector.SwarmCandidates()); // one ship is enough to join
        Assert.True(sector.FormSwarm(rest.Ships).Done);
        Assert.Same(swarm, Assert.Single(sector.Swarms));
        Assert.Equal(7, swarm.Count);
    }

    [Fact]
    public void Splitting_SharesCargoExactly_AndShipsWorkAgain()
    {
        var (sector, _, _, ids) = Game(6);
        var swarm = Form(sector, ids);
        Run(sector, 15 * Minute);
        var mass = TotalMass(sector);
        var before = sector.Crafts.Select(c => c.Id).ToHashSet();
        Assert.True(sector.SplitSwarm(swarm.Id, 2).Done);
        Assert.Equal(4, swarm.Count);
        Assert.Equal(mass, TotalMass(sector));
        Assert.True(sector.SplitSwarm(swarm.Id, 100).Done);
        Assert.Empty(sector.Swarms);
        Assert.Equal(mass, TotalMass(sector));
        var back = sector.Crafts.Where(c => !before.Contains(c.Id)).ToList();
        Assert.Equal(6, back.Count);
        Assert.All(back, c => Assert.True(c.CargoKg <= c.CargoCapacityKg && c.IsAutomated && c.Id.Value > ids.Max(i => i.Value)));
        Run(sector, 40 * Minute);
        Assert.Contains(back, c => c.Job!.DeliveredKg > 0);
    }

    [Fact]
    public void DockingPorts_LimitDeliveries_AndTheReasonShows()
    {
        var (sector, yard, _, ids) = Game(30);
        var swarm = Form(sector, ids);
        swarm.Count = 5_000; // far more than two ports can take
        Run(sector, 60 * Minute);
        var most = yard.Stats.DockingPorts.Count * swarm.LoadKg / SectorState.SwarmBerthS * 3600;
        Assert.InRange(swarm.DeliveredKg, 0.5 * most, most);
        Assert.Contains("docking port", swarm.LimitReason);
    }

    [Fact]
    public void FullDestination_StopsMining_WithoutLosingAnything()
    {
        var (sector, yard, rock, ids) = Game(30);
        yard.LegacyUnlimitedStorage = false;
        yard.Inventory.Add("slag", yard.FreeCargoKg - 1_000);
        var swarm = Form(sector, ids);
        Run(sector, 30 * Minute);
        Assert.Contains("full", swarm.LimitReason);
        Assert.True(swarm.Inventory["iron_ore"] <= (long)(swarm.Count * swarm.LoadKg));
        var left = rock.Deposit("iron_ore");
        Run(sector, Minute);
        Assert.InRange(left - rock.Deposit("iron_ore"), 0, 100_000); // only what the refinery made room for
    }

    [Fact]
    public void MinedOutSource_Waits_AndRetargetingWorks()
    {
        var (sector, yard, rock, ids) = Game(6);
        var swarm = Form(sector, ids);
        rock.Extract("iron_ore", rock.Deposit("iron_ore"));
        Run(sector, 20 * Minute);
        Assert.Contains("no iron_ore left", swarm.LimitReason);
        Assert.Equal(0, swarm.Inventory["iron_ore"]); // what was mined was delivered
        var other = sector.Bodies.First(b => b.Name == "Rock-3");
        Assert.True(sector.RetargetSwarm(swarm.Id, other.Id, "iron_ore", yard.Id).Done);
        var delivered = swarm.DeliveredKg;
        Run(sector, 30 * Minute);
        Assert.True(swarm.DeliveredKg > delivered);
    }

    [Fact]
    public void UnsurveyedSites_AreNotMined()
    {
        var (sector, _, rock, ids) = Game(6);
        var swarm = Form(sector, ids);
        foreach (var site in rock.Sites) site.Surveyed = false;
        var mass = rock.MassKg;
        Run(sector, 5 * Minute);
        Assert.Equal(mass, rock.MassKg);
        Assert.Contains("surveyed", swarm.LimitReason);
    }

    [Fact]
    public void Growing_BuildsShipsIntoTheSwarm_AndWaitsForMaterials()
    {
        var (sector, yard, _, ids) = Game(6);
        var swarm = Form(sector, ids);
        var bill = TestData.StarterShip().ComputeBillOfMaterials(TestData.Catalog()).ToWholeKg();
        foreach (var material in bill.Keys) yard.Inventory.TryRemove(new Dictionary<string, long> { [material] = yard.Inventory[material] });
        foreach (var (material, kg) in bill) yard.Inventory.Add(material, 3 * kg);
        Assert.True(sector.GrowSwarm(swarm.Id, yard.Id, 5).Done);
        var mass = TotalMass(sector);
        var crafts = sector.Crafts.Count;
        Run(sector, 10 * Minute);
        Assert.True(swarm.Count >= 9, $"{swarm.Count}: {swarm.GrowWaitReason}"); // three ships' worth of materials, plus what the refinery made
        Assert.Equal(crafts, sector.Crafts.Count); // no individual craft appeared
        Assert.Equal(mass, TotalMass(sector));
        if (swarm.Count < 11) Assert.Contains("Not enough materials", swarm.GrowWaitReason);
        sector.StopGrowing(swarm.Id);
        Assert.Equal(0, swarm.GrowRemaining);
    }

    [Fact]
    public void SaveAndLoad_ContinuesExactly()
    {
        var (sector, yard, _, ids) = Game(12);
        var swarm = Form(sector, ids);
        sector.GrowSwarm(swarm.Id, yard.Id, 3);
        Run(sector, 7 * Minute + 13);
        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        var loaded = SaveGameSerializer.FromJson(json, TestData.Catalog()).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, CraftId.None));
        sector.PilotCraftId = CraftId.None;
        Run(sector, 9 * Minute);
        Run(loaded, 9 * Minute);
        Assert.Equal(SaveGameSerializer.ToJson(sector, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }

    [Fact]
    public void Cost_DoesNotGrowWithTheSwarm()
    {
        var (sector, _, _, ids) = Game(6);
        var swarm = Form(sector, ids);
        swarm.Count = 1_000_000;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Run(sector, 2_000);
        Assert.True(watch.Elapsed.TotalMilliseconds / 2_000 < 10, $"{watch.Elapsed.TotalMilliseconds / 2_000:0.00} ms per tick");
    }
}
