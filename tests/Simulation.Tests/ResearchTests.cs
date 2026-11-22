using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>#37 research and progression; #25 star types and exotic ore.</summary>
public class ResearchTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();
    private const int TicksPerMinute = 60 * FixedStepClock.TicksPerSecond;

    private static ShipDesign WithPart(string partId) =>
        new($"with_{partId}", "Test", TestData.StarterShip().Cells.Select(c => c.PartId == "drill" ? c with { PartId = partId } : c));

    /// <summary>A small lab station beside the yard (lab, solar panel, storage), stocked with electronics.</summary>
    private static CraftState Lab(SectorState sector, long electronicsKg = 100)
    {
        var yard = sector.Crafts.First(c => c.IsShipyard);
        var design = new ShipDesign("lab", "Lab", new[]
        {
            new DesignCell(new GridPos(0, 0), "research_lab"),
            new DesignCell(new GridPos(1, 0), "solar_panel"),
            new DesignCell(new GridPos(-1, 0), "storage_silo"),
        });
        var lab = new CraftState(sector.AllocateCraftId(), "Lab", design, design.ComputeStats(Catalog), yard.Position + new Vector2D(0, 2_000), 0)
        {
            Velocity = yard.Velocity,
        };
        sector.AddCraft(lab);
        if (electronicsKg > 0) sector.DebugAddStock(lab.Id, "electronics", electronicsKg);
        return lab;
    }

    [Fact]
    public void ANewGame_LocksTheAdvancedParts_AndSaysWhichResearchTheyNeed()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        Assert.False(sector.IsPartUnlocked("drill_mk2"));
        Assert.False(sector.IsPartUnlocked("warp_gate"));
        Assert.True(sector.IsPartUnlocked("drill"));
        Assert.True(sector.IsPartUnlocked("research_lab"));

        var result = sector.OrderBuild(game.ShipyardId, WithPart("drill_mk2"), Catalog);
        Assert.False(result.Accepted);
        Assert.Contains("Deep drilling", string.Join(" ", result.Errors));
        Assert.True(sector.OrderBuild(game.ShipyardId, TestData.StarterShip(), Catalog).Accepted); // basic parts: fine
    }

    [Fact]
    public void ALab_TurnsPowerAndElectronicsIntoPoints_WithMassExact()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        var lab = Lab(sector);
        var massBefore = sector.ComputeMassLedger().TotalKg;

        for (var i = 0; i < TicksPerMinute; i++) sector.Step();

        Assert.Equal(6, sector.ResearchPoints, 3);
        Assert.Equal(100 - 6, lab.Inventory["electronics"]);
        Assert.Equal(massBefore, sector.ComputeMassLedger().TotalKg); // the electronics are counted as consumed
    }

    [Fact]
    public void ALabWithoutElectronics_Idles_AndSaysWhy()
    {
        var sector = TestData.NewGame().Sector;
        var lab = Lab(sector, electronicsKg: 0);
        for (var i = 0; i < 100; i++) sector.Step();
        Assert.Equal(0, sector.ResearchPoints);
        Assert.Contains("no electronics", lab.LabStatus);
    }

    [Fact]
    public void Researching_NeedsThePrerequisites_AndThePoints_ThenUnlocksTheParts()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        Assert.Contains("costs 60", sector.TryResearch("deep_drilling").Message);
        sector.DebugAddResearch(200);
        Assert.Contains("needs Deep drilling", sector.TryResearch("industrial_refining").Message);

        Assert.True(sector.TryResearch("deep_drilling").Done);
        Assert.Equal(140, sector.ResearchPoints, 6);
        Assert.True(sector.IsPartUnlocked("drill_mk2"));
        Assert.True(sector.OrderBuild(game.ShipyardId, WithPart("drill_mk2"), Catalog).Accepted);
        Assert.Contains("already researched", sector.TryResearch("deep_drilling").Message);
    }

    [Fact]
    public void Research_SurvivesSaveLoad()
    {
        var sector = TestData.NewGame().Sector;
        sector.DebugAddResearch(100);
        sector.TryResearch("photovoltaics");
        var loaded = SaveGameSerializer.FromJson(SaveGameSerializer.ToJson(sector, sector.PilotCraftId), Catalog).Sector;
        Assert.Equal(60, loaded.ResearchPoints, 6);
        Assert.True(loaded.IsPartUnlocked("solar_panel_mk2"));
        Assert.False(loaded.IsPartUnlocked("warp_gate"));
        Assert.False(loaded.ResearchAllUnlocked);
    }

    [Fact]
    public void AGameStartedBeforeResearch_KeepsEverythingUnlocked()
    {
        var sector = TestData.NewGame().Sector;
        var json = JsonNode.Parse(SaveGameSerializer.ToJson(sector, sector.PilotCraftId))!.AsObject();
        json["schemaVersion"] = 18; // as saved by the previous version
        var loaded = SaveGameSerializer.FromJson(json.ToJsonString(), Catalog).Sector;
        Assert.True(loaded.ResearchAllUnlocked);
        Assert.True(loaded.IsPartUnlocked("warp_gate"));
    }

    // --- #25 ---

    [Fact]
    public void OtherSystems_HaveStarTypes_AndExoticOre_NeverFoundAtHome()
    {
        var sector = TestData.NewGame().Sector;
        Assert.DoesNotContain(sector.Bodies, b => b.DepositsKg.ContainsKey("exotic_ore"));
        var classes = new HashSet<StarClass>();
        CelestialBodyState? firstStar = null;
        for (var i = 1; i <= 12; i++)
        {
            var (star, _) = SectorGenerator.GenerateSystem(sector, i);
            firstStar ??= star;
            classes.Add(star.StarClass);
        }
        Assert.Equal(3, classes.Count);
        // The first system always has exotic ore (so the Mk3 path is open).
        Assert.Contains(sector.Bodies, b => sector.StarOf(b.Id)?.Id == firstStar!.Id && b.DepositsKg.ContainsKey("exotic_ore"));
        Assert.Contains(Catalog.Recipes.Recipes, r => r.InputsKg!.ContainsKey("exotic_ore") && r.OutputsKg!.ContainsKey("superconductors"));
        Assert.Equal("superconductors", Catalog.Research.TechFor("thruster_mk3")!.Id);
    }

    [Theory]
    [InlineData(StarClass.Red, 0.5)]
    [InlineData(StarClass.Yellow, 1.0)]
    [InlineData(StarClass.Blue, 2.0)]
    public void Sunlight_DependsOnTheStarType(StarClass type, double factor)
    {
        var sector = TestData.NewGame().Sector;
        var (star, _) = SectorGenerator.GenerateSystem(sector, 1);
        star.StarClass = type;
        var point = sector.GetPosition(star.Id) + new Vector2D(SectorGenerator.HomeOrbitRadiusM, 0);
        Assert.Equal(factor, sector.SolarFactorAt(point), 6);
    }
}
