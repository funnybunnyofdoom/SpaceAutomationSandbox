using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

/// <summary>
/// Huge craft (#114): what craft of 100 to 10,000 parts cost. Each line is the mean (and worst) time of one simulation tick, or of one
/// operation. Run: dotnet run -c Release --project tools/Benchmarks -- --huge
/// </summary>
public static class HugeCraft
{
    /// <summary>A ship of about this many parts: a solid, slightly long block, like a big freighter.</summary>
    public static ShipDesign Ship(int parts)
    {
        var width = Math.Max(3, (int)Math.Round(Math.Sqrt(parts / 1.6)));
        var cells = new List<DesignCell>();
        for (var i = 0; i < parts; i++)
        {
            var (x, y) = (i % width, i / width);
            var last = i >= parts - width;
            var id = i == width / 2 ? "cockpit" : last && x % 2 == 0 ? "thruster_mk3" : last ? "hull"
                : (x + 3 * y) % 11 == 0 ? "battery_mk2" : (x + 3 * y) % 7 == 0 ? "solar_panel_mk3" : (x + 2 * y) % 5 == 0 ? "cargo" : i % 97 == 0 ? "docking_port" : "hull";
            cells.Add(new DesignCell(new GridPos(x - width / 2, y), id));
        }
        return new ShipDesign($"huge_{parts}", $"Huge {parts}", cells);
    }

    public static void Run(PartCatalog catalog, ShipDesign starter, ShipDesign yardDesign, int[] sizes)
    {
        Console.WriteLine($"Huge craft, {DateTime.Now:yyyy-MM-dd}, .NET {Environment.Version}, {Environment.ProcessorCount} logical CPUs. ms, mean (worst).");
        Console.WriteLine();
        Console.WriteLine("| What | " + string.Join(" | ", sizes.Select(s => $"{s:N0} parts")) + " |");
        Console.WriteLine("| --- | " + string.Join(" | ", sizes.Select(_ => "---:")) + " |");
        foreach (var (name, measure) in Cases(catalog, starter, yardDesign))
            Console.WriteLine($"| {name} | " + string.Join(" | ", sizes.Select(s =>
            {
                try { return measure(s); }
                catch (Exception e) { return e.GetType().Name; }
            })) + " |");
    }

