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

    public static PartCatalog Catalog() => PartCatalog.FromJson(Read("data/parts/parts.json"));

    public static ShipDesign StarterShip() => ShipDesign.FromJson(Read("data/blueprints/starter_ship.json"));
}
