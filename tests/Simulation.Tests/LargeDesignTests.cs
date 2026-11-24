using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Large-ship building: when it opens (#111) and its tools (#112).</summary>
public sealed class LargeDesignTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    private static BlueprintEditor Editor(int extent = BlueprintEditor.LargeHalfExtent) => new(Catalog) { Extent = extent };

    private static HashSet<GridPos> Set(params (int X, int Y)[] cells) => cells.Select(c => new GridPos(c.X, c.Y)).ToHashSet();

    // --- Opening ---

    [Fact]
    public void It_opens_for_good_when_a_yard_with_the_part_works_and_is_saved()
    {
        var game = TestData.NewGame();
        var sector = game.Sector;
        for (var i = 0; i < 60; i++) sector.Step();
        Assert.False(sector.LargeDesigns); // the starter yard has an ordinary fabricator
        var design = new ShipDesign("y", "Yard", new[]
        {
            new DesignCell(new GridPos(0, 0), "fabricator_mk3"), new DesignCell(new GridPos(1, 0), "docking_port"), new DesignCell(new GridPos(0, 1), "storage_silo"),
        });
        var home = SectorGenerator.HomePlanet(sector);
        var yard = new CraftState(sector.AllocateCraftId(), "Big yard", design, design.ComputeStats(Catalog), sector.GetPosition(home.Id), 0);
        sector.AddCraft(yard);
        sector.PutOnRails(yard.Id, new CircularOrbit(home.Id, home.RadiusM + 9_000, 4_000, 0));
        for (var i = 0; i < 30; i++) sector.Step();
        Assert.True(sector.LargeDesigns);
        Assert.Single(sector.Events, e => e.Text.Contains("101 x 101"));
        sector.DebugHit(yard.Id, 0, 99);
        for (var i = 0; i < 60; i++) sector.Step();
        Assert.True(sector.LargeDesigns); // even without it
        var json = SaveGameSerializer.ToJson(sector, game.PlayerCraftId);
        var loaded = SaveGameSerializer.FromJson(json, Catalog).Sector;
        Assert.True(loaded.LargeDesigns);
        Assert.Equal(json, SaveGameSerializer.ToJson(loaded, game.PlayerCraftId));
    }

    [Fact]
    public void The_grid_is_as_big_as_allowed_and_a_bigger_design_can_only_be_looked_at()
    {
        var small = new BlueprintEditor(Catalog);
        Assert.False(small.Place(new GridPos(11, 0), "hull"));
        var large = Editor();
        Assert.True(large.Place(new GridPos(50, -50), "hull"));
        Assert.False(large.Place(new GridPos(51, 0), "hull"));
        small.Load(large.ToDesign());
        Assert.True(small.IsLocked);
        Assert.Contains("21 x 21", small.LockedReason);
        Assert.False(small.Place(new GridPos(0, 0), "hull"));
        Assert.False(small.Remove(new GridPos(50, -50)));
        Assert.False(small.PlaceAll(new[] { new DesignCell(new GridPos(0, 0), "hull") }));
        Assert.Equal(0, small.ReplaceParts("hull", "battery"));
        small.Extent = BlueprintEditor.LargeHalfExtent;
        Assert.False(small.IsLocked);
        Assert.True(small.Place(new GridPos(0, 0), "hull"));
        small.New();
        Assert.False(small.IsLocked);
    }

    [Fact]
    public void Large_designs_save_load_and_fly()
    {
        var editor = Editor();
        editor.Draw(BlueprintTools.Rectangle(new GridPos(-30, -40), new GridPos(30, 40)), "hull", 0);
        editor.Place(new GridPos(0, -41), "cockpit");
        editor.Draw(BlueprintTools.Line(new GridPos(-30, 41), new GridPos(30, 41)), "thruster_mk3", 0);
        editor.Name = "Leviathan";
        Assert.Empty(editor.Errors);
        var again = ShipDesign.FromJson(editor.ToDesign().ToJson());
        Assert.Equal(editor.ToDesign().Cells, again.Cells);
        var game = TestData.NewGame();
        var ship = new CraftState(game.Sector.AllocateCraftId(), "L", again, again.ComputeStats(Catalog), new Vector2D(5e8, 5e8), 0);
        game.Sector.AddCraft(ship);
        var json = SaveGameSerializer.ToJson(game.Sector, game.PlayerCraftId);
        Assert.Equal(json, SaveGameSerializer.ToJson(SaveGameSerializer.FromJson(json, Catalog).Sector, game.PlayerCraftId));
    }

    // --- Shapes ---

    [Fact]
    public void Lines_rectangles_outlines_and_brushes()
    {
        Assert.Equal(Set((0, 0), (1, 0), (2, 1), (3, 1), (4, 2)).Count, BlueprintTools.Line(new GridPos(0, 0), new GridPos(4, 2)).Distinct().Count());
        Assert.Equal(new GridPos(3, 3), BlueprintTools.Line(new GridPos(3, 3), new GridPos(3, 3)).Single());
        var back = BlueprintTools.Line(new GridPos(-3, 5), new GridPos(4, -9));
        for (var i = 1; i < back.Count; i++) Assert.True(Math.Max(Math.Abs(back[i].X - back[i - 1].X), Math.Abs(back[i].Y - back[i - 1].Y)) == 1); // no gaps
        Assert.Equal(12, BlueprintTools.Rectangle(new GridPos(2, 3), new GridPos(-1, 1)).Count);
        Assert.Equal(10, BlueprintTools.Outline(new GridPos(2, 3), new GridPos(-1, 1)).Count);
        Assert.Equal(25, BlueprintTools.Brush(new GridPos(0, 0), 5).Count);
    }

    [Fact]
    public void Fill_takes_an_enclosed_space_or_a_region_of_one_kind()
    {
        var editor = Editor();
        editor.Draw(BlueprintTools.Outline(new GridPos(0, 0), new GridPos(4, 4)), "hull", 0);
        var inside = BlueprintTools.Region(editor.Cells, new GridPos(2, 2), editor.Extent)!;
        Assert.Equal(9, inside.Count);
        Assert.Null(BlueprintTools.Region(editor.Cells, new GridPos(20, 20), editor.Extent)); // open space
        Assert.Equal(16, BlueprintTools.Region(editor.Cells, new GridPos(0, 0), editor.Extent)!.Count); // the wall itself
        Assert.Null(BlueprintTools.Region(editor.Cells, new GridPos(99, 0), editor.Extent));
    }

    [Fact]
    public void Mirroring_draws_both_sides_with_sensible_facings_in_one_step()
    {
        var editor = new BlueprintEditor(Catalog) { MirrorLeftRight = true };
        editor.Draw(new[] { new GridPos(3, 2) }, "thruster", 1);
        Assert.Equal(1, editor.Cells[new GridPos(3, 2)].Rotation);
        Assert.Equal(3, editor.Cells[new GridPos(-3, 2)].Rotation); // pushes the other way
        editor.MirrorFrontBack = true;
        editor.Draw(new[] { new GridPos(5, 4) }, "thruster", 0);
        Assert.Equal(2, editor.Cells[new GridPos(5, -4)].Rotation);
        Assert.Equal(2, editor.Cells[new GridPos(-5, -4)].Rotation);
        Assert.Equal(6, editor.Cells.Count);
        editor.Draw(new[] { new GridPos(0, 0) }, "thruster", 1); // on both middles: just itself
        Assert.Equal(7, editor.Cells.Count);
        Assert.Equal(1, editor.Cells[new GridPos(0, 0)].Rotation);
        editor.Erase(new[] { new GridPos(5, 4) });
        Assert.Equal(3, editor.Cells.Count);
        editor.Undo();
        Assert.Equal(7, editor.Cells.Count);
        editor.Undo();
        editor.Undo();
        Assert.Equal(2, editor.Cells.Count);
    }

    // --- Selecting ---

    private static BlueprintEditor Block()
    {
        var editor = Editor();
        editor.Place(new GridPos(0, 0), "cockpit");
        editor.Place(new GridPos(1, 0), "thruster", 1);
        editor.Place(new GridPos(0, 1), "hull");
        editor.Selection.UnionWith(Set((0, 0), (1, 0), (0, 1)));
        return editor;
    }

    [Fact]
    public void A_selection_moves_replacing_what_it_lands_on_and_never_off_the_grid()
    {
        var editor = Block();
        editor.Place(new GridPos(2, 0), "battery");
        Assert.True(editor.MoveSelection(1, 0));
        Assert.Equal("thruster", editor.Cells[new GridPos(2, 0)].PartId);
        Assert.False(editor.Cells.ContainsKey(new GridPos(0, 0)));
        Assert.Equal(Set((1, 0), (2, 0), (1, 1)), editor.Selection);
        Assert.False(editor.MoveSelection(49, 0));
        Assert.Equal(3, editor.Cells.Count);
        editor.Undo();
        Assert.Equal("battery", editor.Cells[new GridPos(2, 0)].PartId);
        Assert.Equal("cockpit", editor.Cells[new GridPos(0, 0)].PartId);
        editor.Redo();
        Assert.Equal("cockpit", editor.Cells[new GridPos(1, 0)].PartId);
    }

    [Fact]
    public void Four_turns_and_two_mirrors_give_the_same_again()
    {
        var editor = Block();
        var start = editor.ToDesign().Cells.ToList();
        Assert.True(editor.ReshapeSelection(BlueprintTools.Turned));
        Assert.Equal(2, editor.Cells.Values.Single(c => c.PartId == "thruster").Rotation); // turned with it
        for (var i = 0; i < 3; i++) editor.ReshapeSelection(BlueprintTools.Turned);
        Assert.Equal(start, editor.ToDesign().Cells);
        foreach (var side in new[] { true, false })
        {
            editor.ReshapeSelection(c => BlueprintTools.Flipped(c, side));
            editor.ReshapeSelection(c => BlueprintTools.Flipped(c, side));
            Assert.Equal(start, editor.ToDesign().Cells);
        }
    }

    [Fact]
    public void Copies_paste_whole_or_not_at_all_and_deleting_is_one_step()
    {
        var editor = Block();
        var copy = BlueprintTools.ToCorner(editor.Selected);
        Assert.Equal(new GridPos(0, 0), copy[0].Pos);
        Assert.True(editor.PlaceAll(copy.Select(c => c with { Pos = new GridPos(c.Pos.X + 10, c.Pos.Y + 10) }).ToList()));
        Assert.Equal(6, editor.Cells.Count);
        Assert.False(editor.PlaceAll(copy.Select(c => c with { Pos = new GridPos(c.Pos.X + 50, c.Pos.Y) }).ToList()));
        Assert.Equal(6, editor.Cells.Count);
        editor.ReplaceSelection(Array.Empty<DesignCell>());
        Assert.Equal(3, editor.Cells.Count);
        Assert.Empty(editor.Selection);
        editor.Undo();
        Assert.Equal(6, editor.Cells.Count);
    }

    [Fact]
    public void Replacing_keeps_facings_and_minds_the_selection()
    {
        var editor = Editor();
        for (var i = 0; i < 4; i++) editor.Place(new GridPos(i, 0), "thruster", i);
        editor.Selection.UnionWith(Set((0, 0), (1, 0)));
        Assert.Equal(2, editor.ReplaceParts("thruster", "thruster_mk2"));
        editor.Selection.Clear();
        Assert.Equal(2, editor.ReplaceParts("thruster", "thruster_mk2"));
        for (var i = 0; i < 4; i++) Assert.Equal(new DesignCell(new GridPos(i, 0), "thruster_mk2", i), editor.Cells[new GridPos(i, 0)]);
    }

    [Fact]
    public void Redo_puts_back_what_was_undone_until_something_else_is_done()
    {
        var editor = new BlueprintEditor(Catalog);
        editor.Place(new GridPos(0, 0), "hull");
        editor.Place(new GridPos(0, 0), "battery");
        editor.Place(new GridPos(1, 0), "hull");
        for (var i = 0; i < 3; i++) Assert.True(editor.Undo());
        Assert.Empty(editor.Cells);
        Assert.True(editor.Redo());
        Assert.True(editor.Redo());
        Assert.Equal("battery", editor.Cells[new GridPos(0, 0)].PartId);
        Assert.True(editor.Undo());
        Assert.Equal("hull", editor.Cells[new GridPos(0, 0)].PartId);
        editor.Place(new GridPos(5, 5), "hull");
        Assert.False(editor.CanRedo);
        Assert.False(editor.Redo());
    }

    [Fact]
    public void A_preview_says_the_mass_and_balance_a_change_gives()
    {
        var editor = Editor();
        editor.Place(new GridPos(0, 0), "hull");
        editor.Place(new GridPos(4, 0), "hull");
        var placed = new[] { new DesignCell(new GridPos(0, 0), "battery"), new DesignCell(new GridPos(0, 2), "hull") };
        var removed = new[] { new GridPos(4, 0) };
        var preview = editor.Preview(placed, removed);
        editor.PlaceAll(placed);
        editor.Remove(removed[0]);
        Assert.Equal(editor.Stats!.TotalMassKg, preview.MassKg);
        Assert.Equal(editor.Stats.CenterOfMassX, preview.ComX, 9);
        Assert.Equal(editor.Stats.CenterOfMassY, preview.ComY, 9);
        Assert.Equal(preview.MassKg - 2_000, preview.DeltaKg);
        Assert.Equal("; 1 t Metal, 0.9 t Electronics, 0.4 t Composites", editor.CostText(placed));
    }
}
