using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// On-rails circular orbit around a parent body. PLACEHOLDER approved for Phase 1
/// (docs/06): position is a pure function of simulation time, with no integration,
/// so it can't drift and is identical at any tick rate. Real orbital mechanics are an
/// open question; the period is set directly rather than derived from gravity.
/// </summary>
public sealed record CircularOrbit(BodyId Parent, double RadiusM, double PeriodS, double PhaseAtZeroRad)
{
    /// <summary>Offset from the parent's centre at <paramref name="timeS"/> seconds of simulation time.</summary>
    public Vector2D OffsetAt(double timeS)
    {
        var angle = PhaseAtZeroRad + 2.0 * Math.PI * (timeS / PeriodS);
        return new Vector2D(Math.Cos(angle) * RadiusM, Math.Sin(angle) * RadiusM);
    }

    /// <summary>Velocity relative to the parent at <paramref name="timeS"/> (exact derivative of <see cref="OffsetAt"/>).</summary>
    public Vector2D VelocityAt(double timeS)
    {
        var omega = 2.0 * Math.PI / PeriodS;
        var angle = PhaseAtZeroRad + omega * timeS;
        return new Vector2D(-Math.Sin(angle) * RadiusM * omega, Math.Cos(angle) * RadiusM * omega);
    }

    /// <summary>Orbital speed, constant for a circle.</summary>
    public double SpeedMps => 2.0 * Math.PI * RadiusM / PeriodS;
}
