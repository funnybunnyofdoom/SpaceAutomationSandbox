using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;
using Xunit.Abstractions;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#17: craft far from the player are simulated cheaply, without changing what happens.</summary>
public class LodTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private readonly ITestOutputHelper _log;

    public LodTests(ITestOutputHelper log) => _log = log;

    /// <summary>Six miners working the home rocks for the yard. <paramref name="observed"/>: full detail everywhere; otherwise nobody is watching.</summary>
    private static (SectorState Sector, List<CraftState> Miners) Fleet(bool observed)
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        if (observed) sector.FullDetailEverywhere = true;
        else sector.PilotCraftId = CraftId.None; // no observer: everything counts as far away
        var yard = sector.GetCraft(game.ShipyardId);
        var rocks = TestData.HomeRocks(sector);
        var design = TestData.StarterShip();
        var stats = design.ComputeStats(Catalog);
        var miners = new List<CraftState>();
        for (var i = 0; i < 6; i++)
        {
            var ship = new CraftState(sector.AllocateCraftId(), $"M{i}", design, stats, yard.Position + new Vector2D(200 + 60 * i, -300), 0)
            {
                Velocity = yard.Velocity,
            };
            sector.AddCraft(ship);
            var rock = rocks[i % rocks.Count];
            sector.StartJob(ship.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25));
            miners.Add(ship);
        }
        return (sector, miners);
    }

    private static long Delivered(List<CraftState> miners) => miners.Sum(m => ((MiningJob)m.Job!).DeliveredKg);

    [Fact]
    public void FarAway_SameWorkGetsDone_MassExact_AndMostlyAbstract()
    {
        var (full, fullMiners) = Fleet(observed: true);
        var (cheap, cheapMiners) = Fleet(observed: false);
        var massBefore = cheap.ComputeMassLedger().TotalKg;
        Assert.Equal(full.ComputeMassLedger().TotalKg, massBefore);

        var abstractTicks = 0L;
        for (var t = 0; t < 60 * TicksPerMinute; t++)
        {
            full.Step();
            cheap.Step();
            abstractTicks += cheap.AbstractCraftCount;
            Assert.Equal(0, full.AbstractCraftCount);
        }

        var fullKg = Delivered(fullMiners);
        var cheapKg = Delivered(cheapMiners);
        var share = (double)abstractTicks / (60 * TicksPerMinute * cheapMiners.Count);
        _log.WriteLine($"delivered in full detail {fullKg} kg, far away {cheapKg} kg; craft-time abstract {share:P0}");

        // Mass is exact both ways: nothing made or lost by skipping the detail.
        Assert.Equal(massBefore, cheap.ComputeMassLedger().TotalKg);
        Assert.Equal(massBefore, full.ComputeMassLedger().TotalKg);
        // Throughput stays close (an abstract leg isn't the exact path), and the cheap path is really used.
        Assert.True(fullKg > 0);
        Assert.InRange(cheapKg, fullKg * 0.75, fullKg * 1.25);
        Assert.True(share > 0.2, $"only {share:P0} of craft-time was abstract");
        // No crashes either way.
        Assert.All(fullMiners.Concat(cheapMiners), m => Assert.Equal(0, m.PartsLost));
    }

    [Fact]
    public void SaveLoad_MidTransit_ContinuesExactly()
    {
        var (reference, _) = Fleet(observed: false);
        var (sector, miners) = Fleet(observed: false);
        var ticks = 0;
        while (miners.All(m => m.Transit is null) && ticks++ < 20 * TicksPerMinute) sector.Step();
        Assert.Contains(miners, m => m.Transit is not null);
        while (reference.Tick < sector.Tick) reference.Step();

        var json = SaveGameSerializer.ToJson(sector, CraftId.None);
        Assert.Contains("\"transit\"", json);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        for (var i = 0; i < 5 * TicksPerMinute; i++) { reference.Step(); loaded.Step(); }

        Assert.Equal(SaveGameSerializer.ToJson(reference, CraftId.None), SaveGameSerializer.ToJson(loaded, CraftId.None));
    }

    [Fact]
    public void ObserverComingNear_DropsTheCraftBackIntoFullFlight()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var pilot = sector.GetCraft(sector.PilotCraftId);
        var yard = sector.GetCraft(game.ShipyardId);
        var rock = TestData.HomeRocks(sector).OrderByDescending(r => sector.GetPosition(r.Id).DistanceTo(yard.Position)).First();
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), "M", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(200, -300), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(ship);
        sector.StartJob(ship.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25));
        sector.PilotCraftId = CraftId.None; // watched by nobody: it goes abstract
        var ticks = 0;
        while (ship.Transit is null && ticks++ < 10 * TicksPerMinute) sector.Step();
        Assert.NotNull(ship.Transit);

        // The player turns up right beside it.
        pilot.Position = ship.Position + new Vector2D(500, 0);
        pilot.Velocity = ship.Velocity;
        sector.PilotCraftId = pilot.Id;
        sector.Step();
        Assert.Null(ship.Transit);
        Assert.False(ship.Dormant);
        for (var i = 0; i < TicksPerMinute; i++) sector.Step();
        Assert.Null(ship.Transit); // stays in full flight while watched
        Assert.Equal(0, ship.PartsLost);
    }
}
