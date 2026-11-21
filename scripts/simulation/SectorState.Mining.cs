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
    /// <summary>What a craft would mine right now: its job's material, else the body's first deposit.</summary>
    public string? MiningMaterial(CraftState craft)
    {
        if (!craft.IsAnchored || !craft.IsLanded || craft.Stats.MiningRateKgPerS <= 0) return null;
        var body = GetBody(craft.LandedOnBodyId);
        if (craft.Job is MiningJob { Active: true } job) return body.Deposit(job.MaterialId) > 0 ? job.MaterialId : null;
        return body.DepositsKg.Keys.FirstOrDefault();
    }

    private void StepMining()
    {
        foreach (var craft in _crafts)
        {
            var material = MiningMaterial(craft);
            if (material is null || craft.FreeCargoKg <= 0) continue;

            var drilled = craft.MiningRemainderKg + craft.Stats.MiningRateKgPerS * Tuning.DrillRateMultiplier * FixedStepClock.TickSeconds;
            var whole = (long)Math.Floor(drilled);
            var taken = GetBody(craft.LandedOnBodyId).Extract(material, Math.Min(whole, craft.FreeCargoKg));
            craft.Inventory.Add(material, taken);
            // Keep the unfinished fraction only while the drill can continue; a full hold or an
            // empty deposit stops the drill, and partly-cut material stays in the rock.
            craft.MiningRemainderKg = taken == whole ? drilled - whole : 0;
        }
    }
}
