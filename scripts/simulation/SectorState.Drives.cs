using System;
using System.Collections.Generic;
using System.Linq;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Propellant drives (#80): chemical rockets and ion drives push like thrusters but burn propellant from the
/// craft's own stock while they push, in proportion to how hard they're used. A drive with an empty tank gives no
/// thrust (electric thrusters still work). Burned propellant leaves as exhaust (ejected mass) in whole kg, the
/// fraction kept for next time, so the mass ledger stays exact.
/// </summary>
public sealed partial class SectorState
{
    /// <summary>A drive is dry when any of its propellants is out of stock.</summary>
    private static bool Dry(CraftState craft, PropellantDrive drive) => drive.KgPerS.Keys.Any(m => craft.Inventory[m] <= 0);

    /// <summary>The thrust to fly with this tick: the craft's own, less any drives with an empty tank. Null when nothing is dry.</summary>
    private ShipStats? DriveThrust(CraftState craft)
    {
        var drives = craft.Stats.DriveList;
        if (drives.Count == 0) { craft.DryDrives = Array.Empty<string>(); return null; }
        List<PropellantDrive>? dry = null;
        foreach (var drive in drives) if (Dry(craft, drive)) (dry ??= new()).Add(drive);
        craft.DryDrives = dry is null ? Array.Empty<string>() : dry.Select(d => d.PartId).Distinct().ToList();
        if (dry is null) return null;
        double Less(int direction) => dry.Where(d => d.Direction == direction).Sum(d => d.ThrustN);
        var s = craft.Stats;
        return s with
        {
            TotalThrustN = Math.Max(0, s.TotalThrustN - Less(0)),
            RightThrustN = Math.Max(0, s.RightThrustN - Less(1)),
            ReverseThrustN = Math.Max(0, s.ReverseThrustN - Less(2)),
            LeftThrustN = Math.Max(0, s.LeftThrustN - Less(3)),
        };
    }

    /// <summary>How hard the drives facing each way pushed this tick (0..1): throttle, strafe, or braking (forward drives).</summary>
    private static double DriveUse(ShipControls controls, int direction, bool braking) => direction switch
    {
        0 => braking ? 1 : Math.Max(0, controls.Throttle),
        2 => braking ? 0 : Math.Max(0, -controls.Throttle),
        1 => braking ? 0 : Math.Max(0, controls.Strafe),
        _ => braking ? 0 : Math.Max(0, -controls.Strafe),
    };

    private void BurnPropellant(CraftState craft, Vector2D brakeReference, Vector2D velocityBefore)
    {
        var drives = craft.Stats.DriveList;
        if (drives.Count == 0) return;
        var controls = craft.Controls.Clamped();
        var braking = controls.Brake && (velocityBefore - brakeReference).Length > 1e-6;
        foreach (var drive in drives)
        {
            var use = DriveUse(controls, drive.Direction, braking);
            if (use <= 0 || Dry(craft, drive)) continue;
            foreach (var (material, perSecond) in drive.KgPerS)
            {
                var debt = craft.PropellantDebtKg.GetValueOrDefault(material) + perSecond * use * FixedStepClock.TickSeconds;
                var whole = (long)Math.Floor(debt);
                if (whole > 0)
                {
                    var taken = Math.Min(whole, craft.Inventory[material]);
                    craft.Inventory.TryRemove(new Dictionary<string, long> { [material] = taken });
                    _ejectedKg[material] = _ejectedKg.GetValueOrDefault(material) + taken;
                    debt = taken == whole ? debt - whole : 0; // ran out mid-tick: the rest of the burn didn't happen
                }
                if (debt > 0) craft.PropellantDebtKg[material] = debt; else craft.PropellantDebtKg.Remove(material);
            }
        }
    }

    /// <summary>The craft's propellant drives and what's left for them (#80), for the HUD; "" without any.</summary>
    public static string PropellantStatus(CraftState craft)
    {
        var drives = craft.Stats.DriveList;
        if (drives.Count == 0) return "";
        var parts = new List<string>();
        foreach (var group in drives.GroupBy(d => d.PartId))
        {
            var rates = new Dictionary<string, double>();
            foreach (var d in group) foreach (var (m, kg) in d.KgPerS) rates[m] = rates.GetValueOrDefault(m) + kg;
            var seconds = rates.Min(r => craft.Inventory[r.Key] / r.Value);
            var name = group.Key.Replace('_', ' ');
            parts.Add(craft.DryDrives.Contains(group.Key)
                ? $"{name} DRY (needs {string.Join(" + ", rates.Keys)})"
                : $"{name}: {string.Join(", ", rates.Keys.Select(m => $"{craft.Inventory[m] / 1000.0:0.##} t {m}"))} ({(seconds >= 3600 ? $"{seconds / 3600:0.#} h" : $"{seconds / 60:0} min")} at full thrust)");
        }
        return "drives: " + string.Join("; ", parts);
    }
}
