using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>The drawn tech tree and the map's routes (#104).</summary>
public sealed class MapAndTreeTests
{
    [Fact]
    public void Every_tech_has_its_own_place_below_everything_it_needs()
    {
        var catalog = TestData.Catalog().Research;
        var places = TechTree.Layout(catalog);
        Assert.Equal(catalog.Techs.Count, places.Count);
        Assert.Equal(places.Count, places.Select(p => (p.Row, p.Column)).Distinct().Count());
        var row = places.ToDictionary(p => p.Tech.Id, p => p.Row);
        Assert.NotEmpty(places);
        foreach (var place in places)
            foreach (var need in place.Tech.Requires ?? new()) Assert.True(row[need] < place.Row, $"{need} above {place.Tech.Id}");
        foreach (var group in places.GroupBy(p => p.Row)) Assert.Equal(Enumerable.Range(0, group.Count()), group.Select(p => p.Column).OrderBy(c => c)); // no gaps
    }

    [Fact]
    public void Routes_join_jobs_with_the_same_ends_and_count_whats_waiting()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        sector.DebugSurveyAll();
        var yard = sector.Crafts.First(c => c.IsShipyard);
        var rock = sector.Bodies.First(b => b.IsMineable && b.Name == "Rock-1");
        var ore = rock.MineableMaterials.First(m => rock.SurveyedDeposit(m) > 0);
        var design = TestData.StarterShip();
        var stats = design.ComputeStats(TestData.Catalog());
        for (var i = 0; i < 3; i++)
        {
            var ship = new CraftState(sector.AllocateCraftId(), "S", design, stats, yard.Position + new Vector2D(2_000 + 100 * i, 0), 0);
            sector.AddCraft(ship);
            Assert.True(sector.StartJob(ship.Id, new MiningJob(rock.Id, ore, yard.Id, 0.25)).Done);
        }
        var route = Assert.Single(sector.MapRoutes());
        Assert.Equal(TargetRef.Of(rock), route.From);
        Assert.Equal(TargetRef.Of(yard), route.To);
        Assert.Equal(3, route.Ships);
        Assert.InRange(route.Waiting, 0, 3);
        Assert.Equal(sector.StarAt(yard.Position)!.Id, sector.SystemOf(route.To));
        Assert.Equal(sector.SystemOf(route.To), sector.SystemOf(route.From));
        foreach (var ship in sector.Crafts.Where(c => c.Job is not null)) sector.StopJob(ship.Id);
        Assert.Empty(sector.MapRoutes());
    }
}
