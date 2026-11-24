using System;
using System.Collections.Generic;
using System.Linq;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Power (#10) and slag ejection (#7).
/// Each tick a craft's energy pool is its battery charge plus this tick's solar generation. Thrust,
/// drills, refineries, fabricators and ejectors each take their share from the pool (in that order)
/// if there is enough; otherwise they don't run this tick and the craft is marked short of power.
/// What's left recharges the battery up to its capacity; a station's leftover then recharges the
/// ships docked at it. Energy is in kJ (kW × s). Craft from saves made before power existed have
/// <see cref="CraftState.LegacyFreePower"/> and never run short.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>Slag kept back from ejection, because hull plates and foundations are built from it.</summary>
    public const long SlagReserveKg = 20_000;

    private readonly SortedDictionary<string, long> _ejectedKg = new(StringComparer.Ordinal);

    /// <summary>Mass thrown out of the sector by ejectors, by material (whole kg). Part of the mass ledger.</summary>
    public IReadOnlyDictionary<string, long> EjectedKg => _ejectedKg;

    /// <summary>Restores the ejected-mass ledger from a save.</summary>
    public void RestoreEjected(string materialId, long kg)
    {
        if (kg < 0) throw new ArgumentOutOfRangeException(nameof(kg));
        if (kg > 0) _ejectedKg[materialId] = kg;
    }

    /// <summary>
    /// Builds this tick's power networks (#75): a craft docked to another draws from that one's pool (followed to
    /// the end of the chain), so a station and its docked ships share generation, generators and batteries.
    /// </summary>
    private void BeginPowerTick()
    {
        foreach (var craft in _crafts)
        {
            craft.PowerShort = false;
            craft.PowerMembers.Clear();
            craft.EnergyPoolKJ = 0;
            craft.GeneratedKJThisTick = 0;
            craft.NetworkStartKJ = 0;
            var root = craft;
            for (var hops = 0; hops < 8 && root.IsDocked && _craftsById.TryGetValue(root.DockedToCraftId, out var host); hops++) root = host;
            craft.PowerRoot = root;
        }
        foreach (var craft in _crafts) // id order: members are listed deterministically
        {
            var root = craft.PowerRoot!;
            root.PowerMembers.Add(craft);
            craft.SolarFactor = craft.Stats.PowerGenerationKW > 0 ? SolarFactorAt(craft.Position) : 1;
            craft.SolarKJThisTick = craft.Stats.PowerGenerationKW * craft.SolarFactor * FixedStepClock.TickSeconds;
            root.EnergyPoolKJ += craft.StoredEnergyKJ + craft.SolarKJThisTick;
            root.NetworkStartKJ += craft.StoredEnergyKJ;
        }
    }

    /// <summary>The craft whose pool another draws from (its network's root, #75); itself before the first tick.</summary>
    public CraftState PowerRootOf(CraftState craft) => craft.PowerRoot ?? craft;

    /// <summary>The craft sharing a root's pool, in id order.</summary>
    public IReadOnlyList<CraftState> PowerMembersOf(CraftState root) => root.PowerMembers.Count > 0 ? root.PowerMembers : new[] { root };

    /// <summary>The energy a craft's network has left this tick (kJ).</summary>
    public double PowerPoolKJ(CraftState craft) => (craft.PowerRoot ?? craft).EnergyPoolKJ;

    /// <summary>
    /// Makes sure the craft's network has <paramref name="kJ"/> this tick, running its generators for what's
    /// missing (#75). True if it has it now. Nothing is spent.
    /// </summary>
    private bool EnsurePower(CraftState craft, double kJ)
    {
        var root = craft.PowerRoot ?? craft;
        if (root.EnergyPoolKJ + 1e-9 >= kJ) return true;
        root.EnergyPoolKJ += Generate(root, kJ - root.EnergyPoolKJ);
        return root.EnergyPoolKJ + 1e-9 >= kJ;
    }

    /// <summary>
    /// Runs the network's generators for up to <paramref name="neededKJ"/> (members in id order, each up to its
    /// rating this tick), burning whole batches from the generator craft's own stock. Returns the energy made.
    /// What's burned leaves as exhaust (ejected mass) or as the generator's products in stock, so mass stays exact.
    /// </summary>
    private double Generate(CraftState root, double neededKJ)
    {
        double made = 0;
        foreach (var craft in root.PowerMembers.Count > 0 ? root.PowerMembers : new List<CraftState> { root })
        {
            if (made >= neededKJ - 1e-9) break;
            if (craft.Stats.GeneratorList.Count == 0 || !Operational(craft)) continue;
            foreach (var gen in craft.Stats.GeneratorList)
            {
                var want = neededKJ - made;
                var capacity = gen.KW * FixedStepClock.TickSeconds - craft.GeneratorTickUseKJ(gen.PartId);
                if (want <= 1e-9 || capacity <= 1e-9) continue;
                var give = Math.Min(want, capacity);
                var bank = craft.GeneratorBankKJ.GetValueOrDefault(gen.PartId);
                while (bank + 1e-9 < give && craft.Inventory.TryRemove(gen.BurnsKg)) // burn another whole batch
                {
                    bank += gen.KJPerBatch;
                    foreach (var (material, kg) in gen.MakesKg) craft.Inventory.Add(material, kg);
                    var exhaust = gen.ExhaustKg;
                    if (exhaust > 0) // exhaust is recorded as the burned material (like cruise fuel)
                    {
                        var exhaustMaterial = gen.BurnsKg.Keys.First();
                        _ejectedKg[exhaustMaterial] = _ejectedKg.GetValueOrDefault(exhaustMaterial) + exhaust;
                    }
                }
                give = Math.Min(give, bank);
                if (give <= 0) continue;
                bank -= give;
                if (bank > 1e-9) craft.GeneratorBankKJ[gen.PartId] = bank; else craft.GeneratorBankKJ.Remove(gen.PartId);
                craft.AddGeneratorTickUse(gen.PartId, give);
                craft.GeneratedKJThisTick += give;
                made += give;
            }
        }
        return made;
    }

    /// <summary>Takes <paramref name="kJ"/> from the craft's network if it can (running generators for the rest); otherwise marks it short.</summary>
    private bool TryUsePower(CraftState craft, double kJ)
    {
        if (kJ <= 0 || craft.LegacyFreePower) return true;
        if (!EnsurePower(craft, kJ))
        {
            craft.PowerShort = true;
            return false;
        }
        (craft.PowerRoot ?? craft).EnergyPoolKJ -= kJ;
        return true;
    }

    /// <summary>Energy the craft's thrusters need this tick for its controls (brake counts as full thrust while it has work to do).</summary>
    private double ThrustEnergyKJ(CraftState craft, Vector2D brakeReference)
    {
        var controls = craft.Controls.Clamped();
        var stats = craft.Stats;
        if (controls.Brake) return (craft.Velocity - brakeReference).Length > 1e-6 ? stats.ThrustPowerKW * FixedStepClock.TickSeconds : 0;
        var kw = controls.Throttle >= 0 ? stats.ThrustPowerKW * controls.Throttle : stats.ReversePowerKW * -controls.Throttle;
        kw += controls.Strafe >= 0 ? stats.RightPowerKW * controls.Strafe : stats.LeftPowerKW * -controls.Strafe;
        return kw * FixedStepClock.TickSeconds;
    }

    private void StepEjection()
    {
        foreach (var craft in _crafts)
        {
            if (craft.Stats.EjectionRateKgPerS <= 0 || !Operational(craft)) continue;
            var surplus = craft.Inventory["slag"] - SlagReserveKg;
            if (surplus <= 0) { craft.EjectionRemainderKg = 0; continue; }
            if (!TryUsePower(craft, craft.Stats.EjectorPowerKW * FixedStepClock.TickSeconds)) continue;

            var due = craft.EjectionRemainderKg + craft.Stats.EjectionRateKgPerS * FixedStepClock.TickSeconds;
            var whole = Math.Min((long)Math.Floor(due), surplus);
            craft.Inventory.TryRemove(new Dictionary<string, long> { ["slag"] = whole });
            _ejectedKg["slag"] = (_ejectedKg.TryGetValue("slag", out var kg) ? kg : 0) + whole;
            craft.EjectionRemainderKg = due - Math.Floor(due);
        }
    }

    /// <summary>
    /// Ends the tick's power (#75): generators top the batteries up if there's room (so they run only on demand),
    /// then each network's energy is shared out to its members' batteries in proportion to their capacity.
    /// </summary>
    private void EndPowerTick()
    {
        foreach (var root in _crafts)
        {
            if (root.PowerRoot != root) continue;
            double capacity = 0;
            foreach (var member in root.PowerMembers) capacity += member.Stats.EnergyCapacityKJ;
            if (capacity > root.EnergyPoolKJ) root.EnergyPoolKJ += Generate(root, capacity - root.EnergyPoolKJ);
            var pool = root.EnergyPoolKJ;
            double made = 0;
            foreach (var member in root.PowerMembers) made += member.SolarKJThisTick + member.GeneratedKJThisTick;
            root.NetworkUsedKJThisTick = Math.Max(0, root.NetworkStartKJ + made - pool);
            foreach (var member in root.PowerMembers)
            {
                var cap = member.Stats.EnergyCapacityKJ;
                member.StoredEnergyKJ = capacity > 0 ? Math.Min(cap, pool * cap / capacity) : 0;
                member.ClearGeneratorTickUse();
            }
        }
    }

    /// <summary>Human-readable power state for HUD and panels.</summary>
    public static string PowerStatus(CraftState craft)
    {
        if (craft.LegacyFreePower) return "power: unlimited (old save)";
        var stats = craft.Stats;
        if (stats.EnergyCapacityKJ <= 0 && stats.PowerGenerationKW <= 0 && stats.GeneratorKW <= 0 && stats.TotalPowerUseKW <= 0) return "";
        return $"power {craft.StoredEnergyKJ / 1000:0.0}/{stats.EnergyCapacityKJ / 1000:0} MJ, solar {stats.PowerGenerationKW * craft.SolarFactor:0} kW"
               + (craft.SolarFactor != 1 ? $" ({craft.SolarFactor * 100:0}% sunlight)" : "")
               + (stats.GeneratorKW > 0 ? $", generators {craft.GeneratedKJThisTick / FixedStepClock.TickSeconds:0}/{stats.GeneratorKW:0} kW" : "") + ", "
               + $"max use {stats.TotalPowerUseKW:0} kW" + (craft.PowerShort ? "  NO POWER" : "");
    }
}
