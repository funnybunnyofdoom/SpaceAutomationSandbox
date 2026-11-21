using System;
using System.IO;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation.Tests;

/// <summary>Loads the real game data files, so tests catch broken data as well as broken code.</summary>
internal static class TestData
{
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("project.godot not found above test output.");
    });

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot.Value, relativePath));

    public static MaterialCatalog Materials() => MaterialCatalog.FromJson(Read("data/materials/materials.json"));

    public static PartCatalog Catalog() => PartCatalog.FromJson(Read("data/parts/parts.json"), Materials());

    public static ShipDesign StarterShip() => ShipDesign.FromJson(Read("data/blueprints/starter_ship.json"));

    public static ShipDesign StarterShipyard() => ShipDesign.FromJson(Read("data/blueprints/starter_shipyard.json"));

    /// <summary>The same starting state the game creates.</summary>
    public static NewGameResult NewGame(ulong seed = Simulation.NewGame.DefaultSeed) =>
        Simulation.NewGame.Create(seed, Catalog(), StarterShip(), StarterShipyard());
}
