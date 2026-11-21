using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Builds the Phase 1 starter sector from a seed: one central planet fixed at the origin and
/// one small mineable asteroid on a circular orbit. All numbers are placeholders at game
/// scale (far smaller than real bodies) and are expected to be tuned.
/// </summary>
public static class SectorGenerator
{
    public const double PlanetDensityKgPerM3 = 5500;
    public const double AsteroidDensityKgPerM3 = 2500;

    /// <summary>Placeholder deposits (docs/06, Phase 4). Finite, so they can run out.</summary>
    public const long Rock1IronOreKg = 500_000;
    public const long Rock2SilicateKg = 400_000;
    public const long Rock2IronOreKg = 100_000;
    public const long Rock3IronOreKg = 300_000;

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
            new Dictionary<string, long> { ["iron_ore"] = Rock1IronOreKg }));

        // More asteroids further out (drawn after Rock-1, so Rock-1 is unchanged for a given seed).
        AddAsteroid(sector, rng, planet.Id, "Rock-2", 34_000, 40_000, 2_400, 3_600,
            new Dictionary<string, long> { ["silicate"] = Rock2SilicateKg, ["iron_ore"] = Rock2IronOreKg });
        AddAsteroid(sector, rng, planet.Id, "Rock-3", 44_000, 50_000, 3_000, 4_200,
            new Dictionary<string, long> { ["iron_ore"] = Rock3IronOreKg });

        return sector;
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
