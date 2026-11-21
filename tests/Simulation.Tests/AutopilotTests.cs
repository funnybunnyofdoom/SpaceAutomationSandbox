using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;
using Xunit.Abstractions;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#41: automated ships shouldn't touch bodies they aren't heading for.</summary>
public class AutopilotTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private readonly ITestOutputHelper _log;

    public AutopilotTests(ITestOutputHelper log) => _log = log;

    private static readonly Dictionary<string, int> Where = new();

    /// <summary>30 miners spread over the three asteroids, all delivering to the yard, for an hour.</summary>
    private static int CountAccidentalLandings(ulong seed, out int cycles)
    {
        Where.Clear();
        var game = TestData.NewGame(seed);
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rocks = sector.Bodies.Where(b => b.IsMineable).ToList();
        var design = TestData.StarterShip();
        var stats = design.ComputeStats(Catalog);
        var miners = new List<CraftState>();
        for (var i = 0; i < 30; i++)
        {
            var ship = new CraftState(sector.AllocateCraftId(), $"M{i}", design, stats, yard.Position + new Vector2D(200 + 40 * i, -300), 0)
            {
                Velocity = yard.Velocity,
            };
            sector.AddCraft(ship);
            var rock = rocks[i % rocks.Count];
            sector.StartJob(ship.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25));
            miners.Add(ship);
        }

        var accidents = 0;
        var wasLanded = miners.ToDictionary(m => m.Id, m => m.IsLanded);
        for (var t = 0; t < 60 * TicksPerMinute; t++)
        {
            sector.Step();
            foreach (var m in miners)
            {
                if (m.IsLanded && !wasLanded[m.Id] && m.LandedOnBodyId != ((MiningJob)m.Job!).SourceBodyId)
                {
                    accidents++;
                    var key = $"{sector.GetBody(m.LandedOnBodyId).Name} during {((MiningJob)m.Job!).State}";
                    Where[key] = Where.GetValueOrDefault(key) + 1;
                }
                wasLanded[m.Id] = m.IsLanded;
            }
        }
        cycles = miners.Sum(m => m.Job!.CompletedCycles);
        return accidents;
    }

    [Theory]
    [InlineData(20261120UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void AutomatedShips_DontLandOnBodiesTheyAreNotHeadingFor(ulong seed)
    {
        var accidents = CountAccidentalLandings(seed, out var cycles);
        _log.WriteLine($"seed {seed}: {accidents} accidental landings, {cycles} completed cycles; "
                       + string.Join(", ", Where.Select(kv => $"{kv.Key}: {kv.Value}")));
        Assert.True(cycles > 0);
        Assert.Equal(0, accidents);
    }
}
