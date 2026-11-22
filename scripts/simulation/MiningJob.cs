using System;

namespace SpaceAutomationSandbox.Simulation;

/// <summary>
/// A job that drives a craft (data, not code). Common parts: what it moves, where to, whether it
/// runs, progress counters, and why it's waiting. Run by SectorState.StepJobs.
/// </summary>
public abstract class ShipJob
{
    protected ShipJob(string materialId, CraftId destinationId)
    {
        if (destinationId.IsNone) throw new ArgumentException("Job needs a destination.", nameof(destinationId));
        _materialId = materialId;
        _destinationId = destinationId;
    }

    /// <summary>For jobs that pick what and where per trip (<see cref="ServeRequestsJob"/>).</summary>
    protected ShipJob()
    {
        _materialId = "";
        _destinationId = CraftId.None;
    }

    private readonly string _materialId;
    private readonly CraftId _destinationId;

    /// <summary>What it moves ("" when the job hasn't picked anything yet).</summary>
    public virtual string MaterialId => _materialId;

    /// <summary>Where it delivers (None when the job hasn't picked anything yet).</summary>
    public virtual CraftId DestinationId => _destinationId;

    public bool Active { get; internal set; } = true;

    /// <summary>Why the job is waiting, or null when it isn't.</summary>
    public string? WaitReason { get; internal set; }

    /// <summary>Completed cycles (mine or pick up → deliver).</summary>
    public int CompletedCycles { get; internal set; }

    /// <summary>Total kg unloaded at the destination.</summary>
    public long DeliveredKg { get; internal set; }

    /// <summary>While waiting: don't retry before this tick (0 = retry every tick).</summary>
    public long RetryAtTick { get; internal set; }

    /// <summary>What it is doing now, in plain words (with the wait reason when blocked).</summary>
    public abstract string Status { get; }

    /// <summary>Short kind name for UI and saves.</summary>
    public abstract string Kind { get; }
}

/// <summary>Where a mining job is in its cycle.</summary>
public enum MiningJobState
{
    ToSource,
    Landing,
    Mining,
    Lifting,
    ToDestination,
    Unloading,
    Waiting,
}

/// <summary>
/// Repeatable mining: fly to <see cref="SourceBodyId"/>, land, anchor, mine <see cref="ShipJob.MaterialId"/>
/// until the hold is <see cref="ReturnFraction"/> full (or the deposit runs out), fly to the destination,
/// dock, unload, repeat.
/// </summary>
public sealed class MiningJob : ShipJob
{
    public MiningJob(BodyId sourceBodyId, string materialId, CraftId destinationId, double returnFraction)
        : base(materialId, destinationId)
    {
        if (sourceBodyId.IsNone) throw new ArgumentException("Job needs a source body.", nameof(sourceBodyId));
        if (!(returnFraction > 0) || returnFraction > 1) throw new ArgumentOutOfRangeException(nameof(returnFraction));
        SourceBodyId = sourceBodyId;
        ReturnFraction = returnFraction;
    }

    public BodyId SourceBodyId { get; }

    /// <summary>Head home when cargo reaches this fraction of capacity (0–1].</summary>
    public double ReturnFraction { get; }

    public MiningJobState State { get; internal set; } = MiningJobState.ToSource;

    /// <summary>State to resume after a wait clears.</summary>
    public MiningJobState ResumeState { get; internal set; } = MiningJobState.ToSource;

    public override string Kind => "mining";

    public override string Status => !Active ? "stopped"
        : State == MiningJobState.Waiting ? $"waiting: {WaitReason}"
        : State switch
        {
            MiningJobState.ToSource => "flying to the source",
            MiningJobState.Landing => "landing",
            MiningJobState.Mining => "mining",
            MiningJobState.Lifting => "lifting off",
            MiningJobState.ToDestination => "flying to the destination",
            MiningJobState.Unloading => "unloading",
            _ => State.ToString(),
        };
}

/// <summary>Where a freight job is in its cycle.</summary>
public enum HaulJobState
{
    ToPickup,
    Loading,
    ToDropoff,
    Unloading,
    Waiting,
}

/// <summary>
/// Repeatable freight: dock at <see cref="PickupId"/>, load <see cref="ShipJob.MaterialId"/> once at least
/// <see cref="MinPickupKg"/> is there (as much as fits), fly to the destination, dock, unload, repeat.
/// With <see cref="KeepStockedKg"/> &gt; 0 it only delivers until the destination holds that much
/// ("keep stocked"), then waits there with the rest. Replaces the player hauling goods by hand.
/// </summary>
public sealed class HaulJob : ShipJob
{
    public HaulJob(CraftId pickupId, string materialId, CraftId destinationId, long minPickupKg, long keepStockedKg)
        : base(materialId, destinationId)
    {
        if (pickupId.IsNone) throw new ArgumentException("Job needs a pickup station.", nameof(pickupId));
        if (minPickupKg < 1) throw new ArgumentOutOfRangeException(nameof(minPickupKg));
        if (keepStockedKg < 0) throw new ArgumentOutOfRangeException(nameof(keepStockedKg));
        PickupId = pickupId;
        MinPickupKg = minPickupKg;
        KeepStockedKg = keepStockedKg;
    }

    public CraftId PickupId { get; }

    /// <summary>Wait at the pickup until at least this much is there (avoids hauling scraps).</summary>
    public long MinPickupKg { get; }

    /// <summary>0 = deliver everything; otherwise deliver only until the destination holds this much.</summary>
    public long KeepStockedKg { get; }

    public HaulJobState State { get; internal set; } = HaulJobState.ToPickup;
    public HaulJobState ResumeState { get; internal set; } = HaulJobState.ToPickup;

    public override string Kind => "freight";

    public override string Status => !Active ? "stopped"
        : State == HaulJobState.Waiting ? $"waiting: {WaitReason}"
        : State switch
        {
            HaulJobState.ToPickup => "flying to the pickup",
            HaulJobState.Loading => "loading",
            HaulJobState.ToDropoff => "flying to the destination",
            HaulJobState.Unloading => "unloading",
            _ => State.ToString(),
        };
}
