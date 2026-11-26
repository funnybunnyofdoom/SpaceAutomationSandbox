using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Moving worlds (#127).</summary>
public sealed class MoveTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond;

    private static readonly ShipDesign Drivers = new("drivers", "Mass driver base", new[]
    {
        new DesignCell(new GridPos(-1, 0), "mass_driver"), new DesignCell(new GridPos(0, 0), "mass_driver"), new DesignCell(new GridPos(1, 0), "mass_driver"),
        new DesignCell(new GridPos(0, -1), "battery"), new DesignCell(new GridPos(-1, 1), "foundation"), new DesignCell(new GridPos(0, 1), "foundation"), new DesignCell(new GridPos(1, 1), "foundation"),
    });

    /// <summary>A game with everything researched and mass drivers anchored on a rock.</summary>
    private static (SectorState Sector, CelestialBodyState Rock, CraftState Base) Game(string rock = "Belt-1")
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        sector.DebugUnlockAllResearch();
        var body = sector.Bodies.First(b => b.Name == rock);
        var station = new CraftState(sector.AllocateCraftId(), "Drivers", Drivers, Drivers.ComputeStats(Catalog), sector.GetPosition(body.Id), 0);
        sector.AddCraft(station);
        sector.RestoreLanding(station.Id, body.Id, new Vector2D(0, -(body.RadiusM + 6)));
        sector.SetAnchoredForTest(station.Id);
        return (sector, body, station);
    }

    /// <summary>A new orbit a little way off that nothing stands in the way of.</summary>
    private static double Clear(SectorState sector, CelestialBodyState rock) =>
        new[] { 1_500, -1_500, 1_000, -1_000, 600, -600 }.Select(d => rock.Orbit!.RadiusM + d).First(r => sector.PlanMove(rock.Id, r).CanStart);

    private static void Powered(SectorState sector, CraftState station, int ticks)
    {
        for (var i = 0; i < ticks; i++) { station.StoredEnergyKJ = station.Stats.EnergyCapacityKJ; sector.Step(); }
    }

    [Fact]
    public void A_move_is_a_smooth_path_with_no_jumps_and_its_speed_is_its_change()
    {
        var from = new CircularOrbit(new BodyId(1), 40_000, 3_000, 0.3);
        var moving = CircularOrbit.Moving(from, 100, 1_000, 30_000);
        foreach (var t in new[] { 99.999, 100.0, 100.001, 550.0, 999.999, 1_000.0, 1_000.001 })
        {
            var gap = (moving.OffsetAt(t + 0.001) - moving.OffsetAt(t)).Length;
            Assert.True(gap < 0.2, $"{t}: {gap}");
            var speed = (moving.OffsetAt(t + 0.0005) - moving.OffsetAt(t - 0.0005)) * 1000;
            Assert.True((speed - moving.VelocityAt(t)).Length < 0.01, $"{t}");
        }
        Assert.Equal(from.OffsetAt(50), moving.OffsetAt(50));
        Assert.Equal(30_000, moving.OffsetAt(2_000).Length, 6);
        Assert.Equal(30_000, moving.Settled().OffsetAt(2_000).Length, 6);
        Assert.Equal(moving.OffsetAt(2_000).X, moving.Settled().OffsetAt(2_000).X, 6);
        var stopped = moving.StoppedAt(500);
        Assert.True((stopped.OffsetAt(500) - moving.OffsetAt(500)).Length < 1e-6);
    }

    [Fact]
    public void A_plan_shows_the_cost_and_what_stands_in_the_way()
    {
        var (sector, rock, _) = Game();
        var orbit = rock.Orbit!;
        var plan = sector.PlanMove(rock.Id, Clear(sector, rock));
        Assert.True(plan.CanStart, string.Join("; ", plan.Problems));
        Assert.Equal(3, plan.Drivers);
        Assert.True(plan.ReactionKg > 0 && plan.Seconds >= 60);
        Assert.Contains("near", string.Join(";", sector.PlanMove(rock.Id, sector.Bodies.First(b => b.Name == "Cinder").Orbit!.RadiusM).Problems));
        Assert.Contains(sector.PlanMove(rock.Id, 1).Problems, p => p.Contains("Too close"));
        var bare = TestData.NewGame().Sector;
        var plain = bare.Bodies.First(b => b.Name == "Belt-1");
        Assert.Contains(bare.PlanMove(plain.Id, plain.Orbit!.RadiusM + 2_000).Problems, p => p.Contains("Research"));
    }

    [Fact]
    public void It_moves_with_what_is_on_it_throws_exactly_its_rock_and_saves_mid_move()
    {
        var (sector, rock, station) = Game();
        var target = Clear(sector, rock);
        var plan = sector.PlanMove(rock.Id, target);
        var total = sector.ComputeMassLedger().TotalKg;
        var mass = rock.WholeMassKg;
        Assert.True(sector.StartMove(rock.Id, target).Done);
        var move = sector.Moves[rock.Id];
        Powered(sector, station, (int)((move.EndTick - sector.Tick) / 2));
        Assert.True(sector.Moves[rock.Id].ThrownKg > 0);
        var again = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), Catalog).Sector;
        Assert.Equal(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), SaveGameSerializer.ToJson(again, sector.PilotCraftId));
        Powered(sector, station, (int)(move.EndTick - sector.Tick) + 5);
        Assert.Empty(sector.Moves);
        Assert.Equal(target, rock.Orbit!.RadiusM, 3);
        Assert.Null(rock.Orbit.Move);
        Assert.Equal(mass - plan.ReactionKg, rock.WholeMassKg);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        Assert.Equal(rock.Id, station.LandedOnBodyId); // it came along
        Assert.True(station.Position.DistanceTo(sector.GetPosition(rock.Id)) < rock.RadiusM + 50);
        Assert.True(sector.MilestonesReached.ContainsKey("moved_world"));
    }

    [Fact]
    public void Without_power_it_stalls_where_it_is()
    {
        var (sector, rock, station) = Game();
        var start = rock.Orbit!.RadiusM;
        var to = Clear(sector, rock);
        Assert.True(sector.StartMove(rock.Id, to).Done);
        Powered(sector, station, 20 * Second);
        station.StoredEnergyKJ = 0;
        for (var i = 0; i < 3 && sector.Moves.Count > 0; i++) sector.Step();
        Assert.Empty(sector.Moves);
        Assert.Null(rock.Orbit!.Move);
        Assert.InRange(rock.Orbit.RadiusM, Math.Min(start, to), Math.Max(start, to));
        Assert.Contains(sector.Events, e => e.Text.Contains("stalled"));
    }

    [Fact]
    public void Civilizations_protest_in_their_system()
    {
        var (sector, rock, _) = Game();
        var (star, _) = SectorGenerator.GenerateSystem(sector, 1);
        var faction = sector.MeetCivilization(sector.Stars[0], cultureId: "pacifist", baseline: 0); // claims home here, as a test
        Assert.True(sector.StartMove(rock.Id, Clear(sector, rock)).Done);
        Assert.True(faction.Grievance >= 29);
    }
}
