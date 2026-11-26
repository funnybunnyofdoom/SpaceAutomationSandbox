using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Megastructures (#126).</summary>
public sealed class MegaTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;

    private static (SectorState Sector, CraftState Ship, CraftState Yard) Game(bool researched = true)
    {
        var game = TestData.NewGame();
        if (researched) game.Sector.DebugUnlockAllResearch();
        return (game.Sector, game.Sector.GetCraft(game.PlayerCraftId), game.Sector.GetCraft(game.ShipyardId));
    }

    private static MegaKind Kind(string id) => Catalog.Megastructures.Find(id)!;

    private static (MegaProject Project, CraftState Hub) Found(SectorState sector, string kind, BodyId at)
    {
        var done = sector.FoundMega(kind, at);
        Assert.True(done.Done, done.Message);
        var project = sector.Megas[^1];
        return (project, sector.GetCraft(project.HubId));
    }

    /// <summary>Puts a stage's materials in the hub and runs until it's built, with power to spare.</summary>
    private static void BuildStage(SectorState sector, MegaProject project, CraftState hub)
    {
        var kind = sector.KindOf(project)!;
        hub.Inventory.Add(kind.Stages[project.Stages].MaterialsKg);
        var stages = project.Stages;
        for (var i = 0; i < (kind.Stages[stages].WorkMinutes + 1) * Minute && project.Stages == stages; i++)
        {
            hub.StoredEnergyKJ = hub.Stats.EnergyCapacityKJ;
            sector.Step();
        }
        Assert.Equal(stages + 1, project.Stages);
    }

    private static string Json(SectorState sector) => SaveGameSerializer.ToJson(sector, sector.PilotCraftId);

    [Fact]
    public void The_games_megastructures_are_usable_and_need_research()
    {
        Assert.Empty(Catalog.Megastructures.Problems);
        Assert.Equal(new[] { "orbital_dock", "orbital_ring", "solar_swarm", "space_elevator" }, Catalog.Megastructures.Kinds.Select(k => k.Id));
        Assert.All(Catalog.Megastructures.Kinds, k => Assert.True(Catalog.Research.TryGet(k.Tech, out _)));
        var (sector, _, _) = Game(researched: false);
        Assert.Contains("Research", sector.FoundBlocker(Kind("orbital_ring"), SectorGenerator.HomePlanet(sector)));
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"id\": \"x\", \"name\": \"X\", \"place\": \"planet\", \"altitudeM\": 1, \"stages\": [] }", "stages")]
    [InlineData("{ \"schemaVersion\": 1, \"id\": \"x\", \"name\": \"X\", \"tech\": \"nope\" }", "tech")]
    [InlineData("{ \"schemaVersion\": 1, \"id\": \"y\" }", "file's name")]
    public void A_bad_kind_is_left_out_with_the_reason(string json, string why) =>
        Assert.Contains(why, Assert.Single(MegaCatalog.Load(new[] { ("x", json) }, Catalog).Problems));

    [Fact]
    public void Each_goes_where_it_belongs()
    {
        var (sector, _, _) = Game();
        var home = SectorGenerator.HomePlanet(sector);
        var star = sector.Stars[0];
        var ice = sector.Bodies.First(b => b.Name == "Rime");
        Assert.Null(sector.FoundBlocker(Kind("orbital_ring"), home));
        Assert.NotNull(sector.FoundBlocker(Kind("orbital_ring"), star));
        Assert.Null(sector.FoundBlocker(Kind("solar_swarm"), star));
        Assert.Null(sector.FoundBlocker(Kind("space_elevator"), home));
        Assert.NotNull(sector.FoundBlocker(Kind("space_elevator"), ice)); // an ice world
        Found(sector, "orbital_ring", home.Id);
        Assert.Contains("already", sector.FoundBlocker(Kind("orbital_ring"), home));
    }

    [Fact]
    public void Stages_take_their_materials_from_freight_into_the_structure_with_the_ledger_exact()
    {
        var (sector, _, _) = Game();
        var (project, hub) = Found(sector, "orbital_ring", SectorGenerator.HomePlanet(sector).Id);
        var kind = Kind("orbital_ring");
        Assert.Equal(kind.Stages[0].MaterialsKg.ToDictionary(m => m.Key, m => m.Value), hub.Requests.ToDictionary(m => m.Key, m => m.Value));
        var total = sector.ComputeMassLedger().TotalKg;
        hub.Inventory.Add(kind.Stages[0].MaterialsKg);
        total += kind.Stages[0].MaterialsKg.Values.Sum();
        for (var i = 0; i < 2 * Second; i++) sector.Step();
        Assert.True(project.Working);
        Assert.Equal(kind.Stages[0].MaterialsKg.Values.Sum(), project.BuiltKg);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
        Assert.Equal(Json(sector), Json(SaveGameSerializer.FromJson(Json(sector), Catalog).Sector)); // part way
        Assert.Equal(0, sector.Share(project));
        project.WorkTicks = (long)(kind.Stages[0].WorkMinutes * Minute) - 10;
        for (var i = 0; i < 20; i++) { hub.StoredEnergyKJ = hub.Stats.EnergyCapacityKJ; sector.Step(); }
        Assert.Equal(1, project.Stages);
        Assert.Equal(0.25, sector.Share(project));
        Assert.Contains(sector.Events, e => e.Text.Contains("First arc done"));
        Assert.Contains("metal", hub.Requests.Keys); // the next stage, and upkeep
    }

    [Fact]
    public void Paused_it_waits_and_keeps_what_is_built()
    {
        var (sector, _, _) = Game();
        var (project, hub) = Found(sector, "orbital_ring", SectorGenerator.HomePlanet(sector).Id);
        BuildStage(sector, project, hub);
        Assert.True(sector.PauseMega(project.Id, true).Done);
        Assert.Equal(new[] { "metal" }, hub.Requests.Keys); // only upkeep
        hub.Inventory.Add(Kind("orbital_ring").Stages[1].MaterialsKg);
        for (var i = 0; i < 5 * Second; i++) sector.Step();
        Assert.False(project.Working);
        Assert.Equal(0.25, sector.Share(project));
    }

    [Fact]
    public void A_ring_stores_and_powers_its_hub_by_its_share()
    {
        var (sector, _, _) = Game();
        var (project, hub) = Found(sector, "orbital_ring", SectorGenerator.HomePlanet(sector).Id);
        var before = hub.CargoCapacityKg;
        sector.DebugBuildMega(project.Id, 2);
        hub.Inventory.Add("metal", 1_000);
        sector.Step();
        Assert.Equal(before + Kind("orbital_ring").Effect.StorageKg / 2, hub.CargoCapacityKg);
        hub.StoredEnergyKJ = 0;
        for (var i = 0; i < Second; i++) sector.Step();
        Assert.True(hub.StoredEnergyKJ > 1_000);
    }

    [Fact]
    public void A_swarm_powers_every_station_in_its_system()
    {
        var (sector, ship, yard) = Game();
        var (project, _) = Found(sector, "solar_swarm", sector.Stars[0].Id);
        sector.DebugBuildMega(project.Id, 5);
        Assert.Equal(Kind("solar_swarm").Effect.PowerKW / 2, sector.MegaPowerKW(yard), 3); // the hub and the yard share it
        Assert.Equal(0, sector.MegaPowerKW(ship)); // ships don't
    }

    [Fact]
    public void An_elevator_lifts_goods_between_landed_stations_and_its_hub()
    {
        var (sector, _, yard) = Game();
        var home = SectorGenerator.HomePlanet(sector);
        var (project, hub) = Found(sector, "space_elevator", home.Id);
        var depot = new CraftState(sector.AllocateCraftId(), "Depot", TestData.StarterShipyard(), TestData.StarterShipyard().ComputeStats(Catalog), sector.GetPosition(home.Id), 0);
        sector.AddCraft(depot);
        sector.RestoreLanding(depot.Id, home.Id, new Vector2D(0, -(home.RadiusM + 20)));
        depot.Inventory.Add("copper_ore", 10_000);
        hub.Inventory.Add("food", 3_000);
        sector.SetRequest(depot.Id, "food", 2_000);
        for (var i = 0; i < 2 * Second; i++) sector.Step();
        Assert.Equal(10_000, depot.Inventory["copper_ore"]); // not built yet: nothing moves
        sector.DebugBuildMega(project.Id, 3);
        var total = sector.ComputeMassLedger().TotalKg;
        for (var i = 0; i < 60 * Second; i++) sector.Step();
        Assert.Equal(0, depot.Inventory["copper_ore"]);
        Assert.True(hub.Inventory["copper_ore"] >= 10_000);
        Assert.True(depot.Inventory["food"] >= 2_000);
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void A_dock_builds_faster()
    {
        var (sector, _, _) = Game();
        var (project, hub) = Found(sector, "orbital_dock", SectorGenerator.HomePlanet(sector).Id);
        Assert.True(hub.IsShipyard);
        var plain = sector.FabricationRate(hub);
        sector.DebugBuildMega(project.Id, 3);
        Assert.Equal(plain + Kind("orbital_dock").Effect.FabricationKgPerS, sector.FabricationRate(hub), 6);
    }

    [Fact]
    public void Without_upkeep_it_stops_and_never_falls_apart_then_works_again()
    {
        var (sector, _, _) = Game();
        var (project, hub) = Found(sector, "orbital_ring", SectorGenerator.HomePlanet(sector).Id);
        sector.DebugBuildMega(project.Id, 4);
        var total = sector.ComputeMassLedger().TotalKg;
        hub.Inventory.TryRemove(new Dictionary<string, long> { ["metal"] = hub.Inventory["metal"] });
        for (var i = 0; i < 61 * Minute && !project.Neglected; i++) sector.Step();
        Assert.True(project.Neglected);
        Assert.Equal(0, sector.Share(project));
        Assert.Equal(4, project.Stages);
        hub.Inventory.Add("metal", 1_000);
        total += 1_000;
        for (var i = 0; i < 61 * Minute && project.Neglected; i++) sector.Step();
        Assert.False(project.Neglected);
        Assert.Equal(1, sector.Share(project));
        Assert.Equal(total, sector.ComputeMassLedger().TotalKg);
    }

    [Fact]
    public void Civilizations_object_in_their_system()
    {
        var (sector, _, _) = Game();
        var (star, planet) = SectorGenerator.GenerateSystem(sector, 1);
        var faction = sector.MeetCivilization(star, cultureId: "pacifist", baseline: 0);
        var (project, hub) = Found(sector, "orbital_ring", planet.Id);
        BuildStage(sector, project, hub);
        Assert.True(faction.Grievance >= Kind("orbital_ring").Grievance - 1);
        Assert.Contains(sector.Events, e => e.Text.Contains("object to"));
    }

    [Fact]
    public void A_finished_one_is_a_milestone_and_saves_continue_exactly()
    {
        var (sector, _, _) = Game();
        var (project, hub) = Found(sector, "space_elevator", SectorGenerator.HomePlanet(sector).Id);
        sector.DebugBuildMega(project.Id, 2);
        BuildStage(sector, project, hub);
        Assert.True(sector.MilestonesReached.ContainsKey("megastructure"));
        var again = SaveGameSerializer.FromJson(Json(sector), Catalog).Sector;
        Assert.Equal(Json(sector), Json(again));
        for (var i = 0; i < Minute; i++) { sector.Step(); again.Step(); }
        Assert.Equal(Json(sector), Json(again));
    }
}
