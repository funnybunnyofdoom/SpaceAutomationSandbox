using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Crafts held on fixed circular orbits ("on rails"): orbiting stations and parked ships.</summary>
public sealed partial class SectorState
{
    /// <summary>Puts a craft on a fixed orbit and snaps it there now. Used for new stations, parking and save restore.</summary>
    public void PutOnRails(CraftId craftId, CircularOrbit orbit)
    {
        var craft = GetCraft(craftId);
        GetBody(orbit.Parent); // throws for unknown parents
        if (!(orbit.RadiusM > 0) || !(orbit.PeriodS > 0)) throw new ArgumentException("Orbit needs positive radius and period.", nameof(orbit));
        if (craft.IsLanded) throw new InvalidOperationException($"{craft.Id} is landed; it can't also be on rails.");

        craft.RailOrbit = orbit;
        craft.Position = craft.PreviousPosition = RailPositionAt(orbit, TimeS);
        craft.Velocity = RailVelocityAt(orbit, TimeS);
    }

    /// <summary>Restores a saved rail orbit without moving the craft (its saved position is already on it).</summary>
    public void RestoreRails(CraftId craftId, CircularOrbit orbit)
    {
        var craft = GetCraft(craftId);
        GetBody(orbit.Parent);
        if (!(orbit.RadiusM > 0) || !(orbit.PeriodS > 0)) throw new ArgumentException("Orbit needs positive radius and period.", nameof(orbit));
        if (craft.IsLanded) throw new InvalidOperationException($"{craft.Id} is landed; it can't also be on rails.");
        craft.RailOrbit = orbit;
    }

    public Vector2D RailPositionAt(CircularOrbit orbit, double timeS) => GetPositionAt(orbit.Parent, timeS) + orbit.OffsetAt(timeS);

    public Vector2D RailVelocityAt(CircularOrbit orbit, double timeS) => GetVelocityAt(orbit.Parent, timeS) + orbit.VelocityAt(timeS);

    // Same rule as landed craft: only the craft's own throttle releases it. Brake, and craft without
    // thrust (stations), stay put.
    private void UpdateOnRails(CraftState craft, Vector2D ownDeltaV, double endS)
    {
        var orbit = craft.RailOrbit!;
        var railPos = RailPositionAt(orbit, endS);
        var railVel = RailVelocityAt(orbit, endS);

        if (!craft.Controls.Clamped().Brake && ownDeltaV.Length > 0)
        {
            craft.RailOrbit = null;
            craft.Velocity = railVel + ownDeltaV;
            craft.Position = railPos + ownDeltaV * FixedStepClock.TickSeconds;
            return;
        }

        craft.Velocity = railVel;
        craft.Position = railPos;
    }
}
