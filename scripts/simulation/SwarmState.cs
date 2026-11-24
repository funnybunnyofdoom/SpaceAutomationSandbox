using System;
using System.Collections.Generic;
using SpaceAutomationSandbox.Construction;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>Stable identifier of a swarm (#30), never reused. 0 = none.</summary>
public readonly record struct SwarmId(uint Value)
{
    public static readonly SwarmId None = new(0);
    public bool IsNone => Value == 0;
    public override string ToString() => $"swarm:{Value}";
}

/// <summary>
/// Many identical ships on one mining job, simulated as a single flow (#30). The ships aren't
/// individual craft while in the swarm: it holds their count, their design, what's in their holds
/// (exact whole kg) and their batteries. See SectorState.Swarms for the rules.
/// </summary>
public sealed class SwarmState
{
    public SwarmState(SwarmId id, string name, ShipDesign design, ShipStats stats, int count,
        BodyId sourceBodyId, string materialId, CraftId destinationId, double returnFraction)
    {
        if (id.IsNone) throw new ArgumentException("Swarm needs a real id.", nameof(id));
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (!(returnFraction > 0) || returnFraction > 1) throw new ArgumentOutOfRangeException(nameof(returnFraction));
        Id = id;
        Name = name;
        Design = design;
        Stats = stats;
        Count = count;
        SourceBodyId = sourceBodyId;
        MaterialId = materialId;
        DestinationId = destinationId;
        ReturnFraction = returnFraction;
    }

    public SwarmId Id { get; }
    public string Name { get; }
    public ShipDesign Design { get; }
    public ShipStats Stats { get; }

    /// <summary>Ships in the swarm.</summary>
    public int Count { get; internal set; }

    public BodyId SourceBodyId { get; internal set; }
    public string MaterialId { get; internal set; }
    public CraftId DestinationId { get; internal set; }
    public double ReturnFraction { get; internal set; }

    /// <summary>What's in all the ships' holds together.</summary>
    public Inventory Inventory { get; } = new();

    /// <summary>Charge in all the ships' batteries together, kJ. Kept while swarming, shared out when ships leave.</summary>
    public double StoredEnergyKJ { get; internal set; }

    /// <summary>Mined and delivered mass not yet a whole kg (0..1). Saved.</summary>
    public double MineRemainderKg { get; internal set; }
    public double DeliverRemainderKg { get; internal set; }

    public long DeliveredKg { get; internal set; }

    /// <summary>Standing growth order: the yard that builds more ships for it, and how many are still to be ordered.</summary>
    public CraftId GrowYardId { get; internal set; } = CraftId.None;
    public int GrowRemaining { get; internal set; }

    /// <summary>Why growth is waiting, or null. Not saved.</summary>
    public string? GrowWaitReason { get; internal set; }

    /// <summary>Why it isn't working at its full rate (blocked or queued), or null. Recomputed every tick.</summary>
    public string? LimitReason { get; internal set; }

    /// <summary>What slows it without stopping it, or null. Recomputed every tick.</summary>
    public string? LimitNote { get; internal set; }

    /// <summary>The trip times and rate last worked out. Recomputed every tick; not saved.</summary>
    public SwarmRate Rate { get; internal set; }

    /// <summary>Deliveries smoothed over about a minute, kg/s (for display). Not saved.</summary>
    public double DeliveryKgPerS { get; internal set; }

    /// <summary>One ship's load per trip.</summary>
    public double LoadKg => ReturnFraction * Stats.CargoCapacityKg;

    public long CargoCapacityKg => Count * (long)Stats.CargoCapacityKg;

    /// <summary>The ships themselves, without cargo.</summary>
    public double DryMassKg => Count * Stats.TotalMassKg;

    public string Status => LimitReason is { } why ? $"waiting: {why}" : LimitNote is { } note ? $"working, {note}" : "working";
}

/// <summary>One ship's trip, worked out from its design and the distance now (#30).</summary>
public readonly record struct SwarmRate(double OutS, double MineS, double BackS, double CycleS, bool PowerLimited)
{
    /// <summary>The whole swarm's rate if nothing else limits it, kg/s.</summary>
    public double KgPerS(SwarmState swarm) => CycleS > 0 ? swarm.Count * swarm.LoadKg / CycleS : 0;
}

/// <summary>Ships that could swarm together: same design, same job (#30).</summary>
public sealed record SwarmCandidate(string DesignName, BodyId SourceBodyId, string MaterialId, CraftId DestinationId, IReadOnlyList<CraftId> Ships);
