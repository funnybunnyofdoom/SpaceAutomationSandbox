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
public readonly record struct ShipControls(double Throttle, double Turn, bool Brake, double Strafe = 0)
{
    public static readonly ShipControls Idle = new(0, 0, false);

    /// <summary>
    /// Throttle clamped to [−1,1] (negative = reverse thrusters), turn and strafe to [−1,1]
    /// (positive = clockwise / to the right on screen); NaN becomes 0.
    /// </summary>
    public ShipControls Clamped() => new(Clamp(Throttle, -1, 1), Clamp(Turn, -1, 1), Brake, Clamp(Strafe, -1, 1));

    /// <summary>True if any thruster is asked to fire (brake aside).</summary>
    public bool Thrusting => Throttle != 0 || Strafe != 0;

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
        StoredEnergyKJ = stats.EnergyCapacityKJ; // new craft start fully charged
    }

    /// <summary>Battery charge, kJ (0..Stats.EnergyCapacityKJ).</summary>
    public double StoredEnergyKJ { get; internal set; }

    /// <summary>This tick's available energy (battery + solar), spent by machines in order. Not saved.</summary>
    internal double EnergyPoolKJ { get; set; }

    /// <summary>True if something on this craft couldn't run this tick for lack of power.</summary>
    public bool PowerShort { get; internal set; }

    /// <summary>Set only by the save migration for craft from before power existed: they never run short.</summary>
    public bool LegacyFreePower { get; internal set; }

    /// <summary>Crew aboard (#11). Live in habitats; bigger craft need them to fly and work.</summary>
    public int Crew { get; internal set; }

    /// <summary>Why the crew can't run the craft this tick ("no food", "needs 3 crew (has 1)"…), or null. Not saved.</summary>
    public string? CrewShortReason { get; internal set; }

    /// <summary>Craft from saves older than v14: no crew rules apply.</summary>
    public bool LegacyNoCrew { get; internal set; }

    public double FoodRemainderKg { get; internal set; }
    public double WaterRemainderKg { get; internal set; }
    public double FarmRemainderKg { get; internal set; }
    public double CrewGrowthProgressS { get; internal set; }

    /// <summary>Cruise drive engaged (#53): moving at cruise speed along its heading. See SectorState.Cruise.</summary>
    public bool Cruising { get; internal set; }

    /// <summary>Cruise fuel burned but not yet whole kg (0..1).</summary>
    public double CruiseFuelRemainderKg { get; internal set; }

    /// <summary>Why cruise last ended ("arrived near Home", "out of fuel"…), for the HUD. Not saved.</summary>
    public string? CruiseStopReason { get; internal set; }

    /// <summary>Solar output share at this craft's distance from the star, set each tick (1 = Home's distance). Not saved.</summary>
    public double SolarFactor { get; internal set; } = 1;

    /// <summary>Ejected slag not yet whole kg (0..1).</summary>
    public double EjectionRemainderKg { get; internal set; }

    public CraftId Id { get; }
    public string Name { get; }
    /// <summary>Current design: parts that broke off (#34) are gone from it.</summary>
    public ShipDesign Design { get; internal set; }

    /// <summary>Stats of the current design and its part health.</summary>
    public ShipStats Stats { get; internal set; }

    internal Dictionary<GridPos, double> HealthList { get; } = new();

    /// <summary>Damaged parts (#34): health 0..1 by cell. Missing = undamaged. Parts at 0 have broken off.</summary>
    public IReadOnlyDictionary<GridPos, double> PartHealth => HealthList;

    /// <summary>How many parts broke off this craft (#34), for the HUD.</summary>
    public int PartsLost { get; internal set; }

    public Vector2D Position { get; internal set; }
    public Vector2D Velocity { get; internal set; }
    public double HeadingRad { get; internal set; }

    /// <summary>State at the start of the last tick, for render interpolation only.</summary>
    public Vector2D PreviousPosition { get; internal set; }
    public double PreviousHeadingRad { get; internal set; }

    public ShipControls Controls { get; set; } = ShipControls.Idle;

    /// <summary>Body the brake matches velocity with. Set via <see cref="SectorState.SetTarget"/> so it's validated.</summary>
    public BodyId TargetBodyId { get; internal set; } = BodyId.None;

    /// <summary>Craft the brake matches velocity with (exclusive with <see cref="TargetBodyId"/>).</summary>
    public CraftId TargetCraftId { get; internal set; } = CraftId.None;

    /// <summary>Station this ship is docked at, or None. See SectorState.TryDock/TryUndock.</summary>
    public CraftId DockedToCraftId { get; internal set; } = CraftId.None;

    /// <summary>Which of the station's docking ports (index into its stats) this ship occupies.</summary>
    public int DockPortIndex { get; internal set; }

    public bool IsDocked => !DockedToCraftId.IsNone;

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

    /// <summary>
    /// Every craft holds at most its cargo bays' and storage silos' capacity (#2). The only exception:
    /// stations from saves older than v9, made before storage parts existed, keep unlimited storage
    /// (<see cref="LegacyUnlimitedStorage"/>, set by the save migration) so those games keep working.
    /// </summary>
    public bool HasCargoLimit => !LegacyUnlimitedStorage;

    /// <summary>Set only by the v8→v9 save migration on stations that had no storage at all.</summary>
    public bool LegacyUnlimitedStorage { get; internal set; }

    public long CargoCapacityKg => HasCargoLimit ? (long)Stats.CargoCapacityKg : long.MaxValue;

    public long FreeCargoKg => HasCargoLimit ? Math.Max(0, CargoCapacityKg - CargoKg) : long.MaxValue;

    /// <summary>Everything in the hold: materials plus packed construction kits (whole kg).</summary>
    public long CargoKg => Inventory.TotalKg + KitsKg;

    internal List<ConstructionKit> KitsList { get; } = new();

    /// <summary>Construction kits carried or stored here (#8), in the order they arrived.</summary>
    public IReadOnlyList<ConstructionKit> Kits => KitsList;

    public long KitsKg
    {
        get
        {
            long kg = 0;
            foreach (var kit in KitsList) kg += kit.MassKg;
            return kg;
        }
    }

    /// <summary>Station requests (#1): "keep at least this much here", by material (whole kg).</summary>
    internal SortedDictionary<string, long> RequestsList { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Requests => RequestsList;

    /// <summary>Automation: a job (mining or freight) that drives this craft's controls while active. Null = none.</summary>
    public ShipJob? Job { get; internal set; }

    public bool IsAutomated => Job is { Active: true };

    /// <summary>The refinery batch in progress (inputs held here), or null.</summary>
    public RefiningBatch? RefiningBatch { get; internal set; }

    /// <summary>Drilled material not yet whole kg (0..1), carried between ticks so no fraction is lost.</summary>
    public double MiningRemainderKg { get; internal set; }

    /// <summary>Dry mass plus cargo: what thrust has to push.</summary>
    public double MassKg => Stats.TotalMassKg + CargoKg;

    internal List<BuildOrder> BuildQueueList { get; } = new();

    /// <summary>Orders waiting or in progress; only the first one advances. Empty unless the craft has fabricators.</summary>
    public IReadOnlyList<BuildOrder> BuildQueue => BuildQueueList;

    public bool IsShipyard => Stats.FabricationRateKgPerS > 0;

    /// <summary>Collision radius in metres (bounding circle of the grid).</summary>
    public double CollisionRadiusM => Stats.BoundingRadiusM;

    public double SpeedMps => Velocity.Length;

    public Vector2D Forward => FlightModel.ForwardFromHeading(HeadingRad);
}