    private static string Time(int warm, int count, Action action, int most = 4_000)
    {
        for (var i = 0; i < warm; i++) action();
        var samples = new List<double>();
        var all = Stopwatch.StartNew();
        for (var i = 0; i < count && (all.ElapsedMilliseconds < most || i == 0); i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        return $"{samples.Average():0.###} ({samples.Max():0.##})";
    }

    private static IEnumerable<(string, Func<int, string>)> Cases(PartCatalog catalog, ShipDesign starter, ShipDesign yardDesign)
    {
        SectorState New() => NewGame.Create(NewGame.DefaultSeed, catalog, starter, yardDesign).Sector;

        CraftState Add(SectorState sector, ShipDesign design, Vector2D at, Vector2D velocity)
        {
            var craft = new CraftState(sector.AllocateCraftId(), design.Name, design, design.ComputeStats(catalog), at, 0) { Velocity = velocity, LegacyNoCrew = true, LegacyFreePower = true };
            sector.AddCraft(craft);
            return craft;
        }

        // Out in space, far from everything.
        (SectorState, Vector2D, Vector2D) Out()
        {
            var sector = New();
            var home = SectorGenerator.HomePlanet(sector);
            return (sector, sector.GetPosition(home.Id) + new Vector2D(90_000, 40_000), sector.GetVelocityAt(home.Id, sector.TimeS));
        }

        yield return ("design and stats", n => Time(2, 50, () => Ship(n).ComputeStats(catalog)));
        yield return ("stats only", n => { var d = Ship(n); return Time(2, 50, () => d.ComputeStats(catalog)); });
        yield return ("tick: one flying", n =>
        {
            var (sector, at, v) = Out();
            var ship = Add(sector, Ship(n), at, v);
            ship.Controls = new ShipControls(1, 0.2, false);
            return Time(40, 400, sector.Step);
        });
        yield return ("tick: ten flying apart", n =>
        {
            var (sector, at, v) = Out();
            for (var i = 0; i < 10; i++) Add(sector, Ship(n), at + new Vector2D(i * 3_000, 0), v).Controls = new ShipControls(1, 0.2, false);
            return Time(40, 400, sector.Step);
        });
        yield return ("tick: one landed, one coming down", n =>
        {
            var sector = New();
            var home = SectorGenerator.HomePlanet(sector);
            var design = Ship(n);
            var r = design.ComputeStats(catalog).BoundingRadiusM;
            var centre = sector.GetPosition(home.Id);
            Add(sector, design, centre + new Vector2D(home.RadiusM + 0.9 * r, 0), sector.GetVelocityAt(home.Id, sector.TimeS));
            Add(sector, design, centre + new Vector2D(0, home.RadiusM + 1.2 * r), sector.GetVelocityAt(home.Id, sector.TimeS));
            return Time(0, 400, sector.Step);
        });
        yield return ("tick: two side by side, circles overlapping", n =>
        {
            var (sector, at, v) = Out();
            var design = Ship(n);
            var width = design.Cells.Max(c => c.Pos.X) - design.Cells.Min(c => c.Pos.X) + 1;
            Add(sector, design, at, v);
            Add(sector, design, at + new Vector2D((width + 3) * ShipDesign.CellSizeM, 0), v);
            return Time(5, 200, sector.Step);
        });
        yield return ("tick: two in contact", n =>
        {
            var (sector, at, v) = Out();
            var design = Ship(n);
            var width = design.Cells.Max(c => c.Pos.X) - design.Cells.Min(c => c.Pos.X) + 1;
            Add(sector, design, at, v);
            Add(sector, design, at + new Vector2D((width + 1) * ShipDesign.CellSizeM, 0), v + new Vector2D(-1, 0));
            return Time(5, 200, sector.Step);
        });
        yield return ("tick: 50 small ships close to it", n =>
        {
            var (sector, at, v) = Out();
            var design = Ship(n);
            var r = design.ComputeStats(catalog).BoundingRadiusM;
            Add(sector, design, at, v);
            for (var i = 0; i < 50; i++)
            {
                var a = Math.Tau * i / 50;
                Add(sector, starter, at + new Vector2D(Math.Cos(a), Math.Sin(a)) * (r * 1.02), v);
            }
            return Time(5, 200, sector.Step);
        });
        yield return ("tick: 20 small ships docked or docking", n =>
        {
            var (sector, at, v) = Out();
            var big = Add(sector, Ship(n), at, v);
            for (var i = 0; i < 20; i++)
            {
                var small = Add(sector, starter, at, v);
                var port = i % Math.Max(1, big.Stats.DockingPorts.Count);
                if (big.Stats.DockingPorts.Count == 0) break;
                small.Position = sector.BerthPosition(big, port, small.CollisionRadiusM, at);
                sector.SetTargetCraft(small.Id, big.Id);
                sector.TryDock(small.Id);
            }
            return Time(5, 200, sector.Step);
        });
        yield return ("tick: under fire (20 hits a tick)", n =>
        {
            var (sector, at, v) = Out();
            var big = Add(sector, Ship(n), at, v);
            var i = 0;
            return Time(0, 100, () =>
            {
                for (var h = 0; h < 20; h++) sector.DebugHit(big.Id, (i++ * 7919) % big.Design.Cells.Count, 0.05);
                sector.Step();
            });
        });
        yield return ("tick: losing 5 parts a tick", n =>
        {
            var (sector, at, v) = Out();
            var big = Add(sector, Ship(n), at, v);
            return Time(0, 15, () =>
            {
                for (var h = 0; h < 5; h++) sector.DebugHit(big.Id, big.Design.Cells.Count - 1 - h, 50);
                sector.Step();
            });
        });
        yield return ("tick: 30 shots crossing it", n =>
        {
            var (sector, at, v) = Out();
            var big = Add(sector, Ship(n), at, v);
            return Time(0, 100, () =>
            {
                for (var s = 0; s < 30; s++) sector.DebugShoot(big.Position + new Vector2D(-0.72, -0.68) * big.CollisionRadiusM, v + new Vector2D(1, 0), 0.5);
                sector.Step();
            });
        });
        yield return ("editor: one part placed, all shown", n =>
        {
            var editor = new BlueprintEditor(catalog);
            editor.Load(Ship(n));
            var i = 0;
            return Time(2, 50, () =>
            {
                editor.Place(new GridPos(0, 1), i++ % 2 == 0 ? "battery" : "hull");
                _ = editor.Stats;
                _ = editor.Errors;
                _ = editor.BillOfMaterials;
                _ = editor.ToDesign().ProblemCells(catalog, editor.Role);
            });
        });
        yield return ("editor: undo steps kept, MB", n =>
        {
            var editor = new BlueprintEditor(catalog);
            editor.Load(Ship(n));
            var before = GC.GetTotalMemory(true);
            for (var i = 0; i < 200; i++) editor.Place(new GridPos(0, 1), i % 2 == 0 ? "battery" : "hull");
            var after = GC.GetTotalMemory(true);
            GC.KeepAlive(editor);
            return $"{(after - before) / 1e6:0.#}";
        });
    }
}
