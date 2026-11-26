using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// On-rails circular orbit around a parent body. PLACEHOLDER approved for Phase 1
/// (docs/06): position is a pure function of simulation time, with no integration,
/// so it can't drift and is identical at any tick rate. Real orbital mechanics are an
/// open question; the period is set directly rather than derived from gravity.
/// </summary>
/// <summary>A move from one circular orbit to another round the same parent (#127), between two times; smooth in radius and speed.</summary>
public sealed record OrbitMove(double StartS, double EndS, double FromRadiusM, double FromPeriodS, double FromPhaseRad)
{
    public double Seconds => EndS - StartS;
}

public sealed record CircularOrbit(BodyId Parent, double RadiusM, double PeriodS, double PhaseAtZeroRad)
{
    /// <summary>A move to this orbit under way (#127): before its start the old orbit, during it a smooth blend, after it this one.</summary>
    public OrbitMove? Move { get; init; }

    /// <summary>
    /// A move from <paramref name="from"/> to a circle of <paramref name="radiusM"/>, between two times: its period by Kepler's third law from the
    /// old one, and its phase so that the angle carries on without a jump. Pure function of time.
    /// </summary>
    public static CircularOrbit Moving(CircularOrbit from, double startS, double endS, double radiusM)
    {
        var period = from.PeriodS * Math.Pow(radiusM / from.RadiusM, 1.5);
        var move = new OrbitMove(startS, endS, from.RadiusM, from.PeriodS, from.PhaseAtZeroRad);
        var (w0, w1) = (2 * Math.PI / from.PeriodS, 2 * Math.PI / period);
        var endAngle = from.PhaseAtZeroRad + w0 * endS + (w1 - w0) * move.Seconds * 0.5;
        return new CircularOrbit(from.Parent, radiusM, period, endAngle - w1 * endS) { Move = move };
    }

    /// <summary>The move done: just the new circle.</summary>
    public CircularOrbit Settled() => this with { Move = null };

    /// <summary>The move stopped at a time: a circle where it was then, its period by Kepler's law.</summary>
    public CircularOrbit StoppedAt(double timeS)
    {
        if (Move is null) return this;
        var (radius, angle, _, _) = Blend(timeS);
        var period = Move.FromPeriodS * Math.Pow(radius / Move.FromRadiusM, 1.5);
        return new CircularOrbit(Parent, radius, period, angle - 2 * Math.PI * timeS / period);
    }

    /// <summary>Radius, angle and their rates of change during the move.</summary>
    private (double Radius, double Angle, double RadiusRate, double AngleRate) Blend(double timeS)
    {
        var m = Move!;
        var u = Math.Clamp((timeS - m.StartS) / m.Seconds, 0, 1);
        var smooth = u * u * (3 - 2 * u);
        var (w0, w1) = (2 * Math.PI / m.FromPeriodS, 2 * Math.PI / PeriodS);
        var radius = m.FromRadiusM + (RadiusM - m.FromRadiusM) * smooth;
        var angle = m.FromPhaseRad + w0 * timeS + (w1 - w0) * m.Seconds * (u * u * u - u * u * u * u / 2);
        return (radius, angle, (RadiusM - m.FromRadiusM) * 6 * u * (1 - u) / m.Seconds, w0 + (w1 - w0) * smooth);
    }

    /// <summary>Where a move stands at a time: before, during (with the blend) or after.</summary>
    private int Stage(double timeS) => Move is null ? 2 : timeS < Move.StartS ? 0 : timeS < Move.EndS ? 1 : 2;

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
        switch (Stage(timeS))
        {
            case 0:
                var a0 = Move!.FromPhaseRad + 2.0 * Math.PI * (timeS / Move.FromPeriodS);
                return new Vector2D(Math.Cos(a0) * Move.FromRadiusM, Math.Sin(a0) * Move.FromRadiusM);
            case 1:
                var (r, a, _, _) = Blend(timeS);
                return new Vector2D(Math.Cos(a) * r, Math.Sin(a) * r);
        }
        if (Eccentricity > 0) return PathPoint(EccentricAnomaly(timeS));
        var angle = PhaseAtZeroRad + 2.0 * Math.PI * (timeS / PeriodS);
        return new Vector2D(Math.Cos(angle) * RadiusM, Math.Sin(angle) * RadiusM);
    }

    /// <summary>Velocity relative to the parent at <paramref name="timeS"/> (exact derivative of <see cref="OffsetAt"/>).</summary>
    public Vector2D VelocityAt(double timeS)
    {
        switch (Stage(timeS))
        {
            case 0:
                var w = 2.0 * Math.PI / Move!.FromPeriodS;
                var a0 = Move.FromPhaseRad + w * timeS;
                return new Vector2D(-Math.Sin(a0) * Move.FromRadiusM * w, Math.Cos(a0) * Move.FromRadiusM * w);
            case 1:
                var (r, a, dr, da) = Blend(timeS);
                var (sin, cos) = Math.SinCos(a);
                return new Vector2D(dr * cos - r * da * sin, dr * sin + r * da * cos);
        }
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
