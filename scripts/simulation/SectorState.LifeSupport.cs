using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Crew and life support (#11).
/// <list type="bullet">
/// <item>Small craft (up to <see cref="Construction.ShipStats.UncrewedMaxParts"/> parts) run unmanned. Bigger ones
/// need <see cref="Construction.ShipStats.CrewRequired"/> crew aboard and supplied, or they can't fly or
/// work (no thrust, drill, refinery, fabricator or ejector) and say why.</item>
/// <item>Crew live in habitats (<see cref="Construction.ShipStats.CrewCapacity"/>), which use power, and eat food
/// and drink water from the craft's own stock (<see cref="FoodKgPerCrewPerS"/>, <see cref="WaterKgPerCrewPerS"/>).
/// What they use is recorded in <see cref="EjectedKg"/> (consumed), so the mass ledger balances.
/// Without supplies the crew stop working; nobody dies.</item>
/// <item>A supplied habitat grows its population by one every <see cref="CrewGrowthS"/> up to capacity.</item>
/// <item>Farms turn water into the same mass of food, using power.</item>
/// <item>Crew move between craft docked together (<see cref="TryTransferCrew"/>).</item>
/// </list>
/// Craft from saves older than v14 have <see cref="CraftState.LegacyNoCrew"/>: no crew rules apply to them.
/// </summary>
public sealed partial class SectorState
{
    public const string FoodMaterial = "food";
    public const string WaterMaterial = "water";

    /// <summary>3 kg of food per crew member per hour.</summary>
    public const double FoodKgPerCrewPerS = 3.0 / 3600;

    /// <summary>6 kg of water per crew member per hour (drinking, and oxygen made from it).</summary>
    public const double WaterKgPerCrewPerS = 6.0 / 3600;

    /// <summary>A supplied habitat with room gains a crew member this often.</summary>
    public const double CrewGrowthS = 600;

    /// <summary>True if the craft can fly and work this tick (crew-wise).</summary>
    public static bool Operational(CraftState craft) => craft.CrewShortReason is null;

    /// <summary>Hours the craft's food and water last for its crew at the current rate (∞ without crew).</summary>
    public static double SuppliesLastHours(CraftState craft)
    {
        if (craft.Crew <= 0) return double.PositiveInfinity;
        var food = craft.Inventory[FoodMaterial] / (craft.Crew * FoodKgPerCrewPerS);
        var water = craft.Inventory[WaterMaterial] / (craft.Crew * WaterKgPerCrewPerS);
        return Math.Min(food, water) / 3600;
    }

    private void StepLifeSupport()
    {
        foreach (var craft in _crafts)
        {
            StepFarm(craft);
            if (craft.LegacyNoCrew) { craft.CrewShortReason = null; continue; }

            string? reason = null;
            var supplied = false;
            if (craft.Crew > 0)
            {
                if (!TryUsePower(craft, craft.Stats.HabitatPowerKW * FixedStepClock.TickSeconds)) reason = "no power for life support";
                else if (!Consume(craft, FoodMaterial, craft.FoodRemainderKg, craft.Crew * FoodKgPerCrewPerS, out var food)) reason = "no food";
                else if (!Consume(craft, WaterMaterial, craft.WaterRemainderKg, craft.Crew * WaterKgPerCrewPerS, out var water)) { reason = "no water"; craft.FoodRemainderKg = food; }
                else { craft.FoodRemainderKg = food; craft.WaterRemainderKg = water; supplied = true; }
            }

            // Growth: a supplied habitat with room slowly gains crew.
            if (supplied && craft.Crew < craft.Stats.CrewCapacity)
            {
                craft.CrewGrowthProgressS += FixedStepClock.TickSeconds;
                if (craft.CrewGrowthProgressS >= CrewGrowthS) { craft.Crew++; craft.CrewGrowthProgressS = 0; }
            }
            else craft.CrewGrowthProgressS = 0;

            var required = craft.Stats.CrewRequired;
            if (required == 0) { craft.CrewShortReason = null; continue; }
            if (craft.Crew < required) reason = $"needs {required} crew (has {craft.Crew})";
            craft.CrewShortReason = reason;
        }
    }

    /// <summary>Takes this tick's share of a supply (whole kg, the fraction carried on). False (and takes nothing) if it's not there.</summary>
    private bool Consume(CraftState craft, string material, double remainder, double kgPerS, out double newRemainder)
    {
        newRemainder = remainder;
        var due = remainder + kgPerS * FixedStepClock.TickSeconds;
        var whole = (long)Math.Floor(due);
        if (craft.Inventory[material] < Math.Max(whole, 1)) return false;
        if (whole > 0)
        {
            craft.Inventory.TryRemove(new Dictionary<string, long> { [material] = whole });
            _ejectedKg[material] = (_ejectedKg.TryGetValue(material, out var kg) ? kg : 0) + whole;
        }
        newRemainder = due - whole;
        return true;
    }

    private void StepFarm(CraftState craft)
    {
        var rate = craft.Stats.FoodRateKgPerS;
        if (rate <= 0 || !Operational(craft) || craft.Inventory[WaterMaterial] < 1) return;
        if (!TryUsePower(craft, craft.Stats.FarmPowerKW * FixedStepClock.TickSeconds)) return;
        var due = craft.FarmRemainderKg + rate * FixedStepClock.TickSeconds;
        var whole = Math.Min((long)Math.Floor(due), craft.Inventory[WaterMaterial]);
        if (whole > 0)
        {
            craft.Inventory.TryRemove(new Dictionary<string, long> { [WaterMaterial] = whole });
            craft.Inventory.Add(FoodMaterial, whole);
        }
        craft.FarmRemainderKg = due - Math.Floor(due);
    }

    /// <summary>Moves crew between two craft docked together, if the receiver has habitat room.</summary>
    public ActionResult TryTransferCrew(CraftId fromId, CraftId toId, int count)
    {
        var from = GetCraft(fromId);
        var to = GetCraft(toId);
        if (count <= 0) return new(false, "No one to move.");
        if (from.DockedToCraftId != to.Id && to.DockedToCraftId != from.Id)
            return new(false, $"{from.Name} and {to.Name} must be docked together to move crew.");
        if (from.Crew < count) return new(false, $"{from.Name} has only {from.Crew} crew.");
        var room = to.Stats.CrewCapacity - to.Crew;
        if (room < count) return new(false, $"{to.Name} has habitat room for {Math.Max(0, room)} more.");
        from.Crew -= count;
        to.Crew += count;
        return new(true, $"Moved {count} crew from {from.Name} to {to.Name}.");
    }

    /// <summary>Restores saved crew state.</summary>
    public void RestoreCrew(CraftId craftId, int crew, double foodRemainder, double waterRemainder, double farmRemainder, double growthS, bool legacyNoCrew)
    {
        if (crew < 0) throw new ArgumentOutOfRangeException(nameof(crew));
        if (foodRemainder is < 0 or >= 1 || waterRemainder is < 0 or >= 1 || farmRemainder is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(foodRemainder), "Supply remainders must be 0..1 kg.");
        if (!(growthS >= 0)) throw new ArgumentOutOfRangeException(nameof(growthS));
        var craft = GetCraft(craftId);
        craft.Crew = crew;
        craft.FoodRemainderKg = foodRemainder;
        craft.WaterRemainderKg = waterRemainder;
        craft.FarmRemainderKg = farmRemainder;
        craft.CrewGrowthProgressS = growthS;
        craft.LegacyNoCrew = legacyNoCrew;
    }

    /// <summary>Plain words for HUD and panels: crew, need, supplies and why it can't work.</summary>
    public static string CrewStatus(CraftState craft)
    {
        var stats = craft.Stats;
        if (craft.LegacyNoCrew || (stats.CrewCapacity == 0 && stats.CrewRequired == 0 && craft.Crew == 0)) return "";
        var text = $"crew {craft.Crew}/{stats.CrewCapacity}" + (stats.CrewRequired > 0 ? $" (needs {stats.CrewRequired})" : "");
        if (craft.Crew > 0)
        {
            var hours = SuppliesLastHours(craft);
            text += $", food {craft.Inventory[FoodMaterial] / 1000.0:0.0} t, water {craft.Inventory[WaterMaterial] / 1000.0:0.0} t"
                    + (double.IsFinite(hours) ? $" ({hours:0} h)" : "");
        }
        return text + (craft.CrewShortReason is { } why ? $"  STOPPED: {why}" : "");
    }
}
