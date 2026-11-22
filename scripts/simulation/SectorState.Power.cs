using System;
using System.Collections.Generic;

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

    private void BeginPowerTick()
    {
        foreach (var craft in _crafts)
        {
            craft.PowerShort = false;
            craft.SolarFactor = craft.Stats.PowerGenerationKW > 0 ? SolarFactorAt(craft.Position) : 1;
            craft.EnergyPoolKJ = craft.StoredEnergyKJ + craft.Stats.PowerGenerationKW * craft.SolarFactor * FixedStepClock.TickSeconds;
        }
    }

    /// <summary>Takes <paramref name="kJ"/> from the craft's pool if it can; otherwise marks it short and takes nothing.</summary>
    private bool TryUsePower(CraftState craft, double kJ)
    {
        if (kJ <= 0 || craft.LegacyFreePower) return true;
        if (craft.EnergyPoolKJ + 1e-9 < kJ)
        {
            craft.PowerShort = true;
            return false;
        }
        craft.EnergyPoolKJ -= kJ;
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
            if (craft.Stats.EjectionRateKgPerS <= 0) continue;
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

    private void EndPowerTick()
    {
        // Charge batteries; remember each station's leftover for its docked ships.
        Dictionary<CraftId, double>? leftovers = null;
        foreach (var craft in _crafts)
        {
            var capacity = craft.Stats.EnergyCapacityKJ;
            craft.StoredEnergyKJ = Math.Min(capacity, craft.EnergyPoolKJ);
            var leftover = craft.EnergyPoolKJ - craft.StoredEnergyKJ;
            if (leftover > 0 && craft.Stats.DockingPorts.Count > 0) (leftovers ??= new())[craft.Id] = leftover;
        }
        if (leftovers is null) return;

        // Docked ships recharge from their station's spare power, in id order (deterministic).
        foreach (var ship in _crafts)
        {
            if (!ship.IsDocked || !leftovers.TryGetValue(ship.DockedToCraftId, out var spare) || spare <= 0) continue;
            var room = ship.Stats.EnergyCapacityKJ - ship.StoredEnergyKJ;
            var given = Math.Min(room, spare);
            ship.StoredEnergyKJ += given;
            leftovers[ship.DockedToCraftId] = spare - given;
        }
    }

    /// <summary>Human-readable power state for HUD and panels.</summary>
    public static string PowerStatus(CraftState craft)
    {
        if (craft.LegacyFreePower) return "power: unlimited (old save)";
        var stats = craft.Stats;
        if (stats.EnergyCapacityKJ <= 0 && stats.PowerGenerationKW <= 0 && stats.TotalPowerUseKW <= 0) return "";
        return $"power {craft.StoredEnergyKJ / 1000:0.0}/{stats.EnergyCapacityKJ / 1000:0} MJ, solar {stats.PowerGenerationKW * craft.SolarFactor:0} kW"
               + (craft.SolarFactor != 1 ? $" ({craft.SolarFactor * 100:0}% sunlight)" : "") + ", "
               + $"max use {stats.TotalPowerUseKW:0} kW" + (craft.PowerShort ? "  NO POWER" : "");
    }
}
