using System.Linq;
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

    /// <summary>This tick's available energy (battery + solar), spent by machines in order. Not saved.
    /// Only meaningful on a power network's root (#75): use <c>SectorState.PowerPoolKJ</c>.</summary>
    internal double EnergyPoolKJ { get; set; }

    // --- Power networks and generators (#75) ---

    /// <summary>The craft whose pool this craft draws from this tick: what it's docked to (followed to the end), else itself. Not saved.</summary>
    internal CraftState? PowerRoot { get; set; }

    /// <summary>On a root: every craft sharing its pool this tick, in id order. Not saved.</summary>
    internal List<CraftState> PowerMembers { get; } = new();

    /// <summary>Energy from batches already burned but not yet used (kJ), by generator part id: a batch is whole kg, so
    /// what one batch gives beyond this tick's need is kept for later (saved).</summary>
    internal SortedDictionary<string, double> GeneratorBankKJ { get; } = new(StringComparer.Ordinal);

    /// <summary>Energy this craft's generators gave this tick (kJ), for the power panel. Not saved.</summary>
    public double GeneratedKJThisTick { get; internal set; }

    /// <summary>On a network root: energy its members used this tick (kJ), for the power panel. Not saved.</summary>
    public double NetworkUsedKJThisTick { get; internal set; }

    /// <summary>On a network root: energy in its members' batteries at the start of this tick (kJ). Not saved.</summary>
    internal double NetworkStartKJ { get; set; }

    /// <summary>On a network root: its members' battery capacity this tick (kJ). Not saved.</summary>
    internal double NetworkCapacityKJ { get; set; }

    /// <summary>True while a body is between this craft and its star (#76): solar is dimmed. Not saved.</summary>
    public bool InShadow { get; internal set; }

    /// <summary>Energy put into each converter's current batch so far (kJ), by part id (#78). Saved.</summary>
    internal SortedDictionary<string, double> ConverterProgressKJ { get; } = new(StringComparer.Ordinal);

    /// <summary>Energy this craft's converters used this tick (kJ), for the power panel. Not saved.</summary>
    public double ConvertedKJThisTick { get; internal set; }

    /// <summary>Propellant burned but not yet a whole kg, by material (#80). Saved.</summary>
    internal SortedDictionary<string, double> PropellantDebtKg { get; } = new(StringComparer.Ordinal);

    /// <summary>Drives with an empty tank this tick, by part id (#80), for the HUD. Not saved.</summary>
    public IReadOnlyList<string> DryDrives { get; internal set; } = Array.Empty<string>();

    /// <summary>Energy this craft's stellar heat collectors made this tick (kJ, #77). Not saved.</summary>
    public double StellarHeatKJThisTick { get; internal set; }

    /// <summary>Geothermal energy this craft's taps made this tick (kJ, #77). Not saved.</summary>
    public double GeothermalKJThisTick { get; internal set; }

    /// <summary>Solar energy this craft made this tick (kJ). Not saved.</summary>
    public double SolarKJThisTick { get; internal set; }

    /// <summary>Generator energy kept from burned batches, kJ (#75).</summary>
    public double GeneratorBankTotalKJ => GeneratorBankKJ.Values.Sum();

    private Dictionary<string, double>? _generatorTickUse;

    /// <summary>Energy a generator type already gave this tick (its rating caps it). Not saved.</summary>
    internal double GeneratorTickUseKJ(string partId) => _generatorTickUse?.GetValueOrDefault(partId) ?? 0;

    internal void AddGeneratorTickUse(string partId, double kJ) =>
        (_generatorTickUse ??= new())[partId] = GeneratorTickUseKJ(partId) + kJ;

    internal void ClearGeneratorTickUse() => _generatorTickUse?.Clear();

    /// <summary>True if something on this craft couldn't run this tick for lack of power.</summary>
    public bool PowerShort { get; internal set; }

    /// <summary>Set only by the save migration for craft from before power existed: they never run short.</summary>
    public bool LegacyFreePower { get; internal set; }

    /// <summary>The travel leg it's on while simulated abstractly, far from the player (#17); null when flying for real.</summary>
    public TransitLeg? Transit { get; internal set; }

    /// <summary>Far from the player and waiting: moved with what it holds beside, not flown (#17). Recomputed each tick.</summary>
    public bool Dormant { get; internal set; }

    /// <summary>
    /// This tick only: brake relative to this craft instead of the current target (giving way, #17/#55).
    /// Set by the jobs step and cleared by the flight step of the same tick, so never saved.
    /// </summary>
    internal CraftId BrakeAgainst { get; set; }

    /// <summary>The craft this one is giving way to this tick (#55), for display. Not saved.</summary>
    public CraftId GivingWayTo { get; internal set; } = CraftId.None;

    /// <summary>Berth claimed for the approach (#55): the station, or None.</summary>
    public CraftId DockClaimStation { get; internal set; } = CraftId.None;

    /// <summary>The claimed port's index at <see cref="DockClaimStation"/>.</summary>
    public int DockClaimPort { get; internal set; }

    /// <summary>Crew aboard (#11). Live in habitats; bigger craft need them to fly and work.</summary>
    public int Crew { get; internal set; }

    /// <summary>Crew's food and water, as a multiple of the usual (#116). Set every tick; not saved.</summary>
    internal double CrewSupplyFactor { get; set; } = 1;

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
    /// <summary>What it's called; only shown, never used to find it (#115).</summary>
    public string Name { get; internal set; }
    /// <summary>Current design: parts that broke off (#34) are gone from it.</summary>
    public ShipDesign Design { get; internal set; }

    /// <summary>Stats of the current design and its part health.</summary>
    public ShipStats Stats
    {
        get
        {
            if (_healthChanged is not { } parts) return _stats;
            _healthChanged = null;
            return _stats = Design.ComputeStats(parts, HealthList);
        }
        internal set
        {
            _stats = value;
            _healthChanged = null;
            LookVersion++;
        }
    }

    private ShipStats _stats = null!;
    private PartCatalog? _healthChanged;

    /// <summary>What depends on the design only (centre of mass, size, ports, mass): right even before <see cref="Stats"/> catches up with damage (#114).</summary>
    internal ShipStats Shape => _stats;

    /// <summary>A part's health changed: the stats are worked out again when next read, so many hits in a tick cost it once (#114).</summary>
    internal void HealthChanged(PartCatalog parts)
    {
        _healthChanged = parts;
        LookVersion++;
    }

    /// <summary>Goes up when parts or their damage change: a view knows to draw it anew (#114). Not saved.</summary>
    internal int LookVersion { get; private set; }

    internal Dictionary<GridPos, double> HealthList { get; } = new();

    /// <summary>The design before parts broke off (#3), so a shipyard can rebuild them; null when nothing is missing.</summary>
    public ShipDesign? IntactDesign { get; internal set; }

    /// <summary>Work done on the current repair, in kg (#3). Saved.</summary>
    public double RepairProgressKg { get; internal set; }

    /// <summary>The shipyard an automated job is taking this craft to for repairs (#3); None otherwise. Saved.</summary>
    public CraftId ServiceAt { get; internal set; }

    /// <summary>What repairs on this craft are doing this tick, for display. Not saved.</summary>
    public string? RepairStatus { get; internal set; }

    /// <summary>Thrust the tugs docked to it gave this tick (#29), for display. Not saved.</summary>
    public double TugThrustN { get; internal set; }

    /// <summary>Fraction of a kg of electronics the lab has used but not yet taken from stock (#37). Saved.</summary>
    public double LabRemainderKg { get; internal set; }

    /// <summary>What the lab is doing this tick, for display. Not saved.</summary>
    public string? LabStatus { get; internal set; }

    // --- Combat (#84, #85) ---

    /// <summary>Whose craft this is: 0 the player's, else the civilization's id. Saved.</summary>
    public int FactionId { get; internal set; }

    /// <summary>One of a civilization's standing patrol, not a raider (#89), and which planet it heads for. Saved.</summary>
    public bool IsPatrol { get; internal set; }
    public int PatrolStop { get; internal set; }

    /// <summary>A civilization's trade station (#89). Saved.</summary>
    public bool IsTradeStation { get; internal set; }

    /// <summary>Shot to pieces: it drifts with what it holds and does nothing. Saved.</summary>
    public bool IsWreck { get; internal set; }

    /// <summary>The player's order not to fire. Saved.</summary>
    public bool HoldFire { get; set; }

    /// <summary>Flying itself to its target (#108): 0 no, 1 turning to it, 2 cruising. Saved.</summary>
    public int FlyToPhase { get; internal set; }

    /// <summary>A warp gate the player has closed with its gate lock (#95). Saved.</summary>
    public bool GateClosed { get; internal set; }

    // --- Antimatter and the quantum drive (#81, #82) ---

    /// <summary>How long its antimatter containment has been failing, s; 0 while it holds. Saved.</summary>
    public double ContainmentFailS { get; internal set; }

    /// <summary>Charge in its quantum drive, whether the player asked for a jump, and when it has cooled down. Saved.</summary>
    public double QuantumChargeKJ { get; internal set; }
    public bool QuantumRequested { get; internal set; }
    public long QuantumReadyTick { get; internal set; }

    // --- Animals (#93) ---

    /// <summary>Live animals in its pens: kg, kind and temper. Saved.</summary>
    public long PenKg { get; internal set; }
    public string PenAnimal { get; internal set; } = "";
    public AnimalTemper PenTemper { get; internal set; }

    /// <summary>Its weapons are hunting the animals where it's anchored. Saved.</summary>
    public bool Culling { get; internal set; }

    /// <summary>Ranched and culled mass not yet a whole kg. Saved.</summary>
    internal double RanchRemainderKg { get; set; }
    internal double CullRemainderKg { get; set; }

    /// <summary>Its repeller worked this tick; what its animal machines are doing. Recomputed every tick; not saved.</summary>
    public bool RepellerOn { get; internal set; }
    public string? AnimalStatus { get; internal set; }

    /// <summary>Charge in its shield (#90), and the tick it was last hit. Saved.</summary>
    public double ShieldCharge { get; internal set; }
    public long ShieldHitTick { get; internal set; } = -1_000_000;

    /// <summary>The shield is collapsing for lack of power (or crew). Not saved.</summary>
    public bool ShieldPowerless { get; internal set; }

    /// <summary>Work done on the part being cut (or not yet a whole kg loaded), kg (#86). Saved.</summary>
    internal double SalvageProgressKg { get; set; }

    /// <summary>What the salvage arm is doing this tick, for display. Not saved.</summary>
    public string? SalvageStatus { get; internal set; }

    /// <summary>When each weapon can fire again, by cell. Saved.</summary>
    internal Dictionary<GridPos, long> WeaponReadyTicks { get; } = new();

    /// <summary>For a warp gate (#22): the gate it sends craft to (in another system); None until its first jump. Saved.</summary>
    public CraftId GateLinkId { get; internal set; }

    /// <summary>The system whose far gate was destroyed (#36): the next jump sends a new one there. None otherwise.</summary>
    public BodyId GateLostStarId { get; internal set; }

    /// <summary>For a warp gate: the tick it can send the next craft (0 = ready now). Saved.</summary>
    public long GateReadyTick { get; internal set; }

    /// <summary>The tick this craft asked to jump through the gate it's docked at (0 = not asking). Saved; it sets the queue order.</summary>
    public long JumpRequestTick { get; internal set; }

    /// <summary>Jumps this craft has made (for display). Saved.</summary>
    public int Jumps { get; internal set; }

    /// <summary>Where it stands at a gate this tick ("queued at X, 2 ahead"), for display. Not saved.</summary>
    public string? GateStatus { get; internal set; }

    /// <summary>Robots working this tick (#13): 0 without power. Recomputed every tick; not saved.</summary>
    public int RobotsWorking { get; internal set; }

    /// <summary>A refit, dismantle or pack-as-kit order a shipyard is working on (#56, #57); null when none. Saved.</summary>
    public YardOrder? YardOrder { get; internal set; }

    /// <summary>Work done on the current step of <see cref="YardOrder"/>, in kg. Saved.</summary>
    public double YardProgressKg { get; internal set; }

    /// <summary>What the yard order is doing this tick, for display. Not saved.</summary>
    public string? YardStatus { get; internal set; }

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
    public double MassKg => Stats.TotalMassKg + CargoKg + PenKg;

    internal List<BuildOrder> BuildQueueList { get; } = new();

    /// <summary>Orders waiting or in progress; only the first one advances. Empty unless the craft has fabricators.</summary>
    public IReadOnlyList<BuildOrder> BuildQueue => BuildQueueList;

    public bool IsShipyard => Stats.FabricationRateKgPerS > 0;

    /// <summary>Collision radius in metres (bounding circle of the grid).</summary>
    public double CollisionRadiusM => Shape.BoundingRadiusM;

    public double SpeedMps => Velocity.Length;

    public Vector2D Forward => FlightModel.ForwardFromHeading(HeadingRad);
}
