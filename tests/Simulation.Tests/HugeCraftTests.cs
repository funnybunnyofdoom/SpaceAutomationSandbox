using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Save;
using Xunit;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>The shortcuts for huge craft give exactly what trying every part gives (#114).</summary>
public sealed class HugeCraftTests
{
    private static readonly PartCatalog Catalog = TestData.Catalog();

    /// <summary>A ragged, connected blob: blocks, spikes and holes.</summary>
    private static ShipDesign Blob(int seed, int parts, string extra = "hull")
    {
        var rng = new Random(seed);
        var cells = new Dictionary<GridPos, DesignCell> { [new GridPos(0, 0)] = new(new GridPos(0, 0), "cockpit") };
        var list = new List<GridPos> { new(0, 0) };
        while (cells.Count < parts)
        {
            var from = list[rng.Next(list.Count)];
            var to = rng.Next(4) switch { 0 => new GridPos(from.X + 1, from.Y), 1 => new GridPos(from.X - 1, from.Y), 2 => new GridPos(from.X, from.Y + 1), _ => new GridPos(from.X, from.Y - 1) };
            if (cells.ContainsKey(to)) continue;
            cells[to] = new DesignCell(to, rng.Next(6) switch { 0 => "thruster", 1 => "battery", 2 => extra, _ => "hull" });
            list.Add(to);
        }
        return new ShipDesign($"blob{seed}", "Blob", cells.Values);
    }

    private static CraftState Add(SectorState sector, ShipDesign design, Vector2D at, Vector2D velocity, double heading)
    {
        var craft = new CraftState(sector.AllocateCraftId(), design.Name, design, design.ComputeStats(Catalog), at, heading) { Velocity = velocity };
        sector.AddCraft(craft);
        return craft;
    }

    [Fact]
    public void The_gap_to_a_surface_is_the_same_as_over_every_cell()
    {
        var rng = new Random(1);
        for (var n = 0; n < 40; n++)
        {
            var sector = TestData.NewGame().Sector;
            var craft = Add(sector, Blob(n, 80 + 20 * n), Vector2D.Zero, Vector2D.Zero, 0);
            for (var i = 0; i < 20; i++)
            {
                var heading = rng.NextDouble() * Math.Tau;
                var position = new Vector2D(rng.NextDouble() * 1e5, rng.NextDouble() * 1e5);
                var centre = position + new Vector2D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * (craft.CollisionRadiusM * 20 + 4_000);
                var brute = double.PositiveInfinity;
                foreach (var (_, p) in SectorState.CellPositions(craft, position, heading)) brute = Math.Min(brute, p.DistanceTo(centre) - 500 - SectorState.CellContactRadiusM);
                Assert.Equal(brute, SectorState.SurfaceGap(craft, position, heading, centre, 500)); // bit for bit
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Bumping_shooting_and_breaking_up_go_exactly_as_with_every_cell(int seed)
    {
        string Play(bool everyCell)
        {
            var sector = TestData.NewGame().Sector;
            sector.CheckEveryCell = everyCell;
            var rng = new Random(seed);
            var home = SectorGenerator.HomePlanet(sector);
            var at = sector.GetPosition(home.Id) + new Vector2D(90_000, 40_000);
            var v = sector.GetVelocityAt(home.Id, sector.TimeS);
            var big = Add(sector, Blob(seed, 400), at, v, 0.3);
            var other = Add(sector, Blob(seed + 50, 250), at + new Vector2D(big.CollisionRadiusM * 1.3, 10), v + new Vector2D(-14, 1), 1.7);
            for (var i = 0; i < 8; i++)
                Add(sector, TestData.StarterShip(), at + new Vector2D(Math.Cos(i), Math.Sin(i)) * (big.CollisionRadiusM * 1.1), v - new Vector2D(Math.Cos(i), Math.Sin(i)) * (8 + i), rng.NextDouble() * 6);
            for (var tick = 0; tick < 300; tick++)
            {
                if (tick % 5 == 0)
                {
                    var angle = rng.NextDouble() * Math.Tau;
                    var from = big.Position + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (big.CollisionRadiusM + 60);
                    sector.DebugShoot(from, v - new Vector2D(Math.Cos(angle + 0.2), Math.Sin(angle + 0.2)) * 600, 1, 0.8);
                }
                if (tick % 11 == 0) sector.DebugHit(other.Id, rng.Next(other.Design.Cells.Count), 0.4);
                sector.Step();
            }
            Assert.True(big.PartsLost + big.PartHealth.Count > 0, "nothing hit it"); // the check means something
            return SaveGameSerializer.ToJson(sector, CraftId.None);
        }

        Assert.Equal(Play(true), Play(false));
    }

    [Fact]
    public void Stats_catch_up_with_damage_when_read()
    {
        var sector = TestData.NewGame().Sector;
        var craft = Add(sector, Blob(7, 150, "drill"), new Vector2D(5e8, 5e8), Vector2D.Zero, 0);
        var drill = Enumerable.Range(0, craft.Design.Cells.Count).First(i => craft.Design.Cells[i].PartId == "drill");
        var before = craft.Stats.MiningRateKgPerS;
        sector.DebugHit(craft.Id, drill, 0.5);
        sector.DebugHit(craft.Id, drill, 0.25);
        Assert.Equal(craft.Design.ComputeStats(Catalog, craft.PartHealth).MiningRateKgPerS, craft.Stats.MiningRateKgPerS);
        Assert.True(craft.Stats.MiningRateKgPerS < before);
    }

    [Fact]
    public void Keeping_some_cells_finding_them_and_loose_ones_match_the_plain_way()
    {
        var design = Blob(9, 300);
        var kept = design.Keep(c => (c.Pos.X + c.Pos.Y) % 5 != 0);
        Assert.Equal(new ShipDesign("x", "x", design.Cells.Where(c => (c.Pos.X + c.Pos.Y) % 5 != 0)).Cells, kept.Cells);
        for (var i = 0; i < design.Cells.Count; i++) Assert.Equal(i, design.IndexOf(design.Cells[i].Pos));
        Assert.Equal(-1, design.IndexOf(new GridPos(999, 999)));
        Assert.Empty(design.LooseCells());
        var split = new ShipDesign("s", "s", new[] { (0, 0), (1, 0), (5, 5), (5, 6), (6, 6), (9, 0) }.Select(p => new DesignCell(new GridPos(p.Item1, p.Item2), "hull")));
        Assert.Equal(new[] { new GridPos(0, 0), new GridPos(1, 0), new GridPos(9, 0) }, split.LooseCells());
        Assert.Equal(design.Cells.Sum(c => Catalog.Get(c.PartId).MassKg), design.ComputeBillOfMaterials(Catalog).TotalKg);
    }

    [Fact]
    public void Undo_puts_back_what_each_step_changed()
    {
        var editor = new BlueprintEditor(Catalog);
        editor.Load(TestData.StarterShip());
        var start = editor.ToDesign().Cells.ToList();
        editor.Place(new GridPos(8, 8), "hull");
        editor.BeginStroke();
        editor.Place(new GridPos(8, 8), "battery");
        editor.Place(new GridPos(8, 8), "cargo");
        editor.Remove(new GridPos(8, 8));
        editor.Place(new GridPos(9, 8), "hull");
        editor.EndStroke();
        Assert.True(editor.Undo());
        Assert.Equal("hull", editor.Cells[new GridPos(8, 8)].PartId);
        Assert.False(editor.Cells.ContainsKey(new GridPos(9, 8)));
        Assert.True(editor.Undo());
        Assert.Equal(start, editor.ToDesign().Cells);
        Assert.False(editor.Undo());
    }

    [Fact]
    public void A_huge_ship_still_stops_a_small_one()
    {
        var sector = TestData.NewGame().Sector;
        var home = SectorGenerator.HomePlanet(sector);
        var at = sector.GetPosition(home.Id) + new Vector2D(90_000, 40_000);
        var v = sector.GetVelocityAt(home.Id, sector.TimeS);
        var big = Add(sector, new ShipDesign("b", "b", Enumerable.Range(0, 1_600).Select(i => new DesignCell(new GridPos(i % 40, i / 40), i == 0 ? "cockpit" : "hull"))), at, v, 0);
        Assert.True(big.CollisionRadiusM > 64); // too big for the grid
        var small = Add(sector, TestData.StarterShip(), at + new Vector2D(big.CollisionRadiusM + 60, 0), v + new Vector2D(-20, 0), 0);
        for (var i = 0; i < 200; i++) sector.Step(); // 10 s: 200 m, into the hull
        Assert.True(small.Position.X > big.Position.X + 60, "it flew through");
        Assert.True(Math.Abs(small.Velocity.X - big.Velocity.X) < 5);
    }
}
