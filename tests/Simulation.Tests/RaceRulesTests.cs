using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Races that play differently (#125): civilization traits, favoured goods, designs, and starting designs of the player's own.</summary>
public sealed class RaceRulesTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private static readonly List<string> LawIds = Laws.All.Select(l => l.Id).ToList();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    private static RaceDefinition Race(string id) => Catalog.Races.TryGet(id, out var race) ? race : throw new ArgumentException(id);

    /// <summary>A civilization of a race (as it is, or changed) in a second system, with its trade station and patrol up.</summary>
    private static (SectorState Sector, Faction Faction) Meet(RaceDefinition? race, double baseline = 0)
    {
        var sector = TestData.NewGame().Sector;
        var (star, _) = SectorGenerator.GenerateSystem(sector, 1);
        var faction = sector.MeetCivilization(star, cultureId: "pacifist", baseline: baseline);
        sector.SetFactionRaceForTest(faction, race);
        for (var i = 0; i < 2 * Second; i++) sector.Step();
        return (sector, faction);
    }

    private static RaceDefinition With(params (string Trait, int Percent)[] traits) =>
        Race("human") with { CivTraits = traits.ToDictionary(t => t.Trait, t => t.Percent) };

    private static List<CraftState> Patrol(SectorState sector, Faction faction) => sector.Crafts.Where(c => c.IsPatrol && c.FactionId == faction.Id && !c.IsWreck).ToList();

    // --- The data ---

    [Fact]
    public void The_games_races_have_traits_designs_and_favours_that_work()
    {
        Assert.Empty(Catalog.Races.Problems);
        var robots = Race("robotic");
        Assert.NotNull(robots.Patrol);
        Assert.NotNull(robots.Raider);
        Assert.NotNull(robots.TradeStation);
        Assert.Contains("electronics", robots.Favours);
        Assert.All(Catalog.Races.Races, r => Assert.All(r.CivTraits.Values, v => Assert.InRange(v, -RaceTraits.Limit, RaceTraits.Limit)));
        Assert.Contains(SectorState.Describe(robots, Catalog), l => l.StartsWith("As a civilization: keeps 30% more patrol ships"));
    }

    [Theory]
    [InlineData("\"civTraits\": { \"patrolShips\": 80 }", "-50 to 50")]
    [InlineData("\"civTraits\": { \"wings\": 10 }", "unknown civilization trait")]
    [InlineData("\"favours\": [\"gold\"]", "unknown material")]
    [InlineData("\"favours\": [\"metal\", \"metal\"]", "different goods")]
    public void A_race_with_bad_civilization_data_is_left_out_with_the_reason(string extra, string why)
    {
        var json = "{ \"schemaVersion\": 2, \"id\": \"x\", \"name\": \"X\", " + extra + " }";
        Assert.Contains(why, Assert.Single(RaceCatalog.Load(new[] { new RaceSource("x", json) }, Catalog, LawIds).Problems));
    }

    [Theory]
    [InlineData("patrol.json", "weapons")]
    [InlineData("trade_station.json", "docking port")]
    [InlineData("raider.json", "at most")]
    public void Its_designs_must_work_and_not_be_too_strong(string file, string why)
    {
        var json = "{ \"schemaVersion\": 2, \"id\": \"x\", \"name\": \"X\" }";
        var unarmed = TestData.StarterShip();
        var big = new ShipDesign("big", "Big", Enumerable.Range(0, 32).Select(i => new DesignCell(new GridPos(i % 4 - 2, i / 4), "hull"))
            .Concat(new[] { new DesignCell(new GridPos(0, -1), "cockpit"), new DesignCell(new GridPos(0, 8), "thruster"), new DesignCell(new GridPos(1, -1), "autocannon") }));
        var source = file switch
        {
            "patrol.json" => new RaceSource("x", json, Patrol: unarmed.ToJson()),
            "trade_station.json" => new RaceSource("x", json, TradeStation: SectorState.RaiderDesign.ToJson()),
            _ => new RaceSource("x", json, Raider: big.ToJson()),
        };
        var problem = Assert.Single(RaceCatalog.Load(new[] { source }, Catalog, LawIds).Problems);
        Assert.Contains(file, problem);
        Assert.Contains(why, problem);
    }

    // --- Civilization traits ---

    [Fact]
    public void Patrol_size_and_rebuilding_follow_the_race()
    {
        var (few, slow) = Meet(With((CivilizationTraits.PatrolShips, -50)));
        for (var i = 0; i < Minute; i++) few.Step();
        Assert.Equal(2, Patrol(few, slow).Count);
        var (many, quick) = Meet(With((CivilizationTraits.PatrolShips, 50), (CivilizationTraits.PatrolRebuild, 50)));
        for (var i = 0; i < Minute; i++) many.Step();
        Assert.Equal(5, Patrol(many, quick).Count);
        var lost = Patrol(many, quick)[0];
        many.RemoveCraftForTest(lost.Id);
        var rebuilt = 0;
        for (var i = 0; i < 500 * Second && Patrol(many, quick).Count < 5; i++, rebuilt++) many.Step();
        Assert.InRange(rebuilt, 1, (int)(SectorState.FleetReplaceS / 1.5 * Second) + 2 * Second);
    }

    [Fact]
    public void Raids_are_bigger_or_smaller_by_race()
    {
        Assert.Equal(6, SectorState.RaidShips(Meet(With((CivilizationTraits.RaidSize, 50))).Faction, 4));
        Assert.Equal(2, SectorState.RaidShips(Meet(With((CivilizationTraits.RaidSize, -50))).Faction, 4));
        Assert.Equal(1, SectorState.RaidShips(Meet(With((CivilizationTraits.RaidSize, -50))).Faction, 1));
    }

    [Fact]
    public void Trade_margin_stock_and_favoured_goods_follow_the_race()
    {
        var (plainSector, plain) = Meet(Race("human"));
        var (dearSector, dear) = Meet(With((CivilizationTraits.TradeMargin, 50), (CivilizationTraits.TradeStock, 50)) with { Favours = new() { "metal" } });
        var good = plainSector.SellsOf(plain).First(m => m != "metal");
        Assert.True(dearSector.SellPrice(dear, good) > plainSector.SellPrice(plain, good));
        Assert.Equal((long)(SectorState.TradeStockKg * 1.5), SectorState.StockOf(dear));
        Assert.Equal(SectorState.StockOf(dear), dearSector.TradeStationOf(dear)!.Inventory[dearSector.SellsOf(dear)[0]]);
        Assert.DoesNotContain("metal", dearSector.SellsOf(dear));
        Assert.Equal(plainSector.ValueTo(plain, "metal") * CivilizationTraits.FavourFactor, dearSector.ValueTo(dear, "metal"), 6);
    }

    [Fact]
    public void Its_own_designs_fly_its_patrols_raids_and_station_with_the_ledger_exact()
    {
        var (sector, faction) = Meet(Race("robotic"));
        for (var i = 0; i < Minute; i++) sector.Step();
        Assert.All(Patrol(sector, faction), c => Assert.StartsWith("robotic_patrol", c.Design.Id));
        Assert.Equal("robotic_trade_station", sector.TradeStationOf(faction)!.Design.Id);
        var gate = new CraftState(sector.AllocateCraftId(), "Gate", TestData.StarterShipyard(), TestData.StarterShipyard().ComputeStats(Catalog), sector.TradeStationOf(faction)!.Position + new Vector2D(0, 30_000), 0);
        sector.AddCraft(gate);
        var total = sector.ComputeMassLedger().TotalKg;
        sector.StartRaid(faction, gate, 4);
        var raiders = sector.Crafts.Where(c => c.FactionId == faction.Id && !c.IsPatrol && !c.IsTradeStation).ToList();
        Assert.Equal(4, raiders.Count);
        Assert.Equal(3, raiders.Count(r => r.Design.Id == "robotic_raider"));
        Assert.Single(raiders, r => r.Design.Id == "robotic_raider_missiles" && r.Inventory[SectorState.MissileMaterial] > 0);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
    }

    // --- Saves ---

    [Fact]
    public void Civilizations_met_before_play_as_they_did_and_new_ones_keep_playing_by_race_after_a_load()
    {
        var (sector, faction) = Meet(Race("robotic"));
        Assert.True(faction.PlaysByRace);
        var json = SaveGameSerializer.ToJson(sector, sector.PilotCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.True(loaded.Factions[0].PlaysByRace);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, sector.PilotCraftId));
        var older = SaveGameSerializer.FromJson(System.Text.RegularExpressions.Regex.Replace(json, @",\s*""playsByRace"": true|""playsByRace"": true,\s*", ""), Catalog).Sector;
        var old = older.Factions[0];
        Assert.False(old.PlaysByRace);
        Assert.Equal("robotic", old.RaceId); // still its race in looks and words
        Assert.Equal(SectorState.FleetPatrolShips, SectorState.PatrolSize(old));
        Assert.False(SectorState.Favours(old, "electronics"));
    }

    // --- The player's own ---

    private static RaceDefinition Own(ShipDesign? ship = null, ShipDesign? yard = null, int crew = 3) => new()
    {
        SchemaVersion = RaceCatalog.SchemaVersion, Id = "custom_own", Name = "Own", Crew = crew, Supplies = true, Civilization = true,
        CivTraits = new() { [CivilizationTraits.RaidRate] = -40 }, Favours = new() { "water" }, ShipDesign = ship?.ToJson(), ShipyardDesign = yard?.ToJson(),
    };

    [Fact]
    public void A_race_of_yours_can_start_with_your_designs_kept_in_its_files_and_the_game()
    {
        var ship = new ShipDesign("my_ship", "My ship", TestData.StarterShip().Cells.Where(c => c.PartId != "drill"));
        var yard = new ShipDesign("my_yard", "My yard", TestData.StarterShipyard().Cells.Append(new DesignCell(new GridPos(-2, 1), "storage_silo")));
        var race = RaceCatalog.Check(Own(ship, yard), Catalog, LawIds);
        Assert.Null(RaceFiles.Problem(race));
        Assert.Equal(("my_ship", "my_yard"), (race.Ship!.Id, race.Shipyard!.Id));
        Assert.Contains(RacePoints.Items(race, Catalog), i => i.What.StartsWith("Starting shipyard") && i.Points > 0);
        Assert.Contains(RacePoints.Items(race, Catalog), i => i.What.StartsWith("Starting ship:") && i.Points < 0);
        var again = RaceFiles.FromShareFile(RaceFiles.ToShareFile(race, null), Catalog, LawIds).Race;
        Assert.Equal("my_yard", again.Shipyard!.Id);
        var game = NewGame.Create(3, Catalog, TestData.StarterShip(), TestData.StarterShipyard(), race: race);
        Assert.Equal("my_yard", game.Sector.GetCraft(game.ShipyardId).Design.Id);
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId), Catalog).Sector;
        Assert.Equal("my_yard", loaded.Race.Shipyard!.Id);
        Assert.Equal(-40, loaded.Race.CivTraits[CivilizationTraits.RaidRate]);
    }

    [Fact]
    public void A_shipyard_of_yours_must_work_with_the_crew_it_has()
    {
        var noFabricator = new ShipDesign("x", "X", TestData.StarterShipyard().Cells.Where(c => c.PartId != "fabricator"));
        Assert.Contains("fabricator", RaceFiles.Problem(RaceCatalog.Check(Own(yard: noFabricator), Catalog, LawIds)));
        var noHabitat = new ShipDesign("y", "Y", TestData.StarterShipyard().Cells.Where(c => c.PartId != "habitat"));
        Assert.Contains("hold 0 crew", RaceFiles.Problem(RaceCatalog.Check(Own(yard: noHabitat), Catalog, LawIds)));
        Assert.Contains("can't be read", Assert.Throws<FormatException>(() => RaceCatalog.Check(Own() with { ShipDesign = "{ nope" }, Catalog, LawIds)).Message);
    }

    [Fact]
    public void Civilization_traits_cost_no_points()
    {
        var plain = Own() with { CivTraits = new(), Favours = new() };
        Assert.Equal(RacePoints.Total(plain, Catalog), RacePoints.Total(Own(), Catalog));
    }
}
