using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Steering for automated craft. It only produces <see cref="ShipControls"/> (turn, throttle,
/// brake), the same inputs a player gives, so automated ships obey exactly the same flight rules.
/// Brake matches the craft's current target (body or craft), so callers set that target to the
/// thing being approached. Deterministic: a pure function of the current state.
/// </summary>
public static class Autopilot
{
    /// <summary>Fraction of full deceleration planned for stopping, leaving margin for turning and curving targets.</summary>
    public const double StoppingMargin = 0.5;

    /// <summary>
    /// Controls that fly the craft towards <paramref name="targetPosition"/> (moving at
    /// <paramref name="targetVelocity"/>) and slow it to <paramref name="arrivalSpeed"/> relative to the
    /// target on arrival (0 = stop there). Speed relative to the target never exceeds <paramref name="maxSpeed"/>.
    /// </summary>
    public static ShipControls Approach(CraftState craft, Vector2D targetPosition, Vector2D targetVelocity,
        double maxSpeed, double arrivalSpeed = 0)
    {
        var toTarget = targetPosition - craft.Position;
        var distance = toTarget.Length;
        var relative = craft.Velocity - targetVelocity;
        var accel = craft.Stats.TotalThrustN / craft.MassKg;
        if (accel <= 0) return ShipControls.Idle;

        var direction = distance > 1e-6 ? toTarget * (1 / distance) : Vector2D.Zero;
        // Fastest speed from which we can still slow to the arrival speed in the distance left.
        var desiredSpeed = Math.Min(maxSpeed, Math.Sqrt(arrivalSpeed * arrivalSpeed + 2 * accel * StoppingMargin * distance));
        var closing = Dot(relative, direction);
        var lateral = (relative - direction * closing).Length;

        // Too fast, or sliding sideways: brake (matches the target's velocity) while turning to face it.
        if (closing > desiredSpeed + 1 || lateral > Math.Max(2, 0.25 * desiredSpeed))
            return new ShipControls(0, TurnToward(craft, direction), true);

        var error = direction * desiredSpeed - relative;
        var steer = error.Length > 0.3 ? error * (1 / error.Length) : direction;
        var turn = TurnToward(craft, steer);
        var aligned = Math.Abs(AngleTo(craft, steer)) < 0.3;
        return new ShipControls(aligned && error.Length > 0.5 ? 1 : 0, turn, false);
    }

    /// <summary>Turns towards a direction and thrusts along it, e.g. to climb away from a surface.</summary>
    public static ShipControls ThrustAlong(CraftState craft, Vector2D direction) =>
        new(Math.Abs(AngleTo(craft, direction)) < 0.3 ? 1 : 0, TurnToward(craft, direction), false);

    /// <summary>Turn input (−1..1) that rotates the nose towards <paramref name="direction"/> without overshooting.</summary>
    public static double TurnToward(CraftState craft, Vector2D direction)
    {
        if (direction.Length < 1e-9) return 0;
        var maxPerTick = FlightModel.TurnRateRadPerS * FixedStepClock.TickSeconds;
        return Math.Clamp(AngleTo(craft, direction) / maxPerTick, -1, 1);
    }

    private static double AngleTo(CraftState craft, Vector2D direction)
    {
        var wanted = Math.Atan2(direction.X, -direction.Y); // heading whose forward is `direction`
        return FlightModel.WrapAngle(wanted - craft.HeadingRad);
    }

    private static double Dot(Vector2D a, Vector2D b) => a.X * b.X + a.Y * b.Y;
}
