using System;
using System.Diagnostics;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;
using Xunit.Abstractions;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>The production planner (#121), against the real simulation.</summary>
public sealed class PlannerTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int Second = FixedStepClock.TicksPerSecond, Minute = 60 * Second;
    private readonly ITestOutputHelper _out;

    public PlannerTests(ITestOutputHelper output) => _out = output;

    private static (SectorState Sector, CraftState Ship, CraftState Yard) Game()
    {
        var game = TestData.NewGame();
        return (game.Sector, game.Sector.GetCraft(game.PlayerCraftId), game.Sector.GetCraft(game.ShipyardId));
    }

    private static CraftState Miner(SectorState sector, CraftState yard, string rock = "Rock-1", string ore = "iron_ore")
    {
        var design = TestData.StarterShip();
        var miner = new CraftState(sector.AllocateCraftId(), "Miner", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(0, -400), 0) { Velocity = yard.Velocity };
        sector.AddCraft(miner);
        miner.Inventory.Add("fuel", 100);
        Assert.True(sector.StartJob(miner.Id, new MiningJob(sector.Bodies.First(b => b.Name == rock).Id, ore, yard.Id, 0.8)).Done);
        return miner;
    }

    private static BodyId Home(SectorState sector) => sector.Stars[0].Id;

    [Fact]
    public void Its_mining_estimate_matches_the_simulation()
    {
        foreach (var rock in new[] { "Rock-1", "Rock-3" })
        {
            var (sector, _, yard) = Game();
            var miner = Miner(sector, yard, rock);
            var estimate = sector.PlanRate("iron_ore", 1_000, Home(sector)).Tree[0].MadePerHour;
            var job = (MiningJob)miner.Job!;
            for (var i = 0; i < 20 * Minute; i++) sector.Step(); // the first trip starts away from the rock
            var start = job.DeliveredKg;
            for (var i = 0; i < 90 * Minute; i++) sector.Step();
            var measured = (job.DeliveredKg - start) / 1.5;
            _out.WriteLine($"{rock}: estimate {estimate / 1000:0.0} t/h, measured {measured / 1000:0.0} t/h");
            Assert.InRange(estimate / measured, 0.7, 1.4);
        }
    }

    [Fact]
    public void Its_refining_and_building_estimates_match_the_simulation()
    {
        var (sector, _, yard) = Game();
        yard.Inventory.Add("iron_ore", 150_000);
        var refined = sector.PlanRates(Home(sector)).RefiningPerHour * 0.7; // smelting: 70% of the ore is metal
        var metal = yard.Inventory["metal"];
        for (var i = 0; i < 5 * Minute; i++) sector.Step();
        var measured = (yard.Inventory["metal"] - metal) * 12.0;
        _out.WriteLine($"refining: estimate {refined / 1000:0.0} t/h of metal, measured {measured / 1000:0.0}");
        Assert.InRange(refined / measured, 0.8, 1.25);

        var (fresh, _, freshYard) = Game();
        var design = TestData.StarterShip();
        var hours = fresh.PlanDesign(design, Home(fresh)).FabricationHours;
        Assert.True(fresh.OrderBuild(freshYard.Id, design, Catalog, "Copy").Accepted);
        var ticks = 0;
        while (freshYard.BuildQueue.Count > 0 && ticks < 4 * 3600 * Second) { fresh.Step(); ticks++; }
        _out.WriteLine($"building: estimate {hours * 60:0.0} min, measured {ticks / (double)Minute:0.0} min");
        Assert.InRange(hours * 3600 * Second / ticks, 0.8, 1.25);
    }

    [Fact]
    public void A_design_in_stock_needs_nothing_and_takes_the_building_time()
    {
        var (sector, _, _) = Game();
        var plan = sector.PlanDesign(TestData.StarterShip(), Home(sector));
        Assert.Empty(plan.Issues);
        Assert.Equal(plan.FabricationHours, plan.Hours!.Value, 6);
        Assert.All(plan.Tree, n => Assert.Equal(1, n.Share));
        Assert.All(plan.Tree, n => Assert.Empty(n.Inputs)); // what's in stock needs no making
    }

    [Fact]
    public void Without_stock_it_finds_what_holds_it_back_with_somewhere_to_go()
    {
        var (sector, _, yard) = Game();
        foreach (var (material, kg) in yard.Inventory.Kg.ToList()) yard.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { [material] = kg });
        foreach (var body in sector.Bodies) body.Surveyed = false;
        // Nothing surveyed here: survey first.
        var plan = sector.PlanDesign(TestData.StarterShip(), Home(sector));
        Assert.Null(plan.Hours);
        Assert.Equal(PlanLimit.Survey, plan.Issues[0].Limit);
        Assert.False(plan.Issues[0].Target.IsNone);
        // Surveyed, nobody mining: miners, at a body with the ore.
        sector.DebugSurveyAll();
        plan = sector.PlanDesign(TestData.StarterShip(), Home(sector));
        var miners = plan.Issues.First(i => i.Limit == PlanLimit.Miners && i.Material == "iron_ore");
        Assert.True(sector.GetBody(miners.Target.Body).SurveyedDeposit("iron_ore") > 0);
        // A miner on it: a time, and quicker with another.
        Miner(sector, yard);
        plan = sector.PlanRate("metal", 200_000, Home(sector));
        Assert.Equal(PlanLimit.Miners, plan.Issues[0].Limit);
        Assert.True(plan.HoursWithFix > plan.Tree[0].Share * 100);
        Assert.Contains("one more miner", plan.Fix);
    }

    [Fact]
    public void Slag_is_a_by_product_and_stock_cuts_the_chain()
    {
        var (sector, _, yard) = Game();
        Assert.True(sector.IsByProduct("slag"));
        Assert.False(sector.IsByProduct("metal"));
        Assert.Null(sector.RecipeFor("slag"));
        Assert.Equal("smelt_iron", sector.RecipeFor("metal")!.Id);
        Assert.Equal("make_electronics", sector.RecipeFor("electronics")!.Id);
        yard.Inventory.TryRemove(new System.Collections.Generic.Dictionary<string, long> { ["metal"] = yard.Inventory["metal"] - 1_000 });
        var plan = sector.PlanDesign(TestData.StarterShip(), Home(sector));
        var metal = plan.Tree.First(n => n.Material == "metal");
        Assert.Equal(metal.Need - 1_000, metal.Inputs.Single().Need / 1000 * 700, 0); // ore for what's still to make
    }

    [Fact]
    public void It_sees_ore_waiting_where_nothing_refines_it_and_power_short()
    {
        var (sector, _, yard) = Game();
        sector.DebugSurveyAll();
        var store = TestData.StarterShipyard().Keep(c => c.PartId is not ("refinery" or "fabricator"));
        var depot = new CraftState(sector.AllocateCraftId(), "Depot", store, store.ComputeStats(Catalog), yard.Position + new Vector2D(3_000, 0), 0);
        sector.AddCraft(depot);
        sector.PutOnRails(depot.Id, yard.RailOrbit! with { RadiusM = yard.RailOrbit!.RadiusM + 3_000 });
        depot.Inventory.Add("iron_ore", 20_000);
        Miner(sector, yard);
        var freight = sector.PlanRate("metal", 50_000, Home(sector)).Issues.Single(i => i.Limit == PlanLimit.Freight);
        Assert.Equal((depot.Id, yard.Id, "iron_ore"), (freight.From, freight.To, freight.Material));
        yard.PowerShort = true;
        Assert.Contains(sector.PlanRate("metal", 50_000, Home(sector)).Issues, i => i.Limit == PlanLimit.Power);
    }

    [Fact]
    public void It_changes_nothing_and_stays_quick_with_hundreds_of_stations()
    {
        var (sector, _, yard) = Game();
        sector.DebugSurveyAll();
        for (var i = 0; i < 300; i++)
        {
            var station = new CraftState(sector.AllocateCraftId(), $"S{i}", TestData.StarterShipyard(), TestData.StarterShipyard().ComputeStats(Catalog), yard.Position + new Vector2D(100 * i, 5_000), 0);
            sector.AddCraft(station);
            station.Inventory.Add("silicate", 1_000);
        }
        for (var i = 0; i < 100; i++) Miner(sector, yard, i % 2 == 0 ? "Rock-1" : "Rock-3");
        var json = Save.SaveGameSerializer.ToJson(sector, sector.PilotCraftId);
        sector.PlanDesign(TestData.StarterShipyard(), null);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) sector.PlanDesign(TestData.StarterShipyard(), null);
        _out.WriteLine($"{watch.Elapsed.TotalMilliseconds / 5:0.0} ms a plan");
        Assert.True(watch.Elapsed.TotalMilliseconds / 5 < 60);
        Assert.Equal(json, Save.SaveGameSerializer.ToJson(sector, sector.PilotCraftId));
    }
}
