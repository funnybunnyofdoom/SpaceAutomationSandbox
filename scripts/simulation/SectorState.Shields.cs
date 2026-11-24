using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Energy shields (#90; the creator's choices: one bubble round the whole craft, stopping weapons only, not crashes or
/// landings). Every working generator adds to one pool of charge, which takes weapon damage before any part does. A shield uses a
/// little power to stay up and more to recharge, which starts <see cref="ShieldRechargeDelayS"/> after the last
/// hit. Without power it collapses.
/// </summary>
public sealed partial class SectorState
{
    public const double ShieldRechargeDelayS = 4;

    /// <summary>Power a shield uses to stay up, as a share of what it uses to recharge.</summary>
    public const double ShieldUpkeepShare = 0.2;

    /// <summary>How fast a shield without power collapses, as a share of its capacity per second.</summary>
    public const double ShieldCollapsePerS = 0.5;

    private void StepShields()
    {
        foreach (var craft in _crafts)
        {
            var capacity = craft.IsWreck ? 0 : craft.Stats.ShieldCapacity;
            craft.ShieldPowerless = false;
            if (capacity <= 0) { craft.ShieldCharge = 0; continue; }
            craft.ShieldCharge = Math.Min(craft.ShieldCharge, capacity);
            var dt = FixedStepClock.TickSeconds;
            if (!Operational(craft) || !TryUsePower(craft, craft.Stats.ShieldPowerKW * ShieldUpkeepShare * dt))
            {
                craft.ShieldPowerless = true;
                craft.ShieldCharge = Math.Max(0, craft.ShieldCharge - capacity * ShieldCollapsePerS * dt);
                continue;
            }
            if (craft.ShieldCharge >= capacity || Tick < craft.ShieldHitTick + (long)(ShieldRechargeDelayS * FixedStepClock.TicksPerSecond)) continue;
            if (TryUsePower(craft, craft.Stats.ShieldPowerKW * (1 - ShieldUpkeepShare) * dt))
                craft.ShieldCharge = Math.Min(capacity, craft.ShieldCharge + craft.Stats.ShieldRechargePerS * dt);
        }
    }

    /// <summary>Takes weapon damage off the shield first; returns what gets through.</summary>
    private double AbsorbWithShield(CraftState craft, double damage)
    {
        if (craft.ShieldCharge <= 0) return damage;
        craft.ShieldHitTick = Tick;
        var absorbed = Math.Min(craft.ShieldCharge, damage);
        craft.ShieldCharge -= absorbed;
        return damage - absorbed;
    }
}
