using System.Linq;
using SpaceAutomationSandbox.Construction;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

public class BlueprintEditorTests
{
    private static BlueprintEditor NewEditor() => new(TestData.Catalog());

    [Fact]
    public void PlacingAndRemovingParts_UpdatesStatsImmediately()
    {
        var editor = NewEditor();
        var catalog = TestData.Catalog();
        Assert.Null(editor.Stats);

        editor.Place(new GridPos(0, 0), "cockpit");
        editor.Place(new GridPos(0, 1), "thruster");
        var stats = editor.Stats!;
        Assert.Equal(catalog.Get("cockpit").MassKg + catalog.Get("thruster").MassKg, stats.TotalMassKg);
        Assert.Equal(catalog.Get("thruster").ThrustN, stats.TotalThrustN);

        editor.Place(new GridPos(0, 1), "cargo"); // replace
        Assert.Equal(0, editor.Stats!.TotalThrustN);
        Assert.Equal(catalog.Get("cargo").CargoCapacityKg, editor.Stats!.CargoCapacityKg);

        editor.Remove(new GridPos(0, 1));
        Assert.Equal(catalog.Get("cockpit").MassKg, editor.Stats!.TotalMassKg);
    }

    [Fact]
    public void Errors_FollowTheFlyingRules_AndClearWhenFixed()
    {
        var editor = NewEditor();
        Assert.Contains(editor.Errors, e => e.Contains("no parts"));

        editor.Place(new GridPos(0, 0), "hull");
        Assert.Contains(editor.Errors, e => e.Contains("control source"));
        Assert.Contains(editor.Errors, e => e.Contains("thruster"));

        editor.Place(new GridPos(0, -1), "cockpit");
        editor.Place(new GridPos(0, 1), "thruster");
        Assert.Empty(editor.Errors);

        editor.Name = "   ";
        Assert.Contains(editor.Errors, e => e.Contains("name"));
    }

    [Fact]
    public void Place_RejectsUnknownPartsOutOfBoundsAndNoOps()
    {
        var editor = NewEditor();

        Assert.False(editor.Place(new GridPos(0, 0), "warp_core"));
        Assert.False(editor.Place(new GridPos(BlueprintEditor.HalfExtent + 1, 0), "hull"));
        Assert.True(editor.Place(new GridPos(BlueprintEditor.HalfExtent, -BlueprintEditor.HalfExtent), "hull"));
        Assert.False(editor.Place(new GridPos(BlueprintEditor.HalfExtent, -BlueprintEditor.HalfExtent), "hull"));
        Assert.False(editor.Remove(new GridPos(3, 3)));
        Assert.Single(editor.Cells);
    }

    [Fact]
    public void Undo_RevertsOneActionOrOneWholeStroke()
    {
        var editor = NewEditor();
        editor.Place(new GridPos(0, 0), "hull");

        editor.BeginStroke(); // one drag painting three cells
        editor.Place(new GridPos(1, 0), "hull");
        editor.Place(new GridPos(2, 0), "hull");
        editor.Place(new GridPos(3, 0), "hull");
        editor.EndStroke();

        editor.BeginStroke(); // a click that changes nothing leaves no undo step
        editor.Place(new GridPos(3, 0), "hull");
        editor.EndStroke();

        Assert.Equal(4, editor.Cells.Count);
        Assert.True(editor.Undo());
        Assert.Single(editor.Cells);
        Assert.True(editor.Undo());
        Assert.Empty(editor.Cells);
        Assert.False(editor.Undo());
    }

