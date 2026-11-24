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

    public static PartCatalog Catalog() =>
        PartCatalog.FromJson(Read("data/parts/parts.json"), Materials(), Recipes()).WithResearch(Research());

    public static ResearchCatalog Research() => ResearchCatalog.FromJson(Read("data/research/research.json"));

    public static ShipDesign StarterShip() => ShipDesign.FromJson(Read("data/blueprints/starter_ship.json"));

    public static ShipDesign StarterShipyard() => ShipDesign.FromJson(Read("data/blueprints/starter_shipyard.json"));

    /// <summary>Mineable bodies around the home planet (the rocks, the Moon and Ice-1), in id order.</summary>
    /// <summary>
    /// The angle around Home (from its centre) of a point this far above its surface that's in full sunlight (#76):
    /// the side facing the star, turned a little either way if a moon or rock happens to be in the way.
    /// </summary>
    public static double SunnyAngle(SectorState sector, double altitudeM)
    {
        var home = SectorGenerator.HomePlanet(sector);
        var centre = sector.GetPosition(home.Id);
        var toStar = sector.GetPosition(sector.Stars[0].Id) - centre;
        var facing = System.Math.Atan2(toStar.Y, toStar.X);
        for (var step = 0; step < 60; step++)
        {
            var angle = facing + (step % 2 == 0 ? 1 : -1) * (step + 1) / 2 * 0.05;
            bool Sunny(double a) => !sector.InShadowAt(centre + new Vector2D(System.Math.Cos(a), System.Math.Sin(a)) * (home.RadiusM + altitudeM));
            if (Sunny(angle) && Sunny(angle - 0.3) && Sunny(angle + 0.3)) return angle; // a margin either way: the craft moves
        }
        return facing;
    }

    /// <summary>A point above Home, this far above its surface, in full sunlight (#76).</summary>
    public static Vector2D SunnySideOfHome(SectorState sector, double altitudeM)
    {
        var home = SectorGenerator.HomePlanet(sector);
        var angle = SunnyAngle(sector, altitudeM);
        return sector.GetPosition(home.Id) + new Vector2D(System.Math.Cos(angle), System.Math.Sin(angle)) * (home.RadiusM + altitudeM);
    }

    /// <summary>The phase (at time zero) of a circular orbit around Home that's in full sunlight now (#76).</summary>
    public static double SunnyPhase(SectorState sector, double altitudeM, double periodS) =>
        SunnyAngle(sector, altitudeM) - 2 * System.Math.PI * sector.TimeS / periodS;

    public static System.Collections.Generic.List<CelestialBodyState> HomeRocks(SectorState sector)
    {
        var home = SectorGenerator.HomePlanet(sector).Id;
        return System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(sector.Bodies, b => b.IsMineable && b.Orbit?.Parent == home));
    }

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
    /// <remarks>
    /// The player's starting cruise fuel is taken out of its hold unless <paramref name="keepPlayerFuel"/>
    /// (as mass, so the ledger still balances), so tests about cargo and mining see an empty hold.
    /// </remarks>
    public static NewGameResult NewGame(ulong seed = Simulation.NewGame.DefaultSeed, bool surveyAll = true, bool keepPlayerFuel = false)
    {
        var game = Simulation.NewGame.Create(seed, Catalog(), StarterShip(), StarterShipyard());
        if (surveyAll) foreach (var body in game.Sector.Bodies) game.Sector.MarkSurveyed(body.Id);
        if (!keepPlayerFuel)
            game.Sector.GetCraft(game.PlayerCraftId).Inventory.TryRemove(
                new System.Collections.Generic.Dictionary<string, long> { [SectorState.FuelMaterial] = Simulation.NewGame.PlayerStartingFuelKg });
        return game;
    }
}
