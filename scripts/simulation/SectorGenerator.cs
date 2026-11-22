using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Builds the starter sector from a seed: one central planet fixed at the origin, three mineable
/// asteroids, and (#16) a moon, an ice asteroid and a distant gas giant, all on circular orbits.
/// All numbers are placeholders at game scale (far smaller than real bodies) and are expected to
/// be tuned. Bodies are drawn in a fixed order, so adding new ones at the end keeps earlier ones
/// unchanged for a given seed.
/// </summary>
public static class SectorGenerator
{
    public const double PlanetDensityKgPerM3 = 5500;
    public const double AsteroidDensityKgPerM3 = 2500;
    public const double MoonDensityKgPerM3 = 3300;
    public const double IceDensityKgPerM3 = 1000;
    public const double GasGiantDensityKgPerM3 = 1300;

    /// <summary>Placeholder deposits (docs/06, Phase 4). Finite, so they can run out.</summary>
    public const long Rock1IronOreKg = 500_000;
    public const long Rock2SilicateKg = 400_000;
    public const long Rock2IronOreKg = 100_000;
    public const long Rock3IronOreKg = 300_000;
    public const long Rock1CopperOreKg = 50_000;
    public const long Rock3CopperOreKg = 200_000;
    public const long MoonIronOreKg = 800_000;
    public const long MoonSilicateKg = 600_000;
    public const long IceIceKg = 400_000;
    public const long IceSilicateKg = 50_000;

    public static SectorState Generate(ulong seed)
    {
        var rng = new DeterministicRandom(seed);
        var sector = new SectorState(seed);

        var planetRadiusM = rng.Range(5_000, 7_000);
        var planet = new CelestialBodyState(
            sector.AllocateBodyId(), "Primary", BodyKind.Planet,
            Math.Round(CelestialBodyState.MassFromRadius(planetRadiusM, PlanetDensityKgPerM3)), // whole kg: exact ledger
            PlanetDensityKgPerM3, isMineable: false, orbit: null);
        sector.AddBody(planet);

        var asteroidRadiusM = rng.Range(500, 900);
        var orbit = new CircularOrbit(
            planet.Id,
            RadiusM: rng.Range(22_000, 30_000),
            // 20–40 min per orbit gives ~60–150 m/s and under 1 m/s² of curvature, so a starter
            // ship (≈17 m/s²) can catch it and hold position with the brake. Was 3–5 min (too fast).
            PeriodS: rng.Range(1_200, 2_400),
            PhaseAtZeroRad: rng.Range(0, 2 * Math.PI));
        sector.AddBody(new CelestialBodyState(
            sector.AllocateBodyId(), "Rock-1", BodyKind.Asteroid,
            Math.Round(CelestialBodyState.MassFromRadius(asteroidRadiusM, AsteroidDensityKgPerM3)),
            AsteroidDensityKgPerM3, isMineable: true, orbit,
            new Dictionary<string, long> { ["iron_ore"] = Rock1IronOreKg, ["copper_ore"] = Rock1CopperOreKg }));

        // More asteroids further out (drawn after Rock-1, so Rock-1 is unchanged for a given seed).
        AddAsteroid(sector, rng, planet.Id, "Rock-2", 34_000, 40_000, 2_400, 3_600,
            new Dictionary<string, long> { ["silicate"] = Rock2SilicateKg, ["iron_ore"] = Rock2IronOreKg });
        AddAsteroid(sector, rng, planet.Id, "Rock-3", 44_000, 50_000, 3_000, 4_200,
            new Dictionary<string, long> { ["iron_ore"] = Rock3IronOreKg, ["copper_ore"] = Rock3CopperOreKg });

        // #16: drawn after the asteroids, so the bodies above are unchanged for a given seed.
        AddBody(sector, rng, planet.Id, "Moon", BodyKind.Moon, MoonDensityKgPerM3, 1_200, 1_600, 14_000, 18_000, 900, 1_500, true,
            new Dictionary<string, long> { ["iron_ore"] = MoonIronOreKg, ["silicate"] = MoonSilicateKg });
        AddBody(sector, rng, planet.Id, "Ice-1", BodyKind.Asteroid, IceDensityKgPerM3, 500, 800, 56_000, 64_000, 3_600, 4_800, true,
            new Dictionary<string, long> { ["ice"] = IceIceKg, ["silicate"] = IceSilicateKg });
        AddBody(sector, rng, planet.Id, "Giant", BodyKind.GasGiant, GasGiantDensityKgPerM3, 9_000, 11_000, 120_000, 140_000, 9_000, 12_000, false,
            new Dictionary<string, long>());

        return sector;
    }

    private static void AddBody(SectorState sector, DeterministicRandom rng, BodyId parent, string name, BodyKind kind, double density,
        double minRadiusM, double maxRadiusM, double minOrbitM, double maxOrbitM, double minPeriodS, double maxPeriodS,
        bool mineable, Dictionary<string, long> deposits)
    {
        var radiusM = rng.Range(minRadiusM, maxRadiusM);
        var orbit = new CircularOrbit(parent, rng.Range(minOrbitM, maxOrbitM), rng.Range(minPeriodS, maxPeriodS), rng.Range(0, 2 * Math.PI));
        sector.AddBody(new CelestialBodyState(sector.AllocateBodyId(), name, kind,
            Math.Round(CelestialBodyState.MassFromRadius(radiusM, density)), density, mineable, orbit, deposits));
    }

    private static void AddAsteroid(SectorState sector, DeterministicRandom rng, BodyId parent, string name,
        double minOrbitM, double maxOrbitM, double minPeriodS, double maxPeriodS, Dictionary<string, long> deposits)
    {
        var radiusM = rng.Range(400, 800);
        var orbit = new CircularOrbit(parent, rng.Range(minOrbitM, maxOrbitM), rng.Range(minPeriodS, maxPeriodS), rng.Range(0, 2 * Math.PI));
        sector.AddBody(new CelestialBodyState(sector.AllocateBodyId(), name, BodyKind.Asteroid,
            Math.Round(CelestialBodyState.MassFromRadius(radiusM, AsteroidDensityKgPerM3)),
            AsteroidDensityKgPerM3, isMineable: true, orbit, deposits));
    }

    /// <summary>Gap between the primary's surface and the player's spawn point. Placeholder.</summary>
    public const double PlayerSpawnAltitudeM = 300;

    /// <summary>Spawn point straight "above" (−Y) the first fixed body, with the ship facing away from it.</summary>
    public static Vector2D PlayerSpawnPoint(SectorState sector)
    {
        foreach (var body in sector.Bodies)
        {
            if (body.Orbit is null)
                return sector.GetPosition(body.Id) + new Vector2D(0, -(body.RadiusM + PlayerSpawnAltitudeM));
        }
        return Vector2D.Zero;
    }
}
