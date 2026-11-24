using System;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class ConstructionTests
{
    [Fact]
    public void Every_part_says_what_it_does_and_only_the_hull_just_joins()
    {
        var catalog = TestData.Catalog();
        foreach (var part in catalog.Parts)
        {
            var facts = PartFacts.Of(part, catalog.Materials);
            Assert.NotEmpty(facts);
            Assert.All(facts, f => Assert.False(string.IsNullOrWhiteSpace(f.Text) || string.IsNullOrWhiteSpace(f.Icon) || f.Icon == "mat_", part.Id));
            if (part.Id != "hull") Assert.DoesNotContain(facts, f => f.Text.StartsWith("Joins"));
        }
        Assert.Contains(PartFacts.Of(catalog.Get("autocannon"), catalog.Materials), f => f.Text.Contains("2 kg of shells"));
    }

    [Fact]
    public void Every_part_has_a_known_category_and_each_category_has_parts()
    {
        var names = PartFacts.Categories.Select(c => c.Name).ToList();
        var used = TestData.Catalog().Parts.Select(PartFacts.Category).ToList();
        Assert.All(used, c => Assert.Contains(c, names));
        Assert.All(names, n => Assert.Contains(n, used));
    }

    [Fact]
    public void Problem_cells_say_where_and_why()
    {
        var catalog = TestData.Catalog();
        var good = TestData.StarterShip();
        Assert.Empty(good.ProblemCells(catalog, DesignRole.Ship));
        var bad = new ShipDesign("bad", "Bad", good.Cells.Append(new DesignCell(new GridPos(9, 9), "hull")).Append(new DesignCell(new GridPos(9, 8), "cockpit")));
        var cells = bad.ProblemCells(catalog, DesignRole.Ship);
        Assert.Contains("Not connected", cells[new GridPos(9, 9)]);
        Assert.Contains("one control source", cells[new GridPos(9, 8)]);
        Assert.Contains(cells, c => c.Key != new GridPos(9, 8) && c.Value.Contains("one control source")); // the other cockpit too
    }

    [Fact]
    public void PartCatalog_LoadsTheStarterParts()
    {
        var ids = TestData.Catalog().Parts.Select(p => p.Id).OrderBy(id => id);

        Assert.Equal(new[]
        {
            "anchor_clamp", "animal_pen", "antimatter_drive", "antimatter_factory", "antimatter_reactor", "armour_plate", "autocannon", "battery", "battery_mk2", "cargo", "cockpit", "containment", "docking_port", "drill", "drill_mk2", "drill_mk3", "electrolyser",
            "fabricator", "fabricator_mk2", "farm", "foundation", "fuel_cell", "fuel_generator", "fusion_reactor", "gate_defense", "gate_lock", "geothermal_tap",
            "habitat", "harvester", "heat_collector", "hull", "ion_drive", "isotope_separator", "laser", "methalox_rocket", "methane_generator", "missile_launcher", "nanite_bay", "nano_assembler", "nuclear_rocket", "point_defense",
            "quantum_drive", "ranch", "reactor", "refinery", "refinery_mk2", "repair_drones", "repeller", "research_lab", "robot_bay", "rocket", "salvage_arm", "shield_generator", "slag_ejector", "solar_array", "solar_panel",
            "solar_panel_mk2", "solar_panel_mk3", "storage_silo", "thruster", "thruster_mk2", "thruster_mk3", "warp_gate",
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
