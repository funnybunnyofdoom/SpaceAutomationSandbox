using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Tugs (#29): moving big structures with the ships docked to them.
/// <list type="bullet">
/// <item>Everything docked to a craft rides with it, so its mass counts: a structure flies as heavy as
/// it and everything docked to it.</item>
/// <item>An idle ship docked to it (no running job, crew and power to fly) is a tug. When the structure
/// is flown (the player flies it from the fleet list, or its own controls), each tug facing within
/// 60° of the push direction (thrust, reverse, strafe, or against its motion for the brake) adds its
/// thrust there, paid from its own power. A station without thrusters can be moved this way.</item>
/// <item>Craft anchored by a foundation never move; a clamped one lifts off once released (G); a parked
/// one leaves its parking orbit when pushed, like any craft on rails.</item>
/// </list>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>A tug helps if it faces within this angle's cosine of the push direction (60°).</summary>
    public const double TugAlignment = 0.5;

    /// <summary>Per tick: what is docked to each craft (its mass and its tugs). Built before the flight step.</summary>
    private readonly Dictionary<CraftId, (double MassKg, List<CraftState> Tugs)> _attached = new();

    /// <summary>Docked ships that can push: idle, crewed (or small), with thrust.</summary>
    public static bool IsTug(CraftState craft) =>
        craft.IsDocked && !craft.IsAutomated && craft.Stats.TotalThrustN > 0 && craft.JumpRequestTick == 0 && craft.YardOrder is null;

    private void BuildAttached()
    {
        foreach (var entry in _attached.Values) entry.Tugs.Clear();
        _attached.Clear();
        foreach (var craft in _crafts)
        {
            if (!craft.IsDocked) continue;
            var entry = _attached.TryGetValue(craft.DockedToCraftId, out var e) ? e : (0, new List<CraftState>());
            entry.MassKg += craft.MassKg;
            if (IsTug(craft)) entry.Tugs.Add(craft);
            _attached[craft.DockedToCraftId] = entry;
        }
    }

    /// <summary>Total mass that moves with a craft: itself and everything docked to it.</summary>
    public double MovingMassKg(CraftState craft) =>
        craft.MassKg + (_attached.TryGetValue(craft.Id, out var e) ? e.MassKg : 0);

    /// <summary>Tugs docked to a craft now (for display).</summary>
    public IReadOnlyList<CraftState> TugsOf(CraftId craftId) =>
        _attached.TryGetValue(craftId, out var e) ? e.Tugs : Array.Empty<CraftState>();

    /// <summary>
    /// The velocity change the tugs add this tick (applied after the craft's own step). For the brake
    /// it never overshoots the reference velocity.
    /// </summary>
    private Vector2D TugDeltaV(CraftState craft, Vector2D reference, double dt)
    {
        craft.TugThrustN = 0;
        if (!_attached.TryGetValue(craft.Id, out var entry) || entry.Tugs.Count == 0 || craft.IsAnchored || craft.IsDocked) return Vector2D.Zero;
        var controls = craft.Controls.Clamped();
        Vector2D direction;
        double demand, cap;
        if (controls.Brake)
        {
            var relative = craft.Velocity - reference;
            if (relative.Length < 1e-6) return Vector2D.Zero;
            direction = relative * (-1 / relative.Length);
            demand = 1;
            cap = relative.Length;
        }
        else
        {
            var push = craft.Forward * controls.Throttle + FlightModel.RightFromHeading(craft.HeadingRad) * controls.Strafe;
            if (push.Length < 1e-6) return Vector2D.Zero;
            direction = push * (1 / push.Length);
            demand = Math.Min(1, push.Length);
            cap = double.PositiveInfinity;
        }

        var mass = MovingMassKg(craft);
        double force = 0;
        foreach (var tug in entry.Tugs)
        {
            if (!Operational(tug)) continue;
            var forward = tug.Forward;
            var alignment = forward.X * direction.X + forward.Y * direction.Y;
            if (alignment < TugAlignment) continue;
            if (!TryUsePower(tug, tug.Stats.ThrustPowerKW * demand * dt)) continue;
            force += tug.Stats.TotalThrustN * demand * alignment;
        }
        craft.TugThrustN = force;
        return direction * Math.Min(force / mass * dt, cap);
    }
}
