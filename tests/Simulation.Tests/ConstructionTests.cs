using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class ConstructionTests
{
    [Fact]
    public void PartCatalog_LoadsTheStarterParts()
    {
        var ids = TestData.Catalog().Parts.Select(p => p.Id).OrderBy(id => id);

        Assert.Equal(new[]
        {
            "anchor_clamp", "battery", "battery_mk2", "cargo", "cockpit", "docking_port", "drill", "drill_mk2", "drill_mk3", "electrolyser", "fabricator", "fabricator_mk2",
            "farm", "foundation", "fuel_cell", "fuel_generator", "gate_defense", "habitat", "hull", "refinery", "refinery_mk2", "research_lab", "robot_bay", "slag_ejector", "solar_panel", "solar_panel_mk2",
            "solar_panel_mk3", "storage_silo", "thruster", "thruster_mk2", "thruster_mk3", "warp_gate",
        }, ids);
    }

    [Fact]
    public void StarterShip_Validates_AndStatsAreSumsOfItsParts()
    {
        var catalog = TestData.Catalog();
        var design = TestData.StarterShip();

        Assert.Empty(design.Validate(catalog));

        var stats = design.ComputeStats(catalog);
        var parts = design.Cells.Select(c => catalog.Get(c.PartId)).ToList();
        Assert.Equal(design.Cells.Count, stats.CellCount);
        Assert.Equal(parts.Sum(p => p.MassKg), stats.TotalMassKg);
        Assert.Equal(parts.Sum(p => p.ThrustN), stats.TotalThrustN);
        Assert.Equal(parts.Sum(p => p.CargoCapacityKg), stats.CargoCapacityKg);
        Assert.Equal(parts.Sum(p => p.EnergyCapacityKJ), stats.EnergyCapacityKJ);
        Assert.Equal(0, stats.CenterOfMassX, 9); // the starter ship is left-right symmetric
        Assert.True(stats.MaxAccelerationMps2 > 0);
    }

    [Fact]
    public void ChangingTheLayout_ChangesTheStats()
    {
        var catalog = TestData.Catalog();
        var starter = TestData.StarterShip();
        var withoutOneThruster = new ShipDesign("t", "t",
            starter.Cells.Where(c => c.Pos != new GridPos(1, 1)));

        var before = starter.ComputeStats(catalog);
        var after = withoutOneThruster.ComputeStats(catalog);

        Assert.Equal(before.TotalThrustN - catalog.Get("thruster").ThrustN, after.TotalThrustN);
        Assert.Equal(before.TotalMassKg - catalog.Get("thruster").MassKg, after.TotalMassKg);
        Assert.True(after.CenterOfMassX < 0, "Removing the right thruster shifts mass to the left.");
    }

    [Fact]
    public void Validate_ReportsEveryProblemInsteadOfThrowing()
    {
        var design = new ShipDesign("bad", "bad", new[]
        {
            new DesignCell(new GridPos(0, 0), "hull"),
            new DesignCell(new GridPos(1, 0), "warp_core"),
        });

        var errors = design.Validate(TestData.Catalog());

        Assert.Contains(errors, e => e.Contains("warp_core"));
        Assert.Contains(errors, e => e.Contains("control source"));
        Assert.Contains(errors, e => e.Contains("thruster"));
    }

    [Fact]
    public void Validate_RejectsTwoCockpits()
    {
        var design = new ShipDesign("two", "two", new[]
        {
            new DesignCell(new GridPos(0, 0), "cockpit"),
            new DesignCell(new GridPos(1, 0), "cockpit"),
            new DesignCell(new GridPos(0, 1), "thruster"),
        });

        Assert.Contains(design.Validate(TestData.Catalog()), e => e.Contains("found 2"));
    }

    [Fact]
    public void FromJson_RejectsDuplicateCellsAndUnknownSchema()
    {
        const string duplicate = """
            { "schemaVersion": 1, "id": "d", "cells": [
              { "x": 0, "y": 0, "part": "hull" }, { "x": 0, "y": 0, "part": "cockpit" } ] }
            """;
        const string future = """{ "schemaVersion": 99, "id": "f", "cells": [] }""";

        Assert.Throws<FormatException>(() => ShipDesign.FromJson(duplicate));
        Assert.Throws<FormatException>(() => ShipDesign.FromJson(future));
        Assert.Throws<FormatException>(() => PartCatalog.FromJson("""{ "schemaVersion": 1, "parts": [] }""", TestData.Materials()));
    }

    [Fact]
    public void PartCatalog_RejectsDuplicateIdsAndMasslessParts()
    {
        Assert.Throws<FormatException>(() => PartCatalog.FromJson(
            """{ "schemaVersion": 2, "parts": [ { "id": "a", "massKg": 1, "materialsKg": { "metal": 1 } }, { "id": "a", "massKg": 2, "materialsKg": { "metal": 2 } } ] }""",
            TestData.Materials()));
        Assert.Throws<FormatException>(() => PartCatalog.FromJson(
            """{ "schemaVersion": 2, "parts": [ { "id": "a", "massKg": 0, "materialsKg": { "metal": 0 } } ] }""", TestData.Materials()));
    }
}
