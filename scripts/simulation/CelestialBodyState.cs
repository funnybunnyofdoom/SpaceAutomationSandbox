using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Coarse body category. Placeholder set; extend through the decision log.</summary>
public enum BodyKind
{
    Planet,
    Asteroid,
    /// <summary>A large rocky body orbiting a planet: mineable and landable (#16).</summary>
    Moon,
    /// <summary>Not mineable; you can't anchor or deploy on its cloud tops (#16). Skimming comes later.</summary>
    GasGiant,
    /// <summary>The system's star at the centre (#53): not mineable, nothing anchors or deploys on it.</summary>
    Star,
}

/// <summary>A star's type (#25): sets how much sunlight its system gets.</summary>
public enum StarClass
{
    /// <summary>Like the home star: full sunlight.</summary>
    Yellow,
    /// <summary>A red dwarf: half the sunlight.</summary>
    Red,
    /// <summary>A bright blue star: double the sunlight.</summary>
    Blue,
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
        DensityKgPerM3 = densityKgPerM3;
        MassKg = massKg;
        IsMineable = isMineable;
        Orbit = orbit;
        Surveyed = !isMineable; // nothing to discover on the others yet
        foreach (var (material, kg) in depositsKg ?? new Dictionary<string, long>())
        {
            if (kg < 0) throw new ArgumentOutOfRangeException(nameof(depositsKg), $"{material}: {kg} kg");
            if (kg > 0) _deposits[material] = kg;
        }
        if (_deposits.Values.Sum() > massKg) throw new ArgumentException("Deposits can't exceed the body's mass.", nameof(depositsKg));
        OriginalMassKg = massKg;
    }

    // --- Complete dismantling (#27) ---

    /// <summary>A body is taken apart down to this share of its starting mass; the last of it then scatters and the body is gone.</summary>
    public const double RemnantFraction = 0.01;

    /// <summary>The material a body's bulk is mined as: ice for icy bodies, rock otherwise.</summary>
    public const string RockMaterial = "rock";
    public const string IceMaterial = "ice";

    /// <summary>Mass when the sector was made (saved), for the remnant at which the body is gone.</summary>
    public double OriginalMassKg { get; internal set; }

    /// <summary>Mineable rocky or icy bodies (planets, moons, asteroids) can be mined away entirely (#27).</summary>
    public bool IsDismantlable => IsMineable && Kind is BodyKind.Planet or BodyKind.Moon or BodyKind.Asteroid;

    /// <summary>What the bulk of the body is mined as.</summary>
    public string BulkMaterialId => DensityKgPerM3 <= 1000 ? IceMaterial : RockMaterial;

    /// <summary>Kg kept back as the remnant (only removed all at once when the body goes).</summary>
    public long RemnantKg => (long)Math.Floor(OriginalMassKg * RemnantFraction);

    /// <summary>The body's mass as whole kg (mass is kept in whole kg).</summary>
    public long WholeMassKg => (long)Math.Round(MassKg);

    /// <summary>Bulk left to mine beyond the deposits, down to the remnant (0 for bodies that can't be dismantled).</summary>
    public long BulkKg => IsDismantlable ? Math.Max(0, WholeMassKg - _deposits.Values.Sum() - RemnantKg) : 0;

    /// <summary>Everything that can be mined here now: the deposits and the bulk (A–Z).</summary>
    public IReadOnlyList<string> MineableMaterials =>
        (BulkKg > 0 ? _deposits.Keys.Append(BulkMaterialId) : _deposits.Keys).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>True when nothing is left to mine and only the remnant remains.</summary>
    public bool MinedOut => IsDismantlable && _deposits.Count == 0 && BulkKg == 0;

    private readonly SortedDictionary<string, long> _deposits = new(StringComparer.Ordinal);

    /// <summary>Minable material left, whole kg by material id. Part of <see cref="MassKg"/>.</summary>
    public IReadOnlyDictionary<string, long> DepositsKg => _deposits;

    /// <summary>Kg of a material that can be mined here: its deposit, plus the bulk if it's the bulk material (#27).</summary>
    public long Deposit(string materialId) =>
        (_deposits.TryGetValue(materialId, out var kg) ? kg : 0) + (materialId == BulkMaterialId ? BulkKg : 0);

    /// <summary>
    /// Removes up to <paramref name="kg"/> of a material (from its deposit first, then the bulk) and the
    /// same mass from the body. Returns what was removed.
    /// </summary>
    internal long Extract(string materialId, long kg)
    {
        var taken = Math.Min(kg, Deposit(materialId));
        if (taken <= 0) return 0;
        var had = _deposits.TryGetValue(materialId, out var d) ? d : 0;
        var fromDeposit = Math.Min(taken, had);
        if (fromDeposit > 0)
        {
            if (had - fromDeposit == 0) _deposits.Remove(materialId);
            else _deposits[materialId] = had - fromDeposit;
        }
        MassKg -= taken; // the rest came from the bulk, which is simply the body's mass
        return taken;
    }

    public BodyId Id { get; }

    /// <summary>For a star: its type (#25). Yellow for the home star and older saves.</summary>
    public StarClass StarClass { get; internal set; }

    /// <summary>Sunlight compared with a yellow star at the same distance.</summary>
    public double SunlightFactor => StarClass switch { StarClass.Red => 0.5, StarClass.Blue => 2.0, _ => 1.0 };

    /// <summary>
    /// Whether the player has visited it (a piloted ship within <see cref="SectorState.SurveyRangeM"/>).
    /// Mineable bodies start unsurveyed; jobs and structures can only use surveyed bodies (docs/06).
    /// </summary>
    public bool Surveyed { get; internal set; }
    public string Name { get; }
    public BodyKind Kind { get; }

    /// <summary>Gross natural-body mass. Mining is the only thing that changes it (see <see cref="Extract"/>).</summary>
    public double MassKg
    {
        get => _massKg;
        private set
        {
            _massKg = value;
            RadiusM = RadiusFromMass(value, DensityKgPerM3); // kept with the mass: read very often (contact, autopilot)
        }
    }

    private double _massKg;

    public double DensityKgPerM3 { get; }
    public bool IsMineable { get; }

    /// <summary>Null for a body fixed at the sector origin.</summary>
    public CircularOrbit? Orbit { get; }

    /// <summary>Follows the mass at fixed density (updated whenever the mass changes).</summary>
    public double RadiusM { get; private set; }

    public static double RadiusFromMass(double massKg, double densityKgPerM3) =>
        Math.Cbrt(3.0 * massKg / (4.0 * Math.PI * densityKgPerM3));

    public static double MassFromRadius(double radiusM, double densityKgPerM3) =>
        4.0 / 3.0 * Math.PI * radiusM * radiusM * radiusM * densityKgPerM3;
}
