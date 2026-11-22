using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Time acceleration gives identical results (#18); diagnostics: ledger, waiting groups, job history (#19).</summary>
public class TimeAndDiagnosticsTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    /// <summary>A busy start: a miner on Rock-1 and the yard building a ship.</summary>
    private static NewGameResult BusyGame()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        Assert.True(sector.StartJob(game.PlayerCraftId, new MiningJob(sector.Bodies.Single(b => b.Name == "Rock-1").Id, "iron_ore", game.ShipyardId, 0.5)).Done);
        Assert.True(sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), Catalog).Accepted);
        return game;
    }

    /// <summary>Runs a sector for <paramref name="realSeconds"/> of real time at a speed, the way the game loop does.</summary>
    private static void Play(SectorState sector, TimeWarp warp, double realSeconds, double frameS)
    {
        for (var t = 0.0; t < realSeconds - 1e-9; t += frameS)
        {
            var due = warp.TicksDue(frameS);
            for (var i = 0; i < due; i++) sector.Step();
            warp.RecordFrame(frameS, due, due);
        }
    }

    [Fact]
    public void FastForward_GivesExactlyTheSameResults_AsNormalSpeed()
    {
        var normal = BusyGame();
        var fast = BusyGame();
        var warp = new TimeWarp { Speed = 50 };

        Play(fast.Sector, warp, 12, 1.0 / 30);                       // 12 s real at 50× = 600 s
        Play(normal.Sector, new TimeWarp(), 600, 1.0 / 60);          // 600 s real at 1×

        Assert.Equal(12_000, fast.Sector.Tick);
        Assert.Equal(fast.Sector.Tick, normal.Sector.Tick);
        Assert.Equal(SaveGameSerializer.ToJson(normal.Sector, normal.PlayerCraftId), SaveGameSerializer.ToJson(fast.Sector, fast.PlayerCraftId));
    }

    [Fact]
    public void TimeWarp_StepsThroughLevels_Pauses_AndReportsSkippedTicks()
    {
        var warp = new TimeWarp();
        warp.Faster(); warp.Faster();
        Assert.Equal(5, warp.Speed);
        warp.Slower();
        Assert.Equal(2, warp.Speed);
        for (var i = 0; i < 20; i++) warp.Faster();
        Assert.Equal(TimeWarp.Levels[^1], warp.Speed);
        warp.Speed = 0;
        Assert.Equal(1, warp.Speed); // clamped

        warp.Paused = true;
        Assert.Equal(0, warp.TicksDue(1));
        Assert.Equal("PAUSED", warp.ToString());
        warp.Paused = false;

        warp.Speed = 100;
        var due = warp.TicksDue(0.1); // 0.1 s × 100 × 20/s = 200 ticks
        Assert.Equal(200, due);
        warp.RecordFrame(0.1, 50, due); // only 50 fitted the budget
        Assert.True(warp.Limited);
        Assert.Equal(150, warp.SkippedTicks);
        Assert.Contains("limited by CPU", warp.ToString());
    }

    [Fact]
    public void TickCostMeter_AveragesTheWindow()
    {
        var meter = new TickCostMeter(window: 3);
        foreach (var ms in new[] { 9.0, 1, 2, 3 }) meter.Add(ms);
        Assert.Equal(3, meter.Count);
        Assert.Equal(2, meter.AverageMs, 6);
        Assert.Equal(3, meter.MaxMs);
    }

    [Fact]
    public void MassLedger_TotalStaysConstant_AndCountsCheatsSeparately()
    {
        var game = BusyGame();
        var sector = game.Sector;
        var start = sector.ComputeMassLedger();
        for (var i = 0; i < 10 * 60 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        var later = sector.ComputeMassLedger();

        Assert.Equal(start.TotalKg, later.TotalKg);
        Assert.True(later.DepositsKg < start.DepositsKg); // mining moved some of it
        Assert.True(later.CraftKg > start.CraftKg);       // the yard finished a ship

        sector.DebugAddStock(game.ShipyardId, "metal", 1_234);
        Assert.Equal(start.TotalKg + 1_234, sector.ComputeMassLedger().TotalKg);
        Assert.Equal(1_234, sector.DebugAddedKg);
    }

    [Fact]
    public void WaitingJobs_AreGroupedByReason()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var design = TestData.StarterShip();
        for (var i = 0; i < 3; i++)
        {
            var ship = new CraftState(sector.AllocateCraftId(), $"F{i}", design, design.ComputeStats(Catalog),
                yard.Position + new Vector2D(300 + 80 * i, -300), 0) { Velocity = yard.Velocity };
            sector.AddCraft(ship);
            sector.StartJob(ship.Id, new ServeRequestsJob(1_000));
        }
        sector.Step();

        var group = Assert.Single(sector.WaitingJobs());
        Assert.Equal("no open requests", group.Reason);
        Assert.Equal(3, group.Crafts.Count);
    }

    [Fact]
    public void JobHistory_RecordsStateChanges_NotEveryTick()
    {
        var game = BusyGame();
        var sector = game.Sector;
        var job = sector.GetCraft(game.PlayerCraftId).Job!;
        for (var i = 0; i < 15 * 60 * FixedStepClock.TicksPerSecond; i++) sector.Step(); // long enough for a full mining trip

        var history = job.History.ToList();
        Assert.InRange(history.Count, 3, ShipJob.HistoryLength);
        Assert.Equal("flying to the source", history[0].Text);
        Assert.Contains(history, e => e.Text == "mining");
        Assert.True(history.Zip(history.Skip(1)).All(p => p.First.Tick <= p.Second.Tick && p.First.Text != p.Second.Text));

        for (var i = 0; i < 60 * 60 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        Assert.Equal(ShipJob.HistoryLength, job.History.Count); // capped
    }
}
