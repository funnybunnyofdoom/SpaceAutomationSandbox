using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>The player's own races (#117): points, rules, files, and games that keep their copy.</summary>
public sealed class RaceCreatorTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private static readonly List<string> LawIds = Laws.All.Select(l => l.Id).ToList();

    private static RaceDefinition Own => new()
    {
        SchemaVersion = RaceCatalog.SchemaVersion, Id = "custom_tinkers", Name = "Tinkers ✦", Description = "They \"tinker\".", Accent = "#112233", Crew = 3, Supplies = true, ShipName = "Spanner",
        Traits = new() { ["building"] = 20, ["mining"] = -10 }, StockKg = new() { ["electronics"] = 2_000 }, Tech = new() { "photovoltaics" },
        Leanings = new() { [Laws.ArmedGates] = new RaceLeaning { Wants = -10 } }, BlockedLaws = new() { [Laws.ProtectLife] = "No." }, Picture = "human",
    };

    private static NewGameResult Start(RaceDefinition race) => NewGame.Create(3, Catalog, TestData.StarterShip(), TestData.StarterShipyard(), race: race);

    // --- Points ---

    [Fact]
    public void Human_costs_nothing_and_every_built_in_race_is_within_the_points()
    {
        Assert.Empty(RacePoints.Items(Catalog.Races.Default, Catalog));
        foreach (var race in Catalog.Races.Races) Assert.False(RacePoints.Unbalanced(race, Catalog), $"{race.Id}: {RacePoints.Total(race, Catalog)}");
    }

    [Fact]
    public void Good_costs_bad_gives_back_and_too_much_is_only_unbalanced()
    {
        // building +4, mining −2, crew −1, electronics +1.5, photovoltaics 40/40 = 1, views −1, a law −1
        Assert.Equal(1.5, RacePoints.Total(Own, Catalog));
        foreach (var trait in RaceTraits.All)
        {
            Assert.True(RacePoints.Trait(trait.Id, 50) != 0, trait.Id);
            Assert.Equal(0, RacePoints.Trait(trait.Id, 35) + RacePoints.Trait(trait.Id, -35), 9);
        }
        Assert.True(RacePoints.Trait(RaceTraits.Power, -10) > 0); // less power is better
        var strong = Own with { Traits = RaceTraits.All.ToDictionary(t => t.Id, t => RacePoints.Trait(t.Id, 1) > 0 ? 50 : -50) };
        Assert.True(RacePoints.Unbalanced(strong, Catalog));
        Assert.Null(RaceFiles.Problem(strong));
        Start(strong);
    }

    // --- Rules ---

    [Fact]
    public void A_race_whose_shipyard_couldnt_work_is_refused()
    {
        Assert.Null(RaceFiles.Problem(Own));
        Assert.Contains("name", RaceFiles.Problem(Own with { Name = " " }));
        Assert.Contains("2 crew", RaceFiles.Problem(Own with { Crew = 1 }));
        Assert.Contains("holds 6", RaceFiles.Problem(Own with { Crew = 7 }));
        Assert.Contains("food", RaceFiles.Problem(Own with { Supplies = false }));
        var robots = Own with { ShipyardParts = new() { ["habitat"] = "robot_bay" } };
        Assert.Contains("nowhere", RaceFiles.Problem(robots));
        Assert.Null(RaceFiles.Problem(robots with { Crew = 0, Supplies = false }));
        Assert.Contains("habitat", RaceFiles.Problem(Own with { ShipyardParts = new() { ["hull"] = "cargo" } }));
        Assert.Contains("20 t", RaceFiles.Problem(Own with { StockKg = new() { ["metal"] = 21_000 } }));
        Assert.Contains("views", RaceFiles.Problem(Own with { Leanings = new() { [Laws.ArmedGates] = new RaceLeaning { Opposes = double.NaN } } }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 1)]
    public void The_least_and_most_crew_can_run_the_shipyard(int crew, int _)
    {
        var race = crew == 0 ? Own with { Crew = 2 } : Own with { Crew = crew };
        var game = Start(race);
        for (var i = 0; i < 100; i++) game.Sector.Step();
        Assert.Null(game.Sector.GetCraft(game.ShipyardId).CrewShortReason);
        Assert.True(game.Sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), Catalog).Accepted);
    }

    // --- Files ---

    [Fact]
    public void Its_file_reads_back_the_same_and_ids_are_kept_apart()
    {
        var json = RaceFiles.ToJson(Own);
        var races = RaceCatalog.Load(new[] { new RaceSource("custom_tinkers", json, Custom: true) }, Catalog, LawIds);
        Assert.Empty(races.Problems);
        Assert.True(races.TryGet("custom_tinkers", out var race));
        Assert.Equal(json, RaceFiles.ToJson(race));
        Assert.True(race.IsCustom);
        Assert.Contains("ids start with", Assert.Single(RaceCatalog.Load(new[] { new RaceSource("custom_tinkers", json) }, Catalog, LawIds).Problems)); // not among the game's
        Assert.Contains("starts with", Assert.Single(RaceCatalog.Load(new[] { new RaceSource("human", RaceFiles.ToJson(Own with { Id = "human" }), Custom: true) }, Catalog, LawIds).Problems)); // can't replace one
        Assert.Equal("custom_tinkers_2", RaceFiles.NewId("Tinkers!", id => id == "custom_tinkers"));
        Assert.Equal("custom_design", RaceFiles.NewId("✦✦", _ => false));
    }

    [Fact]
    public void Shared_files_carry_the_picture_and_read_back()
    {
        var picture = new byte[] { 1, 2, 3, 250 };
        var (race, bytes) = RaceFiles.FromShareFile(RaceFiles.ToShareFile(Own, picture), Catalog, LawIds);
        Assert.Equal(picture, bytes);
        Assert.Equal(RaceFiles.ToJson(Own), RaceFiles.ToJson(race));
        Assert.Null(RaceFiles.FromShareFile(RaceFiles.ToShareFile(Own, null), Catalog, LawIds).Picture);
    }

    [Theory]
    [InlineData("", "isn't a race file")]
    [InlineData("null", "isn't a race file")]
    [InlineData("[1]", "isn't a race file")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module" }""", "isn't a race file")]
    [InlineData("""{ "schemaVersion": 1, "kind": "race" }""", "isn't a race file")]
    [InlineData("""{ "schemaVersion": 9, "kind": "race", "race": {} }""", "version 9")]
    [InlineData("""{ "schemaVersion": 1, "kind": "race", "race": { "schemaVersion": 2, "name": null } }""", "no name")]
    [InlineData("""{ "schemaVersion": 1, "kind": "race", "race": { "schemaVersion": 2, "name": "X", "crew": "many" } }""", "can't be read")]
    [InlineData("""{ "schemaVersion": 1, "kind": "race", "race": { "schemaVersion": 2, "name": "X", "traits": null, "crew": 0 } }""", "2 crew")]
    [InlineData("""{ "schemaVersion": 1, "kind": "race", "race": { "schemaVersion": 2, "name": "X", "crew": 2, "supplies": true, "leanings": { "armed_gates": null } } }""", "empty")]
    [InlineData("""{ "schemaVersion": 1, "kind": "race", "race": { "schemaVersion": 2, "name": "X", "crew": 2, "supplies": true, "traits": { "mining": 99 } } }""", "99%")]
    [InlineData("""{ "schemaVersion": 1, "kind": "race", "picture": "!!", "race": { "schemaVersion": 2, "name": "X", "crew": 2, "supplies": true } }""", "damaged")]
    public void Bad_shared_files_are_refused_with_the_reason(string json, string reason) =>
        Assert.Contains(reason, Assert.Throws<FormatException>(() => RaceFiles.FromShareFile(json, Catalog, LawIds)).Message);

    [Fact]
    public void A_shared_id_cant_lead_out_of_the_folder()
    {
        var (race, _) = RaceFiles.FromShareFile(RaceFiles.ToShareFile(Own with { Id = "../../evil", Name = "../../Evil" }, null), Catalog, LawIds);
        Assert.Equal("custom_evil", race.Id);
    }

    [Fact]
    public void Pictures_keep_their_middle_at_four_to_five()
    {
        Assert.Equal((0, 0, 800, 1000), RaceFiles.Crop(800, 1000));
        Assert.Equal((600, 0, 800, 1000), RaceFiles.Crop(2000, 1000));
        Assert.Equal((0, 500, 800, 1000), RaceFiles.Crop(800, 2000));
        Assert.Null(RaceFiles.Crop(63, 500));
    }

    // --- Games ---

    [Fact]
    public void A_game_keeps_its_own_copy_and_continues_exactly()
    {
        var game = Start(Own);
        Assert.Equal("Spanner", game.Sector.GetCraft(game.PlayerCraftId).Name);
        Assert.Contains("won't", game.Sector.SetLaw(Laws.ProtectLife, true).Message.Replace("No.", "won't"));
        game.Sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), Catalog);
        for (var i = 0; i < 60; i++) game.Sector.Step();
        var json = SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId);
        Assert.DoesNotContain(Catalog.Races.Races, r => r.Id == Own.Id); // not installed here at all
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        Assert.Equal(RaceFiles.ToJson(Own), RaceFiles.ToJson(loaded.Race));
        for (var i = 0; i < 600; i++) { game.Sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        // A save that's been tampered with: refused, with the reason.
        Assert.Contains("id of another", Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json.Replace("custom_tinkers", "human"), Catalog)).Message);
        Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json.Replace("\"building\": 20", "\"building\": 2000"), Catalog));
    }
}
