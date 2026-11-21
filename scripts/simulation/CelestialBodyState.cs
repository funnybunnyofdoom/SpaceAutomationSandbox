using System;
using System.Collections.Generic;
using System.Linq;

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
/// Deposits are the minable part of that mass, in whole kg by material. Mining moves whole kg
/// from a deposit into a craft's cargo and removes exactly the same kg from the body's mass.
/// Units: kilograms, metres, seconds.
/// </summary>
public sealed class CelestialBodyState
{
    public CelestialBodyState(BodyId id, string name, BodyKind kind, double massKg,
        double densityKgPerM3, bool isMineable, CircularOrbit? orbit,
        IReadOnlyDictionary<string, long>? depositsKg = null)
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
        Surveyed = !isMineable; // nothing to discover on the others yet
        foreach (var (material, kg) in depositsKg ?? new Dictionary<string, long>())
        {
            if (kg < 0) throw new ArgumentOutOfRangeException(nameof(depositsKg), $"{material}: {kg} kg");
            if (kg > 0) _deposits[material] = kg;
        }
        if (_deposits.Values.Sum() > massKg) throw new ArgumentException("Deposits can't exceed the body's mass.", nameof(depositsKg));
    }

    private readonly SortedDictionary<string, long> _deposits = new(StringComparer.Ordinal);

    /// <summary>Minable material left, whole kg by material id. Part of <see cref="MassKg"/>.</summary>
    public IReadOnlyDictionary<string, long> DepositsKg => _deposits;

    public long Deposit(string materialId) => _deposits.TryGetValue(materialId, out var kg) ? kg : 0;

    /// <summary>Removes up to <paramref name="kg"/> of a deposit and the same mass from the body. Returns what was removed.</summary>
    internal long Extract(string materialId, long kg)
    {
        var taken = Math.Min(kg, Deposit(materialId));
        if (taken <= 0) return 0;
        var left = Deposit(materialId) - taken;
        if (left == 0) _deposits.Remove(materialId);
        else _deposits[materialId] = left;
        MassKg -= taken;
        return taken;
    }

    public BodyId Id { get; }

    /// <summary>
    /// Whether the player has visited it (a piloted ship within <see cref="SectorState.SurveyRangeM"/>).
    /// Mineable bodies start unsurveyed; jobs and structures can only use surveyed bodies (docs/06).
    /// </summary>
    public bool Surveyed { get; internal set; }
    public string Name { get; }
    public BodyKind Kind { get; }

    /// <summary>Gross natural-body mass. Mining is the only thing that changes it (see <see cref="Extract"/>).</summary>
    public double MassKg { get; private set; }

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
