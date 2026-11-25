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

    /// <summary>
    /// The seed for what the player typed (#62): a whole number is used as it is; any other text (a
    /// word or phrase, to share a world easily) becomes a number by a fixed hash (FNV-1a, 64-bit), so
    /// the same text always gives the same world. Leading and trailing spaces are ignored.
    /// </summary>
    public static ulong SeedFromText(string text)
    {
        text = text.Trim();
        if (ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)) return number;
        var hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    /// <summary>Fuel in the player's hold at the start: about 10 minutes of cruise for the starter ship.</summary>
    public const long PlayerStartingFuelKg = 150;

    /// <summary>People at the home shipyard at the start (#11). Its habitat holds a few more, so they can grow.</summary>
    public const int StartingCrew = 4; // Human

    /// <summary>Shipyard orbit height above the home planet's surface. Its period is a real circular orbit there (#32).</summary>
    public const double ShipyardAltitudeM = 600; // 300 m above the player's spawn, so it's in view at the start

    /// <summary>Period used for rails where there is no gravity to set one.</summary>
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
        ["fuel"] = 5_000, // for the cruise drive (#53)
        // Food and water for the yard's crew depend on the starting supplies chosen (see SuppliesKg).
    };

    /// <summary>
    /// The yard's starting food and water (creator request, 2026-11-23): chosen on the new-game screen. Its 4 crew
    /// eat 3 kg and drink 6 kg each an hour, so Lean lasts about 3.5 days, Standard about 17 and Generous about 52.
    /// </summary>
    public static (long FoodKg, long WaterKg) SuppliesKg(StartingSupplies supplies) => supplies switch
    {
        StartingSupplies.Lean => (1_000, 2_000),
        StartingSupplies.Generous => (15_000, 30_000),
        _ => (5_000, 10_000),
    };

    /// <summary>Everything the starting yard holds with these supplies: <see cref="StartingStockKg"/> plus food and water.</summary>
    public static IReadOnlyDictionary<string, long> StartingYardStockKg(StartingSupplies supplies = StartingSupplies.Standard)
    {
        var (food, water) = SuppliesKg(supplies);
        var all = new SortedDictionary<string, long>(StartingStockKg.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal)
        {
            [SectorState.FoodMaterial] = food,
            [SectorState.WaterMaterial] = water,
        };
        return all;
    }

    public static NewGameResult Create(ulong seed, PartCatalog catalog, ShipDesign starterShip, ShipDesign starterShipyard,
        StartingSupplies supplies = StartingSupplies.Standard, RaceDefinition? race = null)
    {
        race ??= catalog.Races.Default;
        if (race.ShipyardParts.Count > 0) // #97
            starterShipyard = new ShipDesign($"{starterShipyard.Id}_{race.Id}", $"{starterShipyard.Name} ({race.Name})",
                starterShipyard.Cells.Select(c => race.ShipyardParts.TryGetValue(c.PartId, out var instead) ? c with { PartId = instead } : c));
        Require(starterShip.Validate(catalog, DesignRole.Ship), starterShip);
        Require(starterShipyard.Validate(catalog, DesignRole.Station), starterShipyard);

        var sector = SectorGenerator.Generate(seed);
        sector.Recipes = catalog.Recipes;
        sector.Parts = catalog;
        sector.Race = race;
        if (race.Tech.Count > 0) sector.RestoreResearch(0, race.Tech, allUnlocked: false);
        var primary = SectorGenerator.HomePlanet(sector);

        var player = new CraftState(sector.AllocateCraftId(), "Player", starterShip, starterShip.ComputeStats(catalog),
            SectorGenerator.PlayerSpawnPoint(sector), headingRad: 0)
        {
            // In a circular orbit around Home (#32): it doesn't fall, and it keeps pace with the yard.
            Velocity = SectorGenerator.PlayerSpawnVelocity(sector)
                       + new Vector2D(SectorState.CircularOrbitSpeedMps(primary, primary.RadiusM + SectorGenerator.PlayerSpawnAltitudeM), 0),
        };
        sector.AddCraft(player);
        sector.SetTarget(player.Id, primary.Id);
        player.Inventory.Add(SectorState.FuelMaterial, PlayerStartingFuelKg);
        sector.PilotCraftId = player.Id;

        var yard = new CraftState(sector.AllocateCraftId(), "Shipyard", starterShipyard, starterShipyard.ComputeStats(catalog),
            Vector2D.Zero, headingRad: 0);
        sector.AddCraft(yard);
        // Spawn point is straight "up" (−Y) from the primary, i.e. angle −π/2 on its orbit circle.
        var yardRadius = primary.RadiusM + ShipyardAltitudeM;
        sector.PutOnRails(yard.Id, new CircularOrbit(primary.Id, yardRadius,
            SectorState.CircularOrbitPeriodS(primary, yardRadius, ShipyardOrbitPeriodS), -Math.PI / 2 + ShipyardPhaseLeadRad));
        foreach (var (material, kg) in StartingStockKg)
        {
            if (!catalog.Materials.Contains(material)) throw new InvalidOperationException($"Starting stock uses unknown material '{material}'.");
            yard.Inventory.Add(material, kg);
        }
        if (race.Supplies)
        {
            var (food, water) = SuppliesKg(supplies);
            yard.Inventory.Add(SectorState.FoodMaterial, food);
            yard.Inventory.Add(SectorState.WaterMaterial, water);
        }

        sector.RestoreCrew(yard.Id, race.Crew, 0, 0, 0, 0, legacyNoCrew: false);
        return new NewGameResult(sector, player.Id, yard.Id);
    }

    private static void Require(IReadOnlyList<string> errors, ShipDesign design)
    {
        if (errors.Count > 0) throw new InvalidOperationException($"Starter design '{design.Id}' is invalid: {string.Join("; ", errors)}");
    }
}

/// <summary>How much food and water the starting yard has (new-game option).</summary>
public enum StartingSupplies
{
    /// <summary>About 3.5 days for the starting crew (the amount before this option existed).</summary>
    Lean,
    /// <summary>About 17 days (the default).</summary>
    Standard,
    /// <summary>About 52 days.</summary>
    Generous,
}
