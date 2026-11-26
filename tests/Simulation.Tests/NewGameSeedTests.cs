using System.Linq;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#62 new-game options: seeds from text, and the same seed gives the same world.</summary>
public sealed class NewGameSeedTests
{
    [Theory]
    [InlineData("12345", 12345UL)]
    [InlineData("  42 ", 42UL)]
    [InlineData("0", 0UL)]
    public void A_number_is_used_as_it_is(string text, ulong seed) => Assert.Equal(seed, NewGame.SeedFromText(text));

    [Fact]
    public void Text_gives_a_stable_seed()
    {
        Assert.Equal(NewGame.SeedFromText("Andromeda"), NewGame.SeedFromText("Andromeda"));
        Assert.Equal(NewGame.SeedFromText("Andromeda"), NewGame.SeedFromText(" Andromeda "));
        Assert.NotEqual(NewGame.SeedFromText("Andromeda"), NewGame.SeedFromText("andromeda"));
        // FNV-1a 64 of "a", so the mapping never changes between versions (shared seeds keep working).
        Assert.Equal(0xaf63dc4c8601ec8cUL, NewGame.SeedFromText("a"));
    }

    [Fact]
    public void The_same_seed_gives_the_same_starting_world()
    {
        var a = TestData.NewGame(seed: 777, surveyAll: false, keepPlayerFuel: true);
        var b = TestData.NewGame(seed: 777, surveyAll: false, keepPlayerFuel: true);
        Assert.Equal(SaveGameSerializer.ToJson(a.Sector, a.PlayerCraftId), SaveGameSerializer.ToJson(b.Sector, b.PlayerCraftId));
    }

    [Fact]
    public void Different_seeds_give_different_worlds()
    {
        var a = TestData.NewGame(seed: 777, surveyAll: false, keepPlayerFuel: true);
        var b = TestData.NewGame(seed: 778, surveyAll: false, keepPlayerFuel: true);
        Assert.NotEqual(SaveGameSerializer.ToJson(a.Sector, a.PlayerCraftId), SaveGameSerializer.ToJson(b.Sector, b.PlayerCraftId));
    }

    [Fact]
    public void The_game_name_is_kept_in_the_save_header()
    {
        var game = TestData.NewGame(seed: 5);
        var header = new SaveHeader("Run 1 (quicksave)", "2026-11-23T00:00:00Z", 60, game.Sector.Tick, game.Sector.Seed, "Run 1");
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId, header), TestData.Catalog());
        Assert.Equal("Run 1", loaded.Header!.GameName);
        Assert.Equal(5UL, loaded.Sector.Seed);
    }
}

/// <summary>#65 goal list: checked from the simulation state, kept once met, saved in the header.</summary>
public sealed class GoalListTests
{
    [Fact]
    public void A_new_game_meets_no_goals()
    {
        var game = TestData.NewGame(surveyAll: false, keepPlayerFuel: true);
        Assert.Empty(GoalList.MetNow(game.Sector, game.PlayerCraftId, savedDesigns: 0));
        Assert.Equal("survey", GoalList.Current(new System.Collections.Generic.HashSet<string>())!.Id);
    }

    [Fact]
    public void Goals_are_met_from_state_and_stay_done()
    {
        var game = TestData.NewGame(surveyAll: false);
        var done = new System.Collections.Generic.HashSet<string>();
        var asteroid = System.Linq.Enumerable.First(game.Sector.Bodies, b => b.IsMineable);
        game.Sector.MarkSurveyed(asteroid.Id);
        Assert.True(GoalList.Update(done, game.Sector, game.PlayerCraftId, savedDesigns: 1));
        Assert.Contains("survey", done);
        Assert.Contains("design", done);
        Assert.False(GoalList.Update(done, game.Sector, game.PlayerCraftId, savedDesigns: 0)); // nothing new; design stays done
        Assert.Contains("design", done);
        Assert.Equal("land", GoalList.Current(done)!.Id);
    }

    [Fact]
    public void The_first_automated_mining_route_needs_no_new_design_or_ship()
    {
        // #147: the goals go survey, land, mine, refine, automate; the starter ship can take the job itself.
        Assert.Equal(new[] { "survey", "land", "mine", "refine", "automate", "build", "design" }, GoalList.Steps.Select(s => s.Id));
        var game = TestData.NewGame();
        var sector = game.Sector;
        var asteroid = sector.Bodies.First(b => b.IsMineable && b.DepositsKg.Count > 0);
        sector.MarkSurveyed(asteroid.Id);
        var ore = asteroid.DepositsKg.Keys.First();
        var started = sector.StartJob(game.PlayerCraftId, new MiningJob(asteroid.Id, ore, game.ShipyardId, 0.25));
        Assert.True(started.Done, started.Message);
        Assert.Contains("automate", GoalList.MetNow(sector, game.PlayerCraftId, savedDesigns: 0));
        // And the next goal: the yard builds the starter design from the stock it starts with.
        Assert.True(sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), TestData.Catalog()).Accepted);
    }

    [Fact]
    public void Ore_in_the_hold_meets_the_mining_goal()
    {
        var game = TestData.NewGame();
        var player = game.Sector.GetCraft(game.PlayerCraftId);
        player.Inventory.Add(new System.Collections.Generic.Dictionary<string, long> { [CelestialBodyState.RockMaterial] = 100 });
        Assert.Contains("mine", GoalList.MetNow(game.Sector, game.PlayerCraftId, 0));
    }

    [Fact]
    public void Goal_progress_is_kept_in_the_save_header()
    {
        var game = TestData.NewGame(seed: 9);
        var header = new SaveHeader("G", "2026-11-23T00:00:00Z", 1, 0, 9, "G", GoalsOn: true, GoalsDone: new[] { "survey", "land" });
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId, header), TestData.Catalog());
        Assert.True(loaded.Header!.GoalsOn);
        Assert.Equal(new[] { "survey", "land" }, loaded.Header.GoalsDone);
        var old = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId, new SaveHeader("G", "x", 1, 0, 9)), TestData.Catalog());
        Assert.False(old.Header!.GoalsOn);
    }
}

/// <summary>Starting supplies option (creator request 2026-11-23): food and water for the yard's crew.</summary>
public sealed class StartingSuppliesTests
{
    [Theory]
    [InlineData(StartingSupplies.Lean, 1_000, 2_000)]
    [InlineData(StartingSupplies.Standard, 5_000, 10_000)]
    [InlineData(StartingSupplies.Generous, 15_000, 30_000)]
    public void The_yard_starts_with_the_chosen_food_and_water(StartingSupplies supplies, long food, long water)
    {
        var game = NewGame.Create(NewGame.DefaultSeed, TestData.Catalog(), TestData.StarterShip(), TestData.StarterShipyard(), supplies);
        var yard = game.Sector.GetCraft(game.ShipyardId);
        Assert.Equal(food, yard.Inventory["food"]);
        Assert.Equal(water, yard.Inventory["water"]);
        Assert.Equal(NewGame.StartingYardStockKg(supplies), yard.Inventory.Kg);
    }

    [Fact]
    public void Standard_is_the_default_and_more_than_before()
    {
        var game = TestData.NewGame();
        Assert.Equal(5_000, game.Sector.GetCraft(game.ShipyardId).Inventory["food"]);
    }
}
