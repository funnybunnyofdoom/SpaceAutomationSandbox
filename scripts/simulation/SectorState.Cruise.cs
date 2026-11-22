using System;
using System.Collections.Generic;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Cruise drive (#53): for crossing the gaps between planets. Far from every body a ship can switch
/// to cruise: it moves at <see cref="CruiseSpeedMps"/> along its heading (steering at half the
/// normal turn rate, no thrust), burning fuel from its hold. It drops out on its own
/// <see cref="CruiseDropoutM"/> from any surface, when the fuel runs out, or when braking, and
/// arrives matched to that body's velocity (PLACEHOLDER flight assist: no gravity yet, #32).
/// Burned fuel leaves the sector and is recorded in <see cref="EjectedKg"/>, so mass is conserved.
/// Job autopilots cruise legs longer than <see cref="AutopilotCruiseMinDistanceM"/> when the way is clear.
/// </summary>
public sealed partial class SectorState
{
    public const double CruiseSpeedMps = 1_000;

    /// <summary>Cruise can only start this far from every body's surface.</summary>
    public const double CruiseClearanceM = 5_000;

    /// <summary>Cruise drops out this close to any surface.</summary>
    public const double CruiseDropoutM = 3_000;

    /// <summary>Fuel burned per tonne of craft (with cargo) per second of cruise.</summary>
    public const double CruiseFuelKgPerTonnePerS = 0.01;

    public const double CruiseTurnFactor = 0.5;

    /// <summary>The autopilot cruises legs longer than this…</summary>
    public const double AutopilotCruiseMinDistanceM = 40_000;

    /// <summary>…and drops out this far before its goal.</summary>
    public const double AutopilotCruiseArriveM = 15_000;

    public const string FuelMaterial = "fuel";

    /// <summary>Fuel the craft would burn per second of cruise right now.</summary>
    public static double CruiseFuelKgPerS(CraftState craft) => CruiseFuelKgPerTonnePerS * craft.MassKg / 1000;

    /// <summary>Why the craft can't start cruising now, or null if it can.</summary>
    public string? CruiseBlocker(CraftState craft)
    {
        if (craft.Cruising) return "Already cruising.";
        if (craft.Stats.TotalThrustN <= 0) return $"{craft.Name} can't fly.";
        if (craft.IsDocked) return "Undock first.";
        if (craft.IsLanded) return "Lift off first.";
        if (craft.IsOnRails) return "Leave the parking orbit first (thrust).";
        if (craft.Inventory[FuelMaterial] < 1) return "No fuel in the hold (refine ice into fuel, then load it).";
        var (body, gap) = NearestSurface(craft.Position, TimeS);
        if (gap < CruiseClearanceM) return $"Too close to {body.Name}: cruise needs {CruiseClearanceM / 1000:0} km of clear space (now {gap / 1000:0.0} km).";
        return null;
    }

    public ActionResult TryStartCruise(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (CruiseBlocker(craft) is { } reason) return new(false, reason);
        craft.Cruising = true;
        craft.CruiseStopReason = null;
        return new(true, $"{craft.Name}: cruising at {CruiseSpeedMps:0} m/s. Steer with A/D; it drops out near any body.");
    }

    public ActionResult TryStopCruise(CraftId craftId)
    {
        var craft = GetCraft(craftId);
        if (!craft.Cruising) return new(false, $"{craft.Name} isn't cruising.");
        var (body, _) = NearestSurface(craft.Position, TimeS);
        StopCruise(craft, GetVelocityAt(body.Id, TimeS), "stopped");
        return new(true, $"{craft.Name}: dropped out of cruise, matched to {body.Name}.");
    }

    private void StopCruise(CraftState craft, Vector2D velocity, string reason)
    {
        craft.Cruising = false;
        craft.Velocity = velocity;
        craft.CruiseStopReason = reason;
    }

    /// <summary>The body whose surface is nearest, and the distance to it, at a time.</summary>
    private (CelestialBodyState Body, double Gap) NearestSurface(Vector2D point, double timeS)
    {
        CelestialBodyState? best = null;
        var bestGap = double.PositiveInfinity;
        var positions = AllBodyPositions(timeS);
        for (var i = 0; i < _bodies.Count; i++)
        {
            var gap = positions[i].DistanceTo(point) - _bodies[i].RadiusM;
            if (gap < bestGap) { best = _bodies[i]; bestGap = gap; }
        }
        return best is null ? throw new InvalidOperationException("Sector has no bodies.") : (best, bestGap);
    }

    /// <summary>One tick of cruise flight (instead of the flight model). Drops out instead of ever reaching a surface.</summary>
    private void StepCruise(CraftState craft, double endS)
    {
        craft.PreviousPosition = craft.Position;
        craft.PreviousHeadingRad = craft.HeadingRad;
        var controls = craft.Controls.Clamped();
        if (controls.Brake)
        {
            var (near, _) = NearestSurface(craft.Position, TimeS);
            StopCruise(craft, GetVelocityAt(near.Id, TimeS), "braked");
            craft.Position += craft.Velocity * FixedStepClock.TickSeconds;
            return;
        }

        // Fuel first: whole kg, the fraction carried to the next tick.
        var due = craft.CruiseFuelRemainderKg + CruiseFuelKgPerS(craft) * FixedStepClock.TickSeconds;
        var whole = (long)Math.Floor(due);
        if (craft.Inventory[FuelMaterial] < whole)
        {
            var (near, _) = NearestSurface(craft.Position, TimeS);
            StopCruise(craft, GetVelocityAt(near.Id, TimeS), "out of fuel");
            craft.CruiseFuelRemainderKg = 0;
            craft.Position += craft.Velocity * FixedStepClock.TickSeconds;
            return;
        }
        if (whole > 0)
        {
            craft.Inventory.TryRemove(new Dictionary<string, long> { [FuelMaterial] = whole });
            _ejectedKg[FuelMaterial] = (_ejectedKg.TryGetValue(FuelMaterial, out var kg) ? kg : 0) + whole;
        }
        craft.CruiseFuelRemainderKg = due - whole;

        craft.HeadingRad = FlightModel.WrapAngle(craft.HeadingRad + controls.Turn * FlightModel.TurnRateRadPerS * CruiseTurnFactor * FixedStepClock.TickSeconds);
        var velocity = craft.Forward * CruiseSpeedMps;
        var next = craft.Position + velocity * FixedStepClock.TickSeconds;
        var (body, gap) = NearestSurface(next, endS);
        if (gap < CruiseDropoutM)
        {
            // Arrive: drop out here, matched to the body ahead.
            StopCruise(craft, GetVelocityAt(body.Id, endS), $"arrived near {body.Name}");
            craft.Position += craft.Velocity * FixedStepClock.TickSeconds;
            return;
        }
        craft.Velocity = velocity;
        craft.Position = next;
    }

    /// <summary>
    /// For job autopilots: cruise long legs. Returns the controls for this tick while it is turning to
    /// cruise or cruising, or null when the normal autopilot should fly (short leg, no fuel, blocked
    /// path, or just arrived: then the craft is matched to the goal's velocity).
    /// </summary>
    private ShipControls? CruiseLeg(CraftState craft, Vector2D goal, Vector2D goalVelocity)
    {
        var toGoal = goal - craft.Position;
        var distance = toGoal.Length;
        if (!(distance > 0)) return null;
        var direction = toGoal * (1 / distance);

        if (craft.Cruising)
        {
            if (distance > AutopilotCruiseArriveM) return new ShipControls(0, Autopilot.TurnToward(craft, direction), false);
            StopCruise(craft, goalVelocity, "arrived");
            return null;
        }

        if (distance < AutopilotCruiseMinDistanceM) return null;
        // Checking whether to start costs a scan of every body: once a second per craft is plenty.
        if ((Tick + craft.Id.Value) % FixedStepClock.TicksPerSecond != 0 || CruiseBlocker(craft) is not null) return null;
        // Only if the straight line to the drop-out point stays clear of every body.
        var dropOut = goal - direction * AutopilotCruiseArriveM;
        for (var i = 0; i < _bodies.Count; i++)
            if (!SegmentClears(craft.Position, dropOut, BodyPositionAt(i, TimeS), _bodies[i].RadiusM + CruiseDropoutM + 1_000)) return null;

        var turn = Autopilot.TurnToward(craft, direction);
        var forward = craft.Forward;
        if (forward.X * direction.X + forward.Y * direction.Y < Math.Cos(5 * Math.PI / 180))
            return new ShipControls(0, turn, true); // line up first, holding still
        craft.Cruising = true;
        craft.CruiseStopReason = null;
        return new ShipControls(0, turn, false);
    }

    /// <summary>Solar output here, as a share of the output at Home's distance from the star (#53).</summary>
    public double SolarFactorAt(Vector2D point)
    {
        // The nearest star (#54: each system has its own).
        if (StarAt(point) is not { } star) return 1; // saves from before the star existed
        var distance = Math.Max(GetPosition(star.Id).DistanceTo(point), 1);
        var factor = SectorGenerator.HomeOrbitRadiusM * SectorGenerator.HomeOrbitRadiusM / (distance * distance);
        // In 10% steps, so everything around one planet gets the same power. 10% minimum (deep space), 400% maximum.
        return Math.Clamp(Math.Round(factor * 10) / 10, 0.1, 4);
    }

}