    [Fact]
    public void Dirty_TracksUnsavedEdits()
    {
        var editor = NewEditor();
        editor.Load(TestData.StarterShip());
        Assert.False(editor.IsDirty);

        editor.Remove(new GridPos(0, -3));
        Assert.True(editor.IsDirty);
        editor.MarkSaved();
        Assert.False(editor.IsDirty);
        editor.Name = "Renamed";
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void LoadedDesign_RoundTripsThroughTheEditorAndJson()
    {
        var editor = NewEditor();
        var starter = TestData.StarterShip();
        editor.Load(starter);
        editor.Name = "My Prospector Mk.2";

        var json = editor.ToDesign().ToJson();
        var reloaded = ShipDesign.FromJson(json);

        Assert.Equal("my_prospector_mk_2", reloaded.Id);
        Assert.Equal("My Prospector Mk.2", reloaded.Name);
        Assert.Equal(starter.Cells, reloaded.Cells);
        Assert.Equal(json, reloaded.ToJson()); // byte-stable
        Assert.Empty(reloaded.Validate(TestData.Catalog()));
    }

    [Fact]
    public void ShippedStarterFile_IsWhatTheWriterProduces()
    {
        // The editor and the hand-written data file agree on content (not whitespace).
        var starter = TestData.StarterShip();
        Assert.Equal(starter.Cells, ShipDesign.FromJson(starter.ToJson()).Cells);
        Assert.Equal(starter.Id, ShipDesign.FromJson(starter.ToJson()).Id);
    }

    [Fact]
    public void DisconnectedParts_AreAnError_NamingTheLooseCells()
    {
        var editor = NewEditor();
        editor.Name = "Test";
        editor.Place(new GridPos(0, 0), "cockpit");
        editor.Place(new GridPos(0, 1), "thruster");
        Assert.Empty(editor.Errors);

        editor.Place(new GridPos(4, 4), "cargo"); // floating
        Assert.Equal(2, editor.CountConnectedGroups());
        Assert.Contains(editor.Errors, e => e.Contains("1 part(s) aren't connected") && e.Contains("(4,4)"));
        Assert.Equal(new[] { new GridPos(4, 4) }, editor.ToDesign().LooseCells());

        editor.Place(new GridPos(1, 1), "cargo"); // touches the thruster: still loose at (4,4) only
        Assert.Single(editor.ToDesign().LooseCells());
        Assert.Empty(editor.ToDesign().Validate(TestData.Catalog(), DesignRole.Ship, requireConnected: false));
    }

    [Fact]
    public void BuiltInDesigns_AreAllConnected()
    {
        foreach (var file in new[] { "starter_ship", "starter_shipyard", "surface_depot" })
            Assert.Empty(ShipDesign.FromJson(TestData.Read($"data/blueprints/{file}.json")).LooseCells());
    }

    [Fact]
    public void StarterShip_IsOneConnectedGroup()
    {
        var editor = NewEditor();
        editor.Load(TestData.StarterShip());
        Assert.Equal(1, editor.CountConnectedGroups());
    }

    [Theory]
    [InlineData("My Ship", "my_ship")]
    [InlineData("  Ore--Hauler  #3 ", "ore_hauler_3")]
    [InlineData("!!!", "design")]
    [InlineData("ÜberMiner", "uberminer")] // accents are removed, not the letters
    [InlineData("Straße Æther Ørn", "strasse_aether_orn")]
    [InlineData("日本", "design")] // scripts without a Latin transliteration still fall back
    public void SlugFromName_IsFileSafe(string name, string expected)
    {
        Assert.Equal(expected, BlueprintEditor.SlugFromName(name));
    }

    [Fact]
    public void LoadingADesignWithAnUnknownPart_KeepsIt_AndReportsIt()
    {
        var design = new ShipDesign("old", "Old", new[]
        {
            new DesignCell(new GridPos(0, 0), "cockpit"),
            new DesignCell(new GridPos(0, 1), "thruster"),
            new DesignCell(new GridPos(1, 0), "retired_part"),
        });
        var editor = NewEditor();
        editor.Load(design);

        Assert.Equal(3, editor.Cells.Count);
        Assert.Contains(editor.Errors, e => e.Contains("retired_part"));
        Assert.Equal(2, editor.Stats!.CellCount); // stats from the known parts only
        Assert.True(editor.Remove(new GridPos(1, 0)));
        Assert.Empty(editor.Errors);
    }
}
