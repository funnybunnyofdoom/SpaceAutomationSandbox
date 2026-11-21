using System;
using System.Diagnostics;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;
using Xunit.Abstractions;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Phase 4: refining, survey, freight, and the "autonomous network" gate (docs/04). Headless.</summary>
public class ProductionNetworkTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private readonly ITestOutputHelper _log;

    public ProductionNetworkTests(ITestOutputHelper log) => _log = log;

    private static long TotalMass(SectorState sector) =>
        sector.Bodies.Sum(b => b.DepositsKg.Values.Sum())
        + sector.Crafts.Sum(c => c.Inventory.TotalKg + c.BuildQueue.Sum(o => o.MaterialsKg.Values.Sum())
                                 + (c.RefiningBatch?.InputsKg.Values.Sum() ?? 0));

    private static void Run(SectorState sector, int ticks, Action? everyTick = null)
    {
        for (var i = 0; i < ticks; i++) { sector.Step(); everyTick?.Invoke(); }
    }

    private static bool RunUntil(SectorState sector, Func<bool> done, int maxTicks, Action? everyTick = null)
    {
        for (var i = 0; i < maxTicks && !done(); i++) { sector.Step(); everyTick?.Invoke(); }
        return done();
    }

    private static CelestialBodyState Rock(SectorState sector, string name) => sector.Bodies.Single(b => b.Name == name);

    // --- Refining -----------------------------------------------------------------------------

    [Fact]
    public void Recipes_ConserveMass_AndBadRecipesAreRejected()
    {
        foreach (var recipe in TestData.Recipes().Recipes)
            Assert.Equal(recipe.InputsKg!.Values.Sum(), recipe.OutputsKg!.Values.Sum());

        var ex = Assert.Throws<FormatException>(() => RecipeCatalog.FromJson(
            """{ "schemaVersion": 1, "recipes": [ { "id": "free_metal", "inputsKg": { "iron_ore": 10 }, "outputsKg": { "metal": 12 } } ] }""",
            TestData.Materials()));
        Assert.Contains("slag", ex.Message);
    }

    [Fact]
    public void Yard_RefinesIronOre_IntoMetalAndSlag_AtTheRefineryRate()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var metal = yard.Inventory["metal"];
        yard.Inventory.Add("iron_ore", 2_000);
        var mass = TotalMass(game.Sector);

        Run(game.Sector, (int)Math.Ceiling(2_000 / yard.Stats.RefiningRateKgPerS + 1) * FixedStepClock.TicksPerSecond,
            () => Assert.Equal(mass, TotalMass(game.Sector)));

        Assert.Equal(0, yard.Inventory["iron_ore"]);
        Assert.Equal(metal + 1_400, yard.Inventory["metal"]);
        Assert.Equal(600, yard.Inventory["slag"]);
        Assert.Null(yard.RefiningBatch);
    }

    [Fact]
    public void RefiningBatch_SurvivesSaveLoad_Exactly()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        yard.Inventory.Add("silicate", 1_000);
        Run(game.Sector, 2 * FixedStepClock.TicksPerSecond); // mid-batch (1 t at 200 kg/s takes 5 s)
        Assert.NotNull(yard.RefiningBatch);

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId), Catalog).Sector;
        Run(game.Sector, 30 * FixedStepClock.TicksPerSecond);
        Run(loaded, 30 * FixedStepClock.TicksPerSecond);

        Assert.Equal(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        Assert.Equal(500, loaded.GetCraft(game.ShipyardId).Inventory["slag"]);
    }

    // --- Survey -------------------------------------------------------------------------------

    [Fact]
    public void NewGame_MineableBodiesStartUnsurveyed_AndBlockJobsAndStructures()
    {
        var game = TestData.NewGame(surveyAll: false);
        var sector = game.Sector;
        var rock2 = Rock(sector, "Rock-2");
        Assert.All(sector.Bodies.Where(b => b.IsMineable), b => Assert.False(b.Surveyed));

        var job = sector.StartJob(game.PlayerCraftId, new MiningJob(rock2.Id, "silicate", game.ShipyardId, 0.5));
        Assert.False(job.Done);
        Assert.Contains("hasn't been surveyed", job.Message);

        var depot = sector.OrderBuild(game.ShipyardId, ShipDesign.FromJson(TestData.Read("data/blueprints/surface_depot.json")), Catalog,
            landingSite: rock2.Id);
        Assert.False(depot.Accepted);
        Assert.Contains(depot.Errors, e => e.Contains("hasn't been surveyed"));
    }

    [Fact]
    public void FlyingThePilotedShipClose_SurveysTheBody_OtherShipsDont()
    {
        var game = TestData.NewGame(surveyAll: false);
        var sector = game.Sector;
        var rock1 = Rock(sector, "Rock-1");
        var rock2 = Rock(sector, "Rock-2");
        var pilot = sector.GetCraft(game.PlayerCraftId);
        var yard = sector.GetCraft(game.ShipyardId);

        // A non-piloted craft (the yard) placed next to Rock-2 doesn't survey it.
        yard.Position = sector.GetPosition(rock2.Id) + new Vector2D(rock2.RadiusM + 500, 0);
        yard.RailOrbit = null;
        pilot.Position = sector.GetPosition(rock1.Id) + new Vector2D(0, -(rock1.RadiusM + SectorState.SurveyRangeM - 10));
        sector.Step();

        Assert.True(rock1.Surveyed);
        Assert.False(rock2.Surveyed);
        Assert.False(Rock(sector, "Rock-3").Surveyed);

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), Catalog).Sector;
        Assert.True(Rock(loaded, "Rock-1").Surveyed);
        Assert.False(Rock(loaded, "Rock-2").Surveyed);
    }

    // --- Freight ------------------------------------------------------------------------------

    [Fact]
    public void Freight_WaitsForTheMinimum_ThenHaulsEverything()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = AddOrbitalDepot(sector, yard, phaseOffset: 0.3);
        depot.Inventory.Add("silicate", 3_000);

        Assert.True(sector.StartJob(game.PlayerCraftId, new HaulJob(depot.Id, "silicate", yard.Id, minPickupKg: 5_000, keepStockedKg: 0)).Done);
        var job = (HaulJob)sector.GetCraft(game.PlayerCraftId).Job!;

        Assert.True(RunUntil(sector, () => job.State == HaulJobState.Waiting && job.WaitReason!.Contains("waiting for"), 20 * TicksPerMinute), job.Status);
        Assert.Contains("waiting for 5.0 t", job.Status);
        Assert.False(sector.GetCraft(game.PlayerCraftId).IsDocked); // doesn't block the pickup's port

        depot.Inventory.Add("silicate", 4_000); // now 7 t
        Assert.True(RunUntil(sector, () => job.CompletedCycles >= 1, 30 * TicksPerMinute), job.Status);
        Assert.Equal(7_000, job.DeliveredKg);
        Assert.Equal(0, depot.Inventory["silicate"]);
    }

    [Fact]
    public void Freight_KeepStocked_StopsAtTheTarget_AndWaitsWithTheRest()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = AddOrbitalDepot(sector, yard, phaseOffset: 0.3);
        depot.Inventory.Add("electronics", 10_000);

        // Keep 2 t of electronics at the *depot's twin*: use a second orbital depot as destination (no refinery to eat it).
        var store = AddOrbitalDepot(sector, yard, phaseOffset: -0.3);
        Assert.True(sector.StartJob(game.PlayerCraftId, new HaulJob(depot.Id, "electronics", store.Id, 1_000, keepStockedKg: 2_000)).Done);
        var job = (HaulJob)sector.GetCraft(game.PlayerCraftId).Job!;

        Assert.True(RunUntil(sector, () => job.State == HaulJobState.Waiting && job.WaitReason!.Contains("already has"), 40 * TicksPerMinute), job.Status);
        Assert.Equal(2_000, store.Inventory["electronics"]);
        Assert.Contains("already has 2.0 t", job.Status);
        Assert.Equal(8_000, sector.GetCraft(game.PlayerCraftId).Inventory["electronics"]);
    }

    [Fact]
    public void Freight_StartIsRefused_WithTheReason()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        Assert.Contains("different", sector.StartJob(game.PlayerCraftId,
            new HaulJob(game.ShipyardId, "metal", game.ShipyardId, 1, 0)).Message);
        Assert.Contains("another craft", sector.StartJob(game.PlayerCraftId,
            new HaulJob(game.PlayerCraftId, "metal", game.ShipyardId, 1, 0)).Message);
    }

    [Fact]
    public void FreightJob_SurvivesSaveLoad_Exactly()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var depot = AddOrbitalDepot(sector, yard, phaseOffset: 0.3);
        depot.Inventory.Add("silicate", 20_000);
        sector.StartJob(game.PlayerCraftId, new HaulJob(depot.Id, "silicate", yard.Id, 1_000, 0));
        Run(sector, 90 * FixedStepClock.TicksPerSecond);

        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), Catalog).Sector;
        Run(sector, 10 * TicksPerMinute);
        Run(loaded, 10 * TicksPerMinute);

        Assert.Equal(SaveGameSerializer.ToJson(sector, game.PlayerCraftId), SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
        Assert.IsType<HaulJob>(loaded.GetCraft(game.PlayerCraftId).Job);
    }

    // --- The Phase 4 gate ---------------------------------------------------------------------

    [Fact]
    public void Gate_AutonomousNetwork_ReplacesHaulingByHand()
    {
        // Setup a player would do once: build a depot on Rock-2 and a second ship, then hand out two jobs.
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rock2 = Rock(sector, "Rock-2");
        Assert.True(sector.OrderBuild(yard.Id, ShipDesign.FromJson(TestData.Read("data/blueprints/surface_depot.json")), Catalog,
            landingSite: rock2.Id).Accepted);
        Assert.True(sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog).Accepted);
        Assert.True(RunUntil(sector, () => sector.Crafts.Count == 4 && yard.BuildQueue.Count == 0, 5 * TicksPerMinute));
        var depot = sector.Crafts.Single(c => c.Design.Id == "surface_depot");
        var miner = sector.Crafts.Single(c => c.Design.Id == "starter_ship" && c.Id != game.PlayerCraftId);
        Assert.Equal(rock2.Id, depot.LandedOnBodyId);

        Assert.True(sector.StartJob(miner.Id, new MiningJob(rock2.Id, "silicate", depot.Id, 0.5)).Done);
        Assert.True(sector.StartJob(game.PlayerCraftId, new HaulJob(depot.Id, "silicate", yard.Id, minPickupKg: 10_000, keepStockedKg: 0)).Done);

        var composites = yard.Inventory["composites"];
        var mass = TotalMass(sector);
        // From here on, no input: two automated ships and two stations run the chain
        // Rock-2 silicate → depot → yard refinery → composites.
        var ok = RunUntil(sector, () => yard.Inventory["composites"] >= composites + 10_000, 6 * 60 * TicksPerMinute,
            () => Assert.Equal(mass, TotalMass(sector)));

        _log.WriteLine($"t={sector.TimeS / 60:0} min  miner: {miner.Job!.Status} ({miner.Job.CompletedCycles} cycles)  "
                       + $"freighter: {sector.GetCraft(game.PlayerCraftId).Job!.Status} ({sector.GetCraft(game.PlayerCraftId).Job!.CompletedCycles} cycles)  "
                       + $"yard composites +{(yard.Inventory["composites"] - composites) / 1000.0:0.0} t");
        Assert.True(ok, "the network didn't deliver 10 t of composites");
        Assert.True(sector.GetCraft(game.PlayerCraftId).Job!.CompletedCycles >= 1); // one 20 t haul already gives 10 t of composites
        Assert.True(miner.Job!.CompletedCycles >= 2);
    }

    // --- Scale --------------------------------------------------------------------------------

    [Fact]
    public void ThirtyAutomatedShips_RunTenMinutes_QuicklyAndConserveMass()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        var rocks = sector.Bodies.Where(b => b.IsMineable).ToList();
        var design = TestData.StarterShip();
        var stats = design.ComputeStats(Catalog);
        for (var i = 0; i < 30; i++)
        {
            var ship = new CraftState(sector.AllocateCraftId(), $"Miner {i + 1}", design, stats,
                yard.Position + new Vector2D(200 + 40 * i, -300), 0) { Velocity = yard.Velocity };
            sector.AddCraft(ship);
            var rock = rocks[i % rocks.Count];
            Assert.True(sector.StartJob(ship.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25)).Done);
        }
        var mass = TotalMass(sector);

        var watch = Stopwatch.StartNew();
        Run(sector, 10 * TicksPerMinute);
        watch.Stop();

        var perTickMs = watch.Elapsed.TotalMilliseconds / (10 * TicksPerMinute);
        _log.WriteLine($"30 ships, 10 min sim: {watch.Elapsed.TotalSeconds:0.00} s real, {perTickMs:0.000} ms/tick (budget 50 ms/tick at 20 Hz)");
        Assert.Equal(mass, TotalMass(sector));
        Assert.True(perTickMs < 5, $"{perTickMs:0.000} ms per tick"); // generous: 10% of the 50 ms tick budget
    }

    /// <summary>An orbital depot (docking port + 2 cargo bays, 40 t) on the yard's orbit, offset by a phase.</summary>
    private static CraftState AddOrbitalDepot(SectorState sector, CraftState yard, double phaseOffset)
    {
        var design = new ShipDesign("orbital_depot", "Depot", new[]
        {
            new DesignCell(new GridPos(0, -1), "docking_port"),
            new DesignCell(new GridPos(-1, 0), "cargo"), new DesignCell(new GridPos(0, 0), "hull"), new DesignCell(new GridPos(1, 0), "cargo"),
        });
        var depot = new CraftState(sector.AllocateCraftId(), $"Depot {sector.Crafts.Count}", design, design.ComputeStats(Catalog), yard.Position, 0);
        sector.AddCraft(depot);
        sector.PutOnRails(depot.Id, yard.RailOrbit! with { PhaseAtZeroRad = yard.RailOrbit!.PhaseAtZeroRad + phaseOffset });
        return depot;
    }
}
