using System;
using System.IO;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Reusable modules (#113).</summary>
public sealed class ModuleTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static ShipModule Engine() => new(" Engine  block ", new[]
    {
        new DesignCell(new GridPos(5, 7), "thruster", 1), new DesignCell(new GridPos(4, 7), "battery"), new DesignCell(new GridPos(4, 6), "hull"),
    });

    [Fact]
    public void Files_round_trip_exactly()
    {
        var module = Engine();
        Assert.Equal(("Engine block", "engine_block", 2, 2), (module.Name, module.Id, module.Width, module.Height));
        Assert.Equal(new GridPos(0, 0), module.Cells[0].Pos);
        var again = ShipModule.FromJson(module.ToJson(), Catalog);
        Assert.Equal(module.Cells, again.Cells);
        Assert.Equal(module.ToJson(), again.ToJson());
        Assert.Equal("Other", module.Renamed("Other").Name);
        Assert.Equal(module.Cells, module.Renamed("Other").Cells);
    }

    [Theory]
    [InlineData("", "JSON")]
    [InlineData("null", "empty")]
    [InlineData("{ nonsense", "JSON")]
    [InlineData("""{ "schemaVersion": 1, "id": "d", "cells": [] }""", "Not a module")]
    [InlineData("""{ "schemaVersion": 2, "kind": "module", "name": "A", "cells": [ { "x": 0, "y": 0, "part": "hull" } ] }""", "schemaVersion 2")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "cells": [ { "x": 0, "y": 0, "part": "hull" } ] }""", "name")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "name": "A" }""", "at least one part")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "name": "A", "cells": [ null ] }""", "empty")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "name": "A", "cells": [ { "x": 0, "y": 0 } ] }""", "no part")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "name": "A", "cells": [ { "x": 0, "y": 0, "part": "warp_core" } ] }""", "warp_core")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "name": "A", "cells": [ { "x": 0, "y": 0, "part": "hull", "rot": 4 } ] }""", "rotation")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "name": "A", "cells": [ { "x": 1, "y": 0, "part": "hull" }, { "x": 1, "y": 0, "part": "hull" } ] }""", "Two parts")]
    [InlineData("""{ "schemaVersion": 1, "kind": "module", "name": "A", "cells": [ { "x": -2000000000, "y": 0, "part": "hull" }, { "x": 2000000000, "y": 0, "part": "hull" } ] }""", "at most 101")]
    public void Bad_files_are_refused_with_the_reason(string json, string reason)
    {
        var e = Assert.Throws<FormatException>(() => ShipModule.FromJson(json, Catalog));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void Names_must_fit() => Assert.Throws<FormatException>(() => new ShipModule(new string('x', 41), Engine().Cells));

    [Fact]
    public void Built_in_modules_are_good_and_connected()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(dir!.FullName, "data", "modules"))) dir = dir.Parent;
        var files = Directory.GetFiles(Path.Combine(dir.FullName, "data", "modules"), "*.json");
        Assert.Equal(6, files.Length);
        foreach (var file in files)
        {
            var module = ShipModule.FromJson(File.ReadAllText(file), Catalog);
            Assert.Equal(Path.GetFileNameWithoutExtension(file), module.Id);
            Assert.Empty(module.Design.LooseCells());
            Assert.All(module.Cells, c => Assert.Null(Catalog.Research.TechFor(c.PartId))); // nothing to research
        }
    }

    [Fact]
    public void Stamps_repeat_side_by_side_the_way_dragged()
    {
        var stamp = Engine().Cells;
        Assert.Equal(3, BlueprintTools.Repeated(stamp, new GridPos(0, 0), new GridPos(1, 1)).Count); // not far enough for another
        var right = BlueprintTools.Repeated(stamp, new GridPos(10, 10), new GridPos(15, 12)); // 5 across: at 0, 2 and 4
        Assert.Equal(9, right.Count);
        Assert.Equal(new[] { 10, 12, 14 }, right.Where(c => c.PartId == "battery").Select(c => c.Pos.X));
        var up = BlueprintTools.Repeated(stamp, new GridPos(0, 0), new GridPos(1, -4));
        Assert.Equal(new[] { 1, -1, -3 }, up.Where(c => c.PartId == "battery").Select(c => c.Pos.Y));
        Assert.Equal(up.Count, up.Select(c => c.Pos).Distinct().Count());
    }

    [Fact]
    public void A_stamp_is_a_copy_and_one_undo_step_and_never_half_off_the_grid()
    {
        var editor = new BlueprintEditor(Catalog) { Extent = BlueprintEditor.LargeHalfExtent };
        Assert.True(editor.PlaceAll(BlueprintTools.Repeated(Engine().Cells, new GridPos(0, 0), new GridPos(6, 0))));
        Assert.Equal(12, editor.Cells.Count);
        Assert.False(editor.PlaceAll(BlueprintTools.Repeated(Engine().Cells, new GridPos(46, 0), new GridPos(52, 0))));
        Assert.Equal(12, editor.Cells.Count);
        var saved = editor.ToDesign().ToJson();
        Assert.DoesNotContain("module", saved);
        editor.Undo();
        Assert.Empty(editor.Cells);
        // A module of a selection is its parts as they stand.
        editor.Redo();
        editor.Selection.UnionWith(editor.Cells.Keys.Where(p => p.X < 2));
        Assert.Equal(Engine().Cells, new ShipModule("again", editor.Selected).Cells);
    }
}
