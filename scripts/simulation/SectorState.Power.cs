using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

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
            craft.NetworkCapacityKJ = 0;
            craft.ConvertedKJThisTick = 0;
            var root = craft;
            for (var hops = 0; hops < 8 && root.IsDocked && _craftsById.TryGetValue(root.DockedToCraftId, out var host); hops++) root = host;
            craft.PowerRoot = root;
        }
        foreach (var craft in _crafts) // id order: members are listed deterministically
        {
            var root = craft.PowerRoot!;
            root.PowerMembers.Add(craft);
            craft.InShadow = craft.Stats.PowerGenerationKW > 0 && InShadowAt(craft.Position);
            craft.SolarFactor = craft.Stats.PowerGenerationKW > 0 ? SolarFactorAt(craft.Position) * (craft.InShadow ? ShadowLight : 1) : 1;
            craft.SolarKJThisTick = craft.Stats.PowerGenerationKW * craft.SolarFactor * FixedStepClock.TickSeconds;
            craft.GeothermalKJThisTick = craft.Stats.GeothermalKW > 0 && GeothermalHeat(craft) is null
                ? craft.Stats.GeothermalKW * FixedStepClock.TickSeconds : 0; // #77: steady while on a hot spot or near the star
            root.EnergyPoolKJ += craft.StoredEnergyKJ + craft.SolarKJThisTick + craft.GeothermalKJThisTick;
            root.NetworkStartKJ += craft.StoredEnergyKJ;
            root.NetworkCapacityKJ += craft.Stats.EnergyCapacityKJ;
        }
    }

    /// <summary>Bodies this close to their star (sunlight at least this many times Home's) are hot all over (#77).</summary>
    public const double HotSunlightFactor = 2.0;

    /// <summary>A body near enough its star that geothermal taps work anywhere on it (#77).</summary>
    public bool IsHotBody(CelestialBodyState body) =>
        body.Kind is BodyKind.Planet or BodyKind.Moon or BodyKind.Asteroid && SolarFactorAt(GetPosition(body.Id)) >= HotSunlightFactor;

    /// <summary>
    /// Why a craft's geothermal taps give nothing (#77), or null when they work: anchored on a hot surface site,
    /// or on any body close enough to its star.
    /// </summary>
    public string? GeothermalHeat(CraftState craft)
    {
        if (!craft.IsLanded || !craft.IsAnchored) return "needs to be anchored on a surface";
        var body = GetBody(craft.LandedOnBodyId);
        if (IsHotBody(body)) return null;
        if (SiteUnder(craft) is { IsHot: true }) return null;
        return $"no heat here: needs a hot spot (survey shows them) or a body nearer its star";
    }

    /// <summary>Share of sunlight that still reaches a craft in a body's shadow (#76: dimmed, not dark; the creator's choice).</summary>
    public const double ShadowLight = 0.25;

    /// <summary>True if a body (not the star) is between the point and its system's star (#76).</summary>
    public bool InShadowAt(Vector2D point)
    {
        if (StarAt(point) is not { } star) return false;
        var sun = GetPosition(star.Id);
        var positions = AllBodyPositions(TimeS);
        for (var i = 0; i < _bodies.Count; i++)
        {
            var body = _bodies[i];
            if (body.Kind == BodyKind.Star) continue;
            var centre = positions[i];
            // The body must be between the star and the point, and the line between them must pass through it.
            if (centre.DistanceTo(sun) >= point.DistanceTo(sun)) continue;
            if (!SegmentClears(sun, point, centre, body.RadiusM)) return true;
        }
        return false;
    }

    /// <summary>Converters (#78) run on spare power only: what's in the pool above this share of the network's batteries.</summary>
    public const double ConverterSpareAbove = 0.9;

    /// <summary>
    /// Converters (#78, e.g. electrolysers) turn whole batches of materials into others using spare power only:
    /// energy above <see cref="ConverterSpareAbove"/> of the network's battery capacity, never from generators, so
    /// they can't turn fuel into fuel. Progress on a batch is kept (saved); materials change form, mass stays exact.
    /// An electrolyser leaves the water the station's crew and farms need (its supply request).
    /// </summary>
    private void StepConverters()
    {
        foreach (var craft in _crafts)
        {
            if (craft.Stats.ConverterList.Count == 0 || !Operational(craft)) continue;
            var root = craft.PowerRoot ?? craft;
            foreach (var conv in craft.Stats.ConverterList)
            {
                var spare = root.EnergyPoolKJ - ConverterSpareAbove * root.NetworkCapacityKJ;
                if (spare <= 0) break;
                var progress = craft.ConverterProgressKJ.GetValueOrDefault(conv.PartId);
                if (progress <= 0 && !CanStartConversion(craft, conv)) continue;
                var use = Math.Min(Math.Min(spare, conv.KW * FixedStepClock.TickSeconds), conv.KJPerBatch - progress);
                root.EnergyPoolKJ -= use;
                craft.ConvertedKJThisTick += use;
                progress += use;
                if (progress + 1e-9 >= conv.KJPerBatch)
                {
                    if (craft.Inventory.TryRemove(conv.InputsKg)) craft.Inventory.Add(conv.OutputsKg);
                    progress = 0;
                }
                if (progress > 0) craft.ConverterProgressKJ[conv.PartId] = progress; else craft.ConverterProgressKJ.Remove(conv.PartId);
            }
        }
    }

    /// <summary>A converter starts a batch only with its inputs in stock beyond what the craft wants kept (its supply requests).</summary>
    private static bool CanStartConversion(CraftState craft, ConverterStats conv) =>
        conv.InputsKg.All(i => craft.Inventory[i.Key] - WantedKg(craft, i.Key) >= i.Value);

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
            if (craft.Inventory[SpentFuelMaterial] > 0) { EjectSpentFuel(craft); continue; } // spent fuel first, none kept (#79)
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
    /// <summary>What reactors leave (#79): it piles up in stock until shipped away or thrown out by a slag ejector.</summary>
    public const string SpentFuelMaterial = "spent_fuel";

    private void EjectSpentFuel(CraftState craft)
    {
        if (!TryUsePower(craft, craft.Stats.EjectorPowerKW * FixedStepClock.TickSeconds)) return;
        var due = craft.EjectionRemainderKg + craft.Stats.EjectionRateKgPerS * FixedStepClock.TickSeconds;
        var whole = Math.Min((long)Math.Floor(due), craft.Inventory[SpentFuelMaterial]);
        craft.Inventory.TryRemove(new Dictionary<string, long> { [SpentFuelMaterial] = whole });
        _ejectedKg[SpentFuelMaterial] = _ejectedKg.GetValueOrDefault(SpentFuelMaterial) + whole;
        craft.EjectionRemainderKg = due - Math.Floor(due);
    }

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
            foreach (var member in root.PowerMembers) made += member.SolarKJThisTick + member.GeothermalKJThisTick + member.GeneratedKJThisTick;
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
        if (stats.EnergyCapacityKJ <= 0 && stats.PowerGenerationKW <= 0 && stats.GeneratorKW <= 0 && stats.GeothermalKW <= 0 && stats.TotalPowerUseKW <= 0) return "";
        return $"power {craft.StoredEnergyKJ / 1000:0.0}/{stats.EnergyCapacityKJ / 1000:0} MJ, solar {stats.PowerGenerationKW * craft.SolarFactor:0} kW"
               + (craft.SolarFactor != 1 ? $" ({craft.SolarFactor * 100:0}% sunlight{(craft.InShadow ? ", in shadow" : "")})" : "")
               + (stats.GeneratorKW > 0 ? $", generators {craft.GeneratedKJThisTick / FixedStepClock.TickSeconds:0}/{stats.GeneratorKW:0} kW" : "")
               + (stats.GeothermalKW > 0 ? $", geothermal {craft.GeothermalKJThisTick / FixedStepClock.TickSeconds:0}/{stats.GeothermalKW:0} kW" : "") + ", "
               + $"max use {stats.TotalPowerUseKW:0} kW" + (craft.PowerShort ? "  NO POWER" : "");
    }
}
