using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// Local gravity (#32). Each body pulls craft within <see cref="GravityReachRadii"/> of its radius,
/// falling off with 1/distance². Only the innermost reach a craft is inside counts (like patched
/// conics): next to a rock that orbits Home you feel the rock, not Home. Far from every body there
/// is no gravity, so cruise and long flights work as before. Bodies stay on rails (no gravity
/// between bodies). Applied to free-flying craft only: landed, docked, parked (rails) and cruising
/// craft are held by their own rules.
/// <para>PLACEHOLDER strengths at game scale (not from real masses, which would be ~1000× weaker):
/// see <see cref="SurfaceGravityMps2"/>. A starter ship accelerates at ~16 m/s², so it can always
/// climb away, and its brake can hover.</para>
/// </summary>
public sealed partial class SectorState
{
    /// <summary>A body pulls craft out to this many of its radii.</summary>
    public const double GravityReachRadii = 5;

    /// <summary>Gravity at the surface, by kind (m/s²).</summary>
    public static double SurfaceGravityMps2(BodyKind kind) => kind switch
    {
        BodyKind.Star => 3.0,
        BodyKind.GasGiant => 1.5,
        BodyKind.Planet => 0.6,
        BodyKind.Moon => 0.2,
        _ => 0.02, // asteroids: barely there
    };

    /// <summary>The body whose gravity applies at a point (innermost reach), or −1 for none.</summary>
    private int GravitySourceIndex(Vector2D point, double timeS)
    {
        var best = -1;
        var bestReach = double.PositiveInfinity;
        var positions = AllBodyPositions(timeS);
        for (var i = 0; i < _bodies.Count; i++)
        {
            var reach = _bodies[i].RadiusM * GravityReachRadii;
            if (reach >= bestReach) continue;
            var offset = point - positions[i];
            if (offset.X * offset.X + offset.Y * offset.Y <= reach * reach) { best = i; bestReach = reach; }
        }
        return best;
    }

    /// <summary>Gravitational acceleration at a point (zero outside every body's reach).</summary>
    public Vector2D GravityAt(Vector2D point, double timeS) => GravityAt(point, timeS, GravitySourceIndex(point, timeS));

    private Vector2D GravityAt(Vector2D point, double timeS, int index)
    {
        if (index < 0) return Vector2D.Zero;
        var body = _bodies[index];
        var toCentre = BodyPositionAt(index, timeS) - point;
        var distance = Math.Max(toCentre.Length, body.RadiusM);
        var g = SurfaceGravityMps2(body.Kind) * (body.RadiusM / distance) * (body.RadiusM / distance);
        return toCentre * (g / Math.Max(toCentre.Length, 1e-9));
    }

    /// <summary>Name of the body pulling at a point, or null (for the HUD).</summary>
    public CelestialBodyState? GravitySource(Vector2D point) =>
        GravitySourceIndex(point, TimeS) is var i and >= 0 ? _bodies[i] : null;

    /// <summary>
    /// Speed of a circular orbit at <paramref name="radiusM"/> from the body's centre under its gravity.
    /// Zero outside its reach (no gravity there: anything holds position).
    /// </summary>
    public static double CircularOrbitSpeedMps(CelestialBodyState body, double radiusM)
    {
        if (radiusM > body.RadiusM * GravityReachRadii) return 0;
        var g = SurfaceGravityMps2(body.Kind) * body.RadiusM * body.RadiusM / (radiusM * radiusM);
        return Math.Sqrt(g * radiusM);
    }

    /// <summary>Period of that circular orbit, for things put on rails around a body; <paramref name="fallbackS"/> outside its reach.</summary>
    public static double CircularOrbitPeriodS(CelestialBodyState body, double radiusM, double fallbackS)
    {
        var speed = CircularOrbitSpeedMps(body, radiusM);
        return speed > 0 ? 2 * Math.PI * radiusM / speed : fallbackS;
    }
}
