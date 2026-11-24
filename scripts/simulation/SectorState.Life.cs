using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>How a world's native plants trouble machines working among them (#92).</summary>
public enum FloraHazard
{
    None,
    /// <summary>They cling to machines: drilling and harvesting are slower where they're thick.</summary>
    Clinging,
    /// <summary>They eat at machines: parts of anchored craft wear slowly (never all the way).</summary>
    Corrosive,
}

/// <summary>
/// Planetary life (#91) and native plants (#92; the creator's choices). About one temperate rocky planet in eight, in systems beyond home,
/// has plants. Each surface site (#70) has a cover 0..1 that grows back, spreads to its neighbours, and can die out: harvesting takes
/// from it, mining harms it, and a site at nothing only comes back from a neighbour. Harvested biomass is part of the body's
/// own mass, so the mass ledger stays exact. Finding and first sampling a living world give research points. The Protect
/// native life law stops mining and harvesting on living sites; harming life in a claimed system is a grievance.
/// </summary>
public sealed partial class SectorState
{
    public const string BiomassMaterial = "biomass";

    /// <summary>Chance that a temperate planet has life, and what counts as temperate (sunlight compared with Home's).</summary>
    public const double LifeChance = 1.0 / 8;
    public const double TemperateMin = 0.35, TemperateMax = 1.9;

    /// <summary>Cover grows by this share of itself a minute where there's room, and creeps in from a fuller neighbour.</summary>
    public const double FloraGrowthPerMinute = 0.03, FloraSpreadPerMinute = 0.004;

    /// <summary>Below this a site's plants are gone.</summary>
    public const double FloraGone = 0.01;

    /// <summary>Jobs leave this much cover standing, so it grows back; by hand you can take it all.</summary>
    public const double JobLeavesCover = 0.25;

    /// <summary>Mining this much at a site clears it completely.</summary>
    public const double MinedKgClearsSite = 2_000_000;

    public const double LifeFoundPoints = 20, LifeSampledPoints = 30;

    /// <summary>Grievance for a whole site's plants lost in a claimed system.</summary>
    public const double GrievancePerSiteOfLife = 15;

    private const int FloraStepTicks = 10 * FixedStepClock.TicksPerSecond;

    /// <summary>Is a site protected from mining and harvesting by the law in force?</summary>
    public bool LifeProtected(SurfaceSite? site) => site is { Flora: > 0 } && LawActive(Laws.ProtectLife);

    /// <summary>Kg of a material a craft takes a second, here and now: harvesters for plants, drills for the rest; plants can slow both.</summary>
    public double ExtractionRateKgPerS(CraftState craft, string material)
    {
        var rate = material == BiomassMaterial ? craft.Stats.HarvestRateKgPerS : craft.Stats.MiningRateKgPerS;
        return SiteUnder(craft) is { } site && GetBody(craft.LandedOnBodyId).FloraHazard == FloraHazard.Clinging ? rate * (1 - 0.5 * site.Flora) : rate;
    }

    /// <summary>Generation only: does this new planet get life? From its own roll, so the rest of the system is unchanged.</summary>
    internal static void MaybeAddLife(SectorState sector, CelestialBodyState star, CelestialBodyState planet)
    {
        if (planet.Kind != BodyKind.Planet || planet.BulkMaterialId != CelestialBodyState.RockMaterial || planet.Orbit is not { } orbit) return;
        var light = star.SunlightFactor * SectorGenerator.HomeOrbitRadiusM * SectorGenerator.HomeOrbitRadiusM / (orbit.RadiusM * orbit.RadiusM);
        if (light is < TemperateMin or > TemperateMax) return;
        var rng = new DeterministicRandom(sector.Seed ^ (0x4C494645UL * planet.Id.Value));
        if (rng.NextDouble() < LifeChance) AddLife(planet, rng);
    }

    /// <summary>DEBUG and tests: gives a rocky body life.</summary>
    public void DebugAddLife(BodyId bodyId, FloraHazard? hazard = null)
    {
        var body = GetBody(bodyId);
        AddLife(body, new DeterministicRandom(Seed ^ (0x4C494645UL * bodyId.Value)));
        if (hazard is { } h) body.FloraHazard = h;
    }

    private static void AddLife(CelestialBodyState body, DeterministicRandom rng)
    {
        body.FloraHazard = (FloraHazard)(int)Math.Min(2, rng.NextDouble() * 3);
        foreach (var site in body.SiteList) site.Flora = rng.NextDouble() < 0.75 ? rng.Range(0.4, 1) : 0;
        if (body.Sites.Count > 0 && body.Sites.All(s => s.Flora <= 0)) body.SiteList[0].Flora = 0.8;
        body.HasLife = true;
    }

