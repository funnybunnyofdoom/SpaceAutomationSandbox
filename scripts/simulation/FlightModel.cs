using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// PLACEHOLDER flight model for Phase 1 (docs/06): Newtonian inertia with no gravity, drag or
/// collisions. Acceleration = total thrust / total mass along the nose. "Brake" is a flight assist
/// that spends the same thrust directly against velocity, so the ship can stop without turning
/// around. With a target body set, braking matches the target's velocity instead (rendezvous assist).
/// Turning is a fixed rate. No fuel or power is used yet. Stepped only by whole fixed ticks.
/// </summary>
public static class FlightModel
{
    /// <summary>Placeholder turn rate (about 86°/s), the same for every design for now.</summary>
    public const double TurnRateRadPerS = 1.5;

    public static Vector2D ForwardFromHeading(double headingRad) =>
        new(Math.Sin(headingRad), -Math.Cos(headingRad));

    /// <param name="brakeReferenceVelocity">
    /// Velocity the brake matches: the target body's velocity, or zero to stop relative to the sector.
    /// </param>
    /// <param name="thrustAvailable">False when the craft has no power for its thrusters this tick: it can still turn.</param>
    public static void Step(CraftState craft, double dt, Vector2D brakeReferenceVelocity = default, bool thrustAvailable = true)
    {
        craft.PreviousPosition = craft.Position;
        craft.PreviousHeadingRad = craft.HeadingRad;

        var controls = craft.Controls.Clamped();
        if (!thrustAvailable) controls = controls with { Throttle = 0, Brake = false };
        craft.HeadingRad = WrapAngle(craft.HeadingRad + controls.Turn * TurnRateRadPerS * dt);

        // Cargo counts: a loaded ship accelerates and brakes more slowly.
        var maxDeltaV = craft.Stats.TotalThrustN / craft.MassKg * dt;
        var velocity = craft.Velocity;
        if (controls.Brake)
        {
            var relative = velocity - brakeReferenceVelocity;
            var speed = relative.Length;
            // Never overshoot: match exactly once the remaining difference is within one tick of thrust.
            velocity = speed <= maxDeltaV
                ? brakeReferenceVelocity
                : brakeReferenceVelocity + relative * ((speed - maxDeltaV) / speed);
        }
        else if (controls.Throttle > 0)
        {
            velocity += ForwardFromHeading(craft.HeadingRad) * (maxDeltaV * controls.Throttle);
        }

        // Semi-implicit Euler: the new velocity moves the ship this tick.
        craft.Velocity = velocity;
        craft.Position += velocity * dt;
    }

    /// <summary>Wraps to (−π, π].</summary>
    public static double WrapAngle(double rad)
    {
        var wrapped = Math.IEEERemainder(rad, 2 * Math.PI);
        return wrapped <= -Math.PI ? wrapped + 2 * Math.PI : wrapped;
    }
}
