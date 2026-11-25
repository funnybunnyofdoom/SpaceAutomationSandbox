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
    [InlineData("alien")]
    [InlineData("technophile")]
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
    public void Every_race_is_usable_and_says_what_it_does()
    {
        Assert.Empty(Catalog.Races.Problems);
        Assert.Equal(new[] { "human", "robotic", "alien", "technophile" }, Catalog.Races.Races.Select(r => r.Id));
        Assert.Contains("Researches 25% faster", SectorState.Describe(Race("technophile"), Catalog));
        Assert.Contains("Machines use 10% more power", SectorState.Describe(Race("alien"), Catalog));
        Assert.Contains("The shipyard has a robot bay instead of a habitat", SectorState.Describe(Race("robotic"), Catalog));
        Assert.Contains("Knows Automation", SectorState.Describe(Race("robotic"), Catalog));
        Assert.Contains("5 t less metal at the start", SectorState.Describe(Race("technophile"), Catalog));
        Assert.Contains(SectorState.Describe(Race("robotic"), Catalog), l => l.Contains("Organic crews only") && l.Contains("20 colder") && l.Contains("10 warmer"));
        var yard = Start("technophile") is var t ? t.Sector.GetCraft(t.ShipyardId) : null!;
        Assert.Equal((25_000, 9_000), (yard.Inventory["metal"], yard.Inventory["electronics"]));
        Assert.Equal("Hive yard", Start("alien") is var a ? a.Sector.GetCraft(a.ShipyardId).Name : "");
    }

    // --- Each trait, measured ---

    private static SectorState With(string trait, int percent, out CraftState yard)
    {
        var game = NewGame.Create(NewGame.DefaultSeed, Catalog, TestData.StarterShip(), TestData.StarterShipyard(),
            race: Race("human") with { Traits = new() { [trait] = percent } });
        yard = game.Sector.GetCraft(game.ShipyardId);
        return game.Sector;
    }

    private static void Run(SectorState sector, double seconds)
    {
        for (var i = 0; i < seconds * FixedStepClock.TicksPerSecond; i++) sector.Step();
    }

    private static double Ratio(string trait, Func<SectorState, CraftState, double> measure)
    {
        var plain = measure(With(trait, 0, out var a), a);
        Assert.True(plain > 0);
        return measure(With(trait, 50, out var b), b) / plain;
    }

    [Fact]
    public void Building_speed() => Assert.Equal(1.5, Ratio(RaceTraits.Building, (s, yard) =>
    {
        s.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        Run(s, 3);
        return yard.BuildQueue[0].ProgressKg;
    }), 6);

    [Fact]
    public void Refining_speed() => Assert.Equal(1.5, Ratio(RaceTraits.Refining, (s, yard) =>
    {
        yard.Inventory.Add("iron_ore", 20_000);
        Run(s, 1);
        return yard.RefiningBatch!.ProgressKg;
    }), 6);

    [Fact]
    public void Research_speed() => Assert.Equal(1.5, Ratio(RaceTraits.Research, (s, yard) =>
    {
        var design = new ShipDesign("lab", "Lab", TestData.StarterShipyard().Cells.Select(c => c.PartId == "fabricator" ? c with { PartId = "research_lab" } : c));
        yard.Design = design;
        yard.Stats = design.ComputeStats(Catalog);
        Run(s, 30);
        return s.ResearchPoints;
    }), 6);

    [Fact]
    public void Mining_speed_and_machines_power()
    {
        double Mined(int percent, string trait, out double usedKJ)
        {
            var sector = With(trait, percent, out _);
            var ship = sector.GetCraft(sector.PilotCraftId);
            var rock = TestData.HomeRocks(sector).First(b => b.DepositsKg.ContainsKey("iron_ore"));
            var site = rock.Sites.First(x => x.DepositsKg.ContainsKey("iron_ore"));
            var offset = new Vector2D(Math.Cos(site.CentreRad), Math.Sin(site.CentreRad)) * (rock.RadiusM + ship.CollisionRadiusM + 1);
            ship.Position = ship.PreviousPosition = sector.GetPosition(rock.Id) + offset;
            ship.HeadingRad = ship.PreviousHeadingRad = FlightModel.WrapAngle(site.CentreRad + Math.PI / 2);
            ship.Velocity = sector.GetVelocityAt(rock.Id, sector.TimeS);
            sector.RestoreLanding(ship.Id, rock.Id, offset);
            Assert.True(sector.TryAnchor(ship.Id).Done);
            ship.StoredEnergyKJ = 30_000;
            var solar = ship.Stats.PowerGenerationKW;
            ship.Stats = ship.Stats with { PowerGenerationKW = 0 };
            Run(sector, 20);
            ship.Stats = ship.Stats with { PowerGenerationKW = solar };
            usedKJ = 30_000 - ship.StoredEnergyKJ;
            return ship.CargoKg - NewGame.PlayerStartingFuelKg;
        }
        var plain = Mined(0, RaceTraits.Mining, out var power);
        Assert.True(plain > 1_000 && power > 100);
        Assert.Equal(1.5, Mined(50, RaceTraits.Mining, out var same) / plain, 2);
        Assert.Equal(power, same, 3);
        Assert.Equal(plain, Mined(-50, RaceTraits.Power, out var half), 3);
        Assert.Equal(0.5, half / power, 3);
    }

    [Fact]
    public void Crew_supplies_growth_and_recruiting()
    {
        Assert.Equal(1.5, Ratio(RaceTraits.CrewSupplies, (s, yard) =>
        {
            var before = yard.Inventory["food"] - yard.FoodRemainderKg;
            Run(s, 1_200);
            return before - yard.Inventory["food"] + yard.FoodRemainderKg;
        }), 1); // the crew grow meanwhile, the same in both
        Assert.Equal(1.5, Ratio(RaceTraits.CrewGrowth, (s, yard) => { Run(s, 60); return yard.CrewGrowthProgressS; }), 6);
        var sector = With(RaceTraits.RecruitCost, -30, out var home);
        var food = home.Inventory["food"];
        var water = home.Inventory["water"];
        Assert.True(sector.TryRecruit(home.Id, 1).Done);
        Assert.Equal((35, 70), (food - home.Inventory["food"], water - home.Inventory["water"]));
        Assert.Equal((35, 70), sector.RecruitCostKg);
    }

    [Fact]
    public void Traits_leave_others_craft_alone()
    {
        var sector = With(RaceTraits.Building, 50, out var yard);
        yard.FactionId = 4;
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        Run(sector, 3);
        var plain = With(RaceTraits.Building, 0, out var other);
        plain.OrderBuild(other.Id, TestData.StarterShip(), Catalog);
        Run(plain, 3);
        Assert.Equal(other.BuildQueue[0].ProgressKg, yard.BuildQueue[0].ProgressKg, 6);
    }

    // --- Files ---

    [Theory]
    [InlineData(null, "no race.json")]
    [InlineData("{ nope", "invalid")]
    [InlineData("""{ "schemaVersion": 1, "id": "x", "name": "X" }""", "schemaVersion 1")]
    [InlineData("""{ "schemaVersion": 2, "id": "y", "name": "X" }""", "folder")]
    [InlineData("""{ "schemaVersion": 2, "id": "x" }""", "no name")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "crew": -1 }""", "crew")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "accent": "red" }""", "#rrggbb")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "traits": { "luck": 10 } }""", "luck")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "traits": { "mining": 51 } }""", "51%")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "tech": ["nope"] }""", "tech 'nope'")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "stockKg": { "nope": 1 } }""", "material 'nope'")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "shipyardParts": { "habitat": "nope" } }""", "part 'nope'")]
    [InlineData("""{ "schemaVersion": 2, "id": "x", "name": "X", "blockedLaws": { "nope": "" } }""", "law 'nope'")]
    public void A_bad_race_is_left_out_with_the_reason(string? json, string reason)
    {
        var races = RaceCatalog.Load(new[] { new RaceSource("x", json) }, Catalog, Laws.All.Select(l => l.Id).ToList());
        Assert.Contains(reason, Assert.Single(races.Problems));
        Assert.Equal("human", Assert.Single(races.Races).Id); // the game still plays
    }

    [Fact]
    public void A_race_can_bring_its_own_ship_and_shipyard()
    {
        var laws = Laws.All.Select(l => l.Id).ToList();
        const string json = """{ "schemaVersion": 2, "id": "x", "name": "X", "crew": 2, "supplies": true, "shipName": "Seed" }""";
        var ship = new ShipDesign("seed", "Seed pod", TestData.StarterShip().Cells.Append(new DesignCell(new GridPos(0, 9), "hull")).Where(c => c.Pos.Y < 9));
        var race = RaceCatalog.Read(new RaceSource("x", json, ship.ToJson()), Catalog, laws);
        var game = NewGame.Create(1, Catalog, TestData.StarterShip(), TestData.StarterShipyard(), race: race);
        Assert.Equal(("Seed", "seed"), (game.Sector.GetCraft(game.PlayerCraftId).Name, game.Sector.GetCraft(game.PlayerCraftId).Design.Id));
        var bad = new ShipDesign("bad", "Bad", new[] { new DesignCell(new GridPos(0, 0), "hull") });
        Assert.Contains("ship.json", Assert.Throws<FormatException>(() => RaceCatalog.Read(new RaceSource("x", json, bad.ToJson()), Catalog, laws)).Message);
    }

    [Fact]
    public void A_save_whose_race_is_missing_says_so()
    {
        var game = Start("robotic");
        var json = SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId).Replace("\"race\": \"robotic\"", "\"race\": \"slime\"");
        Assert.Contains("data/races/slime", Assert.Throws<FormatException>(() => SaveGameSerializer.FromJson(json, Catalog)).Message);
    }
}
