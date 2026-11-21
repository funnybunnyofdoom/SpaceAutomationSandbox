using System;

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

    public static SectorState Generate(ulong seed)
    {
        var rng = new DeterministicRandom(seed);
        var sector = new SectorState(seed);

        var planetRadiusM = rng.Range(5_000, 7_000);
        var planet = new CelestialBodyState(
            sector.AllocateBodyId(), "Primary", BodyKind.Planet,
            CelestialBodyState.MassFromRadius(planetRadiusM, PlanetDensityKgPerM3),
            PlanetDensityKgPerM3, isMineable: false, orbit: null);
        sector.AddBody(planet);

        var asteroidRadiusM = rng.Range(500, 900);
        var orbit = new CircularOrbit(
            planet.Id,
            RadiusM: rng.Range(22_000, 30_000),
            PeriodS: rng.Range(180, 300),
            PhaseAtZeroRad: rng.Range(0, 2 * Math.PI));
        sector.AddBody(new CelestialBodyState(
            sector.AllocateBodyId(), "Rock-1", BodyKind.Asteroid,
            CelestialBodyState.MassFromRadius(asteroidRadiusM, AsteroidDensityKgPerM3),
            AsteroidDensityKgPerM3, isMineable: true, orbit));

        return sector;
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
