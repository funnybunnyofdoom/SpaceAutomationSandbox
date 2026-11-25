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
    /// <summary>0: a circle. Above, an ellipse with the parent at a focus (comets, #123): <see cref="RadiusM"/> is half its long axis and the phase is the mean anomaly.</summary>
    public double Eccentricity { get; init; }

    /// <summary>Direction from the parent to the ellipse's nearest point.</summary>
    public double PeriapsisRad { get; init; }

    /// <summary>The eccentric anomaly: Kepler's equation, by a fixed number of Newton steps (the same everywhere).</summary>
    private double EccentricAnomaly(double timeS)
    {
        var mean = Math.IEEERemainder(PhaseAtZeroRad + 2.0 * Math.PI * (timeS / PeriodS), 2 * Math.PI);
        var e = mean + Eccentricity * Math.Sin(mean);
        for (var i = 0; i < 12; i++) e -= (e - Eccentricity * Math.Sin(e) - mean) / (1 - Eccentricity * Math.Cos(e));
        return e;
    }

    private Vector2D Turned(double x, double y)
    {
        var (sin, cos) = Math.SinCos(PeriapsisRad);
        return new Vector2D(x * cos - y * sin, x * sin + y * cos);
    }

    /// <summary>A point of its path, by eccentric anomaly (for drawing).</summary>
    public Vector2D PathPoint(double anomaly) => Turned(RadiusM * (Math.Cos(anomaly) - Eccentricity), RadiusM * Math.Sqrt(1 - Eccentricity * Eccentricity) * Math.Sin(anomaly));

    /// <summary>Offset from the parent's centre at <paramref name="timeS"/> seconds of simulation time.</summary>
    public Vector2D OffsetAt(double timeS)
    {
        if (Eccentricity > 0) return PathPoint(EccentricAnomaly(timeS));
        var angle = PhaseAtZeroRad + 2.0 * Math.PI * (timeS / PeriodS);
        return new Vector2D(Math.Cos(angle) * RadiusM, Math.Sin(angle) * RadiusM);
    }

    /// <summary>Velocity relative to the parent at <paramref name="timeS"/> (exact derivative of <see cref="OffsetAt"/>).</summary>
    public Vector2D VelocityAt(double timeS)
    {
        if (Eccentricity > 0)
        {
            var e = EccentricAnomaly(timeS);
            var rate = 2.0 * Math.PI / PeriodS / (1 - Eccentricity * Math.Cos(e));
            return Turned(-RadiusM * Math.Sin(e) * rate, RadiusM * Math.Sqrt(1 - Eccentricity * Eccentricity) * Math.Cos(e) * rate);
        }
        var omega = 2.0 * Math.PI / PeriodS;
        var angle = PhaseAtZeroRad + omega * timeS;
        return new Vector2D(-Math.Sin(angle) * RadiusM * omega, Math.Cos(angle) * RadiusM * omega);
    }

    /// <summary>Orbital speed, constant for a circle.</summary>
    public double SpeedMps => 2.0 * Math.PI * RadiusM / PeriodS;
}
