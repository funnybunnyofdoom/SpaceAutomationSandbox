using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Builds the home solar system from a seed (#53): a star at the origin and, around it, a compact
/// system of four planets plus a belt:
/// <list type="bullet">
/// <item>Cinder, an inner rocky planet (~250 km out);</item>
/// <item>Home (~500 km out) with its neighbourhood: Rock-1..3, the Moon and Ice-1 (unchanged from
/// before the star existed, for a given seed);</item>
/// <item>an asteroid belt (~750–850 km out);</item>
/// <item>the Giant, a gas giant (~1,100 km out) with two moons;</item>
/// <item>Rime, an outer ice world (~1,500 km out).</item>
/// </list>
/// Planets move slowly (about 6–14 m/s), so the home neighbourhood feels as before. A trip to a
/// neighbouring planet takes about 5–10 minutes with the cruise drive. All numbers are placeholders
/// at game scale and are expected to be tuned.
/// <para>Random draws: the home neighbourhood uses the seed's own stream in the same order as
/// before; everything new uses a second stream, so the neighbourhood is unchanged for a seed.</para>
/// </summary>
public static class SectorGenerator
{
    public const double StarDensityKgPerM3 = 1400;
    public const double PlanetDensityKgPerM3 = 5500;
    public const double AsteroidDensityKgPerM3 = 2500;
    public const double MoonDensityKgPerM3 = 3300;
    public const double IceDensityKgPerM3 = 1000;
    public const double GasGiantDensityKgPerM3 = 1300;

    /// <summary>Home planet's distance from the star. Solar power is 100% here (#53).</summary>
    public const double HomeOrbitRadiusM = 500_000;

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
    public const int BeltAsteroidCount = 12;

    public const string StarName = "Star";
    public const string HomeName = "Home";

    public static SectorState Generate(ulong seed)
    {
        var rng = new DeterministicRandom(seed);                       // home neighbourhood, same draws as before
        var system = new DeterministicRandom(seed ^ 0x5DEECE66DUL);    // everything added by #53
        var sector = new SectorState(seed);

        // The star is added first (parents before children).
        var star = Body(sector, StarName, BodyKind.Star, system.Range(28_000, 32_000), StarDensityKgPerM3, false, null);

        // The planets start roughly lined up on Home's side of the star, so neighbours are close
        // (Cinder and the belt ~5 min of cruise, the Giant ~10). They drift apart over many hours.
        var homePhase = system.Range(0, 2 * Math.PI);
        var planetRadiusM = rng.Range(5_000, 7_000);
        var home = Body(sector, HomeName, BodyKind.Planet, planetRadiusM, PlanetDensityKgPerM3, false,
            AroundStar(star.Id, HomeOrbitRadiusM, homePhase));

        var asteroidRadiusM = rng.Range(500, 900);
        var rock1Orbit = new CircularOrbit(
            home.Id,
            RadiusM: rng.Range(22_000, 30_000),
            // 20–40 min per orbit gives ~60–150 m/s and under 1 m/s² of curvature, so a starter
            // ship (≈17 m/s²) can catch it and hold position with the brake.
            PeriodS: rng.Range(1_200, 2_400),
            PhaseAtZeroRad: rng.Range(0, 2 * Math.PI));
        sector.AddBody(new CelestialBodyState(
            sector.AllocateBodyId(), "Rock-1", BodyKind.Asteroid,
            Math.Round(CelestialBodyState.MassFromRadius(asteroidRadiusM, AsteroidDensityKgPerM3)),
            AsteroidDensityKgPerM3, isMineable: true, rock1Orbit,
            new Dictionary<string, long> { ["iron_ore"] = Rock1IronOreKg, ["copper_ore"] = Rock1CopperOreKg }));

        AddAsteroid(sector, rng, home.Id, "Rock-2", 34_000, 40_000, 2_400, 3_600,
            new Dictionary<string, long> { ["silicate"] = Rock2SilicateKg, ["iron_ore"] = Rock2IronOreKg });
        AddAsteroid(sector, rng, home.Id, "Rock-3", 44_000, 50_000, 3_000, 4_200,
            new Dictionary<string, long> { ["iron_ore"] = Rock3IronOreKg, ["copper_ore"] = Rock3CopperOreKg });
        AddBody(sector, rng, home.Id, "Moon", BodyKind.Moon, MoonDensityKgPerM3, 1_200, 1_600, 14_000, 18_000, 900, 1_500, true,
            new Dictionary<string, long> { ["iron_ore"] = MoonIronOreKg, ["silicate"] = MoonSilicateKg });
        AddBody(sector, rng, home.Id, "Ice-1", BodyKind.Asteroid, IceDensityKgPerM3, 500, 800, 56_000, 64_000, 3_600, 4_800, true,
            new Dictionary<string, long> { ["ice"] = IceIceKg, ["silicate"] = IceSilicateKg });

        // The rest of the system orbits the star.
        Body(sector, "Cinder", BodyKind.Planet, system.Range(3_000, 4_000), PlanetDensityKgPerM3, true,
            AroundStar(star.Id, system.Range(240_000, 260_000), homePhase + system.Range(-0.3, 0.3)),
            new Dictionary<string, long> { ["iron_ore"] = 2_000_000, ["copper_ore"] = 1_000_000, ["uranium_ore"] = UraniumAtHomeKg }); // rare: only here at home (#79)

        for (var i = 1; i <= BeltAsteroidCount; i++)
        {
            var deposits = new Dictionary<string, long> { ["iron_ore"] = 50_000 + 10_000 * (long)system.Range(0, 10) };
            deposits[i % 3 == 0 ? "copper_ore" : i % 3 == 1 ? "silicate" : "ice"] = 30_000 + 10_000 * (long)system.Range(0, 8);
            // Evenly spread around the belt with some jitter, so the rocks never overlap.
            var orbit = new CircularOrbit(star.Id, system.Range(750_000, 850_000), 0, 2 * Math.PI * (i - 1) / BeltAsteroidCount + system.Range(0, 0.3));
            Body(sector, $"Belt-{i}", BodyKind.Asteroid, system.Range(200, 500), AsteroidDensityKgPerM3, true,
                orbit with { PeriodS = SlowPeriod(orbit.RadiusM) }, deposits);
        }

        var giant = Body(sector, "Giant", BodyKind.GasGiant, system.Range(9_000, 11_000), GasGiantDensityKgPerM3, false,
            AroundStar(star.Id, system.Range(1_080_000, 1_120_000), homePhase + system.Range(-0.15, 0.15)));
        AddBody(sector, system, giant.Id, "Giant-a", BodyKind.Moon, MoonDensityKgPerM3, 900, 1_200, 25_000, 30_000, 1_500, 2_400, true,
            new Dictionary<string, long> { ["silicate"] = 500_000, ["copper_ore"] = 300_000 });
        AddBody(sector, system, giant.Id, "Giant-b", BodyKind.Moon, IceDensityKgPerM3, 1_000, 1_300, 40_000, 50_000, 2_800, 3_600, true,
            new Dictionary<string, long> { ["ice"] = 600_000, ["methane_ice"] = 150_000 }); // #78

        Body(sector, "Rime", BodyKind.Planet, system.Range(4_000, 5_000), IceDensityKgPerM3, true,
            AroundStar(star.Id, system.Range(1_480_000, 1_520_000), homePhase + system.Range(-0.15, 0.15)),
            new Dictionary<string, long> { ["ice"] = 3_000_000, ["silicate"] = 500_000, ["methane_ice"] = 800_000 }); // #78

        return sector;
    }

    /// <summary>Uranium ore on Cinder, the only uranium in the home system (#79): enough to start nuclear power.</summary>
    public const long UraniumAtHomeKg = 400_000;

    // --- Further star systems (#54) ---

    /// <summary>Distance between neighbouring star systems: far beyond any flight (warp is the way there).</summary>
    public const double SystemSpacingM = 1e10;

    private static readonly string[] NameStarts = { "Ar", "Bel", "Cor", "Dra", "El", "Fen", "Gal", "Hyr", "Ix", "Jor", "Kel", "Lum", "Mor", "Nyx", "Or", "Pel", "Quor", "Ry", "Sel", "Tor", "Ul", "Vex", "Wyr", "Zan" };
    private static readonly string[] NameEnds = { "a", "is", "on", "ax", "eth", "ion", "ara", "ul", "ith", "or", "en", "ys" };

    /// <summary>
    /// Generates star system number <paramref name="index"/> (1 = the first beyond home), deterministically
    /// from the sector's seed: a star placed <see cref="SystemSpacingM"/>-scale away on a fixed orbit around
    /// the home star (it effectively doesn't move), two or three planets (one icy) with a moon, and a
    /// small belt. Deposits are richer than at home: a reason to go. Variety (#25): the star is a red
    /// dwarf (40%, half the sunlight), yellow (35%) or blue (25%, double); exotic ore, found nowhere at
    /// home, is on the first system's moon and on about half of the others'. Returns the star and the
    /// planet a warp gate is placed at. Mineable bodies start unsurveyed.
    /// </summary>
    public static (CelestialBodyState Star, CelestialBodyState GatePlanet) GenerateSystem(SectorState sector, int index)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        var rng = new DeterministicRandom(sector.Seed ^ (0x9E3779B97F4A7C15UL * (ulong)index));
        var home = sector.Bodies.First(b => b.Kind == BodyKind.Star && b.Orbit is null);
        var name = NameStarts[(int)rng.Range(0, NameStarts.Length - 0.001)] + NameEnds[(int)rng.Range(0, NameEnds.Length - 0.001)];

        // A fixed place among the stars: a huge orbit with a period so long the star never moves in practice.
        var place = new CircularOrbit(home.Id, SystemSpacingM * (1 + 0.15 * index), 1e18, rng.Range(0, 2 * Math.PI));
        var star = Body(sector, name, BodyKind.Star, rng.Range(25_000, 40_000), StarDensityKgPerM3, false, place);
        var roll = rng.Range(0, 1);
        star.StarClass = roll < 0.4 ? StarClass.Red : roll < 0.75 ? StarClass.Yellow : StarClass.Blue;
        var exotic = index == 1 || rng.Range(0, 1) < 0.5;

        var phase = rng.Range(0, 2 * Math.PI);
        var first = Body(sector, $"{name} I", BodyKind.Planet, rng.Range(3_500, 6_000), PlanetDensityKgPerM3, true,
            AroundStar(star.Id, rng.Range(400_000, 600_000), phase),
            new Dictionary<string, long>
            {
                ["iron_ore"] = 3_000_000 + 500_000 * (long)rng.Range(0, 6),
                ["copper_ore"] = 1_500_000 + 500_000 * (long)rng.Range(0, 4),
                ["silicate"] = 1_000_000 + 500_000 * (long)rng.Range(0, 4),
            });
        // Uranium (#79) on the inner planet of about two systems in three, from its own roll so the rest of the system is unchanged.
        var uranium = new DeterministicRandom(sector.Seed ^ (0x5552414EUL * (ulong)index));
        if (uranium.NextDouble() < 0.65) first.AddDeposit("uranium_ore", 500_000 + 100_000 * (long)(uranium.NextDouble() * 5));
        var moonDeposits = new Dictionary<string, long> { ["iron_ore"] = 1_000_000, ["silicate"] = 800_000 };
        if (exotic) moonDeposits["exotic_ore"] = 300_000 + 100_000 * (long)rng.Range(0, 4);
        AddBody(sector, rng, first.Id, $"{name} I-a", BodyKind.Moon, MoonDensityKgPerM3, 900, 1_400, 15_000, 25_000, 1_000, 1_800, true,
            moonDeposits);

        Body(sector, $"{name} II", BodyKind.Planet, rng.Range(4_000, 5_500), IceDensityKgPerM3, true,
            AroundStar(star.Id, rng.Range(900_000, 1_100_000), phase + rng.Range(-0.3, 0.3)),
            new Dictionary<string, long> { ["ice"] = 4_000_000, ["silicate"] = 800_000, ["methane_ice"] = 1_200_000 }); // #78

        if (rng.Range(0, 1) < 0.5)
            Body(sector, $"{name} III", BodyKind.Planet, rng.Range(3_000, 4_500), PlanetDensityKgPerM3, true,
                AroundStar(star.Id, rng.Range(1_300_000, 1_500_000), phase + rng.Range(-0.3, 0.3)),
                new Dictionary<string, long> { ["copper_ore"] = 3_000_000, ["iron_ore"] = 1_000_000 });

        var belt = 4 + (int)rng.Range(0, 4.999);
        for (var i = 1; i <= belt; i++)
        {
            var orbit = new CircularOrbit(star.Id, rng.Range(700_000, 800_000), 0, 2 * Math.PI * (i - 1) / belt + rng.Range(0, 0.3));
            Body(sector, $"{name} belt-{i}", BodyKind.Asteroid, rng.Range(200, 500), AsteroidDensityKgPerM3, true,
                orbit with { PeriodS = SlowPeriod(orbit.RadiusM) },
                new Dictionary<string, long> { ["iron_ore"] = 100_000 + 20_000 * (long)rng.Range(0, 10), ["copper_ore"] = 80_000 });
        }
        // Native life (#91), from its own rolls, so the rest of the system is unchanged.
        foreach (var planet in sector.Bodies.Where(b => b.Orbit?.Parent == star.Id && b.Kind == BodyKind.Planet).ToList()) SectorState.MaybeAddLife(sector, star, planet);
        return (star, first);
    }

    /// <summary>
    /// A slow orbit around the star: speed falls with distance like real orbits (about 10 m/s at
    /// Home's distance), so planets barely move on a human timescale. Placeholder (no gravity, #32).
    /// </summary>
    private static double SlowPeriod(double radiusM)
    {
        var speed = 10 * Math.Sqrt(HomeOrbitRadiusM / radiusM);
        return 2 * Math.PI * radiusM / speed;
    }

    private static CircularOrbit AroundStar(BodyId star, double radiusM, double phaseRad) =>
        new(star, radiusM, SlowPeriod(radiusM), phaseRad);

    private static CelestialBodyState Body(SectorState sector, string name, BodyKind kind, double radiusM, double density,
        bool mineable, CircularOrbit? orbit, Dictionary<string, long>? deposits = null)
    {
        var body = new CelestialBodyState(sector.AllocateBodyId(), name, kind,
            Math.Round(CelestialBodyState.MassFromRadius(radiusM, density)), density, mineable, orbit,
            deposits ?? new Dictionary<string, long>());
        sector.AddBody(body);
        return body;
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

    /// <summary>The player's home planet: the first planet (in saves from before #53, the fixed "Primary").</summary>
    public static CelestialBodyState HomePlanet(SectorState sector) => sector.Bodies.First(b => b.Kind == BodyKind.Planet);

    /// <summary>Gap between the home planet's surface and the player's spawn point. Placeholder.</summary>
    public const double PlayerSpawnAltitudeM = 300;

    /// <summary>Spawn point straight "above" (−Y) the home planet, with the ship facing away from it.</summary>
    public static Vector2D PlayerSpawnPoint(SectorState sector)
    {
        var home = HomePlanet(sector);
        return sector.GetPosition(home.Id) + new Vector2D(0, -(home.RadiusM + PlayerSpawnAltitudeM));
    }

    /// <summary>Velocity to give something spawned near home, so it stays put relative to the home planet.</summary>
    public static Vector2D PlayerSpawnVelocity(SectorState sector) => sector.GetVelocityAt(HomePlanet(sector).Id, sector.TimeS);
}
