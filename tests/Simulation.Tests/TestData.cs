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

    public static RecipeCatalog Recipes() => RecipeCatalog.FromJson(Read("data/recipes/recipes.json"), Materials());

    public static PartCatalog Catalog() => PartCatalog.FromJson(Read("data/parts/parts.json"), Materials(), Recipes());

    public static ShipDesign StarterShip() => ShipDesign.FromJson(Read("data/blueprints/starter_ship.json"));

    public static ShipDesign StarterShipyard() => ShipDesign.FromJson(Read("data/blueprints/starter_shipyard.json"));

    public static ShipDesign SurfaceDepot() => ShipDesign.FromJson(Read("data/blueprints/surface_depot.json"));

    /// <summary>Test setup shortcut: puts a craft down on a body's surface (top side), undocked and off rails.</summary>
    public static void LandAt(SectorState sector, CraftState craft, CelestialBodyState body)
    {
        if (craft.IsDocked) sector.TryUndock(craft.Id);
        craft.RailOrbit = null;
        var offset = new Vector2D(0, -(body.RadiusM + craft.CollisionRadiusM + 1));
        craft.Position = craft.PreviousPosition = sector.GetPosition(body.Id) + offset;
        craft.HeadingRad = craft.PreviousHeadingRad = 0;
        craft.Velocity = sector.GetVelocityAt(body.Id, sector.TimeS);
        sector.RestoreLanding(craft.Id, body.Id, offset);
    }

    /// <summary>The same starting state the game creates; by default with every body already surveyed.</summary>
    public static NewGameResult NewGame(ulong seed = Simulation.NewGame.DefaultSeed, bool surveyAll = true)
    {
        var game = Simulation.NewGame.Create(seed, Catalog(), StarterShip(), StarterShipyard());
        if (surveyAll) foreach (var body in game.Sector.Bodies) game.Sector.MarkSurveyed(body.Id);
        return game;
    }
}
