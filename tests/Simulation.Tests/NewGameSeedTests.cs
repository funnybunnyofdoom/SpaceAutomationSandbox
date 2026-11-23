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
