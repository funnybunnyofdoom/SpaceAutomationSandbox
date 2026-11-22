using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Traffic rules for automated craft (#55): they collide like everything else, and steer clear.</summary>
public class TrafficTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    private static CraftState AddMiner(SectorState sector, CraftState yard, int index, BodyId rock)
    {
        var design = TestData.StarterShip();
        var ship = new CraftState(sector.AllocateCraftId(), $"M{index}", design, design.ComputeStats(Catalog),
            yard.Position + new Vector2D(200 + 40 * index, -300), 0) { Velocity = yard.Velocity };
        sector.AddCraft(ship);
        Assert.True(sector.StartJob(ship.Id, new MiningJob(rock, "iron_ore", yard.Id, 0.25)).Done);
        return ship;
    }

    [Fact]
    public void BusyYard_ShipsQueueOnTheHoldingRing_AndAllGetServed_WithoutDamage()
    {
        // Six miners, two berths: some must queue. Every one still completes trips, and nobody is hurt.
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rock = sector.Bodies.First(b => b.IsMineable);
        var miners = Enumerable.Range(0, 6).Select(i => AddMiner(sector, yard, i, rock.Id)).ToList();

        var queued = false;
        for (var t = 0; t < 40 * TicksPerMinute && miners.Any(m => m.Job!.CompletedCycles < 2); t++)
        {
            sector.Step();
            queued |= miners.Any(m => m.Job!.Status.Contains("queued for a berth at Shipyard"));
        }

        Assert.True(queued, "with more ships than berths, some queue");
        Assert.All(miners, m => Assert.True(m.Job!.CompletedCycles >= 2, $"{m.Name}: {m.Job.Status}"));
        Assert.All(miners, m => Assert.True(m.PartsLost == 0, $"{m.Name} lost parts"));
    }

    [Fact]
    public void AutomatedCraft_NowCollideWithEachOther()
    {
        // Two idle-but-automated ships set on a collision course out of reach of any body: they push apart.
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rock = sector.Bodies.First(b => b.IsMineable);
        var a = AddMiner(sector, yard, 0, rock.Id);
        var b = AddMiner(sector, yard, 1, rock.Id);
        sector.StopJob(a.Id);
        sector.StopJob(b.Id); // not steering: only the collision rule acts
        var far = new Vector2D(5e6, 5e6);
        a.Position = a.PreviousPosition = far;
        b.Position = b.PreviousPosition = far + new Vector2D(60, 0);
        a.Velocity = new Vector2D(1, 0);
        b.Velocity = new Vector2D(-1, 0);
        for (var i = 0; i < 60 * FixedStepClock.TicksPerSecond; i++) sector.Step();
        Assert.True(b.Position.X - a.Position.X > 10, "they don't pass through each other");
    }

    [Fact]
    public void BerthClaims_SurviveSaveLoad_SoTheQueueContinuesExactly()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rock = sector.Bodies.First(b => b.IsMineable);
        var miners = Enumerable.Range(0, 4).Select(i => AddMiner(sector, yard, i, rock.Id)).ToList();
        for (var t = 0; t < 20 * TicksPerMinute && !miners.Any(m => !m.DockClaimStation.IsNone); t++) sector.Step();
        Assert.Contains(miners, m => !m.DockClaimStation.IsNone);

        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        Assert.Contains("dockClaimStationId", json);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        for (var i = 0; i < 2 * TicksPerMinute; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
    }
}
