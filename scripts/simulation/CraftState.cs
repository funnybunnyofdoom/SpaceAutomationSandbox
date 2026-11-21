using System;
using System.Collections.Generic;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Stable identifier of a craft (ship, later also stations and facilities). 0 = none.</summary>
public readonly record struct CraftId(uint Value)
{
    public static readonly CraftId None = new(0);
    public bool IsNone => Value == 0;
    public override string ToString() => $"craft:{Value}";
}

/// <summary>
/// Pilot commands held as state, so a player, and later an autonomous job, steers the same way
/// and a recorded per-tick input sequence replays exactly.
/// </summary>
public readonly record struct ShipControls(double Throttle, double Turn, bool Brake)
{
    public static readonly ShipControls Idle = new(0, 0, false);

    /// <summary>Throttle clamped to [0,1], turn to [−1,1] (positive = clockwise on screen); NaN becomes 0.</summary>
    public ShipControls Clamped() => new(Clamp(Throttle, 0, 1), Clamp(Turn, -1, 1), Brake);

    private static double Clamp(double v, double min, double max) => double.IsNaN(v) ? 0 : Math.Clamp(v, min, max);
}

/// <summary>
/// Authoritative state of one craft in a sector. Position is the centre of mass in sector metres;
/// heading is in radians, where 0 points the design's forward (−Y) to sector −Y (up on screen).
/// </summary>
public sealed class CraftState
{
    public CraftState(CraftId id, string name, ShipDesign design, ShipStats stats, Vector2D position, double headingRad)
    {
        if (id.IsNone) throw new ArgumentException("Craft needs a real id.", nameof(id));
        if (!(stats.TotalMassKg > 0)) throw new ArgumentException("Craft needs positive mass.", nameof(stats));
        Id = id;
        Name = name;
        Design = design;
        Stats = stats;
        Position = PreviousPosition = position;
        HeadingRad = PreviousHeadingRad = headingRad;
    }

    public CraftId Id { get; }
    public string Name { get; }
    public ShipDesign Design { get; }
    public ShipStats Stats { get; }

    public Vector2D Position { get; internal set; }
    public Vector2D Velocity { get; internal set; }
    public double HeadingRad { get; internal set; }

    /// <summary>State at the start of the last tick, for render interpolation only.</summary>
    public Vector2D PreviousPosition { get; internal set; }
    public double PreviousHeadingRad { get; internal set; }

    public ShipControls Controls { get; set; } = ShipControls.Idle;

    /// <summary>Body the brake matches velocity with. Set via <see cref="SectorState.SetTarget"/> so it's validated.</summary>
    public BodyId TargetBodyId { get; internal set; } = BodyId.None;

    /// <summary>Body the craft rests on, or None while flying. See <see cref="SectorState.Step"/>.</summary>
    public BodyId LandedOnBodyId { get; internal set; } = BodyId.None;

    /// <summary>While landed: the craft's centre relative to the body's centre (constant until lift-off).</summary>
    public Vector2D LandedOffset { get; internal set; }

    public bool IsLanded => !LandedOnBodyId.IsNone;

    /// <summary>Locked to the body it's landed on; thrust can't lift it. See SectorState.TryAnchor/TryRelease.</summary>
    public bool IsAnchored { get; internal set; }

    /// <summary>
    /// Fixed circular orbit the craft is held on ("on rails"): stations, and ships parked next to
    /// an orbiting yard. Null while flying freely or landed. A ship leaves it by thrusting.
    /// </summary>
    public CircularOrbit? RailOrbit { get; internal set; }

    public bool IsOnRails => RailOrbit is not null;

    /// <summary>Materials carried or stored here (whole kg).</summary>
    public Inventory Inventory { get; } = new();

    internal List<BuildOrder> BuildQueueList { get; } = new();

    /// <summary>Orders waiting or in progress; only the first one advances. Empty unless the craft has fabricators.</summary>
    public IReadOnlyList<BuildOrder> BuildQueue => BuildQueueList;

    public bool IsShipyard => Stats.FabricationRateKgPerS > 0;

    /// <summary>Collision radius in metres (bounding circle of the grid).</summary>
    public double CollisionRadiusM => Stats.BoundingRadiusM;

    public double SpeedMps => Velocity.Length;

    public Vector2D Forward => FlightModel.ForwardFromHeading(HeadingRad);
}
