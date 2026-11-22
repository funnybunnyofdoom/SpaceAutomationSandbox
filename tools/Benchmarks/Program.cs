using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SpaceAutomationSandbox.Construction;
using SpaceAutomationSandbox.Simulation;

// Simulation benchmark (#20): N automated miners spread over the three asteroids, all delivering to
// the starter shipyard, in the default starter sector. Reports setup time and ms per simulation tick
// (the game runs 20 ticks per second, so the budget is 50 ms per tick). Prints a Markdown table.

var root = FindRepoRoot();
var materials = MaterialCatalog.FromJson(File.ReadAllText(Path.Combine(root, "data/materials/materials.json")));
var recipes = RecipeCatalog.FromJson(File.ReadAllText(Path.Combine(root, "data/recipes/recipes.json")), materials);
var catalog = PartCatalog.FromJson(File.ReadAllText(Path.Combine(root, "data/parts/parts.json")), materials, recipes);
var ship = ShipDesign.FromJson(File.ReadAllText(Path.Combine(root, "data/blueprints/starter_ship.json")));
var yardDesign = ShipDesign.FromJson(File.ReadAllText(Path.Combine(root, "data/blueprints/starter_shipyard.json")));

var counts = args.Length > 0 ? args.Select(int.Parse).ToArray() : new[] { 10, 100, 1_000, 10_000 };
Console.WriteLine($"Simulation benchmark, {DateTime.Now:yyyy-MM-dd}, .NET {Environment.Version}, {Environment.ProcessorCount} logical CPUs");
Console.WriteLine();
Console.WriteLine("| Automated ships | Setup | Ticks timed | ms per tick (mean) | ms per tick (worst 1%) | % of 50 ms budget |");
Console.WriteLine("| ---: | ---: | ---: | ---: | ---: | ---: |");

foreach (var count in counts)
{
    var setup = Stopwatch.StartNew();
    var game = NewGame.Create(NewGame.DefaultSeed, catalog, ship, yardDesign);
    var sector = game.Sector;
    sector.DebugSurveyAll();
    var yard = sector.GetCraft(game.ShipyardId);
    // The home neighbourhood (same scenario as before the star system, #53): far planets would turn it into a travel benchmark.
    var home = SectorGenerator.HomePlanet(sector).Id;
    var rocks = sector.Bodies.Where(b => b.IsMineable && b.Orbit?.Parent == home).ToList();
    var stats = ship.ComputeStats(catalog);
    for (var i = 0; i < count; i++)
    {
        var craft = new CraftState(sector.AllocateCraftId(), $"M{i}", ship, stats,
            yard.Position + new Vector2D(200 + 30 * (i % 100), -300 - 30 * (i / 100)), 0) { Velocity = yard.Velocity };
        sector.AddCraft(craft);
        var rock = rocks[i % rocks.Count];
        sector.StartJob(craft.Id, new MiningJob(rock.Id, rock.DepositsKg.Keys.First(), yard.Id, 0.25));
    }
    setup.Stop();

    // Warm up (JIT, first departures), then time a fixed number of ticks. Fewer ticks at large counts.
    for (var i = 0; i < 40; i++) sector.Step();
    var ticks = count >= 10_000 ? 100 : count >= 1_000 ? 600 : 2_400;
    var samples = new double[ticks];
    var watch = new Stopwatch();
    for (var i = 0; i < ticks; i++)
    {
        watch.Restart();
        sector.Step();
        samples[i] = watch.Elapsed.TotalMilliseconds;
    }
    var mean = samples.Average();
    var worst = samples.OrderByDescending(s => s).Take(Math.Max(1, ticks / 100)).Average();
    Console.WriteLine($"| {count:N0} | {setup.Elapsed.TotalSeconds:0.00} s | {ticks:N0} | {mean:0.000} | {worst:0.000} | {mean / 50 * 100:0.0}% |");
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("project.godot not found");
}
