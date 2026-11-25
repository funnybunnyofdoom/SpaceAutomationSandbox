using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Races (#97).</summary>
public sealed class RaceTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static RaceDefinition Race(string id) => Catalog.Races.TryGet(id, out var race) ? race : throw new ArgumentException(id);

    private static NewGameResult Start(string race) =>
        NewGame.Create(NewGame.DefaultSeed, Catalog, TestData.StarterShip(), TestData.StarterShipyard(), race: Race(race));

    [Fact]
    public void Human_is_the_default_and_starts_as_it_did_without_races()
    {
        var plain = PartCatalog.FromJson(TestData.Read("data/parts/parts.json"), TestData.Materials(), TestData.Recipes()).WithResearch(TestData.Research());
        var before = NewGame.Create(5, plain, TestData.StarterShip(), TestData.StarterShipyard());
        var now = NewGame.Create(5, Catalog, TestData.StarterShip(), TestData.StarterShipyard());
        var human = NewGame.Create(5, Catalog, TestData.StarterShip(), TestData.StarterShipyard(), race: Race("human"));
        var json = SaveGameSerializer.ToJson(before.Sector, before.PlayerCraftId);
        Assert.Equal(json, SaveGameSerializer.ToJson(now.Sector, now.PlayerCraftId));
        Assert.Equal(json, SaveGameSerializer.ToJson(human.Sector, human.PlayerCraftId));
        Assert.DoesNotContain("\"race\"", json);
        Assert.Equal("human", SaveGameSerializer.FromJson(json, Catalog).Sector.Race.Id);
    }

    [Fact]
    public void Robotic_starts_with_working_robots_and_nothing_to_eat()
    {
        var game = Start("robotic");
        var yard = game.Sector.GetCraft(game.ShipyardId);
        Assert.Equal((0, 0), (yard.Crew, yard.Stats.CrewCapacity));
        Assert.Equal(4, yard.Stats.RobotCount);
        Assert.Equal(0, yard.Inventory["food"] + yard.Inventory["water"]);
        Assert.True(game.Sector.IsPartUnlocked("robot_bay"));
        Assert.False(Start("human").Sector.IsPartUnlocked("robot_bay"));
        for (var i = 0; i < 2 * FixedStepClock.TicksPerSecond; i++) game.Sector.Step();
        Assert.Null(yard.CrewShortReason);
        Assert.Equal(4, yard.RobotsWorking);
    }

    [Theory]
    [InlineData("human")]
    [InlineData("robotic")]
    public void Each_start_can_build_a_first_ship_and_saves_continue_exactly(string race)
    {
        var game = Start(race);
        var sector = game.Sector;
        var mass = sector.ComputeMassLedger().TotalKg;
        Assert.True(sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), Catalog).Accepted);
        var ships = sector.Crafts.Count;
        for (var i = 0; i < 100; i++) sector.Step();
        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(race, loaded.Race.Id);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        for (var i = 0; i < 20 * 60 * FixedStepClock.TicksPerSecond && sector.Crafts.Count == ships; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(ships + 1, sector.Crafts.Count);
        Assert.Equal(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        Assert.Equal(mass, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Robots_keep_the_yard_running_through_a_whole_orbit()
    {
        var game = Start("robotic");
        var yard = game.Sector.GetCraft(game.ShipyardId);
        for (var i = 0; i < 40 * 60 * FixedStepClock.TicksPerSecond; i++)
        {
            game.Sector.Step();
            if (i > 0 && yard.CrewShortReason is { } why) Assert.Fail($"after {i / FixedStepClock.TicksPerSecond} s: {why}");
        }
    }

    [Fact]
    public void Civilizations_lean_and_the_law_is_blocked_only_for_robots()
    {
        double Baseline(string race, string culture)
        {
            var sector = Start(race).Sector;
            return sector.MeetCivilization(sector.Stars[0], cultureId: culture, baseline: 5).Attitude;
        }
        Assert.Equal(5, Baseline("human", "purist"));
        Assert.Equal(-15, Baseline("robotic", "purist"));
        Assert.Equal(15, Baseline("robotic", "militarist"));
        Assert.Equal(5, Baseline("robotic", "pacifist"));

        var robots = Start("robotic").Sector;
        Assert.Contains("robots", robots.SetLaw(Laws.OrganicCrews, true).Message);
        Assert.False(robots.LawActive(Laws.OrganicCrews));
        Assert.True(robots.SetLaw(Laws.ArmedGates, true).Done);
        Assert.True(Start("human").Sector.SetLaw(Laws.OrganicCrews, true).Done);
    }

    [Fact]
    public void Robots_can_house_and_recruit_people()
    {
        var game = Start("robotic");
        var design = TestData.StarterShipyard(); // with a habitat
        var yard = new CraftState(game.Sector.AllocateCraftId(), "Colony", design, design.ComputeStats(Catalog), new Vector2D(5e8, 5e8), 0);
        game.Sector.AddCraft(yard);
        yard.Inventory.Add("food", 100);
        yard.Inventory.Add("water", 200);
        Assert.True(game.Sector.TryRecruit(yard.Id, 2).Done);
        Assert.Equal(2, yard.Crew);
    }

    [Fact]
    public void The_data_names_real_laws_and_bad_files_are_refused()
    {
        foreach (var race in Catalog.Races.Races)
            foreach (var law in race.BlockedLaws.Keys.Concat(race.Leanings.Keys)) Assert.Contains(Laws.All, l => l.Id == law);
        foreach (var bad in new[]
        {
            """{ "schemaVersion": 2, "races": [ { "id": "human", "name": "H" } ] }""",
            """{ "schemaVersion": 1, "races": [] }""",
            """{ "schemaVersion": 1, "races": [ { "id": "robotic", "name": "R" } ] }""",
            """{ "schemaVersion": 1, "races": [ { "id": "human", "name": "H" }, { "id": "human", "name": "H" } ] }""",
            """{ "schemaVersion": 1, "races": [ { "id": "human", "name": "H", "tech": ["nope"] } ] }""",
            """{ "schemaVersion": 1, "races": [ { "id": "human", "name": "H", "shipyardParts": { "habitat": "nope" } } ] }""",
        })
            Assert.Throws<FormatException>(() => RaceCatalog.FromJson(bad, Catalog));
        var game = Start("robotic");
        var json = SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId).Replace("\"race\": \"robotic\"", "\"race\": \"slime\"");
        Assert.Contains("slime", Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json, Catalog)).Message);
    }
}
