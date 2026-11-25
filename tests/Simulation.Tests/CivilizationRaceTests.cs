using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Civilizations' races (#118).</summary>
public sealed class CivilizationRaceTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static RaceDefinition Race(string id) => Catalog.Races.TryGet(id, out var race) ? race : throw new ArgumentException(id);

    private static (SectorState Sector, CraftId Player) Start(ulong seed, string race = "human", PartCatalog? catalog = null)
    {
        var game = NewGame.Create(seed, catalog ?? Catalog, TestData.StarterShip(), TestData.StarterShipyard(), race: Race(race));
        return (game.Sector, game.PlayerCraftId);
    }

    private static Faction Meet(ulong seed, string? culture = null, double? baseline = null, string player = "human") =>
        Start(seed, player).Sector is var s ? s.MeetCivilization(s.Stars[0], cultureId: culture, baseline: baseline) : null!;

    [Fact]
    public void The_data_names_real_cultures_and_every_culture_has_several_races()
    {
        foreach (var race in Catalog.Races.Races)
        {
            foreach (var culture in race.Cultures) Assert.Contains(Culture.All, c => c.Id == culture);
            foreach (var other in race.Views.Keys) Assert.Contains(Catalog.Races.Races, r => r.Id == other);
        }
        foreach (var culture in Culture.All) Assert.True(Catalog.Races.Races.Count(r => r.Civilization && (r.Cultures.Count == 0 || r.Cultures.Contains(culture.Id))) >= 2, culture.Id);
    }

    [Fact]
    public void Its_race_is_the_same_for_a_seed_fits_its_culture_and_varies()
    {
        var seen = Enumerable.Range(1, 60).Select(i => Meet((ulong)i)).ToList();
        Assert.Equal(seen.Select(f => (f.RaceId, f.Name)), Enumerable.Range(1, 60).Select(i => Meet((ulong)i)).Select(f => (f.RaceId, f.Name)));
        Assert.All(seen, f => Assert.True(f.Race!.Cultures.Count == 0 || f.Race.Cultures.Contains(f.Culture.Id)));
        Assert.True(seen.Select(f => f.RaceId).Distinct().Count() >= 4);
        Assert.All(Enumerable.Range(1, 60), i => Assert.NotEqual("robotic", Meet((ulong)i, culture: "purist").RaceId));
        Assert.All(seen.Where(f => f.RaceId == "alien"), f => Assert.Contains(f.Name, Race("alien").CivilizationNames));
        Assert.Contains(Start(3).Sector is var s ? s.MeetCivilization(s.Stars[0]).Race!.Name : "", s.Events[^1].Text);
    }

    [Fact]
    public void Names_arent_used_twice()
    {
        var sector = Start(1).Sector;
        for (var i = 0; i < 25; i++) sector.MeetCivilization(sector.Stars[0]);
        Assert.Equal(25, sector.Factions.Select(f => f.Name).Distinct().Count());
    }

    [Fact]
    public void Its_own_race_it_sees_warmer_and_some_colder_for_good()
    {
        foreach (var player in new[] { "human", "robotic", "alien", "technophile" })
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var f = Meet(seed, player: player);
                var expected = f.RaceId == player ? 10 : (f.RaceId, player) is ("robotic", "alien") or ("alien", "robotic") ? -10 : 0;
                Assert.Equal(expected, f.RaceView);
                Assert.Equal(f.Baseline + expected, f.Attitude);
                Assert.Equal(Math.Clamp(f.Baseline + expected, -100, 100), f.TargetAttitude(Array.Empty<string>()));
            }
        Assert.Equal(0, Meet(1, baseline: 12).RaceView); // a baseline given is exactly that
    }

    [Fact]
    public void Race_changes_only_looks_and_attitude()
    {
        // With and without races the same culture, goods and prices come of the same seed.
        var plain = PartCatalog.FromJson(TestData.Read("data/parts/parts.json"), TestData.Materials(), TestData.Recipes()).WithResearch(TestData.Research());
        for (ulong seed = 1; seed < 10; seed++)
        {
            var (a, _) = Start(seed);
            var (b, _) = Start(seed, catalog: plain);
            var (f, g) = (a.MeetCivilization(a.Stars[0]), b.MeetCivilization(b.Stars[0]));
            Assert.Equal(("", (RaceDefinition?)null), (g.RaceId, g.Race));
            Assert.Equal((f.Culture.Id, f.Baseline), (g.Culture.Id, g.Baseline));
            Assert.Equal(a.SellsOf(f), b.SellsOf(g));
            Assert.Equal(a.ValueTo(f, "metal"), b.ValueTo(g, "metal"));
        }
    }

    // --- Saves ---

    [Fact]
    public void Saves_keep_it_and_continue_exactly()
    {
        var (sector, player) = Start(8, "alien");
        for (var i = 0; i < 3; i++) sector.MeetCivilization(sector.Stars[0]);
        var json = SaveGameSerializer.ToJson(sector, player);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(sector.Factions.Select(f => (f.RaceId, f.RaceView, f.Name)), loaded.Factions.Select(f => (f.RaceId, f.RaceView, f.Name)));
        Assert.All(loaded.Factions, f => Assert.NotNull(f.Race));
        for (var i = 0; i < 200; i++) { sector.Step(); loaded.Step(); }
        Assert.Equal(SaveGameSerializer.ToJson(sector, player), SaveGameSerializer.ToJson(loaded, player));
    }

    [Fact]
    public void Civilizations_from_older_saves_get_a_fitting_race_and_see_you_as_before()
    {
        var (sector, player) = Start(8);
        var f = sector.MeetCivilization(sector.Stars[0], baseline: 7);
        var json = SaveGameSerializer.ToJson(sector, player).Replace($"\"schemaVersion\": {SaveGameSerializer.SchemaVersion}", "\"schemaVersion\": 35").Replace($"\"race\": \"{f.RaceId}\",", "").Replace($"\"race\": \"{f.RaceId}\"", "\"spare\": 0");
        var old = SaveGameSerializer.FromJson(json, Catalog).Sector;
        var g = Assert.Single(old.Factions);
        Assert.Equal((f.RaceId, 0.0, 7.0, f.Name), (g.RaceId, g.RaceView, g.Attitude, g.Name)); // the same choice, the same attitude and name
        var again = SaveGameSerializer.ToJson(old, player);
        Assert.Equal(again, SaveGameSerializer.ToJson(SaveGameSerializer.FromJson(again, Catalog).Sector, player));
    }

    [Fact]
    public void A_race_thats_gone_doesnt_stop_a_save_and_comes_back_with_it()
    {
        var (sector, player) = Start(8);
        var f = sector.MeetCivilization(sector.Stars[0]);
        var json = SaveGameSerializer.ToJson(sector, player).Replace($"\"race\": \"{f.RaceId}\"", "\"race\": \"lost\"");
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.Equal(("lost", (RaceDefinition?)null, f.RaceView), (loaded.Factions[0].RaceId, loaded.Factions[0].Race, loaded.Factions[0].RaceView));
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, player));
    }

    // --- The player's own races ---

    private static PartCatalog WithOwn(bool allowed)
    {
        var catalog = TestData.Catalog();
        var own = new RaceDefinition { SchemaVersion = RaceCatalog.SchemaVersion, Id = "custom_mine", Name = "Mine", Crew = 3, Supplies = true, Civilization = allowed };
        return catalog.WithRaces(RaceCatalog.Load(new[] { new RaceSource("custom_mine", RaceFiles.ToJson(own), Custom: true) }, catalog, Laws.All.Select(l => l.Id).ToList()));
    }

    [Fact]
    public void The_players_own_races_only_if_allowed_and_the_save_keeps_a_copy()
    {
        Assert.Null(Start(1, catalog: WithOwn(false)) is var (s0, _) ? s0.MeetCivilization(s0.Stars[0]).Race : null); // it's the only one there besides a Human that isn't one
        var (sector, player) = Start(1, catalog: WithOwn(true));
        sector.MeetCivilization(sector.Stars[0]);
        Assert.Equal("custom_mine", sector.Factions[0].RaceId);
        var json = SaveGameSerializer.ToJson(sector, player);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector; // where that race isn't installed
        Assert.Equal("Mine", loaded.Factions[0].Race!.Name);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, player));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "views": { "human": 51 } }""", "views")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "civilizationNames": [""] }""", "empty")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "cultures": [null] }""", "empty")]
    public void Bad_data_is_refused(string json, string reason) =>
        Assert.Contains(reason, Assert.Throws<FormatException>(() => RaceCatalog.Read(new RaceSource("x", json), Catalog, Laws.All.Select(l => l.Id).ToList())).Message);
}
