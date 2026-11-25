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

/// <summary>A pit dug by mining (#28): its direction from the body's centre and the mass taken out there.</summary>
public readonly record struct Crater(double BearingRad, long MassKg);

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
        IReadOnlyDictionary<string, long>? depositsKg = null, double? originalMassKg = null)
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
        foreach (var (material, kg) in depositsKg ?? new Dictionary<string, long>())
        {
            if (kg < 0) throw new ArgumentOutOfRangeException(nameof(depositsKg), $"{material}: {kg} kg");
            if (kg > 0) _deposits[material] = kg;
        }
        if (_deposits.Values.Sum() > massKg) throw new ArgumentException("Deposits can't exceed the body's mass.", nameof(depositsKg));
        OriginalMassKg = originalMassKg is > 0 ? originalMassKg.Value : massKg;
        // Surface sites (#70): mineable bodies' rims are split into sites, each holding part of the deposits.
        // The count comes from the body's starting size, so a mined, shrunken body keeps its sites.
        if (isMineable)
        {
            var count = SurfaceSite.SiteCount(RadiusFromMass(OriginalMassKg, densityKgPerM3));
            for (var i = 0; i < count; i++) _sites.Add(new SurfaceSite(i, count, name));
            SurfaceSite.Distribute(_sites, id, _deposits);
            SurfaceSite.PickHotSpots(_sites, id, kind); // geothermal hot spots (#77)
        }
    }

    /// <summary>
    /// Adds an ore deposit to a freshly generated body (#79): part of its existing mass becomes that ore, so the
    /// mass doesn't change. Spread over its sites by the usual rule. Generation only, before anything is mined.
    /// </summary>
    internal void AddDeposit(string material, long kg)
    {
        if (kg <= 0 || _deposits.Values.Sum() + kg > MassKg) return;
        _deposits[material] = _deposits.GetValueOrDefault(material) + kg;
        foreach (var site in _sites) site.Deposits.Remove(material);
        SurfaceSite.Distribute(_sites, Id, new Dictionary<string, long> { [material] = _deposits[material] });
    }

    // --- Native life (#91, #92) ---

    /// <summary>Biomass standing on a fully covered site.</summary>
    public const double FullFloraKg = 500_000;

    /// <summary>Native plants live here now. Saved.</summary>
    public bool HasLife { get; internal set; }

    /// <summary>How its plants trouble machines; whether the player has found and sampled them. Saved.</summary>
    public FloraHazard FloraHazard { get; internal set; }
    public bool LifeFound { get; internal set; }
    public bool LifeSampled { get; internal set; }

    /// <summary>An anomaly not scanned yet (#122): surveys pass it by.</summary>
    internal bool Unscanned { get; set; }

    internal IReadOnlyList<SurfaceSite> SiteList => _sites;

    /// <summary>Native animals live here now, what they're called, and when they damage machines (#93). Saved.</summary>
    public bool HasAnimals { get; internal set; }
    public string AnimalName { get; internal set; } = "";
    public AnimalTemper AnimalTemper { get; internal set; }

    /// <summary>Takes living mass out of the body, or gives it back (#93).</summary>
    internal void TakeBulk(long kg) => MassKg -= kg;
    internal void ReturnBulk(long kg) => MassKg += kg;

    /// <summary>Biomass that can be harvested at a site, leaving this much cover standing. Biomass is part of the body's bulk.</summary>
    public long BiomassKg(SurfaceSite? site, double leaveCover = 0) =>
        site is null ? 0 : Math.Min(BulkKg, (long)Math.Floor(Math.Max(0, site.Flora - leaveCover) * FullFloraKg));

    // --- Surface sites (#70) ---

    private readonly List<SurfaceSite> _sites = new();

    /// <summary>The rim's sites, anticlockwise from bearing −π. Empty for bodies that can't be mined.</summary>
    public IReadOnlyList<SurfaceSite> Sites => _sites;

    /// <summary>The site at a bearing from the body's centre, or null if the body has none.</summary>
    public SurfaceSite? SiteAt(double bearingRad) =>
        _sites.Count == 0 ? null : _sites[SurfaceSite.IndexAt(bearingRad, _sites.Count)];

    /// <summary>True once any site has been surveyed: jobs can use the surveyed ones.</summary>
    public bool AnySiteSurveyed => !IsMineable || _sites.Any(s => s.Surveyed);

    /// <summary>Kg of a material in the surveyed sites (what jobs can count on), plus the bulk if it's the bulk material.</summary>
    /// <param name="spareLiving">Leave out sites with native plants (the Protect native life law, #91).</param>
    /// <param name="leaveCover">For biomass: cover to leave standing.</param>
    public long SurveyedDeposit(string materialId, bool allowBulk = true, bool spareLiving = false, double leaveCover = 0)
    {
        long kg = 0;
        var any = false;
        foreach (var site in _sites)
        {
            if (!site.Surveyed || (spareLiving && site.Flora > 0)) continue;
            any = true;
            kg += materialId == SectorState.BiomassMaterial ? BiomassKg(site, leaveCover) : site.Deposits.GetValueOrDefault(materialId);
        }
        // Bulk belongs to the whole body: a surveyed site to dig it at is enough (or any, as before, without the law).
        return kg + (allowBulk && materialId == BulkMaterialId && (any || !spareLiving) ? BulkKg : 0);
    }

    /// <summary>Kg of a material that can be mined at a bearing: that site's deposit, plus the bulk if it's the bulk material.</summary>
    public long DepositAt(string materialId, double bearingRad, bool allowBulk = true) =>
        materialId == SectorState.BiomassMaterial ? BiomassKg(SiteAt(bearingRad)) : (SiteAt(bearingRad)?.Deposits.GetValueOrDefault(materialId) ?? 0) + (allowBulk && materialId == BulkMaterialId ? BulkKg : 0);

    /// <summary>Restores saved sites: their surveyed flags and deposits. The deposits must add up to the body's.</summary>
    internal void RestoreSites(IReadOnlyList<(bool Surveyed, IReadOnlyDictionary<string, long> DepositsKg)> saved)
    {
        if (saved.Count != _sites.Count) throw new ArgumentException($"{Name}: {saved.Count} saved sites, expected {_sites.Count}.");
        var totals = new SortedDictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < saved.Count; i++)
        {
            _sites[i].Surveyed = saved[i].Surveyed;
            _sites[i].Deposits.Clear();
            foreach (var (material, kg) in saved[i].DepositsKg)
            {
                if (kg < 0) throw new ArgumentException($"{Name}: negative deposit at a site.");
                if (kg == 0) continue;
                _sites[i].Deposits[material] = kg;
                totals[material] = totals.GetValueOrDefault(material) + kg;
            }
        }
        if (!totals.SequenceEqual(_deposits)) throw new ArgumentException($"{Name}: the sites' deposits don't add up to the body's.");
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
        (BulkKg > 0 ? _deposits.Keys.Append(BulkMaterialId) : _deposits.Keys).Concat(HasLife ? new[] { SectorState.BiomassMaterial } : Array.Empty<string>()).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

    /// <summary>True when nothing is left to mine and only the remnant remains.</summary>
    public bool MinedOut => IsDismantlable && _deposits.Count == 0 && BulkKg == 0;

    private readonly SortedDictionary<string, long> _deposits = new(StringComparer.Ordinal);

    /// <summary>Minable material left, whole kg by material id. Part of <see cref="MassKg"/>.</summary>
    public IReadOnlyDictionary<string, long> DepositsKg => _deposits;

    /// <summary>Kg of a material that can be mined here: its deposit, plus the bulk if it's the bulk material (#27).</summary>
    public long Deposit(string materialId) =>
        materialId == SectorState.BiomassMaterial ? _sites.Sum(s => BiomassKg(s)) :
        (_deposits.TryGetValue(materialId, out var kg) ? kg : 0) + (materialId == BulkMaterialId ? BulkKg : 0);

    /// <summary>Removes up to <paramref name="kg"/> of a material from anywhere: the sites in order, then the bulk (tests and scenarios).</summary>
    internal long Extract(string materialId, long kg)
    {
        long taken = 0;
        foreach (var site in _sites)
        {
            if (taken >= kg) break;
            if (site.Deposits.ContainsKey(materialId)) taken += Extract(materialId, kg - taken, site.CentreRad);
        }
        if (taken < kg && materialId == BulkMaterialId) taken += Extract(materialId, kg - taken, 0);
        if (_sites.Count == 0 && taken < kg && _deposits.TryGetValue(materialId, out var had)) // a body without sites
        {
            var more = Math.Min(kg - taken, had);
            if (had == more) _deposits.Remove(materialId); else _deposits[materialId] = had - more;
            MassKg -= more;
            taken += more;
        }
        return taken;
    }

    /// <summary>
    /// Removes up to <paramref name="kg"/> of a material at a bearing (#70): from that site's deposit
    /// first, then the body's bulk; the same mass leaves the body. Returns what was removed.
    /// </summary>
    internal long Extract(string materialId, long kg, double bearingRad, bool allowBulk = true)
    {
        var taken = Math.Min(kg, DepositAt(materialId, bearingRad, allowBulk));
        if (taken <= 0) return 0;
        var site = SiteAt(bearingRad);
        if (materialId == SectorState.BiomassMaterial) // plants: off the cover and out of the bulk (#92)
        {
            site!.Flora = Math.Max(0, site.Flora - taken / FullFloraKg);
            if (site.Flora < SectorState.FloraGone) site.Flora = 0;
            MassKg -= taken;
            return taken;
        }
        var had = site?.Deposits.GetValueOrDefault(materialId) ?? 0;
        var fromDeposit = Math.Min(taken, had);
        if (fromDeposit > 0)
        {
            if (had - fromDeposit == 0) site!.Deposits.Remove(materialId);
            else site!.Deposits[materialId] = had - fromDeposit;
            var total = _deposits[materialId] - fromDeposit; // the body's total is the sum of its sites
            if (total == 0) _deposits.Remove(materialId);
            else _deposits[materialId] = total;
        }
        MassKg -= taken; // the rest came from the bulk, which is simply the body's mass
        return taken;
    }

    public BodyId Id { get; }

    // --- Craters (#28) ---

    /// <summary>A body keeps at most this many craters; a new one beyond that joins the nearest.</summary>
    public const int MaxCraters = 64;

    private readonly List<Crater> _craters = new();

    /// <summary>Where mining has dug into the body (by direction from its centre), largest-first not guaranteed; saved.</summary>
    public IReadOnlyList<Crater> Craters => _craters;

    /// <summary>
    /// Crater radius for a dug-out mass: a shallow bowl (depth about an eighth of its radius, like an
    /// open-pit mine) holding that volume at the body's density: V ≈ πr³/16.
    /// </summary>
    public double CraterRadiusM(long massKg) => Math.Cbrt(16.0 * (massKg / DensityKgPerM3) / Math.PI);

    /// <summary>
    /// Records mass dug out at a direction (#28): it deepens the crater it falls in (within that crater's
    /// radius on the surface), else starts a new one; past <see cref="MaxCraters"/> it joins the nearest.
    /// Purely a record of where mining happened: the body's mass and radius already account for it.
    /// </summary>
    internal void Dig(double bearingRad, long massKg)
    {
        if (massKg <= 0) return;
        var best = -1;
        var bestGap = double.MaxValue;
        for (var i = 0; i < _craters.Count; i++)
        {
            var gap = Math.Abs(Math.IEEERemainder(_craters[i].BearingRad - bearingRad, 2 * Math.PI)) * RadiusM; // along the surface
            if (gap < bestGap) { best = i; bestGap = gap; }
        }
        if (best >= 0 && (bestGap <= CraterRadiusM(_craters[best].MassKg) || _craters.Count >= MaxCraters))
            _craters[best] = _craters[best] with { MassKg = _craters[best].MassKg + massKg };
        else _craters.Add(new Crater(bearingRad, massKg));
    }

    /// <summary>Restores saved craters.</summary>
    internal void RestoreCraters(IEnumerable<Crater> craters)
    {
        _craters.Clear();
        foreach (var crater in craters)
        {
            if (crater.MassKg <= 0 || !double.IsFinite(crater.BearingRad)) throw new ArgumentException("Invalid crater.");
            if (_craters.Count < MaxCraters) _craters.Add(crater);
        }
    }

    /// <summary>For a star: its type (#25). Yellow for the home star and older saves.</summary>
    public StarClass StarClass { get; internal set; }

    /// <summary>Sunlight compared with a yellow star at the same distance.</summary>
    public double SunlightFactor => StarClass switch { StarClass.Red => 0.5, StarClass.Blue => 2.0, _ => 1.0 };

    /// <summary>
    /// Whether every site has been surveyed (#70; bodies that can't be mined count as surveyed).
    /// Setting it marks all sites (scouts circle a body; tests and scenarios). Jobs and structures can use
    /// a body once any site is surveyed (<see cref="AnySiteSurveyed"/>), and mining jobs only its surveyed sites.
    /// </summary>
    public bool Surveyed
    {
        get => !IsMineable || _sites.All(s => s.Surveyed);
        internal set { foreach (var site in _sites) site.Surveyed = value; }
    }
    public string Name { get; private set; }

    /// <summary>What it was called before the player named it (#109), or null.</summary>
    public string? OriginalName { get; private set; }

    /// <summary>Gives it a name of the player's, or (null) its own back. Its sites go by it too.</summary>
    internal void Rename(string? name)
    {
        var own = OriginalName ?? Name;
        OriginalName = name is null || name == own ? null : own;
        Name = name ?? own;
        foreach (var site in _sites) site.Rename(Name);
    }
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
