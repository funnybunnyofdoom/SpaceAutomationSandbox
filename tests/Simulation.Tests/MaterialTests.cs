using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class MaterialTests
{
    private static PartCatalog CatalogFrom(string partsJson) => PartCatalog.FromJson(partsJson, TestData.Materials());

    [Fact]
    public void MaterialCatalog_LoadsTheMaterials_InFileOrder()
    {
        var materials = TestData.Materials();

        Assert.Equal(new[] { "metal", "electronics", "composites", "iron_ore", "silicate", "rock", "copper_ore", "slag", "ice", "water", "fuel", "food", "exotic_ore", "superconductors", "hydrogen", "oxygen", "uranium_ore", "fuel_rods", "spent_fuel", "methane_ice", "methane", "deuterium", "shells", "missiles" }, materials.Materials.Select(m => m.Id));
        Assert.Equal(0, materials.OrderOf("metal"));
        Assert.Equal(int.MaxValue, materials.OrderOf("unobtainium"));
    }

    [Fact]
    public void MaterialCatalog_RejectsDuplicatesAndUnknownSchema()
    {
        Assert.Throws<FormatException>(() => MaterialCatalog.FromJson(
            """{ "schemaVersion": 1, "materials": [ { "id": "a" }, { "id": "a" } ] }"""));
        Assert.Throws<FormatException>(() => MaterialCatalog.FromJson("""{ "schemaVersion": 9, "materials": [] }"""));
    }

    [Fact]
    public void EveryShippedPart_IsMadeOfExactlyItsMass()
    {
        // Loading enforces it; this states the rule (no mass appears or vanishes when building).
        foreach (var part in TestData.Catalog().Parts)
            Assert.Equal(part.MassKg, part.Bill.TotalKg);
    }

    [Theory]
    [InlineData("""{ "id": "x", "massKg": 100 }""", "needs materialsKg")]
    [InlineData("""{ "id": "x", "massKg": 100, "materialsKg": { "unobtainium": 100 } }""", "unknown material")]
    [InlineData("""{ "id": "x", "massKg": 100, "materialsKg": { "metal": 60 } }""", "add up to 60")]
    [InlineData("""{ "id": "x", "massKg": 100, "materialsKg": { "metal": 120, "electronics": -20 } }""", "positive amount")]
    public void PartCatalog_RejectsBadMaterials_WithAReason(string part, string expectedMessage)
    {
        var ex = Assert.Throws<FormatException>(() => CatalogFrom($$"""{ "schemaVersion": 2, "parts": [ {{part}} ] }"""));
        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void StarterShip_BillOfMaterials_SumsItsPartsAndEqualsItsMass()
    {
        var catalog = TestData.Catalog();
        var design = TestData.StarterShip();

        var bill = design.ComputeBillOfMaterials(catalog);

        Assert.Equal(13_000, bill["metal"]);
        Assert.Equal(3_100, bill["electronics"]);
        Assert.Equal(2_100, bill["composites"]);
        Assert.Equal(800, bill["slag"]); // four hull plates (#7)
        Assert.Equal(design.ComputeStats(catalog).TotalMassKg, bill.TotalKg); // conservation
    }

    [Fact]
    public void EditorBill_UpdatesWithTheLayout()
    {
        var catalog = TestData.Catalog();
        var editor = new BlueprintEditor(catalog);
        Assert.Equal(0, editor.BillOfMaterials.TotalKg);

        editor.Place(new GridPos(0, 0), "battery");
        Assert.Equal(catalog.Get("battery").Bill["electronics"], editor.BillOfMaterials["electronics"]);

        editor.Place(new GridPos(0, 0), "hull"); // replace: battery's electronics are gone
        Assert.Equal(0, editor.BillOfMaterials["electronics"]);
        Assert.Equal(catalog.Get("hull").MassKg, editor.BillOfMaterials.TotalKg);
    }

    [Fact]
    public void MaterialBill_MergesIgnoresZeroAndRejectsNegative()
    {
        var a = new MaterialBill(new Dictionary<string, double> { ["metal"] = 5, ["electronics"] = 0 });
        var b = new MaterialBill(new Dictionary<string, double> { ["metal"] = 2, ["composites"] = 1 });

        var sum = a.Plus(b);

        Assert.Equal(new[] { "composites", "metal" }, sum.Kg.Keys); // ordinal order, zero dropped
        Assert.Equal(7, sum["metal"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaterialBill(new Dictionary<string, double> { ["metal"] = -1 }));
    }
}
