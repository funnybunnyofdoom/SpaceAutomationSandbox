using System.Linq;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Renaming craft after they're built (#115).</summary>
public sealed class RenameTests
{
    [Fact]
    public void Ships_and_stations_take_a_new_name_and_keep_it_in_saves()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.Crafts.First(c => c.IsShipyard);
        Assert.True(sector.RenameCraft(game.PlayerCraftId, "  Long \t\n  Haul  ").Done);
        Assert.Equal("Long Haul", sector.GetCraft(game.PlayerCraftId).Name);
        Assert.True(sector.RenameCraft(yard.Id, "Werft Süd: 2").Done);
        for (var i = 0; i < 30; i++) sector.Step();
        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, TestData.Catalog()).Sector;
        Assert.Equal("Long Haul", loaded.GetCraft(game.PlayerCraftId).Name);
        Assert.Equal("Werft Süd: 2", loaded.GetCraft(yard.Id).Name);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
    }

    [Fact]
    public void Bad_names_are_refused_with_the_reason()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var (id, was) = (game.PlayerCraftId, sector.GetCraft(game.PlayerCraftId).Name);
        var yard = sector.Crafts.First(c => c.IsShipyard);
        foreach (var bad in new[] { null, "", " \t ", new string('x', SectorState.MaxCraftName + 1), yard.Name.ToUpperInvariant() })
        {
            var result = sector.RenameCraft(id, bad);
            Assert.False(result.Done);
            Assert.NotEmpty(result.Message);
        }
        Assert.Equal(was, sector.GetCraft(id).Name);
        Assert.True(sector.RenameCraft(id, was.ToUpperInvariant()).Done); // its own name, written differently
        Assert.True(sector.RenameCraft(id, new string('x', SectorState.MaxCraftName)).Done);
        Assert.False(sector.RenameCraft(new CraftId(9999), "Ghost").Done);
    }

    [Fact]
    public void Only_your_own_can_be_named()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.Crafts.First(c => c.IsShipyard);
        yard.FactionId = 3;
        Assert.False(game.Sector.RenameCraft(yard.Id, "Mine").Done);
    }
}