    /// <summary>Plants lost at a bearing because something was mined there.</summary>
    private void HarmLife(CelestialBodyState body, double bearing, long kg)
    {
        if (!body.HasLife || kg <= 0 || body.SiteAt(bearing) is not { Flora: > 0 } site) return;
        LoseFlora(body, site, kg / MinedKgClearsSite);
    }

    private void LoseFlora(CelestialBodyState body, SurfaceSite site, double cover)
    {
        var lost = Math.Min(site.Flora, cover);
        site.Flora -= lost;
        if (site.Flora < FloraGone) site.Flora = 0;
        if (lost > 0 && _factions.Count > 0 && ClaimantOf(body.Id) is { } owner) owner.Grievance = Math.Min(100, owner.Grievance + lost * GrievancePerSiteOfLife);
    }

    /// <summary>After a craft or swarm took something from a site: harvesting and mining both cost plants.</summary>
    private void NoteExtraction(CelestialBodyState body, string material, double bearing, long kg)
    {
        if (kg <= 0) return;
        if (material != BiomassMaterial) { HarmLife(body, bearing, kg); return; }
        // The harvest itself already came off the cover (CelestialBodyState.Extract).
        if (ClaimantOf(body.Id) is { } owner) owner.Grievance = Math.Min(100, owner.Grievance + kg / CelestialBodyState.FullFloraKg * GrievancePerSiteOfLife);
        if (body.LifeSampled) return;
        body.LifeSampled = true;
        ResearchPoints += LifeSampledPoints;
        AddEvent($"First samples of {body.Name}'s native life: {LifeSampledPoints:0} research points.");
    }

    /// <summary>Every ten seconds: living worlds are found, plants grow and spread, and corrosive ones wear at machines.</summary>
    private void StepLife()
    {
        if (Tick % FloraStepTicks != 0) return;
        foreach (var body in _bodies)
        {
            if (!body.HasLife) continue;
            if (!body.LifeFound && body.AnySiteSurveyed)
            {
                body.LifeFound = true;
                ResearchPoints += LifeFoundPoints;
                AddEvent($"Life found on {body.Name}: {LifeFoundPoints:0} research points."
                         + (body.FloraHazard switch { FloraHazard.Clinging => " Its plants cling to machines and slow them.", FloraHazard.Corrosive => " Its plants eat at machines.", _ => "" }));
            }
            GrowFlora(body);
            if (body.FloraHazard == FloraHazard.Corrosive) Corrode(body);
            if (body.Sites.Any(s => s.Flora > 0)) continue;
            body.HasLife = false;
            AddEvent($"The native life of {body.Name} has died out.");
        }
    }

    private static void GrowFlora(CelestialBodyState body)
    {
        const double minutes = FloraStepTicks * FixedStepClock.TickSeconds / 60;
        var sites = body.SiteList;
        var before = new double[sites.Count];
        for (var i = 0; i < before.Length; i++) before[i] = sites[i].Flora;
        for (var i = 0; i < before.Length; i++)
        {
            var c = before[i];
            var fullest = Math.Max(before[(i + 1) % before.Length], before[(i + before.Length - 1) % before.Length]);
            var growth = c * (1 - c) * FloraGrowthPerMinute + Math.Max(0, fullest - c) * FloraSpreadPerMinute;
            sites[i].Flora = Math.Min(1, c + growth * minutes);
        }
    }

    /// <summary>Corrosive plants wear the parts of craft anchored among them, one part at a time, down to a tenth of its health.</summary>
    private void Corrode(CelestialBodyState body)
    {
        if (Parts is null) return;
        foreach (var craft in _crafts)
        {
            if (!craft.IsAnchored || craft.LandedOnBodyId != body.Id || craft.IsWreck || SiteUnder(craft) is not { Flora: > 0 } site) continue;
            var cells = craft.Design.Cells;
            var cell = cells[(int)((Tick / FloraStepTicks + craft.Id.Value) % cells.Count)];
            var health = craft.HealthList.GetValueOrDefault(cell.Pos, 1);
            var worn = Math.Max(Math.Min(health, 0.1), health - 0.02 * site.Flora);
            if (worn >= health) continue;
            craft.HealthList[cell.Pos] = worn;
            craft.Stats = craft.Design.ComputeStats(Parts, craft.HealthList);
        }
    }
}
