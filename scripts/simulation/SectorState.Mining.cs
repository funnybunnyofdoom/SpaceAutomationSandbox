using System;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Drilling: a craft with drills, anchored on a body with a deposit, extracts material into its
/// cargo every tick at its drills' rate, until the cargo is full or the deposit is empty. Whole kg
/// move from the body (deposit and mass) to the cargo; the fraction of a kg waits in
/// <see cref="CraftState.MiningRemainderKg"/>, so nothing is created, lost or rounded away.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>The bearing of a landed craft from its body's centre (where it digs, and which site it's on).</summary>
    public static double LandedBearing(CraftState craft) => Math.Atan2(craft.LandedOffset.Y, craft.LandedOffset.X);

    /// <summary>The site a landed craft sits on (#70), or null.</summary>
    public SurfaceSite? SiteUnder(CraftState craft) =>
        craft.IsLanded ? GetBody(craft.LandedOnBodyId).SiteAt(LandedBearing(craft)) : null;

    /// <summary>What a craft would mine right now: its job's material, else the first ore at its site, else the bulk.</summary>
    public string? MiningMaterial(CraftState craft)
    {
        if (!craft.IsAnchored || !craft.IsLanded || (craft.Stats.MiningRateKgPerS <= 0 && craft.Stats.HarvestRateKgPerS <= 0)) return null;
        var body = GetBody(craft.LandedOnBodyId);
        var bearing = LandedBearing(craft);
        if (LawForbidsMining(body) is not null) return null; // the laws in force (#36)
        if (craft.Job is MiningJob { Active: true } job) return MineableAt(body, job.MaterialId, bearing) > 0 ? job.MaterialId : null;
        if (LifeProtected(body.SiteAt(bearing))) return null; // the law protects its plants (#91)
        if (craft.Stats.MiningRateKgPerS <= 0) return body.BiomassKg(body.SiteAt(bearing)) > 0 ? BiomassMaterial : null; // only a harvester (#92)
        // By hand: the ore at this site first (#70), then the body's bulk (#27) if the laws allow it.
        return body.SiteAt(bearing)?.DepositsKg.Keys.FirstOrDefault() ?? (body.BulkKg > 0 && BulkMiningAllowed ? body.BulkMaterialId : null);
    }

    private void StepMining()
    {
        foreach (var craft in _crafts)
        {
            var material = MiningMaterial(craft);
            if (material is null || craft.FreeCargoKg <= 0 || !Operational(craft)) continue;
            if (!TryUsePower(craft, (material == BiomassMaterial ? craft.Stats.HarvestPowerKW : craft.Stats.DrillPowerKW) * FixedStepClock.TickSeconds)) continue; // drill idle without power

            var drilled = craft.MiningRemainderKg + ExtractionRateKgPerS(craft, material) * Tuning.DrillRateMultiplier * FixedStepClock.TickSeconds;
            var whole = (long)Math.Floor(drilled);
            var body = GetBody(craft.LandedOnBodyId);
            var bearing = LandedBearing(craft);
            var taken = body.Extract(material, Math.Min(whole, craft.FreeCargoKg), bearing, BulkMiningAllowed); // from the site under the drill (#70)
            craft.Inventory.Add(material, taken);
            NoteMining(body, taken); // a grievance in a claimed system (#36)
            NoteExtraction(body, material, bearing, taken); // native life (#92)
            if (material != BiomassMaterial) body.Dig(bearing, taken); // a crater where it digs (#28)
            // Keep the unfinished fraction only while the drill can continue; a full hold or an
            // empty deposit stops the drill, and partly-cut material stays in the rock.
            craft.MiningRemainderKg = taken == whole ? drilled - whole : 0;
        }
    }
}
