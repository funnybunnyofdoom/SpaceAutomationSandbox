using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;
using Xunit.Abstractions;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>
/// The full gameplay loop with no starting stock: mine every raw material, refine, build a ship.
/// Also covers the electronics chain (#6), new pacing (#49) and debug tuning (#50).
/// </summary>
public class ShipLoopTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;
    private readonly ITestOutputHelper _log;

    public ShipLoopTests(ITestOutputHelper log) => _log = log;

    [Fact]
    public void EveryShipMaterial_CanBeProducedFromDeposits()
    {
        var recipes = TestData.Recipes().Recipes;
        var deposits = SectorGenerator.Generate(NewGame.DefaultSeed).Bodies.SelectMany(b => b.DepositsKg.Keys).ToHashSet();
        var bill = TestData.StarterShip().ComputeBillOfMaterials(Catalog);

        foreach (var material in bill.Kg.Keys)
        {
            var recipe = recipes.FirstOrDefault(r => r.OutputsKg!.ContainsKey(material));
            Assert.True(recipe is not null, $"no recipe makes {material}");
            Assert.All(recipe!.InputsKg!.Keys, input => Assert.Contains(input, deposits));
        }
    }

    [Fact]
    public void Electronics_AreMadeFromCopperOreAndSilicate()
    {
        var game = TestData.NewGame();
        var yard = game.Sector.GetCraft(game.ShipyardId);
        var electronics = yard.Inventory["electronics"];
        yard.Inventory.Add("copper_ore", 600);
        yard.Inventory.Add("silicate", 400);

        for (var i = 0; i < 10 * FixedStepClock.TicksPerSecond; i++) game.Sector.Step();

        Assert.Equal(electronics + 500, yard.Inventory["electronics"]);
        Assert.Equal(0, yard.Inventory["copper_ore"] + yard.Inventory["silicate"]);
    }

    [Fact]
    public void FullLoop_FromEmptyStock_MinesRefinesAndBuildsAShip()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        // No starting stock at all, except the crew's food and water (the yard needs its crew, #11).
        yard.Inventory.TryRemove(yard.Inventory.Kg.Where(kv => kv.Key is not ("food" or "water")).ToDictionary(kv => kv.Key, kv => kv.Value));
        var design = TestData.StarterShip();
        var bill = design.ComputeBillOfMaterials(Catalog).ToWholeKg();

        // Three miners, one per raw material, all delivering to the yard (which refines everything).
        var stats = design.ComputeStats(Catalog);
        var jobs = new (string Rock, string Material)[] { ("Rock-1", "iron_ore"), ("Rock-2", "silicate"), ("Rock-3", "copper_ore") };
        var start = new HashSet<CraftId>(sector.Crafts.Select(c => c.Id));
        foreach (var (rock, material) in jobs)
        {
            var miner = new CraftState(sector.AllocateCraftId(), $"{material} miner", design, stats,
                yard.Position + new Vector2D(300 + 60 * sector.Crafts.Count, -400), 0) { Velocity = yard.Velocity };
            sector.AddCraft(miner);
            Assert.True(sector.StartJob(miner.Id, new MiningJob(sector.Bodies.Single(b => b.Name == rock).Id, material, yard.Id, 0.5)).Done);
        }
        start.UnionWith(sector.Crafts.Select(c => c.Id));

        long Mass() => sector.Bodies.Sum(b => b.DepositsKg.Values.Sum())
                       + sector.Crafts.Sum(c => c.Inventory.TotalKg + c.BuildQueue.Sum(o => o.MaterialsKg.Values.Sum())
                                                + (c.RefiningBatch?.InputsKg.Values.Sum() ?? 0))
                       + sector.Crafts.Where(c => !start.Contains(c.Id)).Sum(c => (long)c.Design.ComputeBillOfMaterials(Catalog).TotalKg)
                       + sector.EjectedKg.Values.Sum();
        var mass = Mass();

        var ordered = false;
        CraftState? built = null;
        for (var tick = 0; tick < 4 * 60 * TicksPerMinute && built is null; tick++)
        {
            if (!ordered && yard.Inventory.Shortfall(bill).Count == 0)
            {
                Assert.True(sector.OrderBuild(yard.Id, design, Catalog).Accepted);
                ordered = true;
                _log.WriteLine($"t={sector.TimeS / 60:0.0} min: enough refined material, ship ordered");
            }
            sector.Step();
            if (tick % TicksPerMinute == 0) Assert.Equal(mass, Mass());
            built = sector.Crafts.FirstOrDefault(c => !start.Contains(c.Id));
        }

        _log.WriteLine($"t={sector.TimeS / 60:0.0} min: {(built is null ? "no ship" : $"built {built.Name}")}; yard: "
                       + string.Join(", ", yard.Inventory.Kg.Select(kv => $"{kv.Key} {kv.Value / 1000.0:0.0} t")));
        Assert.NotNull(built);
        Assert.Equal(mass, Mass());
    }

    [Fact]
    public void Tuning_ScalesTheDrillRefineryAndFabricator()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var yard = sector.GetCraft(game.ShipyardId);
        sector.Tuning.RefiningRateMultiplier = 4;
        yard.Inventory.Add("iron_ore", 4_000);
        for (var i = 0; i < 5 * FixedStepClock.TicksPerSecond + 4; i++) sector.Step(); // 4 t at 200 × 4 kg/s ≈ 5 s

        Assert.Equal(0, yard.Inventory["iron_ore"]);

        sector.Tuning.FabricationRateMultiplier = 10;
        sector.OrderBuild(yard.Id, TestData.StarterShip(), Catalog);
        var seconds = TestData.StarterShip().ComputeStats(Catalog).TotalMassKg / (yard.Stats.FabricationRateKgPerS * 10);
        for (var i = 0; i < (int)Math.Ceiling(seconds * FixedStepClock.TicksPerSecond) + 2; i++) sector.Step();
        Assert.Empty(yard.BuildQueue);
    }

    [Fact]
    public void Tuning_SurvivesSaveLoad_AndDefaultsAreOmitted()
    {
        var game = TestData.NewGame();
        Assert.DoesNotContain("\"tuning\"", SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId));

        game.Sector.Tuning.DrillRateMultiplier = 3.5;
        game.Sector.Tuning.FabricationRateMultiplier = 0.5;
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId), Catalog).Sector;

        Assert.Equal(3.5, loaded.Tuning.DrillRateMultiplier);
        Assert.Equal(1, loaded.Tuning.RefiningRateMultiplier);
        Assert.Equal(0.5, loaded.Tuning.FabricationRateMultiplier);
    }

    [Fact]
    public void Tuning_IsClamped_AndResets()
    {
        var tuning = new SimulationTuning { DrillRateMultiplier = 1e9, RefiningRateMultiplier = -3, FabricationRateMultiplier = double.NaN };
        Assert.Equal(SimulationTuning.Max, tuning.DrillRateMultiplier);
        Assert.Equal(SimulationTuning.Min, tuning.RefiningRateMultiplier);
        Assert.Equal(1, tuning.FabricationRateMultiplier);
        tuning.Reset();
        Assert.True(tuning.IsDefault);
    }

    [Fact]
    public void DebugCheats_AddStockAndSurveyEverything()
    {
        var game = TestData.NewGame(surveyAll: false);
        game.Sector.DebugAddStock(game.ShipyardId, "electronics", 10_000);
        game.Sector.DebugSurveyAll();

        Assert.Equal(NewGame.StartingStockKg["electronics"] + 10_000, game.Sector.GetCraft(game.ShipyardId).Inventory["electronics"]);
        Assert.All(game.Sector.Bodies, b => Assert.True(b.Surveyed));
    }
}
