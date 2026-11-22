using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Phase 3 acceptance: an autonomous miner loop (docs/04). All headless: no Godot node exists.</summary>
public class MiningJobTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    /// <summary>New game where the player's starter ship runs a metal job from Rock-1 to the shipyard.</summary>
    private static (SectorState Sector, CraftState Miner, CraftState Yard, CelestialBodyState Rock, MiningJob Job) MiningGame(
        double returnFraction = 0.25)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = sector.Bodies.First(b => b.IsMineable);
        var started = sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, "iron_ore", game.ShipyardId, returnFraction));
        Assert.True(started.Done, started.Message);
        var miner = sector.GetCraft(game.PlayerCraftId);
        return (sector, miner, sector.GetCraft(game.ShipyardId), rock, (MiningJob)miner.Job!);
    }

    /// <summary>
    /// All material anywhere, in kg: deposits + every inventory + build orders + refinery batches.
    /// Mining moves it, refining converts it (equal mass in and out); nothing may change the total.
    /// </summary>
    private static long TotalMetal(SectorState sector) =>
        sector.Bodies.Sum(b => b.DepositsKg.Values.Sum())
        + sector.Crafts.Sum(c => c.Inventory.TotalKg
                                 + c.BuildQueue.Sum(o => o.MaterialsKg.Values.Sum())
                                 + (c.RefiningBatch?.InputsKg.Values.Sum() ?? 0))
        + sector.EjectedKg.Values.Sum(); // thrown out of the sector by ejectors (#7)

    /// <summary>A station's stock plus what its refinery holds: grows by exactly what is delivered.</summary>
    private static long Holdings(CraftState craft) => craft.Inventory.TotalKg + (craft.RefiningBatch?.InputsKg.Values.Sum() ?? 0);

    private static bool RunUntil(SectorState sector, Func<bool> done, int maxTicks, Action? everyTick = null)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (done()) return true;
            sector.Step();
            everyTick?.Invoke();
        }
        return done();
    }

    [Fact]
    public void TenAutonomousCycles_WithoutLossOrDuplication()
    {
        var (sector, miner, yard, rock, job) = MiningGame();
        var metal = TotalMetal(sector);
        var rockMass = rock.MassKg;
        var deposit = rock.Deposit("iron_ore");
        var yardHoldings = Holdings(yard);

        var done = RunUntil(sector, () => job.CompletedCycles >= 10, 240 * TicksPerMinute,
            () => Assert.Equal(metal, TotalMetal(sector))); // checked every single tick

        Assert.True(done, $"only {job.CompletedCycles} cycles; now {job.Status}");
        var mined = deposit - rock.Deposit("iron_ore");
        Assert.Equal(rockMass - mined, rock.MassKg);              // body mass drops by exactly what was mined
        Assert.Equal(job.DeliveredKg, Holdings(yard) + sector.EjectedKg.Values.Sum() - yardHoldings); // everything delivered arrived (some refined, some slag ejected)
        Assert.Equal(mined, job.DeliveredKg + miner.Inventory["iron_ore"]);
        Assert.InRange(job.DeliveredKg, 10 * (long)(0.25 * miner.CargoCapacityKg), 11 * (long)(0.25 * miner.CargoCapacityKg));
    }

    [Fact]
    public void Mining_ShrinksTheAsteroid_ByExactlyTheMinedMass()
    {
        var (sector, miner, _, rock, job) = MiningGame();
        var radius = rock.RadiusM;
        var mass = rock.MassKg;
        Assert.True(RunUntil(sector, () => job.State == MiningJobState.Mining && miner.Inventory["iron_ore"] >= 5_000, 30 * TicksPerMinute));

        Assert.Equal(mass - miner.Inventory["iron_ore"], rock.MassKg);
        Assert.True(rock.RadiusM < radius);
        Assert.Equal(CelestialBodyState.RadiusFromMass(rock.MassKg, rock.DensityKgPerM3), rock.RadiusM);
    }

    [Fact]
    public void Drill_RunsAtItsRate_AndKeepsTheFractionOfAKilogram()
    {
        var (sector, miner, _, _, job) = MiningGame();
        Assert.True(RunUntil(sector, () => job.State == MiningJobState.Mining, 30 * TicksPerMinute));
        var before = miner.Inventory["iron_ore"] + miner.MiningRemainderKg;

        for (var i = 0; i < 7 * FixedStepClock.TicksPerSecond; i++) sector.Step(); // 7 s

        var after = miner.Inventory["iron_ore"] + miner.MiningRemainderKg;
        Assert.Equal(miner.Stats.MiningRateKgPerS * 7, after - before, 6);
        Assert.InRange(miner.MiningRemainderKg, 0, 1);
    }

    [Theory]
    [InlineData(MiningJobState.ToSource)]
    [InlineData(MiningJobState.Landing)]
    [InlineData(MiningJobState.Mining)]
    [InlineData(MiningJobState.Lifting)]
    [InlineData(MiningJobState.ToDestination)]
    [InlineData(MiningJobState.Unloading)]
    public void SaveLoad_InAnyJobState_ContinuesExactlyAsWithoutStopping(MiningJobState state)
    {
        var reference = MiningGame().Sector;
        var (sector, _, _, _, job) = MiningGame();
        // Reach the state (after the first tick, so "ToSource" is mid-flight rather than at start).
        Assert.True(RunUntil(sector, () => sector.Tick > 20 && job.State == state, 60 * TicksPerMinute), $"never reached {state}");
        while (reference.Tick < sector.Tick) reference.Step();

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, CraftId.None), Catalog).Sector;
        for (var i = 0; i < 5 * TicksPerMinute; i++) { reference.Step(); loaded.Step(); }

        Assert.Equal(SaveGameSerializer.ToJson(reference, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }

    [Fact]
    public void EmptySource_DeliversWhatItHas_ThenWaitsWithAReason()
    {
        var (sector, miner, yard, rock, job) = MiningGame();
        rock.Extract("iron_ore", rock.Deposit("iron_ore") - 3_000); // leave only 3 t (test-only shortcut)

        Assert.True(RunUntil(sector, () => job.State == MiningJobState.Waiting && job.CompletedCycles == 1, 60 * TicksPerMinute),
            job.Status);

        Assert.Equal(0, rock.Deposit("iron_ore"));
        Assert.Equal(3_000, job.DeliveredKg);
        Assert.Contains("no iron_ore left", job.Status);
        // It stays put and keeps saying why.
        for (var i = 0; i < TicksPerMinute; i++) sector.Step();
        Assert.Contains("no iron_ore left", job.Status);
    }

    [Fact]
    public void FullDestination_WaitsNearby_WithAReason_LeavingThePortFree()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = sector.Bodies.First(b => b.IsMineable);
        var yard = sector.GetCraft(game.ShipyardId);
        // A small depot next to the yard: one port and one cargo bay (20 t), already full.
        var depotDesign = new ShipDesign("depot", "Depot", new[]
        {
            new DesignCell(new GridPos(0, 0), "cargo"), new DesignCell(new GridPos(0, -1), "docking_port"),
        });
        var depot = new CraftState(sector.AllocateCraftId(), "Depot", depotDesign, depotDesign.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(depot);
        sector.PutOnRails(depot.Id, yard.RailOrbit! with { PhaseAtZeroRad = yard.RailOrbit!.PhaseAtZeroRad - 0.02 });
        depot.Inventory.Add("composites", (long)depotDesign.ComputeStats(Catalog).CargoCapacityKg);

        Assert.True(sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, "iron_ore", depot.Id, 0.25)).Done);
        var job = (MiningJob)sector.GetCraft(game.PlayerCraftId).Job!;

        Assert.True(RunUntil(sector, () => job.State == MiningJobState.Waiting && job.WaitReason!.Contains("is full"),
            60 * TicksPerMinute), job.Status);
        Assert.Contains("Depot is full", job.Status);
        Assert.False(sector.GetCraft(game.PlayerCraftId).IsDocked); // waits nearby: the port stays free
        Assert.Single(sector.FreePorts(depot));
    }

    [Fact]
    public void NoFreePort_WaitsWithAReason()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        for (var i = 0; i < 2 * TicksPerMinute; i++) sector.Step(); // both new ships take the two ports
        Assert.Empty(sector.FreePorts(yard));

        var rock = sector.Bodies.First(b => b.IsMineable);
        Assert.True(sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, "iron_ore", yard.Id, 0.25)).Done);
        var job = (MiningJob)sector.GetCraft(game.PlayerCraftId).Job!;

        Assert.True(RunUntil(sector, () => job.Status.Contains("no free docking port"), 60 * TicksPerMinute), job.Status);
    }

    [Fact]
    public void StartJob_IsRefused_WithTheReason()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var rock = sector.Bodies.First(b => b.IsMineable);
        var primary = sector.Bodies.Single(b => b.Kind == BodyKind.Star); // not mineable

        var noClamp = new ShipDesign("nc", "No clamp", TestData.StarterShip().Cells.Where(c => c.PartId != "anchor_clamp"));
        var plain = new CraftState(sector.AllocateCraftId(), "Plain", noClamp, noClamp.ComputeStats(Catalog), Vector2D.Zero, 0);
        sector.AddCraft(plain);

        Assert.Contains("anchor clamp", sector.StartJob(plain.Id, new MiningJob(rock.Id, "iron_ore", game.ShipyardId, 0.5)).Message);
        Assert.Contains("mineable", sector.StartJob(game.PlayerCraftId, new MiningJob(primary.Id, "iron_ore", game.ShipyardId, 0.5)).Message);
        Assert.Contains("docking port", sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, "iron_ore", plain.Id, 0.5)).Message);
        Assert.Contains("another craft", sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, "iron_ore", game.PlayerCraftId, 0.5)).Message);
        Assert.Null(sector.GetCraft(game.PlayerCraftId).Job);
    }

    [Fact]
    public void StoppedJob_LeavesTheShipIdle()
    {
        var (sector, miner, _, _, job) = MiningGame();
        for (var i = 0; i < 30 * FixedStepClock.TicksPerSecond; i++) sector.Step();

        Assert.True(sector.StopJob(miner.Id).Done);
        Assert.False(job.Active);
        Assert.Equal("stopped", job.Status);
        Assert.Equal(ShipControls.Idle, miner.Controls);
        sector.Step();
        Assert.Equal(ShipControls.Idle, miner.Controls); // nothing drives it any more
    }

    [Fact]
    public void TwoMiners_ShareTheYard_AndStillConserveMetal()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        for (var i = 0; i < TicksPerMinute; i++) sector.Step();
        var second = sector.Crafts.Single(c => c.Id != game.PlayerCraftId && c.Id != game.ShipyardId);
        var rock = sector.Bodies.First(b => b.IsMineable);
        Assert.True(sector.StartJob(game.PlayerCraftId, new MiningJob(rock.Id, "iron_ore", yard.Id, 0.25)).Done);
        Assert.True(sector.StartJob(second.Id, new MiningJob(rock.Id, "iron_ore", yard.Id, 0.25)).Done);
        var metal = TotalMetal(sector);

        Assert.True(RunUntil(sector,
            () => sector.GetCraft(game.PlayerCraftId).Job!.CompletedCycles >= 2 && second.Job!.CompletedCycles >= 2,
            120 * TicksPerMinute, () => Assert.Equal(metal, TotalMetal(sector))));
    }
}
