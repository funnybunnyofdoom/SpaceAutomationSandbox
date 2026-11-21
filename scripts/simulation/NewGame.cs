using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>What a new game contains, by id.</summary>
public sealed record NewGameResult(SectorState Sector, CraftId PlayerCraftId, CraftId ShipyardId);

/// <summary>
/// Builds the starting state: the seeded sector, the player's ship near the primary, and the
/// starter shipyard on a fixed orbit around the primary with a starting stock of materials.
/// Pure C#, so tests and the game start from exactly the same state. All numbers are placeholders.
/// </summary>
public static class NewGame
{
    public const ulong DefaultSeed = 20261120;

    /// <summary>Shipyard orbit height above the primary's surface, and its period.</summary>
    public const double ShipyardAltitudeM = 600; // 300 m above the player's spawn, so it's in view at the start
    public const double ShipyardOrbitPeriodS = 1_800;

    /// <summary>Phase lead over the player's spawn point, so the yard starts in view nearby.</summary>
    public const double ShipyardPhaseLeadRad = 0.04;

    /// <summary>Enough for two starter ships but not three (electronics runs out).</summary>
    public static readonly IReadOnlyDictionary<string, long> StartingStockKg = new SortedDictionary<string, long>(StringComparer.Ordinal)
    {
        ["metal"] = 30_000,
        ["electronics"] = 7_000,
        ["composites"] = 5_000,
        ["slag"] = 5_000, // hull plates contain slag now (#7)
    };

    public static NewGameResult Create(ulong seed, PartCatalog catalog, ShipDesign starterShip, ShipDesign starterShipyard)
    {
        Require(starterShip.Validate(catalog, DesignRole.Ship), starterShip);
        Require(starterShipyard.Validate(catalog, DesignRole.Station), starterShipyard);

        var sector = SectorGenerator.Generate(seed);
        sector.Recipes = catalog.Recipes;
        sector.Parts = catalog;
        var primary = sector.Bodies.First(b => b.Orbit is null);

        var player = new CraftState(sector.AllocateCraftId(), "Player", starterShip, starterShip.ComputeStats(catalog),
            SectorGenerator.PlayerSpawnPoint(sector), headingRad: 0);
        sector.AddCraft(player);
        sector.SetTarget(player.Id, primary.Id);
        sector.PilotCraftId = player.Id;

        var yard = new CraftState(sector.AllocateCraftId(), "Shipyard", starterShipyard, starterShipyard.ComputeStats(catalog),
            Vector2D.Zero, headingRad: 0);
        sector.AddCraft(yard);
        // Spawn point is straight "up" (−Y) from the primary, i.e. angle −π/2 on its orbit circle.
        sector.PutOnRails(yard.Id, new CircularOrbit(primary.Id, primary.RadiusM + ShipyardAltitudeM, ShipyardOrbitPeriodS,
            -Math.PI / 2 + ShipyardPhaseLeadRad));
        foreach (var (material, kg) in StartingStockKg)
        {
            if (!catalog.Materials.Contains(material)) throw new InvalidOperationException($"Starting stock uses unknown material '{material}'.");
            yard.Inventory.Add(material, kg);
        }

        return new NewGameResult(sector, player.Id, yard.Id);
    }

    private static void Require(IReadOnlyList<string> errors, ShipDesign design)
    {
        if (errors.Count > 0) throw new InvalidOperationException($"Starter design '{design.Id}' is invalid: {string.Join("; ", errors)}");
    }
}
