using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Coarse body category. Placeholder set; extend through the decision log.</summary>
public enum BodyKind
{
    Planet,
    Asteroid,
}

/// <summary>
/// Authoritative state of one celestial body. Mass is the stored quantity, and radius is
/// derived from mass at fixed bulk density (docs/02: r = cbrt(3M / 4πρ)), so removing mass
/// later shrinks the body without a second source of truth.
/// Units: kilograms, metres, seconds.
/// </summary>
public sealed class CelestialBodyState
{
    public CelestialBodyState(BodyId id, string name, BodyKind kind, double massKg,
        double densityKgPerM3, bool isMineable, CircularOrbit? orbit)
    {
        if (id.IsNone) throw new ArgumentException("Body needs a real id.", nameof(id));
        if (!(massKg > 0)) throw new ArgumentOutOfRangeException(nameof(massKg));
        if (!(densityKgPerM3 > 0)) throw new ArgumentOutOfRangeException(nameof(densityKgPerM3));

        Id = id;
        Name = name;
        Kind = kind;
        MassKg = massKg;
        DensityKgPerM3 = densityKgPerM3;
        IsMineable = isMineable;
        Orbit = orbit;
    }

    public BodyId Id { get; }
    public string Name { get; }
    public BodyKind Kind { get; }

    /// <summary>Gross natural-body mass. Mining (Phase 3) will be the only thing that changes it.</summary>
    public double MassKg { get; }

    public double DensityKgPerM3 { get; }
    public bool IsMineable { get; }

    /// <summary>Null for a body fixed at the sector origin.</summary>
    public CircularOrbit? Orbit { get; }

    public double RadiusM => RadiusFromMass(MassKg, DensityKgPerM3);

    public static double RadiusFromMass(double massKg, double densityKgPerM3) =>
        Math.Cbrt(3.0 * massKg / (4.0 * Math.PI * densityKgPerM3));

    public static double MassFromRadius(double radiusM, double densityKgPerM3) =>
        4.0 / 3.0 * Math.PI * radiusM * radiusM * radiusM * densityKgPerM3;
}
