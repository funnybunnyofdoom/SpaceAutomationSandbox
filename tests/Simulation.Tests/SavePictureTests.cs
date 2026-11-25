using System;
using System.IO;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>What the save list shows (#98), and that big saves are listed at all.</summary>
public sealed class SavePictureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sas_pictures_" + Guid.NewGuid().ToString("N"));

    public SavePictureTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string race, bool header, int craft = 0)
    {
        var catalog = TestData.Catalog();
        catalog.Races.TryGet(race, out var chosen);
        var game = NewGame.Create(3, catalog, TestData.StarterShip(), TestData.StarterShipyard(), race: chosen ?? new RaceDefinition { SchemaVersion = 2, Id = race, Name = "Own", Crew = 2, Supplies = true });
        for (var i = 0; i < craft; i++)
            game.Sector.AddCraft(new CraftState(game.Sector.AllocateCraftId(), "C", TestData.StarterShip(), TestData.StarterShip().ComputeStats(catalog), new Vector2D(1e8 + i * 1e3, 0), 0));
        var path = Path.Combine(_folder, $"save_{Guid.NewGuid():N}.json");
        SaveStore.Write(path, SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId,
            new SaveHeader("Big", DateTime.UtcNow.ToString("O"), 5, 1, 3, "Game", true, new[] { "survey", "land" }, header ? race : "")));
        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)] // far more than the list reads
    public void Saves_of_any_size_with_a_goal_list_are_listed_and_continue_finds_them(int craft)
    {
        var path = Write("robotic", header: true, craft);
        Assert.Equal(craft > 0, new FileInfo(path).Length > 64 * 1024);
        var slot = Assert.Single(SaveStore.List(_folder));
        Assert.Null(slot.Error);
        Assert.Equal(("Big", "Game", "robotic"), (slot.Name, slot.Header!.GameName, slot.Race));
        Assert.Equal(path, SaveStore.Newest(_folder)!.Path);
        var loaded = SaveGameSerializer.FromJson(File.ReadAllText(path), TestData.Catalog());
        Assert.Equal(new[] { "survey", "land" }, loaded.Header!.GoalsDone);
        Assert.Equal("robotic", loaded.Header.Race);
    }

    [Theory]
    [InlineData("human")]
    [InlineData("technophile")]
    [InlineData("custom_mine")]
    public void Without_it_in_the_header_the_race_is_found_in_the_sector(string race) =>
        Assert.Equal(race, SaveStore.Read(Write(race, header: false)).Race);

    [Fact]
    public void A_picture_goes_with_its_save()
    {
        var path = Write("human", header: true);
        var picture = SaveStore.PictureOf(path);
        Assert.Equal(".jpg", Path.GetExtension(picture));
        SaveStore.WritePicture(path, new byte[] { 1, 2 });
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(picture));
        Assert.Single(SaveStore.List(_folder)); // not a save itself
        SaveStore.Rename(path, "Other");
        Assert.True(File.Exists(picture));
        SaveStore.WritePicture(path, null); // none this time: not another game's
        Assert.False(File.Exists(picture));
        SaveStore.WritePicture(path, new byte[] { 3 });
        SaveStore.Delete(path);
        Assert.False(File.Exists(picture));
        Assert.Empty(Directory.GetFiles(_folder));
    }
}
